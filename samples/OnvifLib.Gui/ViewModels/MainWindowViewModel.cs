using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// The shell: the connection bar, the shared session, and the tabs.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
  private readonly UiLogger _logger = new();
  private readonly DeferredDialogService _dialogs = new();

  public MainWindowViewModel()
  {
    Runner = new OperationRunner(_logger);

    // Restored before the tabs are built so the connection bar is already filled in when the
    // window first appears.
    var settings = AppSettings.Load();
    _ip = settings.Ip;
    _port = settings.Port;
    _user = settings.User;
    _timeoutSeconds = settings.TimeoutSeconds;
    _captureSoap = settings.CaptureSoap;
    _rememberPassword = settings.RememberPassword;
    _password = settings.RememberPassword ? settings.Password : "";

    Discovery = new DiscoveryViewModel(Runner, _logger) { Shell = this };
    Device = new DeviceViewModel(Runner, _logger, _dialogs);
    Media = new MediaViewModel(Runner, _logger);
    Ptz = new PtzViewModel(Runner, _logger);
    Imaging = new ImagingViewModel(Runner, _logger);
    Events = new EventsViewModel(Runner, _logger);
    Analytics = new AnalyticsViewModel(Runner, _logger, _dialogs);
    Recording = new RecordingViewModel(Runner, _logger, _dialogs) { Media = Media };
    DeviceIo = new DeviceIoViewModel(Runner, _logger, _dialogs);
    Log = new LogViewModel(Runner, _logger);

    Tabs = [Discovery, Device, Media, Ptz, Imaging, Events, Analytics, Recording, DeviceIo, Log];

    // PTZ and imaging are both addressed through the media profile, so they follow the one
    // selection rather than each keeping its own.
    Media.ProfileSelected += profile =>
    {
      Ptz.SetProfile(profile);
      Imaging.SetProfile(profile);
    };

    // The analytics configuration tokens come from the media service and have no other source.
    Media.AnalyticsConfigsLoaded += configs => Analytics.SetConfigurations(configs);

    Discovery.UseRequested += (ip, port, xaddr) =>
    {
      Ip = ip;
      Port = port;
      Xaddr = xaddr;
      Runner.Report($"Connection bar set to {ip}:{port}{(xaddr is null ? "" : $" ({xaddr})")}");
    };
  }

  public OperationRunner Runner { get; }

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

  // ── connection bar ─────────────────────────────────────────────────────────────

  [ObservableProperty] private string _ip = "192.168.1.10";
  [ObservableProperty] private int _port = 80;
  [ObservableProperty] private string _user = "admin";
  [ObservableProperty] private string _password = "";
  [ObservableProperty] private double _timeoutSeconds = 15;
  [ObservableProperty] private bool _revealPassword;

  /// <summary>
  /// Off by default, and the label says the password is stored in clear text. A sample that has
  /// to run unchanged on Windows and Linux has no key store to hand, so the honest options are
  /// "do not store it" or "store it and say so".
  /// </summary>
  [ObservableProperty] private bool _rememberPassword;

  /// <summary>
  /// Passing a logger to Camera.Create is what switches on the SOAP request/response dump inside
  /// CustomMessageInspector, and it cannot be changed afterwards — hence "requires a reconnect".
  /// The Log tab's level filter still decides whether those entries are kept.
  /// </summary>
  [ObservableProperty] private bool _captureSoap = true;

  /// <summary>Set from the Discovery tab so a camera on a non-standard path still connects.</summary>
  [ObservableProperty] private string? _xaddr;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(IsConnected))]
  [NotifyPropertyChangedFor(nameof(ConnectionText))]
  private CameraSession? _session;

  public bool IsConnected => Session is not null;

  public string ConnectionText => Session is { } session
    ? $"connected — {session.Url}"
    : "not connected";

  /// <summary>
  /// Writes the connection bar back to disk. Called on a successful connect and at shutdown, not
  /// on every keystroke: the file is a convenience, not a document.
  /// </summary>
  public void SaveSettings()
  {
    var failure = new AppSettings
    {
      Ip = Ip,
      Port = Port,
      User = User,
      TimeoutSeconds = TimeoutSeconds,
      CaptureSoap = CaptureSoap,
      RememberPassword = RememberPassword,
      Password = Password,
    }.Save();

    if (failure is not null) _logger.Warning($"could not save {AppSettings.Path}: {failure}");
  }

  [RelayCommand]
  private async Task ConnectAsync()
  {
    await DisconnectAsync();

    var (ok, session) = await Runner.RunAsync($"Connect to {Ip}:{Port}", () => CameraSession.ConnectAsync(
      Ip, Port, User, Password, TimeoutSeconds, CaptureSoap ? _logger : null, Xaddr));

    if (!ok || session is null) return;

    Session = session;
    foreach (var tab in Tabs) tab.SetSession(session);

    // Only after a connection that worked — there is no point remembering a wrong address.
    SaveSettings();
  }

  [RelayCommand]
  private async Task DisconnectAsync()
  {
    if (Session is not { } session) return;

    // Order matters: the tabs must release their bitmaps, stop their polling loops and cancel
    // their event subscription before the services they were using are disposed.
    foreach (var tab in Tabs) await tab.ShutdownAsync();
    foreach (var tab in Tabs) tab.SetSession(null);

    Session = null;
    session.Dispose();
    Runner.Report("Disconnected");
  }

  /// <summary>Called from the application's shutdown handler before the process exits.</summary>
  public async Task ShutdownAsync()
  {
    SaveSettings();
    foreach (var tab in Tabs) await tab.ShutdownAsync();
    Session?.Dispose();
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
