namespace OnvifLib
{
  // Imaging settings and options. Every member is nullable: null in a value read back means the
  // camera did not report it, and null in a value handed to SetImagingSettingsAsync means "leave
  // as is". Modes are the ONVIF enum names verbatim ("AUTO", "MANUAL", "ON", "OFF", "LowNoise",
  // "FrameRate"), compared case-insensitively on the way in.

  public record OnvifFloatRange(float Min, float Max);

  /// <summary>Exposure: the mode, the limits auto-exposure works within, and the manual values.</summary>
  /// <remarks>
  /// Switching to MANUAL without a valid ExposureTime/Gain/Iris is refused by many cameras; the
  /// library sends what it is given and leaves that judgement to the camera.
  /// </remarks>
  public record OnvifExposure(
    string? Mode = null,
    string? Priority = null,
    float? MinExposureTime = null,
    float? MaxExposureTime = null,
    float? MinGain = null,
    float? MaxGain = null,
    float? MinIris = null,
    float? MaxIris = null,
    float? ExposureTime = null,
    float? Gain = null,
    float? Iris = null);

  public record OnvifWhiteBalance(string? Mode = null, float? CrGain = null, float? CbGain = null);

  /// <summary>The focus <em>configuration</em>. Moving the lens is a separate call — see ImagingService2.MoveFocus*.</summary>
  public record OnvifFocusSettings(
    string? AutoFocusMode = null,
    float? DefaultSpeed = null,
    float? NearLimit = null,
    float? FarLimit = null);

  /// <summary>A mode with an optional strength — the shape of both WDR and backlight compensation.</summary>
  public record OnvifModeLevel(string? Mode = null, float? Level = null);

  /// <remarks>
  /// The four leading members predate the rest and stay positional, so existing
  /// <c>new OnvifImagingSettings(b, c, s, sh)</c> calls keep compiling; everything after them is
  /// optional and defaults to "leave as is".
  /// </remarks>
  public record OnvifImagingSettings(
    float? Brightness,
    float? Contrast,
    float? ColorSaturation,
    float? Sharpness,
    string? IrCutFilter = null,
    OnvifExposure? Exposure = null,
    OnvifWhiteBalance? WhiteBalance = null,
    OnvifFocusSettings? Focus = null,
    OnvifModeLevel? WideDynamicRange = null,
    OnvifModeLevel? BacklightCompensation = null);

  public record OnvifExposureOptions(
    IReadOnlyList<string> Modes,
    IReadOnlyList<string> Priorities,
    OnvifFloatRange? MinExposureTime,
    OnvifFloatRange? MaxExposureTime,
    OnvifFloatRange? MinGain,
    OnvifFloatRange? MaxGain,
    OnvifFloatRange? MinIris,
    OnvifFloatRange? MaxIris,
    OnvifFloatRange? ExposureTime,
    OnvifFloatRange? Gain,
    OnvifFloatRange? Iris);

  /// <summary>White-balance options.</summary>
  /// <remarks>
  /// The schema names the gain ranges Yr/Yb here and the values Cr/Cb in the settings. They are
  /// the same two gains; the names are kept as the schema has them so a reader can match them
  /// against a SOAP dump.
  /// </remarks>
  public record OnvifWhiteBalanceOptions(IReadOnlyList<string> Modes, OnvifFloatRange? YrGain, OnvifFloatRange? YbGain);

  public record OnvifFocusOptions(
    IReadOnlyList<string> AutoFocusModes,
    OnvifFloatRange? DefaultSpeed,
    OnvifFloatRange? NearLimit,
    OnvifFloatRange? FarLimit);

  public record OnvifModeLevelOptions(IReadOnlyList<string> Modes, OnvifFloatRange? Level);

  public record OnvifImagingOptions(
    OnvifFloatRange? Brightness,
    OnvifFloatRange? Contrast,
    OnvifFloatRange? ColorSaturation,
    OnvifFloatRange? Sharpness,
    IReadOnlyList<string>? IrCutFilterModes = null,
    OnvifExposureOptions? Exposure = null,
    OnvifWhiteBalanceOptions? WhiteBalance = null,
    OnvifFocusOptions? Focus = null,
    OnvifModeLevelOptions? WideDynamicRange = null,
    OnvifModeLevelOptions? BacklightCompensation = null);

  /// <summary>Which kinds of focus move the lens accepts, and over what ranges. Null = not supported.</summary>
  public record OnvifFocusMoveOptions(
    OnvifFloatRange? AbsolutePosition,
    OnvifFloatRange? AbsoluteSpeed,
    OnvifFloatRange? RelativeDistance,
    OnvifFloatRange? RelativeSpeed,
    OnvifFloatRange? ContinuousSpeed);

  /// <summary>Where the focus motor is. All null on a camera with no focus motor to report on.</summary>
  /// <param name="FocusMoveStatus">"IDLE", "MOVING" or "UNKNOWN".</param>
  /// <param name="FocusPosition">The lens position, in the absolute focus range.</param>
  /// <param name="FocusError">The camera's error text, or null when there is none.</param>
  public record OnvifImagingStatus(float? FocusPosition, string? FocusMoveStatus, string? FocusError);
}
