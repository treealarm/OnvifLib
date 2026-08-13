using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// The left-hand device list: discovery, manual add, connect, and JPEG thumbnails for connected cameras.
/// </summary>
public sealed partial class DeviceListViewModel : ObservableObject
{
  private readonly OperationRunner _runner;
  private readonly UiLogger _logger;
  private readonly Func<DeviceEntry, Task<CameraSession?>> _connect;
  private CancellationTokenSource? _probeCancellation;
  private CancellationTokenSource? _thumbsCancellation;
  private Task? _thumbsTask;
  private CancellationTokenSource? _watchCancellation;
  private Task? _watchTask;
  private DateTime _nextWatchUtc = DateTime.MinValue;
  private int _reconnectBackoffSec = 5;
  private bool _suppressConnect;

  public DeviceListViewModel(
    OperationRunner runner,
    UiLogger logger,
    Func<DeviceEntry, Task<CameraSession?>> connect)
  {
    _runner = runner;
    _logger = logger;
    _connect = connect;
  }

  public ObservableCollection<DeviceEntry> Devices { get; } = [];

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
  [NotifyCanExecuteChangedFor(nameof(DisconnectSelectedCommand))]
  [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
  [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
  [NotifyPropertyChangedFor(nameof(HasSelection))]
  [NotifyPropertyChangedFor(nameof(EditorTitle))]
  private DeviceEntry? _selected;

  [ObservableProperty] private string _displayName = "";
  [ObservableProperty] private string _ip = "192.168.1.10";
  [ObservableProperty] private int _port = 80;
  [ObservableProperty] private string _user = "admin";
  [ObservableProperty] private string _password = "";
  [ObservableProperty] private bool _rememberPassword;
  [ObservableProperty] private string? _xaddr;
  [ObservableProperty] private bool _revealPassword;
  [ObservableProperty] private bool _isDiscovering;
  [ObservableProperty] private string _listStatus = "no devices yet — Discover or Add";

  public bool HasSelection => Selected is not null;

  public string EditorTitle => Selected is { } device
    ? $"Selected — {device.DisplayName}"
    : "No camera selected — Add or Discover first";

  /// <summary>Raised when the selected row changes, including a re-click of the same row.</summary>
  public event Action<DeviceEntry?>? SelectionCommitted;

  partial void OnSelectedChanged(DeviceEntry? value)
  {
    if (value is not null)
    {
      DisplayName = value.DisplayName;
      Ip = value.Ip;
      Port = value.Port;
      User = value.User;
      Password = value.Password;
      RememberPassword = value.RememberPassword;
      Xaddr = value.Xaddr;
    }

    SelectionCommitted?.Invoke(value);
    if (!_suppressConnect && value is { IsConnected: false, State: not DeviceConnectionState.Connecting })
      _ = ConnectCoreAsync(value);
  }

  public void Load(IEnumerable<SavedDevice> saved, string defaultIp, int defaultPort, string defaultUser, string defaultPassword, bool remember)
  {
    _suppressConnect = true;
    try
    {
      Devices.Clear();
      foreach (var item in saved)
      {
        if (string.IsNullOrWhiteSpace(item.Ip)) continue;
        Devices.Add(DeviceEntry.FromSaved(item));
      }

      if (Devices.Count > 0)
      {
        Selected = Devices[0];
        ListStatus = $"{Devices.Count} remembered device(s)";
      }
      else
      {
        Ip = defaultIp;
        Port = defaultPort;
        User = defaultUser;
        Password = remember ? defaultPassword : "";
        RememberPassword = remember;
      }
    }
    finally { _suppressConnect = false; }
  }

  /// <summary>Connect the selected camera if it is not already. Used once the shell has finished restoring.</summary>
  public void ConnectSelectedIfNeeded()
  {
    if (Selected is { IsConnected: false, State: not DeviceConnectionState.Connecting } device)
      _ = ConnectCoreAsync(device);
  }

  public IReadOnlyList<SavedDevice> Snapshot() => Devices.Select(d => d.ToSaved()).ToList();

  public DeviceEntry AddOrUpdate(string ip, int port, string? xaddr, string? name = null, string? user = null, string? password = null, bool select = true)
  {
    var existing = Devices.FirstOrDefault(d => d.Ip == ip && d.Port == port);
    if (existing is not null)
    {
      if (xaddr is { Length: > 0 }) existing.Xaddr = xaddr;
      if (name is { Length: > 0 }) existing.DisplayName = name;
      if (user is { Length: > 0 }) existing.User = user;
      if (password is { Length: > 0 } && string.IsNullOrEmpty(existing.Password)) existing.Password = password;
      if (select) Selected = existing;
      return existing;
    }

    var entry = new DeviceEntry
    {
      Ip = ip,
      Port = port,
      Xaddr = xaddr,
      User = user ?? User,
      Password = password ?? Password,
      RememberPassword = RememberPassword,
      DisplayName = name is { Length: > 0 } ? name : $"{ip}:{port}",
    };
    Devices.Add(entry);
    if (select) Selected = entry;
    ListStatus = $"{Devices.Count} device(s)";
    return entry;
  }

  [RelayCommand]
  private void Add()
  {
    if (string.IsNullOrWhiteSpace(Ip))
    {
      _runner.Report("Enter an address first", isError: true);
      return;
    }

    var entry = AddOrUpdate(Ip.Trim(), Port, BlankToNull(Xaddr), user: User, password: Password);
    ListStatus = $"added {entry.AddressText}";
  }

  [RelayCommand(CanExecute = nameof(CanApply))]
  private void Apply()
  {
    if (Selected is not { } device) return;
    ApplyFieldsToSelected();
    OnPropertyChanged(nameof(EditorTitle));
    ListStatus = device.IsConnected
      ? $"updated {device.DisplayName} ({device.AddressText}) — Connect again if the address or password changed"
      : $"updated {device.DisplayName} ({device.AddressText})";
    _runner.Report(ListStatus);
  }

  private bool CanApply() => Selected is not null;

  [RelayCommand(CanExecute = nameof(CanConnect))]
  private async Task ConnectAsync()
  {
    ApplyFieldsToSelected();
    if (Selected is not { } device) { Add(); device = Selected!; }

    if (device.IsConnected)
    {
      if (MatchesLiveSession(device))
      {
        SelectionCommitted?.Invoke(device);
        return;
      }

      await DisconnectAsync(device).ConfigureAwait(true);
    }

    await ConnectCoreAsync(device).ConfigureAwait(true);
  }

  private bool CanConnect() => Selected is not { State: DeviceConnectionState.Connecting };

  [RelayCommand(CanExecute = nameof(CanDisconnect))]
  private async Task DisconnectSelectedAsync()
  {
    if (Selected is not { } device) return;
    device.WantConnected = false;
    device.EverConnected = false;
    await DisconnectAsync(device).ConfigureAwait(true);
    SelectionCommitted?.Invoke(device);
  }

  private bool CanDisconnect() => Selected?.WantConnected == true;

  private async Task ConnectCoreAsync(DeviceEntry device)
  {
    device.WantConnected = true;
    device.State = DeviceConnectionState.Connecting;
    device.ErrorText = "";
    ConnectCommand.NotifyCanExecuteChanged();
    DisconnectSelectedCommand.NotifyCanExecuteChanged();

    CameraSession? session;
    try
    {
      session = await _connect(device).ConfigureAwait(true);
    }
    catch (Exception ex)
    {
      if (!device.EverConnected) device.WantConnected = false;
      device.State = DeviceConnectionState.Error;
      device.ErrorText = ex.Message;
      _runner.Report($"Connect {device.AddressText} — {ex.Message}", isError: true);
      ConnectCommand.NotifyCanExecuteChanged();
      DisconnectSelectedCommand.NotifyCanExecuteChanged();
      return;
    }

    if (session is null)
    {
      if (!device.EverConnected) device.WantConnected = false;
      device.State = DeviceConnectionState.Error;
      if (string.IsNullOrEmpty(device.ErrorText)) device.ErrorText = "connection failed";
      ConnectCommand.NotifyCanExecuteChanged();
      DisconnectSelectedCommand.NotifyCanExecuteChanged();
      return;
    }

    if (!device.WantConnected)
    {
      try { session.Dispose(); }
      catch (Exception ex) { _logger.Warning($"discarding cancelled connect {device.AddressText}: {ex.Message}"); }
      device.State = DeviceConnectionState.Disconnected;
      ConnectCommand.NotifyCanExecuteChanged();
      DisconnectSelectedCommand.NotifyCanExecuteChanged();
      return;
    }

    device.EverConnected = true;
    device.Session = session;
    if (session.DeviceInfo is { } info)
    {
      var label = $"{info.Manufacturer} {info.Model}".Trim();
      if (label.Length > 0) device.DisplayName = label;
    }

    ConnectCommand.NotifyCanExecuteChanged();
    DisconnectSelectedCommand.NotifyCanExecuteChanged();
    EnsureThumbs();
    EnsureWatch();
    _reconnectBackoffSec = 5;

    // Do not steal the selection if the user clicked another row while this one was connecting.
    if (ReferenceEquals(Selected, device))
    {
      DisplayName = device.DisplayName;
      OnPropertyChanged(nameof(EditorTitle));
      SelectionCommitted?.Invoke(device);
    }
  }

  public async Task DisconnectAsync(DeviceEntry device)
  {
    var session = device.Session;
    device.Session = null;
    if (!device.WantConnected) device.State = DeviceConnectionState.Disconnected;
    else if (device.State != DeviceConnectionState.Connecting)
      device.State = DeviceConnectionState.Error;
    device.SetThumbnail(null);
    if (session is not null)
    {
      try { session.Dispose(); }
      catch (Exception ex) { _logger.Warning($"disposing {device.AddressText}: {ex.Message}"); }
    }

    DisconnectSelectedCommand.NotifyCanExecuteChanged();
    ConnectCommand.NotifyCanExecuteChanged();
  }

  [RelayCommand(CanExecute = nameof(CanRemove))]
  private async Task RemoveAsync()
  {
    if (Selected is not { } device) return;
    device.WantConnected = false;
    device.EverConnected = false;
    await DisconnectAsync(device).ConfigureAwait(true);
    device.DisposeThumbnail();
    var index = Devices.IndexOf(device);
    Devices.Remove(device);
    Selected = Devices.Count == 0 ? null : Devices[Math.Clamp(index, 0, Devices.Count - 1)];
    ListStatus = Devices.Count == 0 ? "no devices" : $"{Devices.Count} device(s)";
  }

  private bool CanRemove() => Selected is not null;

  [RelayCommand]
  private async Task DiscoverAsync()
  {
    _probeCancellation?.Cancel();
    _probeCancellation = new CancellationTokenSource();
    var token = _probeCancellation.Token;
    IsDiscovering = true;

    var (ok, result) = await _runner.RunAsync("WS-Discovery",
      () => WsDiscovery.ProbeAsync(TimeSpan.FromSeconds(4), token, _logger));

    IsDiscovering = false;
    if (!ok || result is null) { ListStatus = "discovery failed — see the Log tab"; return; }

    if (!result.ScanOk)
    {
      ListStatus = "Could not probe — no interface joined 239.255.255.250:3702";
      return;
    }

    DeviceEntry? firstFound = null;
    foreach (var device in result.Devices)
    {
      var name = device.Name ?? device.Hardware;
      var entry = AddOrUpdate(device.Ip, device.Port, device.XAddrs.FirstOrDefault(), name, User, Password, select: false);
      firstFound ??= entry;
    }

    if (Selected is null && firstFound is not null) Selected = firstFound;

    ListStatus = result.Devices.Count == 0
      ? "discovery finished — nothing answered"
      : $"discovery found {result.Devices.Count} device(s)";
  }

  [RelayCommand]
  private void CancelDiscover() => _probeCancellation?.Cancel();

  public void ApplyFieldsToSelected()
  {
    if (Selected is not { } device) return;
    device.Ip = Ip.Trim();
    device.Port = Port;
    device.User = User;
    device.Password = Password;
    device.RememberPassword = RememberPassword;
    device.Xaddr = BlankToNull(Xaddr);

    var name = DisplayName.Trim();
    device.DisplayName = name.Length == 0 || LooksLikeAddress(name)
      ? $"{device.Ip}:{device.Port}"
      : name;
    DisplayName = device.DisplayName;
  }

  private static bool MatchesLiveSession(DeviceEntry device) =>
    device.Session is { Camera: var camera }
    && camera.Ip == device.Ip
    && camera.Port == device.Port
    && camera.User == device.User
    && camera.Password == device.Password;

  private static string? BlankToNull(string? value) =>
    string.IsNullOrWhiteSpace(value) ? null : value.Trim();

  public void EnsureThumbs()
  {
    if (_thumbsTask is { IsCompleted: false }) return;
    _thumbsCancellation?.Cancel();
    _thumbsCancellation = new CancellationTokenSource();
    _thumbsTask = Task.Run(() => ThumbLoopAsync(_thumbsCancellation.Token));
  }

  public void EnsureWatch()
  {
    if (_watchTask is { IsCompleted: false }) return;
    _watchCancellation?.Cancel();
    _watchCancellation = new CancellationTokenSource();
    _watchTask = Task.Run(() => WatchLoopAsync(_watchCancellation.Token));
  }

  public async Task ShutdownAsync()
  {
    _probeCancellation?.Cancel();
    _thumbsCancellation?.Cancel();
    _watchCancellation?.Cancel();
    if (_watchTask is { } watch)
    {
      try { await watch.ConfigureAwait(false); }
      catch (OperationCanceledException) { }
    }
    if (_thumbsTask is { } thumbs)
    {
      try { await thumbs.ConfigureAwait(false); }
      catch (OperationCanceledException) { }
    }

    foreach (var device in Devices.ToList())
    {
      device.WantConnected = false;
      device.EverConnected = false;
      await DisconnectAsync(device).ConfigureAwait(true);
      device.DisposeThumbnail();
    }
  }

  private async Task WatchLoopAsync(CancellationToken cancellation)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
    try
    {
      while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
      {
        if (DateTime.UtcNow < _nextWatchUtc) continue;

        DeviceEntry? device = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
          device = Selected is { WantConnected: true, State: not DeviceConnectionState.Connecting }
            ? Selected
            : null;
        });

        if (device is null) continue;

        var alive = false;
        if (device.Session is { } session)
        {
          try { alive = await session.Camera.IsAlive().ConfigureAwait(false); }
          catch (Exception ex)
          {
            _logger.Warning($"watch {device.AddressText}: {ex.Message}");
          }
        }
        else if (!device.EverConnected)
        {
          continue;
        }

        if (alive)
        {
          _reconnectBackoffSec = 5;
          _nextWatchUtc = DateTime.UtcNow.AddSeconds(12);
          continue;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
          _runner.Report($"Camera {device.AddressText} dropped — reconnecting…", isError: true));

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
          if (!ReferenceEquals(Selected, device) || !device.WantConnected) return;
          device.ErrorText = "reconnecting…";
          await ReconnectAsync(device);
        });
        _nextWatchUtc = DateTime.UtcNow.AddSeconds(_reconnectBackoffSec);
        _reconnectBackoffSec = Math.Min(30, _reconnectBackoffSec * 2);
      }
    }
    catch (OperationCanceledException) { }
  }

  private async Task ReconnectAsync(DeviceEntry device)
  {
    if (!device.WantConnected || device.State == DeviceConnectionState.Connecting) return;
    await DisconnectAsync(device).ConfigureAwait(true);
    await ConnectCoreAsync(device).ConfigureAwait(true);
  }

  private async Task ThumbLoopAsync(CancellationToken cancellation)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
    try
    {
      while (await timer.WaitForNextTickAsync(cancellation).ConfigureAwait(false))
      {
        DeviceEntry[] connected = [];
        await Dispatcher.UIThread.InvokeAsync(() =>
          connected = Devices.Where(d => d.Session?.Media is not null).ToArray());

        foreach (var device in connected)
        {
          if (cancellation.IsCancellationRequested) return;
          if (device.Session?.Media is not { } media) continue;

          ImageResult? image = null;
          try { image = await media.GetImage().ConfigureAwait(false); }
          catch (Exception ex) { _logger.Warning($"thumbnail {device.AddressText}: {ex.Message}"); }

          if (image?.Data is not { Length: > 0 }) continue;
          if (image.MimeType is { Length: > 0 } mime && !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            continue;

          Bitmap? bitmap = null;
          try
          {
            using var stream = new MemoryStream(image.Data, writable: false);
            bitmap = new Bitmap(stream);
          }
          catch (Exception ex)
          {
            _logger.Warning($"thumbnail decode {device.AddressText}: {ex.Message}");
            continue;
          }

          var captured = bitmap;
          await Dispatcher.UIThread.InvokeAsync(() =>
          {
            if (device.Session is null) captured.Dispose();
            else device.SetThumbnail(captured);
          });
        }
      }
    }
    catch (OperationCanceledException) { }
  }

  private static bool LooksLikeAddress(string name) => name.Contains(':') && name.Any(char.IsDigit);
}
