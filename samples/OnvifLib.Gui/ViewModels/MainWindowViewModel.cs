using CommunityToolkit.Mvvm.ComponentModel;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// The shell: device list, the selected session, the shared live player, and the service tabs.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
  private readonly UiLogger _logger = new();
  private readonly DeferredDialogService _dialogs = new();
  private int _bindGeneration;
  private bool _restoring;
  private bool _binding;

  public MainWindowViewModel()
  {
    Runner = new OperationRunner(_logger);

    var settings = AppSettings.Load();
    _timeoutSeconds = settings.TimeoutSeconds;
    _captureSoap = settings.CaptureSoap;
    _autoPlayLive = settings.AutoPlayLive;

    Video = new VideoPlayerViewModel(Runner)
    {
      CustomFfmpegPath = settings.FfmpegPath,
      FrameWidth = settings.VideoWidth > 0 ? settings.VideoWidth : 640,
      FrameHeight = settings.VideoHeight > 0 ? settings.VideoHeight : 360,
      FrameRate = settings.VideoFps > 0 ? settings.VideoFps : 12,
    };
    Video.RefreshFfmpegStatus();

    Devices = new DeviceListViewModel(Runner, _logger, ConnectDeviceAsync);

    Discovery = new DiscoveryViewModel(Runner, _logger) { Shell = this };
    Device = new DeviceViewModel(Runner, _logger, _dialogs);
    Media = new MediaViewModel(Runner, _logger) { Video = Video };
    Ptz = new PtzViewModel(Runner, _logger) { Video = Video };
    Imaging = new ImagingViewModel(Runner, _logger);
    Events = new EventsViewModel(Runner, _logger);
    Analytics = new AnalyticsViewModel(Runner, _logger, _dialogs);
    Replay = new VideoPlayerViewModel(Runner)
    {
      CustomFfmpegPath = settings.FfmpegPath,
      FrameWidth = settings.VideoWidth > 0 ? settings.VideoWidth : 640,
      FrameHeight = settings.VideoHeight > 0 ? settings.VideoHeight : 360,
      FrameRate = settings.VideoFps > 0 ? settings.VideoFps : 12,
      ShowStreamPicker = false,
      EmptyHint = "no archive playing",
      Status = "select a recording and press Play archive",
    };
    Replay.RefreshFfmpegStatus();
    Recording = new RecordingViewModel(Runner, _logger, _dialogs) { Media = Media, Video = Replay };
    DeviceIo = new DeviceIoViewModel(Runner, _logger, _dialogs);
    Log = new LogViewModel(Runner, _logger);

    Tabs = [Discovery, Device, Media, Ptz, Imaging, Events, Analytics, Recording, DeviceIo, Log];

    Media.ProfileSelected += profile =>
    {
      Ptz.SetProfile(profile);
      Imaging.SetProfile(profile);
      if (!_binding) _ = ReloadDependentTabsAsync();
    };

    Media.AnalyticsConfigsLoaded += configs => Analytics.SetConfigurations(configs);

    Discovery.UseRequested += (ip, port, xaddr) =>
    {
      Devices.AddOrUpdate(ip, port, xaddr, user: Devices.User, password: Devices.Password);
      Runner.Report($"Device list: {ip}:{port}");
    };

    Devices.SelectionCommitted += device => _ = BindSelectionAsync(device);

    _restoring = true;
    Devices.Load(settings.Devices, settings.Ip, settings.Port, settings.User, settings.Password, settings.RememberPassword);
    _restoring = false;
    OnPropertyChanged(nameof(ConnectionText));
    Devices.ConnectSelectedIfNeeded();
  }

  public OperationRunner Runner { get; }
  public DeviceListViewModel Devices { get; }
  public VideoPlayerViewModel Video { get; }
  public VideoPlayerViewModel Replay { get; }

  public DiscoveryViewModel Discovery { get; }
  public DeviceViewModel Device { get; }
  public MediaViewModel Media { get; }
  public PtzViewModel Ptz { get; }
  public ImagingViewModel Imaging { get; }
  public EventsViewModel Events { get; }
  public AnalyticsViewModel Analytics { get; }
  public RecordingViewModel Recording { get; }
  public DeviceIoViewModel DeviceIo { get; }
  public LogViewModel Log { get; }

  public IReadOnlyList<TabViewModelBase> Tabs { get; }

  /// <summary>Supplied by the window once it exists, since a dialog needs an owner.</summary>
  public void AttachDialogs(IDialogService dialogs) => _dialogs.Inner = dialogs;

  [ObservableProperty] private double _timeoutSeconds = 15;
  [ObservableProperty] private bool _autoPlayLive = true;

  /// <summary>
  /// Passing a logger to Camera.Create is what switches on the SOAP request/response dump inside
  /// CustomMessageInspector, and it cannot be changed afterwards — hence "requires a reconnect".
  /// The Log tab's level filter still decides whether those entries are kept.
  /// </summary>
  [ObservableProperty] private bool _captureSoap = true;

  /// <summary>
  /// Matches the TabControl in MainWindow.axaml. Used to load a tab's read-only data when it
  /// becomes visible, rather than firing every service on login.
  /// </summary>
  [ObservableProperty] private int _selectedTabIndex;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(IsConnected))]
  [NotifyPropertyChangedFor(nameof(ConnectionText))]
  private CameraSession? _session;

  public bool IsConnected => Session is not null;

  public string ConnectionText => Session is { } session
    ? $"selected — {session.Url}"
    : Devices.Selected is { } device
      ? $"selected — {device.AddressText} (offline)"
      : "no device selected";

  public string? User => Devices.User;
  public string? Password => Devices.Password;

  public void SaveSettings()
  {
    Devices.ApplyFieldsToSelected();
    var failure = new AppSettings
    {
      Ip = Devices.Ip,
      Port = Devices.Port,
      User = Devices.User,
      TimeoutSeconds = TimeoutSeconds,
      CaptureSoap = CaptureSoap,
      RememberPassword = Devices.RememberPassword,
      Password = Devices.Password,
      FfmpegPath = Video.CustomFfmpegPath,
      VideoWidth = Video.FrameWidth,
      VideoHeight = Video.FrameHeight,
      VideoFps = Video.FrameRate,
      AutoPlayLive = AutoPlayLive,
      Devices = Devices.Snapshot().ToList(),
    }.Save();

    if (failure is not null) _logger.Warning($"could not save {AppSettings.Path}: {failure}");
  }

  private async Task<CameraSession?> ConnectDeviceAsync(DeviceEntry device)
  {
    var (ok, session) = await Runner.RunAsync($"Connect {device.AddressText}", () => CameraSession.ConnectAsync(
      device.Ip, device.Port, device.User, device.Password, TimeoutSeconds, CaptureSoap ? _logger : null, device.Xaddr));

    if (!ok || session is null) return null;
    SaveSettings();
    return session;
  }

  private async Task BindSelectionAsync(DeviceEntry? device)
  {
    if (_restoring) return;

    var generation = ++_bindGeneration;
    var session = device?.Session;

    foreach (var tab in Tabs.Where(t => t.IsSessionScoped))
      await tab.ShutdownAsync();

    await Video.StopInternalAsync();

    if (generation != _bindGeneration) return;

    _binding = true;
    try
    {
      foreach (var tab in Tabs) tab.SetSession(session);

      // Imaging/PTZ follow the media profile. SetSession on Imaging runs OnCleared *after* Media
      // has already pushed the new profile, so the token has to be applied again here.
      var profile = Media.SelectedProfile ?? session?.Media?.GetProfiles().FirstOrDefault();
      Ptz.SetProfile(profile);
      Imaging.SetProfile(profile);
    }
    finally { _binding = false; }

    Session = session;
    OnPropertyChanged(nameof(ConnectionText));
    await ActivateVisibleTabAsync();

    if (session?.Media is { } media)
    {
      var profiles = media.GetProfiles();
      Video.SetProfiles(profiles);
      Video.ResolveUri = async token =>
      {
        var uri = await media.GetStreamUri(token);
        return session is null ? uri : RtspCredentials.Inject(uri, session.Camera.User, session.Camera.Password);
      };

      if (AutoPlayLive) await Video.PlaySelectedAsync();
    }
    else
    {
      Video.ClearProfiles();
      Video.ResolveUri = null;
    }
  }

  partial void OnSelectedTabIndexChanged(int value) => _ = ActivateVisibleTabAsync();

  private async Task ActivateVisibleTabAsync()
  {
    if (_restoring) return;
    var tab = TabAt(SelectedTabIndex);
    if (tab is null || (tab.RequiresConnection && !tab.IsAvailable)) return;
    await tab.ActivateAsync();
  }

  private async Task ReloadDependentTabsAsync()
  {
    if (SelectedTabIndex == 3) await Ptz.ActivateAsync();
    if (SelectedTabIndex == 4) await Imaging.ActivateAsync();
  }

  /// <summary>Index of the TabControl in MainWindow.axaml (Live is 0 and is not a service tab).</summary>
  private TabViewModelBase? TabAt(int index) => index switch
  {
    1 => Device,
    2 => Media,
    3 => Ptz,
    4 => Imaging,
    5 => Events,
    6 => Analytics,
    7 => Recording,
    8 => DeviceIo,
    9 => Discovery,
    10 => Log,
    _ => null,
  };

  /// <summary>Called from the application's shutdown handler before the process exits.</summary>
  public async Task ShutdownAsync()
  {
    SaveSettings();
    await Video.DisposeAsync();
    await Replay.DisposeAsync();
    foreach (var tab in Tabs) await tab.ShutdownAsync();
    await Devices.ShutdownAsync();
    Session = null;
  }

  /// <summary>
  /// Lets the view models hold a dialog service from construction, before the window that owns
  /// the dialogs exists. Confirming without one refuses rather than assuming yes: every caller
  /// is something destructive.
  /// </summary>
  private sealed class DeferredDialogService : IDialogService
  {
    public IDialogService? Inner { get; set; }

    public Task<bool> ConfirmAsync(string title, string message) =>
      Inner?.ConfirmAsync(title, message) ?? Task.FromResult(false);
  }
}
