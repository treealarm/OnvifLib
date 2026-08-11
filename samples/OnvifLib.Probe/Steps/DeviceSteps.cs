namespace OnvifLib.Probe.Steps;

/// <summary>Device Management: identity, clock, and camera-side storage.</summary>
public static class DeviceSteps
{
  public static async Task RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    r.Section(Sections.Device, "device");

    await r.StepAsync("GetDeviceInformation", ctx.Camera.GetDeviceInformationAsync, info => r.Values(
      ("manufacturer", info.Manufacturer),
      ("model", info.Model),
      ("firmware", info.FirmwareVersion),
      ("serial", info.SerialNumber),
      ("hardware id", info.HardwareId)));

    await r.StepAsync("GetDeviceTime",
      async () => await ctx.Camera.GetDeviceTimeAsync()
                  ?? throw new ProbeFailure("the device did not report UTC"),
      utc => r.Value("device UTC", utc));

    // Worth its own step even though GetDeviceTime already read the clock: this one measures
    // against the midpoint of the round trip, and the offset it produces is what makes the
    // Profile G timestamps interpretable.
    await r.StepAsync("MeasureClock",
      async () => await ctx.Camera.MeasureClockAsync()
                  ?? throw new ProbeFailure("the device did not report UTC, so the offset is unknown"),
      reading =>
      {
        ctx.ClockOffset = reading.Offset;
        r.Values(
          ("camera UTC", reading.CameraUtc),
          ("our UTC", reading.ServerUtc),
          ("round trip", reading.RoundTrip),
          ("offset", $"{reading.Offset:g} ({(reading.Offset >= TimeSpan.Zero ? "camera ahead of us" : "camera behind us")})"));
        if (reading.Offset.Duration() > TimeSpan.FromMinutes(1))
          r.Note("a large offset makes recording searches land in the wrong hour unless converted");
      });

    var storage = await r.StepAsync("GetStorageSupport", ctx.Camera.GetStorageSupportAsync, s => r.Values(
      ("storage config", s.StorageConfiguration),
      ("max configs", s.MaxStorageConfigurations),
      ("aux commands", s.AuxiliaryCommands.Count == 0 ? "—" : string.Join(", ", s.AuxiliaryCommands))));

    await r.StepAsync("GetStorageConfigurations", ctx.Camera.GetStorageConfigurationsAsync,
      configs => r.Table(["token", "type", "local path", "storage uri", "user"],
        configs.Select(c => new List<object?> { c.Token, c.Type, c.LocalPath, c.StorageUri, c.User })));

    // Everything below changes the device in a way the probe cannot undo.
    r.SkipDestructive("SyncTime", "SetTime", "Reboot");

    // Cameras do report an auxiliary-command list containing a single empty string, so the
    // entries are filtered rather than counted — otherwise the reason reads "one of []".
    var commands = (storage?.AuxiliaryCommands ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    if (commands.Count > 0)
      r.Skip("SendAuxiliaryCommand", $"destructive — one of [{string.Join(", ", commands)}] may format storage");
    else
      r.SkipDestructive("SendAuxiliaryCommand");
  }
}
