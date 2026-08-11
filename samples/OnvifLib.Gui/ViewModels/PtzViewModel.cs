using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

/// <summary>PTZ: continuous, relative and absolute movement, plus presets.</summary>
public sealed partial class PtzViewModel(OperationRunner runner, UiLogger logger)
  : TabViewModelBase("PTZ", runner, logger)
{
  // Guards the press-and-hold path, which deliberately does not go through a RelayCommand: the
  // generated command blocks re-entry while it runs, which would swallow the release.
  private bool _moving;

  public ObservableCollection<PtzPresetDto> Presets { get; } = [];

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(ProfileText))]
  [NotifyPropertyChangedFor(nameof(HasProfile))]
  private OnvifProfileInfo? _profile;

  [ObservableProperty] private PtzPresetDto? _selectedPreset;
  [ObservableProperty] private string _newPresetName = "";

  /// <summary>Every PTZ call is addressed by media profile, so nothing works without one.</summary>
  public bool HasProfile => Profile is not null;

  /// <summary>Why the controls are not doing anything, when they are not.</summary>
  [ObservableProperty] private string _hint = "";

  [ObservableProperty] private bool _canAbsolute;
  [ObservableProperty] private bool _canRelative;
  [ObservableProperty] private bool _canContinuous;

  [ObservableProperty] private double _speed = 0.5;
  [ObservableProperty] private string _continuousTimeout = "PT3S";

  [ObservableProperty] private double _relativePan;
  [ObservableProperty] private double _relativeTilt;
  [ObservableProperty] private double _relativeZoom;

  [ObservableProperty] private double _absolutePan;
  [ObservableProperty] private double _absoluteTilt;
  [ObservableProperty] private double _absoluteZoom;

  public string ProfileText => Profile is { } p ? $"{p.Token} ({p.Name})" : "no profile selected — pick one on the Media tab";

  protected override string? DescribeUnavailability(CameraSession session) => session.Ptz is null
    ? session.Advertises(PtzService2.GetSupportedWsdls())
      ? "The camera advertises PTZ, but the library could not create a client for it — check the Log tab."
      : "This camera does not advertise a PTZ service."
    : null;

  protected override void OnConnected(CameraSession session)
  {
    // The Media tab pushes its selection through SetProfile, and it is populated before this tab
    // because of the order the shell notifies them. This fallback covers the case where the media
    // service did not resolve at all, or the user never opened that tab: without a profile every
    // PTZ call below is a no-op, which used to look exactly like a camera that would not move.
    Profile ??= session.Media?.GetProfiles().FirstOrDefault();

    Hint = Profile is null
      ? session.Media is null
        ? "No media service resolved, so there is no profile to address PTZ against. The Device tab's service table shows whether the camera advertises one; a rejected credential is the usual cause."
        : "The camera reported no media profiles, so there is nothing to address PTZ against."
      : "";

    // Read the capabilities straight away rather than waiting for a click: the checkboxes are the
    // answer to "why does it not move", and they are worthless while they are blank.
    _ = LoadCapabilitiesAsync();
  }

  protected override void OnCleared()
  {
    Presets.Clear();
    Profile = null;
    CanAbsolute = CanRelative = CanContinuous = false;
    Hint = "";
  }

  /// <summary>Kept in step with the Media tab, since PTZ is addressed by media profile.</summary>
  public void SetProfile(OnvifProfileInfo? profile)
  {
    Profile = profile;
    if (profile is not null) Hint = "";
  }

  /// <summary>
  /// Guards every PTZ call. Reporting the reason matters more than it looks: a silent return
  /// here is indistinguishable from a camera that ignores the command.
  /// </summary>
  private bool TryGetTarget(out PtzService2 ptz, out string profileToken)
  {
    ptz = null!;
    profileToken = "";

    if (Session?.Ptz is not { } service)
    {
      Runner.Report("No PTZ service on this session", isError: true);
      return false;
    }

    if (Profile is not { } profile)
    {
      Runner.Report("PTZ needs a media profile — select one on the Media tab", isError: true);
      return false;
    }

    ptz = service;
    profileToken = profile.Token;
    return true;
  }

  [RelayCommand]
  private async Task LoadCapabilitiesAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken)) return;

    var (ok, caps) = await Runner.RunAsync("GetPtzCapabilities", () => ptz.GetCapabilitiesAsync(profileToken));
    if (!ok || caps is null) return;

    // Asked immediately after connecting, at least one camera answers GetConfigurations with an
    // empty list and only fills it in on a second call. The library cannot tell that apart from
    // "no PTZ" — both come back as three falses, and it swallows a thrown exception into the same
    // answer — so a lone all-false result is retried before it is believed.
    if (caps is { AbsoluteMove: false, RelativeMove: false, ContinuousMove: false })
    {
      var (retried, second) = await Runner.RunAsync("GetPtzCapabilities (retry)",
        () => ptz.GetCapabilitiesAsync(profileToken));
      if (retried && second is not null) caps = second;
    }

    CanAbsolute = caps.AbsoluteMove;
    CanRelative = caps.RelativeMove;
    CanContinuous = caps.ContinuousMove;

    // Read from GetConfigurations, where an empty DefaultXxxSpace means the camera never
    // configured that move mode. Still all false after the retry is a real answer worth stating.
    Hint = caps is { AbsoluteMove: false, RelativeMove: false, ContinuousMove: false }
      ? "The camera reports no configured move mode (no absolute, relative or continuous space), or the request failed — the library returns the same answer for both. The Log tab has the exchange."
      : "";
  }

  [RelayCommand]
  private async Task LoadPresetsAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken)) return;
    var (ok, presets) = await Runner.RunAsync("GetPresets", () => ptz.GetPresetsAsync(profileToken));
    if (!ok || presets is null) return;

    Presets.Clear();
    foreach (var preset in presets) Presets.Add(preset);
  }

  // ── press and hold ─────────────────────────────────────────────────────────────

  /// <summary>
  /// Called on pointer press. Not a command: [RelayCommand] blocks concurrent execution, and a
  /// hold that outlives the press would then have its release ignored, leaving the head moving.
  /// </summary>
  public async Task StartMoveAsync(float pan, float tilt, float zoom)
  {
    if (_moving) return;
    if (!TryGetTarget(out var ptz, out var profileToken)) return;
    _moving = true;

    var speed = (float)Speed;
    var timeout = string.IsNullOrWhiteSpace(ContinuousTimeout) ? "PT3S" : ContinuousTimeout;

    // The timeout is the safety net: if the release never reaches the camera, it stops itself.
    await Runner.RunAsync("ContinuousMove",
      () => ptz.ContinuousMoveAsync(profileToken, pan * speed, tilt * speed, zoom * speed, timeout));
  }

  /// <summary>Called on pointer release, and on pointer-capture loss.</summary>
  public async Task StopMoveAsync()
  {
    if (!_moving) return;
    _moving = false;

    // No TryGetTarget here: a failed stop must not pop an error over a move that never started.
    if (Session?.Ptz is not { } ptz || Profile is not { } profile) return;
    await Runner.RunAsync("Stop", () => ptz.StopAsync(profile.Token));
  }

  [RelayCommand]
  private async Task StopAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken)) return;
    await Runner.RunAsync("Stop", () => ptz.StopAsync(profileToken));
  }

  [RelayCommand]
  private async Task RelativeMoveAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken)) return;
    await Runner.RunAsync("RelativeMove", () => ptz.RelativeMoveAsync(
      profileToken, (float)RelativePan, (float)RelativeTilt, (float)RelativeZoom, (float)Speed, (float)Speed));
  }

  [RelayCommand]
  private async Task AbsoluteMoveAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken)) return;
    await Runner.RunAsync("AbsoluteMove", () => ptz.AbsoluteMoveAsync(
      profileToken, (float)AbsolutePan, (float)AbsoluteTilt, (float)AbsoluteZoom, (float)Speed, (float)Speed));
  }

  [RelayCommand]
  private async Task GotoPresetAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken) || SelectedPreset is not { } preset) return;
    await Runner.RunAsync($"GotoPreset [{preset.Name}]", () => ptz.GotoPresetAsync(profileToken, preset.Token));
  }

  [RelayCommand]
  private async Task SavePresetAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken)) return;

    var name = NewPresetName is { Length: > 0 } n ? n : $"preset-{DateTime.Now:HHmmss}";
    // An empty preset token asks the camera to create a new one; passing an existing token
    // overwrites that preset with the current position instead.
    var token = SelectedPreset?.Token ?? string.Empty;

    var (ok, created) = await Runner.RunAsync($"SetPreset '{name}'",
      () => ptz.SetPresetAsync(profileToken, name, token));
    if (!ok) return;

    Runner.Report($"SetPreset — the camera assigned token {created}");
    await LoadPresetsAsync();
  }

  [RelayCommand]
  private async Task RemovePresetAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken) || SelectedPreset is not { } preset) return;
    if (await Runner.RunAsync($"RemovePreset [{preset.Name}]", () => ptz.RemovePresetAsync(profileToken, preset.Token)))
      await LoadPresetsAsync();
  }
}
