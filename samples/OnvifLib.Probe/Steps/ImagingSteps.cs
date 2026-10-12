namespace OnvifLib.Probe.Steps;

/// <summary>
/// Imaging: every picture setting the library exposes, their ranges, the focus motor, and changes
/// that undo themselves.
/// </summary>
public static class ImagingSteps
{
  /// <summary>How long a continuous focus nudge runs in each direction.</summary>
  private static readonly TimeSpan FocusNudge = TimeSpan.FromMilliseconds(300);

  /// <summary>How long to wait for the focus motor to report IDLE after a move.</summary>
  private static readonly TimeSpan FocusSettle = TimeSpan.FromSeconds(5);

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
      o => PrintOptions(r, o));

    var settings = await r.StepAsync("GetImagingSettings",
      async () => await imaging.GetImagingSettingsAsync(source)
                  ?? throw new ProbeFailure("the camera reported no imaging settings for this video source"),
      s => PrintSettings(r, s));

    var moveOptions = await r.StepAsync("GetMoveOptions (focus)",
      async () => await imaging.GetMoveOptionsAsync(source)
                  ?? throw new ProbeFailure("the camera reported no focus move options"),
      m => r.Table(["move", "range", "speed"], new List<IReadOnlyList<object?>>
      {
        new List<object?> { "absolute", Describe(m.AbsolutePosition), Describe(m.AbsoluteSpeed) },
        new List<object?> { "relative", Describe(m.RelativeDistance), Describe(m.RelativeSpeed) },
        new List<object?> { "continuous", "—", Describe(m.ContinuousSpeed) },
      }));

    var status = await r.StepAsync("GetStatus (imaging)",
      async () => await imaging.GetStatusAsync(source)
                  ?? throw new ProbeFailure("the camera would not report imaging status"),
      s => r.Values(("focus position", s.FocusPosition), ("focus move", s.FocusMoveStatus), ("focus error", s.FocusError)));

    if (!ctx.Options.AllowWrites)
    {
      r.SkipWrites("SetImagingSettings (change and restore)", "SetImagingSettings ir-cut (change and restore)", "MoveFocus (there and back)");
      return;
    }

    await BrightnessAsync(ctx, imaging, source, settings, options);
    await IrCutAsync(ctx, imaging, source, settings, options);
    await FocusAsync(ctx, imaging, source, moveOptions, status);
  }

  private static void PrintOptions(ProbeRunner r, OnvifImagingOptions o)
  {
    var rows = new List<IReadOnlyList<object?>>
    {
      Row("brightness", o.Brightness),
      Row("contrast", o.Contrast),
      Row("saturation", o.ColorSaturation),
      Row("sharpness", o.Sharpness),
    };
    if (o.Exposure is { } e)
    {
      rows.Add(Row("exposure time", e.ExposureTime));
      rows.Add(Row("exposure min time", e.MinExposureTime));
      rows.Add(Row("exposure max time", e.MaxExposureTime));
      rows.Add(Row("gain", e.Gain));
      rows.Add(Row("min gain", e.MinGain));
      rows.Add(Row("max gain", e.MaxGain));
      rows.Add(Row("iris", e.Iris));
      rows.Add(Row("min iris", e.MinIris));
      rows.Add(Row("max iris", e.MaxIris));
    }
    if (o.WhiteBalance is { } wb)
    {
      rows.Add(Row("wb Yr gain", wb.YrGain));
      rows.Add(Row("wb Yb gain", wb.YbGain));
    }
    if (o.Focus is { } f)
    {
      rows.Add(Row("focus default speed", f.DefaultSpeed));
      rows.Add(Row("focus near limit", f.NearLimit));
      rows.Add(Row("focus far limit", f.FarLimit));
    }
    rows.Add(Row("WDR level", o.WideDynamicRange?.Level));
    rows.Add(Row("BLC level", o.BacklightCompensation?.Level));
    r.Table(["setting", "min", "max"], rows.Where(row => row[1] is not null || rows.IndexOf(row) < 4));

    r.Values(
      ("ir-cut modes", Join(o.IrCutFilterModes)),
      ("exposure modes", Join(o.Exposure?.Modes)),
      ("exposure priority", Join(o.Exposure?.Priorities)),
      ("white balance", Join(o.WhiteBalance?.Modes)),
      ("auto focus", Join(o.Focus?.AutoFocusModes)),
      ("WDR modes", Join(o.WideDynamicRange?.Modes)),
      ("BLC modes", Join(o.BacklightCompensation?.Modes)));
  }

  private static void PrintSettings(ProbeRunner r, OnvifImagingSettings s) => r.Values(
    ("brightness", s.Brightness),
    ("contrast", s.Contrast),
    ("saturation", s.ColorSaturation),
    ("sharpness", s.Sharpness),
    ("ir-cut filter", s.IrCutFilter),
    ("exposure", s.Exposure is { } e
      ? $"{e.Mode}{(e.Priority is null ? "" : $", {e.Priority}")}, time {F(e.ExposureTime)} ({F(e.MinExposureTime)}…{F(e.MaxExposureTime)}), gain {F(e.Gain)} ({F(e.MinGain)}…{F(e.MaxGain)}), iris {F(e.Iris)}"
      : null),
    ("white balance", s.WhiteBalance is { } wb ? $"{wb.Mode}, Cr {F(wb.CrGain)}, Cb {F(wb.CbGain)}" : null),
    ("focus", s.Focus is { } f ? $"{f.AutoFocusMode}, speed {F(f.DefaultSpeed)}, near {F(f.NearLimit)}, far {F(f.FarLimit)}" : null),
    ("WDR", s.WideDynamicRange is { } w ? $"{w.Mode}, level {F(w.Level)}" : null),
    ("BLC", s.BacklightCompensation is { } b ? $"{b.Mode}, level {F(b.Level)}" : null));

  private static async Task BrightnessAsync(ProbeContext ctx, ImagingService2 imaging, string source,
    OnvifImagingSettings? settings, OnvifImagingOptions? options)
  {
    var r = ctx.Runner;
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

  /// <summary>
  /// Flips the day/night filter to another mode and back. Visible on the picture for a moment,
  /// but restored in a finally, so it counts as reversible.
  /// </summary>
  private static async Task IrCutAsync(ProbeContext ctx, ImagingService2 imaging, string source,
    OnvifImagingSettings? settings, OnvifImagingOptions? options)
  {
    var r = ctx.Runner;
    const string name = "SetImagingSettings ir-cut (change and restore)";
    if (settings?.IrCutFilter is not { } original)
    {
      r.Skip(name, "the camera reports no current ir-cut mode to restore");
      return;
    }
    var target = options?.IrCutFilterModes?.FirstOrDefault(m => !m.Equals(original, StringComparison.OrdinalIgnoreCase));
    if (target is null)
    {
      r.Skip(name, "the camera offers no second ir-cut mode to switch to");
      return;
    }

    await r.StepAsync($"SetImagingSettings ir-cut {original} → {target} → {original}", async () =>
    {
      try
      {
        await imaging.SetImagingSettingsAsync(source, new OnvifImagingSettings(null, null, null, null, IrCutFilter: target));
        var readBack = await imaging.GetImagingSettingsAsync(source);
        r.Value("read back", readBack?.IrCutFilter);
        if (!string.Equals(readBack?.IrCutFilter, target, StringComparison.OrdinalIgnoreCase))
          throw new ProbeFailure($"the camera accepted ir-cut {target} but reads back {readBack?.IrCutFilter ?? "nothing"}");
      }
      finally
      {
        await imaging.SetImagingSettingsAsync(source, new OnvifImagingSettings(null, null, null, null, IrCutFilter: original));
      }
    });
  }

  /// <summary>
  /// Moves the focus motor a little and back. Relative when the camera offers it and reports a
  /// position to check against; otherwise a pair of continuous nudges, which is only approximately
  /// symmetric.
  /// </summary>
  private static async Task FocusAsync(ProbeContext ctx, ImagingService2 imaging, string source,
    OnvifFocusMoveOptions? moves, OnvifImagingStatus? status)
  {
    var r = ctx.Runner;
    if (moves is null)
    {
      r.Skip("MoveFocus (there and back)", "the camera reported no focus move options");
      return;
    }

    if (moves.RelativeDistance is { } distance && status?.FocusPosition is { } start)
    {
      var d = (distance.Max - distance.Min) * 0.05f / 2;
      await r.StepAsync($"MoveFocus relative +{d:0.###} / -{d:0.###} (there and back)", async () =>
      {
        try
        {
          await imaging.MoveFocusRelativeAsync(source, d);
          var moved = await SettleAsync(imaging, source, ctx.Cancellation);
          r.Value("moved to", moved?.FocusPosition);
        }
        finally
        {
          await imaging.MoveFocusRelativeAsync(source, -d);
          var back = await SettleAsync(imaging, source, ctx.Cancellation);
          r.Values(("started at", start), ("back at", back?.FocusPosition));
        }
      });
      return;
    }

    if (moves.ContinuousSpeed is { } speed)
    {
      var v = Math.Min(Math.Abs(speed.Max), 1f) * 0.5f;
      await r.StepAsync("MoveFocus continuous + Stop (there and back)", async () =>
      {
        try
        {
          await imaging.MoveFocusContinuousAsync(source, v);
          await Task.Delay(FocusNudge, ctx.Cancellation);
        }
        finally
        {
          await imaging.StopFocusAsync(source);
        }
        await Task.Delay(200, ctx.Cancellation);
        try
        {
          await imaging.MoveFocusContinuousAsync(source, -v);
          await Task.Delay(FocusNudge, ctx.Cancellation);
        }
        finally
        {
          await imaging.StopFocusAsync(source);
        }
        if (await imaging.GetStatusAsync(source) is { FocusPosition: { } end })
          r.Values(("started at", status?.FocusPosition), ("ended at", end));
      });
      r.Note("continuous focus is time-based, so the return is approximate, not exact");
      return;
    }

    r.Skip("MoveFocus (there and back)", "the camera offers neither a relative focus move with a readable position nor a continuous one");
  }

  /// <summary>Polls focus status until the motor reports it has stopped, or gives up.</summary>
  private static async Task<OnvifImagingStatus?> SettleAsync(ImagingService2 imaging, string source, CancellationToken ct)
  {
    var until = DateTime.UtcNow + FocusSettle;
    OnvifImagingStatus? status;
    do
    {
      await Task.Delay(250, ct);
      status = await imaging.GetStatusAsync(source);
    }
    while (status?.FocusMoveStatus == "MOVING" && DateTime.UtcNow < until);
    return status;
  }

  private static List<object?> Row(string name, OnvifFloatRange? range) => [name, range?.Min, range?.Max];

  private static string Describe(OnvifFloatRange? range) => range is null ? "—" : $"{range.Min:0.###} … {range.Max:0.###}";

  private static string Join(IReadOnlyList<string>? values) => values is { Count: > 0 } ? string.Join(", ", values) : "—";

  private static string F(float? value) => value is { } v ? v.ToString("0.###") : "—";
}
