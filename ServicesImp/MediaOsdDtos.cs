namespace OnvifLib
{
  /// <summary>A video source configuration: which sensor, and which part of it, a profile uses.</summary>
  /// <param name="Token">The configuration token — the one OSDs are attached to.</param>
  /// <param name="SourceToken">The physical source behind it — the one Imaging is addressed by.</param>
  /// <param name="UseCount">How many profiles share this configuration.</param>
  /// <param name="Name">Display name.</param>
  /// <param name="X">Left edge of the area used, in sensor pixels.</param>
  /// <param name="Y">Top edge.</param>
  /// <param name="Width">Width of the area used.</param>
  /// <param name="Height">Height of the area used.</param>
  public record OnvifVideoSourceConfig(string Token, string Name, string SourceToken, int X, int Y, int Width, int Height, int UseCount);

  /// <summary>A physical video source.</summary>
  /// <remarks>
  /// Framerate and size are zero when they are not known: Media2 has no GetVideoSources, so on
  /// a Media2 camera the sources are derived from the configurations, which name them but do not
  /// describe them.
  /// </remarks>
  public record OnvifVideoSource(string Token, float Framerate, int Width, int Height);

  /// <summary>An OSD colour in the camera's colour space (usually YCbCr), as ONVIF carries it.</summary>
  /// <param name="Transparent">0 = opaque; the scale's maximum is camera-defined. Null = not given.</param>
  /// <param name="X">First colour component (Y in YCbCr).</param>
  /// <param name="Y">Second colour component (Cb in YCbCr).</param>
  /// <param name="Z">Third colour component (Cr in YCbCr).</param>
  /// <param name="Colorspace">The colour space URI, or null for the camera default.</param>
  public record OnvifOsdColor(float X, float Y, float Z, string? Colorspace = null, int? Transparent = null);

  /// <summary>One on-screen display element — a text or an image burnt into the video.</summary>
  /// <remarks>
  /// For an update, null members mean "leave as is": the library reads the OSD back and changes
  /// only what is given, because cameras commonly reject a Set that omits the font colour or the
  /// position they hold.
  /// </remarks>
  /// <param name="Type">"Text", "Image" or "Extended".</param>
  /// <param name="PositionType">"UpperLeft", "UpperRight", "LowerLeft", "LowerRight" or "Custom" — see OnvifOsdOptions.PositionOptions.</param>
  /// <param name="X">Only for Custom: -1 (left) … 1 (right).</param>
  /// <param name="Y">Only for Custom: -1 (bottom) … 1 (top).</param>
  /// <param name="TextType">"Plain", "Date", "Time" or "DateAndTime". Case matters to cameras.</param>
  /// <param name="Token">The OSD token; ignored on create.</param>
  /// <param name="VideoSourceConfigToken">The video source configuration the OSD belongs to.</param>
  /// <param name="PlainText">The text, for TextType Plain.</param>
  /// <param name="DateFormat">Date format, from OnvifOsdOptions.DateFormats.</param>
  /// <param name="TimeFormat">Time format, from OnvifOsdOptions.TimeFormats.</param>
  /// <param name="FontSize">Font size, within OnvifOsdOptions.FontSizeMin … FontSizeMax.</param>
  /// <param name="FontColor">Text colour, or null for the camera default.</param>
  /// <param name="BackgroundColor">Background colour, or null for none / the default.</param>
  /// <param name="ImagePath">Image location on the camera, for Image OSDs.</param>
  public record OnvifOsd(
    string Token,
    string VideoSourceConfigToken,
    string Type,
    string? PositionType = null,
    float? X = null,
    float? Y = null,
    string? TextType = null,
    string? PlainText = null,
    string? DateFormat = null,
    string? TimeFormat = null,
    int? FontSize = null,
    OnvifOsdColor? FontColor = null,
    OnvifOsdColor? BackgroundColor = null,
    string? ImagePath = null);

  /// <summary>What OSDs a video source configuration accepts.</summary>
  /// <param name="MaxTotal">How many OSDs the configuration holds in all; the per-kind limits are null when not stated.</param>
  /// <param name="MaxImage">How many image OSDs it holds; null when not stated.</param>
  /// <param name="MaxPlainText">How many plain-text OSDs.</param>
  /// <param name="MaxDate">How many date OSDs.</param>
  /// <param name="MaxTime">How many time OSDs.</param>
  /// <param name="MaxDateAndTime">How many date-and-time OSDs.</param>
  /// <param name="Types">OSD types accepted: Text, Image, Extended.</param>
  /// <param name="PositionOptions">Position types accepted, e.g. UpperLeft or Custom.</param>
  /// <param name="TextTypes">Text types accepted: Plain, Date, Time, DateAndTime.</param>
  /// <param name="FontSizeMin">Smallest font size, or null when not stated.</param>
  /// <param name="FontSizeMax">Largest font size.</param>
  /// <param name="DateFormats">Date formats accepted.</param>
  /// <param name="TimeFormats">Time formats accepted.</param>
  /// <param name="ImagePaths">Images the camera already holds.</param>
  /// <param name="ImageFormats">Image formats it accepts.</param>
  public record OnvifOsdOptions(
    int MaxTotal,
    int? MaxImage,
    int? MaxPlainText,
    int? MaxDate,
    int? MaxTime,
    int? MaxDateAndTime,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> PositionOptions,
    IReadOnlyList<string> TextTypes,
    int? FontSizeMin,
    int? FontSizeMax,
    IReadOnlyList<string> DateFormats,
    IReadOnlyList<string> TimeFormats,
    IReadOnlyList<string> ImagePaths,
    IReadOnlyList<string> ImageFormats);
}
