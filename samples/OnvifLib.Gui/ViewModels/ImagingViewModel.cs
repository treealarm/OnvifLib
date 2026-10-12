using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// Imaging. Keyed by video source token, which the media profile publishes — imaging is a
/// property of the sensor, not of the profile.
/// </summary>
public sealed partial class ImagingViewModel(OperationRunner runner, UiLogger logger)
  : TabViewModelBase("Imaging", runner, logger)
{
  /// <summary>The shared live view, shown on the focus page so focusing is not done blind.</summary>
  public VideoPlayerViewModel? Video { get; set; }

  [ObservableProperty] private string _videoSourceToken = "";
  [ObservableProperty] private string _sourceLabel = "no stream selected";

  // Only the ticked rows are sent. The library treats null as "leave unchanged" and does its own
  // read-modify-write, so sending everything would overwrite settings that were never touched.
  // Moving a slider or picking a mode ticks its row.
  public SliderSetting Brightness { get; } = new("Brightness");
  public SliderSetting Contrast { get; } = new("Contrast");
  public SliderSetting Saturation { get; } = new("Colour saturation");
  public SliderSetting Sharpness { get; } = new("Sharpness");
  public ObservableCollection<SliderSetting> Picture { get; } = [];
  public ModeSetting IrCutFilter { get; } = new("Day/night (IR cut filter)");

  public ModeSetting ExposureMode { get; } = new("Mode");
  public ModeSetting ExposurePriority { get; } = new("Priority");
  public SliderSetting ExposureTime { get; } = new("Exposure time");
  public SliderSetting MinExposureTime { get; } = new("Min exposure time");
  public SliderSetting MaxExposureTime { get; } = new("Max exposure time");
  public SliderSetting Gain { get; } = new("Gain");
  public SliderSetting MinGain { get; } = new("Min gain");
  public SliderSetting MaxGain { get; } = new("Max gain");
  public SliderSetting Iris { get; } = new("Iris");
  public SliderSetting MinIris { get; } = new("Min iris");
  public SliderSetting MaxIris { get; } = new("Max iris");
  public ObservableCollection<SliderSetting> Exposure { get; } = [];

  public ModeSetting WhiteBalanceMode { get; } = new("White balance");
  public SliderSetting CrGain { get; } = new("Cr gain");
  public SliderSetting CbGain { get; } = new("Cb gain");
  public ObservableCollection<SliderSetting> WhiteBalance { get; } = [];

  public ModeSetting WdrMode { get; } = new("Wide dynamic range");
  public SliderSetting WdrLevel { get; } = new("WDR level");
  public ModeSetting BlcMode { get; } = new("Backlight compensation");
  public SliderSetting BlcLevel { get; } = new("BLC level");

  public ModeSetting FocusMode { get; } = new("Auto focus");
  public SliderSetting FocusDefaultSpeed { get; } = new("Default speed");
  public SliderSetting FocusNearLimit { get; } = new("Near limit");
  public SliderSetting FocusFarLimit { get; } = new("Far limit");
  public ObservableCollection<SliderSetting> FocusConfiguration { get; } = [];

  [ObservableProperty] private string _statusText = "load the options and the settings to begin";

  private IEnumerable<SliderSetting> AllSliders =>
    Picture.Concat(Exposure).Concat(WhiteBalance).Concat(FocusConfiguration).Append(WdrLevel).Append(BlcLevel);

  private IEnumerable<ModeSetting> AllModes =>
    [IrCutFilter, ExposureMode, ExposurePriority, WhiteBalanceMode, WdrMode, BlcMode, FocusMode];

  // The collections are filled here rather than in initialisers because a primary-constructor
  // class cannot order field initialisers that refer to each other.
  private bool _built;
  private void Build()
  {
    if (_built) return;
    _built = true;
    foreach (var s in new[] { Brightness, Contrast, Saturation, Sharpness }) Picture.Add(s);
    foreach (var s in new[] { ExposureTime, Gain, Iris, MinExposureTime, MaxExposureTime, MinGain, MaxGain, MinIris, MaxIris }) Exposure.Add(s);
    WhiteBalance.Add(CrGain);
    WhiteBalance.Add(CbGain);
    foreach (var s in new[] { FocusDefaultSpeed, FocusNearLimit, FocusFarLimit }) FocusConfiguration.Add(s);
  }

  protected override string? DescribeUnavailability(CameraSession session) => session.Imaging is null
    ? session.Advertises(ImagingService2.GetSupportedWsdls())
      ? "The camera advertises imaging, but the library could not create a client for it — see the activity below."
      : "This camera does not advertise an imaging service."
    : null;

  protected override void OnConnected(CameraSession session)
  {
    Build();
    // Do not rely on Media.ProfileSelected alone: BindSelection calls SetSession on Imaging
    // *after* Media has already pushed the new profile, and OnCleared would wipe that token.
    if (string.IsNullOrEmpty(VideoSourceToken))
      SetProfile(session.Media?.GetProfiles().FirstOrDefault());
  }

  protected override void OnCleared()
  {
    VideoSourceToken = "";
    SourceLabel = "no stream selected";
    foreach (var s in AllSliders) s.Reset();
    foreach (var m in AllModes) m.Reset();
    ClearFocusControl();
    StatusText = "open this tab to read the current picture settings";
  }

  /// <summary>Follows the Media tab's stream selection. The ONVIF token stays off the UI.</summary>
  public void SetProfile(OnvifProfileInfo? profile)
  {
    Build();
    VideoSourceToken = profile?.VideoSourceToken ?? "";
    SourceLabel = profile is { } p
      ? string.IsNullOrWhiteSpace(p.Name) ? $"{p.Width}×{p.Height} {p.Encoding}" : p.Name
      : "no stream selected";
  }

  public override async Task ActivateAsync()
  {
    if (!IsAvailable || Session?.Imaging is null) return;
    Build();
    if (string.IsNullOrEmpty(VideoSourceToken))
      SetProfile(Session.Media?.GetProfiles().FirstOrDefault());
    await LoadOptionsAsync();
    await LoadSettingsAsync();
    await LoadFocusControlAsync();
  }

  [RelayCommand]
  private async Task LoadOptionsAsync()
  {
    if (Session?.Imaging is not { } imaging) return;
    if (VideoSourceToken is not { Length: > 0 } source)
    {
      StatusText = "this stream has no sensor attached — pick another on the Media tab";
      return;
    }

    var (ok, o) = await Runner.RunAsync("GetImagingOptions", () => imaging.GetOptionsAsync(source));
    if (!ok) return;
    if (o is null) { StatusText = "the camera reported no imaging options for this video source"; return; }

    Brightness.SetRange(o.Brightness);
    Contrast.SetRange(o.Contrast);
    Saturation.SetRange(o.ColorSaturation);
    Sharpness.SetRange(o.Sharpness);
    IrCutFilter.SetModes(o.IrCutFilterModes);

    ExposureMode.SetModes(o.Exposure?.Modes);
    ExposurePriority.SetModes(o.Exposure?.Priorities);
    ExposureTime.SetRange(o.Exposure?.ExposureTime);
    MinExposureTime.SetRange(o.Exposure?.MinExposureTime);
    MaxExposureTime.SetRange(o.Exposure?.MaxExposureTime);
    Gain.SetRange(o.Exposure?.Gain);
    MinGain.SetRange(o.Exposure?.MinGain);
    MaxGain.SetRange(o.Exposure?.MaxGain);
    Iris.SetRange(o.Exposure?.Iris);
    MinIris.SetRange(o.Exposure?.MinIris);
    MaxIris.SetRange(o.Exposure?.MaxIris);

    WhiteBalanceMode.SetModes(o.WhiteBalance?.Modes);
    CrGain.SetRange(o.WhiteBalance?.YrGain);
    CbGain.SetRange(o.WhiteBalance?.YbGain);

    WdrMode.SetModes(o.WideDynamicRange?.Modes);
    WdrLevel.SetRange(o.WideDynamicRange?.Level);
    BlcMode.SetModes(o.BacklightCompensation?.Modes);
    BlcLevel.SetRange(o.BacklightCompensation?.Level);

    FocusMode.SetModes(o.Focus?.AutoFocusModes);
    FocusDefaultSpeed.SetRange(o.Focus?.DefaultSpeed);
    FocusNearLimit.SetRange(o.Focus?.NearLimit);
    FocusFarLimit.SetRange(o.Focus?.FarLimit);

    StatusText = "ranges loaded";
  }

  [RelayCommand]
  private async Task LoadSettingsAsync()
  {
    if (Session?.Imaging is not { } imaging) return;
    if (VideoSourceToken is not { Length: > 0 } source)
    {
      StatusText = "this stream has no sensor attached — pick another on the Media tab";
      Runner.Report(StatusText, isError: true);
      return;
    }

    var (ok, s) = await Runner.RunAsync("GetImagingSettings", () => imaging.GetImagingSettingsAsync(source));
    if (!ok) return;
    if (s is null) { StatusText = "the camera reported no imaging settings for this video source"; return; }

    Brightness.Load(s.Brightness);
    Contrast.Load(s.Contrast);
    Saturation.Load(s.ColorSaturation);
    Sharpness.Load(s.Sharpness);
    IrCutFilter.Load(s.IrCutFilter);

    ExposureMode.Load(s.Exposure?.Mode);
    ExposurePriority.Load(s.Exposure?.Priority);
    ExposureTime.Load(s.Exposure?.ExposureTime);
    MinExposureTime.Load(s.Exposure?.MinExposureTime);
    MaxExposureTime.Load(s.Exposure?.MaxExposureTime);
    Gain.Load(s.Exposure?.Gain);
    MinGain.Load(s.Exposure?.MinGain);
    MaxGain.Load(s.Exposure?.MaxGain);
    Iris.Load(s.Exposure?.Iris);
    MinIris.Load(s.Exposure?.MinIris);
    MaxIris.Load(s.Exposure?.MaxIris);

    WhiteBalanceMode.Load(s.WhiteBalance?.Mode);
    CrGain.Load(s.WhiteBalance?.CrGain);
    CbGain.Load(s.WhiteBalance?.CbGain);

    WdrMode.Load(s.WideDynamicRange?.Mode);
    WdrLevel.Load(s.WideDynamicRange?.Level);
    BlcMode.Load(s.BacklightCompensation?.Mode);
    BlcLevel.Load(s.BacklightCompensation?.Level);

    FocusMode.Load(s.Focus?.AutoFocusMode);
    FocusDefaultSpeed.Load(s.Focus?.DefaultSpeed);
    FocusNearLimit.Load(s.Focus?.NearLimit);
    FocusFarLimit.Load(s.Focus?.FarLimit);

    StatusText = "settings read from the camera";
  }

  /// <summary>Builds the update from the ticked rows only. Null groups are left as they are.</summary>
  public OnvifImagingSettings BuildRequest()
  {
    OnvifExposure? exposure = new(
      ExposureMode.Requested, ExposurePriority.Requested,
      MinExposureTime.Requested, MaxExposureTime.Requested,
      MinGain.Requested, MaxGain.Requested,
      MinIris.Requested, MaxIris.Requested,
      ExposureTime.Requested, Gain.Requested, Iris.Requested);
    OnvifWhiteBalance? whiteBalance = new(WhiteBalanceMode.Requested, CrGain.Requested, CbGain.Requested);
    OnvifFocusSettings? focus = new(FocusMode.Requested, FocusDefaultSpeed.Requested, FocusNearLimit.Requested, FocusFarLimit.Requested);
    OnvifModeLevel? wdr = new(WdrMode.Requested, WdrLevel.Requested);
    OnvifModeLevel? blc = new(BlcMode.Requested, BlcLevel.Requested);

    return new OnvifImagingSettings(
      Brightness.Requested,
      Contrast.Requested,
      Saturation.Requested,
      Sharpness.Requested,
      IrCutFilter: IrCutFilter.Requested,
      Exposure: exposure == new OnvifExposure() ? null : exposure,
      WhiteBalance: whiteBalance == new OnvifWhiteBalance() ? null : whiteBalance,
      Focus: focus == new OnvifFocusSettings() ? null : focus,
      WideDynamicRange: wdr == new OnvifModeLevel() ? null : wdr,
      BacklightCompensation: blc == new OnvifModeLevel() ? null : blc);
  }

  [RelayCommand]
  private async Task ApplyAsync()
  {
    if (Session?.Imaging is not { } imaging) return;
    if (VideoSourceToken is not { Length: > 0 } source) return;

    var settings = BuildRequest();
    if (settings == new OnvifImagingSettings(null, null, null, null))
    {
      Runner.Report("Nothing is ticked to change", isError: true);
      return;
    }

    if (await Runner.RunAsync("SetImagingSettings", () => imaging.SetImagingSettingsAsync(source, settings)))
      await LoadSettingsAsync();
  }
}
