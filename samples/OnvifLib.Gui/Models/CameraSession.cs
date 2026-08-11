namespace OnvifLib.Gui.Models;

/// <summary>
/// One connected camera and everything resolved from it at connect time.
/// </summary>
/// <remarks>
/// The service handles are resolved once, here, for two reasons. Only <c>Get*Service()</c> can
/// answer "does this camera have it", and the tabs need that answer to gate themselves. And
/// re-fetching the event service after the cache TTL lapses would create a second pull-point
/// subscription on the camera.
///
/// The services must not be disposed while the session lives: <c>OnvifServiceCache</c> owns them
/// with a 10-minute TTL and would keep handing out a disposed instance. <see cref="Dispose"/> is
/// only called when the whole session is being discarded.
/// </remarks>
public sealed class CameraSession : IDisposable
{
  public required Camera Camera { get; init; }
  public required OnvifCapabilities Capabilities { get; init; }
  public required IReadOnlyDictionary<string, string> Services { get; init; }

  public OnvifDeviceInfo? DeviceInfo { get; set; }

  public MediaService? Media { get; init; }
  public PtzService2? Ptz { get; init; }
  public ImagingService2? Imaging { get; init; }
  public EventService1? Events { get; init; }
  public AnalyticsService? Analytics { get; init; }
  public DeviceIOService2? DeviceIo { get; init; }
  public RecordingService? Recording { get; init; }
  public SearchService? Search { get; init; }
  public ReplayService? Replay { get; init; }

  /// <summary>camera − us. Recording timestamps are indexed in the camera's clock, not ours.</summary>
  public TimeSpan ClockOffset { get; set; }

  public string Url => Camera.Url;

  /// <summary>
  /// Builds the session. Throws when the camera cannot be reached, so the caller's operation
  /// wrapper turns that into one status-bar line.
  /// </summary>
  public static async Task<CameraSession> ConnectAsync(
    string ip, int port, string user, string password, double timeoutSeconds,
    IOnvifLogger? logger, string? xaddr)
  {
    var camera = Camera.Create(ip, port, user, password, timeoutSeconds, logger, xaddr);

    // Create returns immediately and discovers services in the background. InitTask does not
    // throw on failure — the library catches and resolves to null — so the real verdict is
    // whether GetServicesAsync produced anything.
    await camera.InitTask;

    var services = await camera.GetServicesAsync()
      ?? throw new InvalidOperationException(
        "The camera returned no service list: it is unreachable, or the credentials were rejected. The Log tab has the details.");

    var session = new CameraSession
    {
      Camera = camera,
      Services = services,
      Capabilities = await camera.GetCapabilitiesSummaryAsync(),
      Media = await Try(camera.GetMediaService, logger),
      Ptz = await Try(camera.GetPtzService, logger),
      Imaging = await Try(camera.GetImagingService, logger),
      Events = await Try(camera.GetEventService, logger),
      Analytics = await Try(camera.GetAnalyticsService, logger),
      DeviceIo = await Try(camera.GetDeviceIOService, logger),
      Recording = await Try(camera.GetRecordingService, logger),
      Search = await Try(camera.GetSearchService, logger),
      Replay = await Try(camera.GetReplayService, logger),
    };

    // Neither of these is worth refusing the connection over: a camera that will not identify
    // itself or report its clock is still a camera worth poking at.
    session.DeviceInfo = await Try<OnvifDeviceInfo>(async () => await camera.GetDeviceInformationAsync(), logger);
    if (await Try(camera.MeasureClockAsync, logger) is { } clock) session.ClockOffset = clock.Offset;

    return session;
  }

  /// <summary>One service refusing to resolve must not cost the session the other eight.</summary>
  private static async Task<T?> Try<T>(Func<Task<T?>> get, IOnvifLogger? logger) where T : class
  {
    try { return await get(); }
    catch (Exception ex)
    {
      logger?.Warning($"resolving a service failed: {ex.Message}");
      return null;
    }
  }

  /// <summary>Whether the camera advertised a namespace, used to explain a null service handle.</summary>
  public bool Advertises(params string[] wsdls) => wsdls.Any(Services.ContainsKey);

  public void Dispose()
  {
    foreach (var service in new OnvifServiceBase?[]
             { Media, Ptz, Imaging, Events, Analytics, DeviceIo, Recording, Search, Replay })
    {
      try { service?.Dispose(); }
      catch { /* the session is going away; a failed dispose changes nothing */ }
    }
  }
}
