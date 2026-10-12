using M = MediaServiceReference;

namespace OnvifLib
{
  // Video source configurations and OSD for Media2 (ver20). The body of this file is kept identical to
  // its Media1 counterpart apart from the proxy namespace and the two calls whose signatures
  // differ: the two proxies generate distinct types of the same shape.
  public partial class MediaService2
  {
    public override async Task<List<OnvifVideoSourceConfig>> GetVideoSourceConfigurationsAsync()
    {
      if (_mediaClient2 == null) return [];
      var resp = await _mediaClient2.GetVideoSourceConfigurationsAsync(new M.GetVideoSourceConfigurationsRequest());
      return (resp?.Configurations ?? []).Select(c => new OnvifVideoSourceConfig(
        c.token ?? string.Empty,
        c.Name ?? string.Empty,
        c.SourceToken ?? string.Empty,
        c.Bounds?.x ?? 0, c.Bounds?.y ?? 0, c.Bounds?.width ?? 0, c.Bounds?.height ?? 0,
        c.UseCount)).ToList();
    }

    public override async Task<List<OnvifOsd>> GetOsdsAsync(string? videoSourceConfigToken = null)
    {
      if (_mediaClient2 == null) return [];
      return (await GetOsdConfigurationsAsync(videoSourceConfigToken)).Select(ToOsd).ToList();
    }

    private async Task<M.OSDConfiguration[]> GetOsdConfigurationsAsync(string? configToken)
    {
      var token = string.IsNullOrEmpty(configToken) ? null : configToken;
      var resp = await _mediaClient2!.GetOSDsAsync(new M.GetOSDsRequest { ConfigurationToken = token });
      return resp?.OSDs ?? [];
    }

    public override async Task<OnvifOsdOptions?> GetOsdOptionsAsync(string videoSourceConfigToken)
    {
      if (_mediaClient2 == null) return null;
      var resp = await _mediaClient2.GetOSDOptionsAsync(new M.GetOSDOptionsRequest { ConfigurationToken = videoSourceConfigToken });
      var o = resp?.OSDOptions;
      if (o == null) return null;
      var max = o.MaximumNumberOfOSDs;
      return new OnvifOsdOptions(
        max?.Total ?? 0,
        max is { ImageSpecified: true } ? max.Image : null,
        max is { PlainTextSpecified: true } ? max.PlainText : null,
        max is { DateSpecified: true } ? max.Date : null,
        max is { TimeSpecified: true } ? max.Time : null,
        max is { DateAndTimeSpecified: true } ? max.DateAndTime : null,
        (o.Type ?? []).Select(t => t.ToString()).ToList(),
        (o.PositionOption ?? []).ToList(),
        (o.TextOption?.Type ?? []).ToList(),
        o.TextOption?.FontSizeRange?.Min,
        o.TextOption?.FontSizeRange?.Max,
        (o.TextOption?.DateFormat ?? []).ToList(),
        (o.TextOption?.TimeFormat ?? []).ToList(),
        (o.ImageOption?.ImagePath ?? []).ToList(),
        (o.ImageOption?.FormatsSupported ?? []).ToList());
    }

    public override async Task<string> CreateOsdAsync(OnvifOsd osd)
    {
      if (_mediaClient2 == null)
        throw new InvalidOperationException("Media client not initialized");
      var config = new M.OSDConfiguration
      {
        // The token attribute is required by the schema even on Create; the camera assigns the
        // real one and returns it.
        token = string.Empty,
        VideoSourceConfigurationToken = new M.OSDReference { Value = osd.VideoSourceConfigToken },
        Type = ParseOsdType(osd.Type),
        Position = new M.OSDPosConfiguration { Type = osd.PositionType ?? "UpperLeft" },
      };
      ApplyOsd(osd, config);
      var resp = await _mediaClient2.CreateOSDAsync(new M.CreateOSDRequest { OSD = config });
      return resp?.OSDToken ?? string.Empty;
    }

    public override async Task SetOsdAsync(OnvifOsd osd)
    {
      if (_mediaClient2 == null)
        throw new InvalidOperationException("Media client not initialized");
      var config = FindConfigOrThrow(await GetOsdConfigurationsAsync(null), osd.Token, c => c.token, "OSD");
      if (!string.IsNullOrEmpty(osd.Type)) config.Type = ParseOsdType(osd.Type);
      ApplyOsd(osd, config);
      await _mediaClient2.SetOSDAsync(new M.SetOSDRequest { OSD = config });
    }

