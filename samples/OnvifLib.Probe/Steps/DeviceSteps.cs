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

    await AdministrationAsync(ctx);

    // Everything below changes the device in a way the probe cannot undo.
    r.SkipDestructive("SyncTime", "SetTime", "SetSystemDateAndTime", "Reboot");
    r.SkipDestructive("CreateUser", "SetUser", "DeleteUser");
    r.SkipDestructive("AddScopes", "RemoveScopes");
    // Re-sending even the same network values restarts the network stack or the web server on
    // some firmware, which drops the very connection the probe is running over.
    r.SkipDestructive("SetNetworkInterface", "SetDNS", "SetNTP", "SetNetworkProtocols", "SetNetworkDefaultGateway");

    // Cameras do report an auxiliary-command list containing a single empty string, so the
    // entries are filtered rather than counted — otherwise the reason reads "one of []".
    var commands = (storage?.AuxiliaryCommands ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    if (commands.Count > 0)
      r.Skip("SendAuxiliaryCommand", $"destructive — one of [{string.Join(", ", commands)}] may format storage");
    else
      r.SkipDestructive("SendAuxiliaryCommand");
  }

  /// <summary>Users, clock, network and discovery scopes: read everything, rewrite two harmless values.</summary>
  private static async Task AdministrationAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;

    await r.StepAsync("GetSystemDateAndTime", ctx.Camera.GetSystemDateAndTimeAsync, t => r.Values(
      ("mode", t.DateTimeType),
      ("daylight saving", t.DaylightSavings),
      ("time zone", t.TimeZone),
      ("camera UTC", t.Utc),
      ("camera local", t.Local),
      ("same moment here", t.Utc is { } utc ? ctx.ToLocalClock(utc).ToLocalTime() : null)));

    // GetUsers is commonly refused to non-administrators; a FAIL here with a not-authorized
    // reason says which kind of account the probe is running as, not that the library is broken.
    await r.StepAsync("GetUsers", ctx.Camera.GetUsersAsync,
      users => r.Table(["user", "level"], users.Select(u => new List<object?> { u.Username, u.Level })));

    var scopes = await r.StepAsync("GetScopes", ctx.Camera.GetScopesAsync,
      list => r.Table(["scope", "fixed"], list.Select(sc => new List<object?> { sc.ScopeItem, sc.Fixed })));

    var hostname = await r.StepAsync("GetHostname", ctx.Camera.GetHostnameAsync,
      h => r.Values(("hostname", h.Name), ("from DHCP", h.FromDhcp)));

    await r.StepAsync("GetNetworkInterfaces", ctx.Camera.GetNetworkInterfacesAsync,
      list => r.Table(["token", "name", "mac", "enabled", "dhcp", "address", "from dhcp", "mtu"],
        list.Select(n => new List<object?>
        {
          n.Token, n.Name, n.HwAddress, n.Enabled, n.Dhcp,
          n.ManualAddress is null ? null : $"{n.ManualAddress}/{n.ManualPrefix}",
          n.FromDhcpAddress, n.Mtu,
        })));

    await r.StepAsync("GetDNS", ctx.Camera.GetDnsAsync, d => r.Values(
      ("from DHCP", d.FromDhcp),
      ("manual", Join(d.ManualServers)),
      ("via DHCP", Join(d.DhcpServers)),
      ("search", Join(d.SearchDomains))));

    await r.StepAsync("GetNTP", ctx.Camera.GetNtpAsync, n => r.Values(
      ("from DHCP", n.FromDhcp),
      ("manual", Join(n.ManualServers)),
      ("via DHCP", Join(n.DhcpServers))));

    await r.StepAsync("GetNetworkProtocols", ctx.Camera.GetNetworkProtocolsAsync,
      list => r.Table(["protocol", "enabled", "ports"],
        list.Select(p => new List<object?> { p.Name, p.Enabled, string.Join(", ", p.Ports) })));

    await r.StepAsync("GetNetworkDefaultGateway", ctx.Camera.GetNetworkDefaultGatewayAsync,
      g => r.Values(("IPv4", Join(g.IPv4)), ("IPv6", Join(g.IPv6))));

    if (!ctx.Options.AllowWrites)
    {
      r.SkipWrites("SetHostname (no-op re-write)", "SetScopes (no-op re-write)");
      return;
    }

    // Both are re-sent with exactly what was just read: proof the call is accepted, with nothing
    // on the camera changed.
    if (hostname is { FromDhcp: false, Name.Length: > 0 } h)
      await r.StepAsync($"SetHostname [{h.Name}] (no-op re-write)", () => ctx.Camera.SetHostnameAsync(h.Name));
    else
      r.Skip("SetHostname (no-op re-write)", hostname is null
        ? "the hostname could not be read, so there is nothing to re-send"
        : "the hostname comes from DHCP or is empty — setting one would turn DHCP naming off");

    var configurable = scopes?.Where(sc => !sc.Fixed).Select(sc => sc.ScopeItem).ToList();
    if (configurable is { Count: > 0 })
      await r.StepAsync($"SetScopes ({configurable.Count} configurable, no-op re-write)", async () =>
      {
        await ctx.Camera.SetScopesAsync(configurable);
        var after = (await ctx.Camera.GetScopesAsync()).Where(sc => !sc.Fixed).Select(sc => sc.ScopeItem).ToHashSet();
        if (!after.SetEquals(configurable))
          throw new ProbeFailure("the configurable scopes read back differ from what was re-sent");
      });
    else
      // An empty SetScopes is how a camera is told to drop every configurable scope.
      r.Skip("SetScopes (no-op re-write)", "no configurable scopes to re-send, and an empty list would clear them");
  }

  private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "—" : string.Join(", ", values);
}
