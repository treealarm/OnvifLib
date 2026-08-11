namespace OnvifLib.Probe.Steps;

/// <summary>
/// Device I/O: relay outputs and digital inputs. Note the split — relays are read and driven
/// through <c>Camera</c> (they live on Device Management), while their options and the digital
/// inputs come from <c>DeviceIOService2</c>.
/// </summary>
public static class DeviceIoSteps
{
  public static async Task RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    r.Section(Sections.DeviceIo, "deviceio");

    await RelaysAsync(ctx);
    await DigitalInputsAsync(ctx);

    r.Note("ONVIF reports no live state for a relay or an input — neither the probe nor the GUI can show one");
  }

  private static async Task RelaysAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;

    var relays = await r.StepAsync("GetRelayOutputs (Camera)", ctx.Camera.GetRelayOutputsAsync,
      list => r.Table(["token", "mode", "idle state", "delay ms"],
        list.Select(x => new List<object?> { x.Token, x.Mode, x.IdleState, x.DelayMs })));

    if (ctx.DeviceIo is { } io)
      await r.StepAsync("GetRelayOutputOptions", io.GetRelayOutputOptionsAsync,
        list => r.Table(["token", "modes", "discrete", "min delay", "max delay", "discrete delays"],
          list.Select(o => new List<object?>
            { o.Token, o.SupportedModes, o.Discrete, o.MinDelayMs, o.MaxDelayMs, o.DiscreteDelaysMs })));
    else
      r.Skip("GetRelayOutputOptions", "deviceIO service not available");

    var first = relays?.FirstOrDefault();
    if (first is null)
    {
      r.Skip("SetRelayOutputSettings", "no relay outputs");
      r.Skip("SetRelayOutputState", "no relay outputs");
      return;
    }

    if (ctx.Options.AllowWrites)
      await r.StepAsync($"SetRelayOutputSettings [{first.Token}] (no-op re-write)",
        () => ctx.Camera.SetRelayOutputSettingsAsync(first.Token, first.Mode, first.IdleState, first.DelayMs));
    else
      r.SkipWrites("SetRelayOutputSettings");

    if (!ctx.Options.AllowRelay)
    {
      r.Skip("SetRelayOutputState", "requires --allow-relay — a relay is usually wired to a door, gate or alarm");
      return;
    }

    await r.StepAsync($"SetRelayOutputState [{first.Token}] active → inactive", async () =>
    {
      try { await ctx.Camera.SetRelayOutputStateAsync(first.Token, true); }
      finally
      {
        await Task.Delay(500, ctx.Cancellation);
        await ctx.Camera.SetRelayOutputStateAsync(first.Token, false);
      }
    });
  }

  private static async Task DigitalInputsAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    if (ctx.DeviceIo is not { } io)
    {
      r.Skip("GetDigitalInputs", "deviceIO service not available");
      r.Skip("GetDigitalInputConfigurationOptions", "deviceIO service not available");
      r.Skip("SetDigitalInputIdleState", "deviceIO service not available");
      return;
    }

    var inputs = await r.StepAsync("GetDigitalInputs", io.GetDigitalInputsAsync,
      list => r.Table(["token", "idle state"],
        list.Select(i => new List<object?> { i.Token, i.IdleState })));

    var first = inputs?.FirstOrDefault();
    if (first is null)
    {
      r.Skip("GetDigitalInputConfigurationOptions", "no digital inputs");
      r.Skip("SetDigitalInputIdleState", "no digital inputs");
      return;
    }

    var options = await r.StepAsync($"GetDigitalInputConfigurationOptions [{first.Token}]",
      async () => await io.GetDigitalInputConfigurationOptionsAsync(first.Token)
                  ?? throw new ProbeFailure("the camera reported no options for this input"),
      o => r.Value("idle state configurable", o.IdleStateConfigurable));

    if (!ctx.Options.AllowWrites) { r.SkipWrites("SetDigitalInputIdleState"); return; }
    if (options is { IdleStateConfigurable: false }) { r.Skip("SetDigitalInputIdleState", "the camera reports the idle state as fixed"); return; }
    if (first.IdleState is not { Length: > 0 } idleState) { r.Skip("SetDigitalInputIdleState", "the camera did not report a current idle state to re-send"); return; }

    await r.StepAsync($"SetDigitalInputIdleState [{first.Token}] = {idleState} (no-op re-write)",
      () => io.SetDigitalInputIdleStateAsync(first.Token, idleState));
  }
}
