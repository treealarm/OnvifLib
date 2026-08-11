namespace OnvifLib.Probe;

/// <summary>
/// What the connect section established, shared by every section after it.
/// </summary>
/// <remarks>
/// The service handles are resolved once and held. They must not be disposed between sections:
/// <c>OnvifServiceCache</c> owns them with a 10-minute TTL and would keep handing out a disposed
/// instance. They are disposed once, at the very end of the run.
/// </remarks>
public sealed class ProbeContext(ProbeOptions options, ProbeRunner runner, IOnvifLogger logger)
{
  public ProbeOptions Options { get; } = options;
  public ProbeRunner Runner { get; } = runner;
  public IOnvifLogger Logger { get; } = logger;

  /// <summary>
  /// Cancelled on Ctrl+C. Only the three library methods that accept a token can honour it
  /// (WsDiscovery.ProbeAsync, CameraScanner.ScanAsync, SearchService.FindRecordingsAsync);
  /// everything else is bounded solely by the per-call timeout from Camera.Create.
  /// </summary>
  public CancellationToken Cancellation { get; set; } = CancellationToken.None;

  public Camera Camera { get; set; } = null!;
  public OnvifCapabilities Capabilities { get; set; } = new(false, false, false, false, false, false);

  public MediaService? Media { get; set; }
  public PtzService2? Ptz { get; set; }
  public ImagingService2? Imaging { get; set; }
  public EventService1? Events { get; set; }
  public AnalyticsService? Analytics { get; set; }
  public DeviceIOService2? DeviceIo { get; set; }
  public RecordingService? Recording { get; set; }
  public SearchService? Search { get; set; }
  public ReplayService? Replay { get; set; }

  public List<OnvifProfileInfo> Profiles { get; } = [];

  /// <summary>The profile every profile-scoped section works against — the first one the camera lists.</summary>
  public OnvifProfileInfo? PrimaryProfile => Profiles.FirstOrDefault();

  /// <summary>VideoAnalyticsConfiguration tokens, filled by the media section and consumed by analytics.</summary>
  public List<OnvifAnalyticsConfig> AnalyticsConfigs { get; } = [];

  /// <summary>camera − us, from MeasureClockAsync. Zero until the device section measures it.</summary>
  public TimeSpan ClockOffset { get; set; }

  /// <summary>A camera timestamp expressed in our clock, for display beside the raw value.</summary>
  public DateTime ToLocalClock(DateTime cameraTime) => cameraTime - ClockOffset;

  /// <summary>A moment of ours expressed in the camera's clock, which is how the archive is indexed.</summary>
  public DateTime ToCameraClock(DateTime ourTime) => ourTime + ClockOffset;

  public void DisposeServices()
  {
    // Only safe here, at the end of the run, because the Camera is being discarded with them.
    foreach (var service in new OnvifServiceBase?[] { Media, Ptz, Imaging, Events, Analytics, DeviceIo, Recording, Search, Replay })
    {
      try { service?.Dispose(); }
      catch (Exception ex) { Logger.Warning($"disposing {service?.GetType().Name}: {ex.Message}"); }
    }
  }
}
