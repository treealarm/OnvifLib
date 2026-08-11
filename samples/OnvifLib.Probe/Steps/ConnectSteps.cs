namespace OnvifLib.Probe.Steps;

/// <summary>
/// Establishes the session every other section depends on. Returns false when the camera could
/// not be reached, which the caller turns into exit code 2.
/// </summary>
public static class ConnectSteps
{
  public static async Task<bool> RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    var o = ctx.Options;
    r.Section(Sections.Connect, "connect");

    ctx.Camera = Camera.Create(o.Ip, o.Port, o.User, o.Password, o.TimeoutSeconds, ctx.Logger, o.XAddr);

    // Create returns immediately and starts service discovery in the background; this is where
    // that work is awaited. It does not throw on failure — DoGetServices catches everything and
    // resolves to null — so the actual verdict comes from GetServices below.
    await r.StepAsync("Camera.Create + await InitTask", () => ctx.Camera.InitTask);

    var services = await r.StepAsync("GetServices",
      async () => await ctx.Camera.GetServicesAsync()
                  ?? throw new ProbeFailure("returned null — unreachable, or the credentials were rejected. Re-run with --verbose for the SOAP exchange."),
      table => r.Table(["wsdl namespace", "XAddr"],
        table.OrderBy(kv => kv.Key).Select(kv => new List<object?> { kv.Key, kv.Value })));

    if (services is null) return false;
    ctx.Runner.Report.Connected = true;

    await r.StepAsync("IsAlive", ctx.Camera.IsAlive, alive => r.Value("alive", alive));

    // One PTZ GetConfigurations round trip hides in here; the result is cached in Camera for 5 min.
    var caps = await r.StepAsync("GetCapabilitiesSummary", ctx.Camera.GetCapabilitiesSummaryAsync, c => r.Values(
      ("PTZ", c.HasPtz),
      ("imaging", c.HasImaging),
      ("events", c.HasEvents),
      ("digital inputs", c.HasDigitalInputs),
      ("edge recording", c.HasEdgeRecording),
      ("analytics", c.HasAnalytics)));
    if (caps is not null) ctx.Capabilities = caps;

    await ResolveServicesAsync(ctx);
    return true;
  }

  /// <summary>
  /// Resolves every service handle once and holds it. Doing this up front is what lets each
  /// later section say "not available" instead of failing, and re-fetching the event service
  /// later would create a second pull-point subscription once the cache TTL lapses.
  /// </summary>
  private static async Task ResolveServicesAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    var rows = new List<IReadOnlyList<object?>>();

    var advertised = await ctx.Camera.GetServicesAsync() ?? [];

    async Task Resolve<T>(string name, string[] wsdls, Func<Task<T?>> get, Action<T> assign) where T : class
    {
      try
      {
        var service = await get();
        if (service is null)
        {
          // "Not advertised" and "advertised but unusable" are very different diagnoses: the
          // second one is almost always a rejected credential, and reporting it as the first
          // sends the reader looking for a missing feature instead of a wrong password.
          rows.Add(new List<object?>
          {
            name,
            wsdls.Any(advertised.ContainsKey)
              ? "advertised, creation failed (see stderr)"
              : "not advertised",
          });
          r.Report.UnavailableServices.Add(name);
          return;
        }
        assign(service);
        rows.Add(new List<object?> { name, "available" });
        r.Report.AvailableServices.Add(name);
      }
      catch (Exception ex)
      {
        // One camera refusing one service must not cost us the other eight.
        rows.Add(new List<object?> { name, "error: " + ProbeRunner.Describe(ex) });
        r.Report.UnavailableServices.Add(name);
      }
    }

    await r.StepAsync("resolve services", async () =>
    {
      await Resolve("media", MediaService.GetSupportedWsdls(), ctx.Camera.GetMediaService, s => ctx.Media = s);
      await Resolve("ptz", PtzService2.GetSupportedWsdls(), ctx.Camera.GetPtzService, s => ctx.Ptz = s);
      await Resolve("imaging", ImagingService2.GetSupportedWsdls(), ctx.Camera.GetImagingService, s => ctx.Imaging = s);
      await Resolve("events", EventService1.GetSupportedWsdls(), ctx.Camera.GetEventService, s => ctx.Events = s);
      await Resolve("analytics", AnalyticsService.GetSupportedWsdls(), ctx.Camera.GetAnalyticsService, s => ctx.Analytics = s);
      await Resolve("deviceIO", DeviceIOService2.GetSupportedWsdls(), ctx.Camera.GetDeviceIOService, s => ctx.DeviceIo = s);
      await Resolve("recording", RecordingService.GetSupportedWsdls(), ctx.Camera.GetRecordingService, s => ctx.Recording = s);
      await Resolve("search", SearchService.GetSupportedWsdls(), ctx.Camera.GetSearchService, s => ctx.Search = s);
      await Resolve("replay", ReplayService.GetSupportedWsdls(), ctx.Camera.GetReplayService, s => ctx.Replay = s);
    });

    r.Table(["service", "status"], rows);
  }
}