    public override async Task DeleteOsdAsync(string osdToken)
    {
      if (_mediaClient2 == null)
        throw new InvalidOperationException("Media client not initialized");
      await _mediaClient2.DeleteOSDAsync(new M.DeleteOSDRequest { OSDToken = osdToken });
    }

    private static M.OSDType ParseOsdType(string type) =>
      Enum.TryParse<M.OSDType>(type, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
        ? parsed
        : throw new ArgumentException($"'{type}' is not an OSD type; expected Text, Image or Extended", nameof(type));

    /// <summary>Writes the non-null members of <paramref name="osd"/> onto a proxy configuration.</summary>
    private static void ApplyOsd(OnvifOsd osd, M.OSDConfiguration config)
    {
      config.Position ??= new M.OSDPosConfiguration { Type = "UpperLeft" };
      if (osd.PositionType is { } position) config.Position.Type = position;
      if (string.Equals(config.Position.Type, "Custom", StringComparison.OrdinalIgnoreCase))
      {
        config.Position.Pos ??= new M.Vector();
        if (osd.X is { } x) { config.Position.Pos.x = x; config.Position.Pos.xSpecified = true; }
        if (osd.Y is { } y) { config.Position.Pos.y = y; config.Position.Pos.ySpecified = true; }
      }
      else
      {
        // A coordinate on a corner position is meaningless and some cameras reject it outright.
        config.Position.Pos = null;
      }

      if (config.Type == M.OSDType.Text)
      {
        var text = config.TextString ??= new M.OSDTextConfiguration { Type = osd.TextType ?? "Plain" };
        if (osd.TextType is { } textType) text.Type = textType;
        if (osd.PlainText is { } plain) text.PlainText = plain;
        if (osd.DateFormat is { } date) text.DateFormat = date;
        if (osd.TimeFormat is { } time) text.TimeFormat = time;
        if (osd.FontSize is { } size) { text.FontSize = size; text.FontSizeSpecified = true; }
        if (osd.FontColor is { } font) text.FontColor = ToProxyColor(font);
        if (osd.BackgroundColor is { } background) text.BackgroundColor = ToProxyColor(background);
        config.Image = null;
      }
      else if (config.Type == M.OSDType.Image)
      {
        var image = config.Image ??= new M.OSDImgConfiguration();
        if (osd.ImagePath is { } path) image.ImgPath = path;
        config.TextString = null;
      }
    }

    private static OnvifOsd ToOsd(M.OSDConfiguration c) => new(
      c.token ?? string.Empty,
      c.VideoSourceConfigurationToken?.Value ?? string.Empty,
      c.Type.ToString(),
      c.Position?.Type,
      c.Position?.Pos is { xSpecified: true } px ? px.x : null,
      c.Position?.Pos is { ySpecified: true } py ? py.y : null,
      c.TextString?.Type,
      c.TextString?.PlainText,
      c.TextString?.DateFormat,
      c.TextString?.TimeFormat,
      c.TextString is { FontSizeSpecified: true } t ? t.FontSize : null,
      FromProxyColor(c.TextString?.FontColor),
      FromProxyColor(c.TextString?.BackgroundColor),
      c.Image?.ImgPath);

    private static OnvifOsdColor? FromProxyColor(M.OSDColor? c) => c?.Color == null
      ? null
      : new OnvifOsdColor(c.Color.X, c.Color.Y, c.Color.Z, c.Color.Colorspace, c.TransparentSpecified ? c.Transparent : null);

    private static M.OSDColor ToProxyColor(OnvifOsdColor c) => new()
    {
      Color = new M.Color { X = c.X, Y = c.Y, Z = c.Z, Colorspace = c.Colorspace },
      Transparent = c.Transparent ?? 0,
      TransparentSpecified = c.Transparent.HasValue,
    };
  }
}

namespace OnvifLib
{
  public partial class MediaService2
  {
    /// <summary>The physical sources, as far as Media2 can name them.</summary>
    /// <remarks>
    /// Media2 dropped GetVideoSources, so these are the distinct sources the configurations point
    /// at, with framerate and size left at zero — the configurations name a source but do not
    /// describe it. The bounds of a configuration are a crop, not the sensor size.
    /// </remarks>
    public override async Task<List<OnvifVideoSource>> GetVideoSourcesAsync() =>
      (await GetVideoSourceConfigurationsAsync())
        .Select(c => c.SourceToken)
        .Where(t => t.Length > 0)
        .Distinct()
        .Select(t => new OnvifVideoSource(t, 0, 0, 0))
        .ToList();
  }
}
