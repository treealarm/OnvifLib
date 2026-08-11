namespace OnvifLib.Probe.Steps;

/// <summary>Imaging: the four picture settings the library exposes, their ranges, and a reversible change.</summary>
public static class ImagingSteps
{
  public static async Task RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    r.Section(Sections.Imaging, "imaging");

    if (ctx.Imaging is not { } imaging) { r.Skip("imaging", "service not available"); return; }

    // Imaging is keyed by video source, not by profile — the media profile is only where the
    // token is published.
    var source = ctx.Profiles.Select(p => p.VideoSourceToken).FirstOrDefault(t => !string.IsNullOrEmpty(t));
    if (source is null)
    {
      r.Skip("imaging", "no profile reports a VideoSourceToken to address imaging against");
      return;
    }
    r.Value("video source", source);

    var options = await r.StepAsync("GetOptions",
      async () => await imaging.GetOptionsAsync(source)
                  ?? throw new ProbeFailure("the camera reported no imaging options for this video source"),
      o => r.Table(["setting", "min", "max"], new List<IReadOnlyList<object?>>
      {
        new List<object?> { "brightness", o.Brightness?.Min, o.Brightness?.Max },
        new List<object?> { "contrast", o.Contrast?.Min, o.Contrast?.Max },
        new List<object?> { "saturation", o.ColorSaturation?.Min, o.ColorSaturation?.Max },
        new List<object?> { "sharpness", o.Sharpness?.Min, o.Sharpness?.Max },
      }));

    var settings = await r.StepAsync("GetImagingSettings",
      async () => await imaging.GetImagingSettingsAsync(source)
                  ?? throw new ProbeFailure("the camera reported no imaging settings for this video source"),
      s => r.Values(
        ("brightness", s.Brightness),
        ("contrast", s.Contrast),
        ("saturation", s.ColorSaturation),
        ("sharpness", s.Sharpness)));

    if (!ctx.Options.AllowWrites) { r.SkipWrites("SetImagingSettings (change and restore)"); return; }

    if (settings?.Brightness is not { } original || options?.Brightness is not { } range)
    {
      r.Skip("SetImagingSettings (change and restore)", "the camera reports neither a brightness value nor a range to move it within");
      return;
    }

    // Five percent of the range, away from whichever end is nearer, so the target is always valid.
    var step = (range.Max - range.Min) * 0.05f;
    var target = original + (original + step <= range.Max ? step : -step);

    await r.StepAsync($"SetImagingSettings brightness {original:0.##} → {target:0.##} → {original:0.##}", async () =>
    {
      try
      {
        // Only the field being changed is sent: the library does its own read-modify-write and
        // treats null as "leave unchanged", so re-sending everything would be a way to overwrite
        // a setting the camera reported but we never meant to touch.
        await imaging.SetImagingSettingsAsync(source, new OnvifImagingSettings(target, null, null, null));

        var readBack = await imaging.GetImagingSettingsAsync(source);
        r.Value("read back", readBack?.Brightness);
        if (readBack?.Brightness is { } actual && Math.Abs(actual - target) > step)
          r.Note($"the camera did not take the value verbatim (asked {target:0.##}, got {actual:0.##}) — many quantise to their own steps");
      }
      finally
      {
        await imaging.SetImagingSettingsAsync(source, new OnvifImagingSettings(original, null, null, null));
      }
    });
  }
}
