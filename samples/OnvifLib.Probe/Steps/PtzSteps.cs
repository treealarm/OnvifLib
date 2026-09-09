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

    await r.StepAsync("GetSpaces", () => ptz.GetSpacesAsync(), spaces => r.Table(
      ["kind", "space", "x range", "y range"],
      new (string Kind, PtzSpace? Space)[]
        {
          ("absolute pan/tilt", spaces?.AbsolutePanTilt),
          ("relative pan/tilt", spaces?.RelativePanTilt),
          ("continuous pan/tilt", spaces?.ContinuousPanTilt),
        }
        .Select(row => new List<object?>
        {
          row.Kind,
          row.Space?.Uri ?? "(none declared)",
          row.Space is null ? null : $"{row.Space.MinX} … {row.Space.MaxX}",
          row.Space is null ? null : $"{row.Space.MinY} … {row.Space.MaxY}",
        })));

    var status = await r.StepAsync("GetStatus", () => ptz.GetStatusAsync(profile.Token), st =>
    {
      if (st is null) { r.Value("position", "not reported"); return; }
      r.Values(
        ("pan", st.Pan), ("tilt", st.Tilt), ("zoom", st.Zoom),
        ("moving", st.Moving), ("camera clock", st.UtcTime));
    });

    await r.StepAsync("GetPresets", () => ptz.GetPresetsAsync(profile.Token),
      presets => r.Table(["token", "name"], presets.Select(p => new List<object?> { p.Token, p.Name })));

    if (!ctx.Options.AllowWrites)
    {
      r.SkipWrites(
        "ContinuousMove + Stop", "AbsoluteMove (there and back)",
        "RelativeMove (there and back)", "SetPreset / GotoPreset / RemovePreset");
      return;
    }

    await ContinuousAsync(ctx, ptz, profile.Token, caps);
    await AbsoluteAsync(ctx, ptz, profile.Token, caps, status);
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

  /// <summary>
  /// Moves the camera to a position, then back to where it started -- and checks it went.
  /// </summary>
  /// <remarks>
  /// This used to be skipped outright, because the library could command an absolute move and had
  /// no way to read a position to return to. GetStatus supplies both halves: somewhere to come back
  /// to, and the only honest test of whether the camera obeyed. It matters because a camera that
  /// declares an absolute space and silently ignores AbsoluteMove is not hypothetical -- this
  /// project's own NVT test camera does exactly that, and reports a frozen position while the motor
  /// turns. So a position that does not change is reported rather than treated as success.
  /// </remarks>
  private static async Task AbsoluteAsync(
    ProbeContext ctx, PtzService2 ptz, string profileToken, PtzCapabilities? caps, PtzStatus? start)
  {
    var r = ctx.Runner;
    if (caps is { AbsoluteMove: false }) { r.Skip("AbsoluteMove (there and back)", "not supported by this camera"); return; }
    if (start is null) { r.Skip("AbsoluteMove (there and back)", "the camera does not report its position, so the move could not be undone"); return; }

    await r.StepAsync($"AbsoluteMove ±{Nudge} pan (there and back)", async () =>
    {
      var target = start.Pan + Nudge;
      try
      {
        await ptz.AbsoluteMoveAsync(profileToken, target, start.Tilt);
        await Task.Delay(1500, ctx.Cancellation);

        var moved = await ptz.GetStatusAsync(profileToken);
        if (moved is null)
          throw new ProbeFailure("the camera stopped reporting its position mid-move");
        if (Math.Abs(moved.Pan - start.Pan) < Nudge / 4)
          // Two faults look identical from here -- an ignored AbsoluteMove and a GetStatus that
          // reports constants -- and this cannot tell them apart, so it does not pretend to. Either
          // way the pair is unusable for absolute positioning: one cannot be commanded, the other
          // cannot be verified. Which one it is takes a picture, not another ONVIF call.
          throw new ProbeFailure(
            $"the camera accepted AbsoluteMove and its position did not change: pan still {moved.Pan}, "
            + $"asked for {target}. Either the move was ignored or GetStatus is a stub -- compare "
            + "two snapshots to tell which");
      }
      finally
      {
        // Back to where it was, whatever happened on the way out.
        await ptz.AbsoluteMoveAsync(profileToken, start.Pan, start.Tilt);
      }
    });
    r.Note("a camera can accept AbsoluteMove and ignore it; only the picture changing proves otherwise");
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
      // A failed SetPreset does not mean no preset. On a slow camera the request channel times out
      // while the camera goes ahead and stores it — observed on the NVT test camera, which left a
      // preset behind that the probe then declined to clean up because it had never learned the
      // token. So when the token is unknown, look for the preset by the name we asked for: it is
      // stamped with the time this run started and cannot collide with the camera's own.
      token ??= await FindByNameAsync(ptz, profileToken, name);

      if (token is null)
        r.Skip("RemovePreset", "nothing was created to remove");
      else
        await r.StepAsync($"RemovePreset [{token}] (cleanup)", () => ptz.RemovePresetAsync(profileToken, token));
    }
  }

  private static async Task<string?> FindByNameAsync(PtzService2 ptz, string profileToken, string name)
  {
    try
    {
      var presets = await ptz.GetPresetsAsync(profileToken);
      return presets.FirstOrDefault(p => p.Name == name)?.Token;
    }
    catch
    {
      // Cleanup is best-effort: a camera that will not list its presets is already being reported
      // as failing, and throwing from here would replace that with a less useful error.
      return null;
    }
  }
}
