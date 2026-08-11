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
  [ObservableProperty] private string _videoSourceToken = "";

  [ObservableProperty] private double _brightness;
  [ObservableProperty] private double _contrast;
  [ObservableProperty] private double _saturation;
  [ObservableProperty] private double _sharpness;

  // Only the ticked ones are sent. The library treats null as "leave unchanged" and does its own
  // read-modify-write, so sending everything would overwrite settings that were never touched.
  [ObservableProperty] private bool _changeBrightness;
  [ObservableProperty] private bool _changeContrast;
  [ObservableProperty] private bool _changeSaturation;
  [ObservableProperty] private bool _changeSharpness;

  [ObservableProperty] private double _brightnessMin;
  [ObservableProperty] private double _brightnessMax = 100;
  [ObservableProperty] private double _contrastMin;
  [ObservableProperty] private double _contrastMax = 100;
  [ObservableProperty] private double _saturationMin;
  [ObservableProperty] private double _saturationMax = 100;
  [ObservableProperty] private double _sharpnessMin;
  [ObservableProperty] private double _sharpnessMax = 100;

  [ObservableProperty] private bool _hasBrightness;
  [ObservableProperty] private bool _hasContrast;
  [ObservableProperty] private bool _hasSaturation;
  [ObservableProperty] private bool _hasSharpness;

  [ObservableProperty] private string _statusText = "load the options and the settings to begin";

  protected override string? DescribeUnavailability(CameraSession session) => session.Imaging is null
    ? session.Advertises(ImagingService2.GetSupportedWsdls())
      ? "The camera advertises imaging, but the library could not create a client for it — check the Log tab."
      : "This camera does not advertise an imaging service."
    : null;

  protected override void OnCleared()
  {
    VideoSourceToken = "";
    HasBrightness = HasContrast = HasSaturation = HasSharpness = false;
    StatusText = "load the options and the settings to begin";
  }

  /// <summary>Follows the Media tab's selection, which is where the video source token comes from.</summary>
  public void SetProfile(OnvifProfileInfo? profile) => VideoSourceToken = profile?.VideoSourceToken ?? "";

  [RelayCommand]
  private async Task LoadOptionsAsync()
  {
    if (Session?.Imaging is not { } imaging) return;
    if (VideoSourceToken is not { Length: > 0 } source)
    {
      StatusText = "the selected profile reports no VideoSourceToken, so imaging cannot be addressed";
      return;
    }

    var (ok, options) = await Runner.RunAsync("GetImagingOptions", () => imaging.GetOptionsAsync(source));
    if (!ok) return;
    if (options is null) { StatusText = "the camera reported no imaging options for this video source"; return; }

    // A setting the camera does not report a range for gets its slider disabled rather than a
    // made-up 0..100, which would let the user send a value the camera will reject.
    Apply(options.Brightness, v => BrightnessMin = v, v => BrightnessMax = v, v => HasBrightness = v);
    Apply(options.Contrast, v => ContrastMin = v, v => ContrastMax = v, v => HasContrast = v);
    Apply(options.ColorSaturation, v => SaturationMin = v, v => SaturationMax = v, v => HasSaturation = v);
    Apply(options.Sharpness, v => SharpnessMin = v, v => SharpnessMax = v, v => HasSharpness = v);
    StatusText = "ranges loaded";

    static void Apply(OnvifFloatRange? range, Action<double> min, Action<double> max, Action<bool> has)
    {
      has(range is not null);
      if (range is null) return;
      min(range.Min);
      max(range.Max);
    }
  }

  [RelayCommand]
  private async Task LoadSettingsAsync()
  {
    if (Session?.Imaging is not { } imaging) return;
    if (VideoSourceToken is not { Length: > 0 } source) return;

    var (ok, settings) = await Runner.RunAsync("GetImagingSettings", () => imaging.GetImagingSettingsAsync(source));
    if (!ok) return;
    if (settings is null) { StatusText = "the camera reported no imaging settings for this video source"; return; }

    if (settings.Brightness is { } b) Brightness = b;
    if (settings.Contrast is { } c) Contrast = c;
    if (settings.ColorSaturation is { } s) Saturation = s;
    if (settings.Sharpness is { } sh) Sharpness = sh;

    ChangeBrightness = ChangeContrast = ChangeSaturation = ChangeSharpness = false;
    StatusText = "settings read from the camera";
  }

  [RelayCommand]
  private async Task ApplyAsync()
  {
    if (Session?.Imaging is not { } imaging) return;
    if (VideoSourceToken is not { Length: > 0 } source) return;

    if (!ChangeBrightness && !ChangeContrast && !ChangeSaturation && !ChangeSharpness)
    {
      Runner.Report("Nothing is ticked to change", isError: true);
      return;
    }

    var settings = new OnvifImagingSettings(
      ChangeBrightness ? (float)Brightness : null,
      ChangeContrast ? (float)Contrast : null,
      ChangeSaturation ? (float)Saturation : null,
      ChangeSharpness ? (float)Sharpness : null);

    if (await Runner.RunAsync("SetImagingSettings", () => imaging.SetImagingSettingsAsync(source, settings)))
      await LoadSettingsAsync();
  }
}
