namespace OnvifLib.Probe.Steps;

/// <summary>
/// Runs before connecting and needs no credentials: WS-Discovery multicast, and optionally a
/// brute-force sweep of an IP range.
/// </summary>
public static class DiscoverySteps
{
  public static async Task RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    r.Section(Sections.Discovery, "discovery");

    await ProbeAsync(ctx);

    if (ctx.Options.ScanFrom is { } from && ctx.Options.ScanTo is { } to)
      await ScanAsync(ctx, from, to);
    else
      r.Skip("CameraScanner.Scan", "pass --scan <from> <to> to sweep an IP range");
  }

  private static async Task ProbeAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    var timeout = TimeSpan.FromSeconds(ctx.Options.DiscoveryTimeoutSeconds);

    await r.StepAsync($"WsDiscovery.Probe ({timeout.TotalSeconds:0.#}s)",
      () => WsDiscovery.ProbeAsync(timeout, ctx.Cancellation, ctx.Logger),
      result =>
      {
        // Three genuinely different outcomes. The library keeps ScanOk separate precisely so
        // "we could not probe at all" is not reported as "there is nothing out there".
        if (!result.ScanOk)
        {
          Con.Line(ConsoleColor.Yellow,
            "            COULD NOT PROBE — no interface could join 239.255.255.250:3702.");
          r.Note("check the firewall, container/VPN networking, and that a real LAN interface exists");
          return;
        }

        if (result.Devices.Count == 0)
        {
          r.Note("probed successfully; no ONVIF device answered");
          return;
        }

        r.Table(["name", "hardware", "ip", "port", "xaddrs"],
          result.Devices.Select(d => new List<object?> { d.Name, d.Hardware, d.Ip, d.Port, string.Join(" ", d.XAddrs) }));
        r.Note("connect to one with --ip <ip> --port <port>, or --xaddr <url> when the path is non-standard");
      });
  }

  private static async Task ScanAsync(ProbeContext ctx, string from, string to)
  {
    var r = ctx.Runner;
    var credentials = new List<(string username, string password)> { (ctx.Options.User, ctx.Options.Password) };
    var found = new List<Camera>();
    var gate = new object();
    var lastPrinted = -1;

    // Both callbacks arrive on Parallel.ForEachAsync workers, and onProgress is additionally
    // invoked from inside the scanner's lock as a discarded task. Neither may block or throw:
    // an exception here escapes into the parallel loop and aborts the whole scan.
    Task OnProgress(int percent, string message)
    {
      try
      {
        lock (gate)
        {
          if (percent / 10 == lastPrinted / 10 && percent != 100) return Task.CompletedTask;
          lastPrinted = percent;
          Con.Line(ConsoleColor.DarkGray, $"            {percent,3}%  {message}");
        }
      }
      catch { /* progress output is never worth failing a scan over */ }
      return Task.CompletedTask;
    }

    Task OnCameraDiscovered(Camera camera, object _)
    {
      try { lock (gate) found.Add(camera); }
      catch { }
      return Task.CompletedTask;
    }

    await r.StepAsync($"CameraScanner.Scan {from}..{to} ports {string.Join(",", ctx.Options.ScanPorts)}",
      () => CameraScanner.ScanAsync(from, to, ctx.Options.ScanPorts, credentials,
        OnProgress, OnCameraDiscovered, ctx.Cancellation, existing: [], context: ctx));

    r.Table(["ip", "port", "user", "url"],
      found.Select(c => new List<object?> { c.Ip, c.Port, c.User, c.Url }));
    if (found.Count > 0)
      r.Note("the scanner creates one Camera per hit and never disposes them; this probe drops them with the process");
  }
}
