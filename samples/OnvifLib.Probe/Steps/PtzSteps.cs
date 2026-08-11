namespace OnvifLib.Probe.Steps;

/// <summary>PTZ: capabilities, movement, presets.</summary>
public static class PtzSteps
{
  /// <summary>How far the reversible relative nudge moves, in normalised PTZ units.</summary>
  private const float Nudge = 0.05f;

  /// <summary>How long the continuous nudge runs before it is stopped and reversed.</summary>
  private static readonly TimeSpan ContinuousNudge = TimeSpan.FromMilliseconds(300);

  public static async Task RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    r.Section(Sections.Ptz, "ptz");

    if (ctx.Ptz is not { } ptz) { r.Skip("ptz", "service not available"); return; }
    if (!ctx.Capabilities.HasPtz)
      r.Note("the camera advertises the PTZ WSDL but reports no move support — calls below may fault");

    if (ctx.PrimaryProfile is not { } profile)
    {
      r.Skip("ptz", "no media profile to address PTZ against");
      return;
    }
    r.Value("profile", $"{profile.Token} ({profile.Name})");

    await r.StepAsync("SupportedCaps", () => Task.FromResult(ptz.SupportedCaps()), v => r.Value("supported", v));

    var caps = await r.StepAsync("GetCapabilities", () => ptz.GetCapabilitiesAsync(profile.Token), c => r.Values(
      ("absolute move", c.AbsoluteMove),
      ("relative move", c.RelativeMove),
      ("continuous move", c.ContinuousMove)));

    await r.StepAsync("GetPresets", () => ptz.GetPresetsAsync(profile.Token),
      presets => r.Table(["token", "name"], presets.Select(p => new List<object?> { p.Token, p.Name })));

    // The library exposes no way to read the current pan/tilt/zoom, so an absolute move has
    // nothing to restore to. It stays out of reach at every flag level.
    r.Skip("AbsoluteMove", "cannot be undone — the library exposes no way to read the current position");

    if (!ctx.Options.AllowWrites)
    {
      r.SkipWrites("ContinuousMove + Stop", "RelativeMove (there and back)", "SetPreset / GotoPreset / RemovePreset");
      return;
    }

    await ContinuousAsync(ctx, ptz, profile.Token, caps);
    await RelativeAsync(ctx, ptz, profile.Token, caps);
    await PresetCycleAsync(ctx, ptz, profile.Token);
  }

  private static async Task ContinuousAsync(ProbeContext ctx, PtzService2 ptz, string profileToken, PtzCapabilities? caps)
  {
    var r = ctx.Runner;
    if (caps is { ContinuousMove: false }) { r.Skip("ContinuousMove + Stop", "not supported by this camera"); return; }

    await r.StepAsync("ContinuousMove + Stop (there and back)", async () =>
    {
      // A symmetric pair of nudges, so the head ends up roughly where it started. Roughly is the
      // best available: continuous movement is time-based, and the camera's ramp-up and ramp-down
      // are not guaranteed to be identical in both directions.
      await ptz.ContinuousMoveAsync(profileToken, Nudge * 4, 0f, 0f, "PT1S");
      await Task.Delay(ContinuousNudge, ctx.Cancellation);
      await ptz.StopAsync(profileToken);

      await Task.Delay(200, ctx.Cancellation);

      await ptz.ContinuousMoveAsync(profileToken, -Nudge * 4, 0f, 0f, "PT1S");
      await Task.Delay(ContinuousNudge, ctx.Cancellation);
      await ptz.StopAsync(profileToken);
    });
    r.Note("continuous movement is time-based, so the return is approximate, not exact");
  }

  private static async Task RelativeAsync(ProbeContext ctx, PtzService2 ptz, string profileToken, PtzCapabilities? caps)
  {
    var r = ctx.Runner;
    if (caps is { RelativeMove: false }) { r.Skip("RelativeMove (there and back)", "not supported by this camera"); return; }

    await r.StepAsync($"RelativeMove ±{Nudge} pan (there and back)", async () =>
    {
      try
      {
        await ptz.RelativeMoveAsync(profileToken, Nudge, 0f);
        await Task.Delay(500, ctx.Cancellation);
      }
      finally
      {
        // Always attempt the return leg, even if the outbound one reported a fault after the
        // camera had already started moving.
        await ptz.RelativeMoveAsync(profileToken, -Nudge, 0f);
      }
    });
  }

  private static async Task PresetCycleAsync(ProbeContext ctx, PtzService2 ptz, string profileToken)
  {
    var r = ctx.Runner;
    var name = $"OnvifLibProbe-{DateTime.Now:HHmmss}";
    string? token = null;

    try
    {
      // An empty preset token asks the camera to create a new one and tell us its token.
      token = await r.StepAsync($"SetPreset '{name}' (create)",
        async () => await ptz.SetPresetAsync(profileToken, name, string.Empty) is { Length: > 0 } t
          ? t
          : throw new ProbeFailure("the camera accepted the preset but returned no token, so it cannot be cleaned up"),
        t => r.Value("new token", t));

      if (token is null) return;

      // Safe: the preset was just stored at the current position, so going to it does not move.
      await r.StepAsync($"GotoPreset [{token}]", () => ptz.GotoPresetAsync(profileToken, token));
    }
    finally
    {
      if (token is null)
        r.Skip("RemovePreset", "nothing was created to remove");
      else
        await r.StepAsync($"RemovePreset [{token}] (cleanup)", () => ptz.RemovePresetAsync(profileToken, token));
    }
  }
}
