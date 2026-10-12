using RecordingServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  /// <summary>
  /// ONVIF Profile G Recording — the recordings, tracks and recording jobs the camera keeps on its
  /// own storage. Deletions here happen on the device and cannot be undone.
  /// </summary>
  public class RecordingService : OnvifServiceBase, IOnvifServiceFactory<RecordingService>
  {
    public const string WSDL_V10 = "http://www.onvif.org/ver10/recording/wsdl";

    /// <summary>A job source that is a media profile — what a camera recording itself uses.</summary>
    public const string SourceTypeProfile = "http://www.onvif.org/ver10/schema/Profile";

    /// <summary>
    /// A job source that is a receiver (a stream the device pulls from elsewhere). This is the
    /// schema's default, which is why a job fed by a profile must say Profile explicitly.
    /// </summary>
    public const string SourceTypeReceiver = "http://www.onvif.org/ver10/schema/Receiver";

    private RecordingPortClient? _client;

    protected RecordingService(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    public static string[] GetSupportedWsdls() => new[] { WSDL_V10 };

    public static async Task<RecordingService?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      var instance = new RecordingService(url, binding, username, password, profile, tokenFactory, logger);
      await instance.InitializeAsync();
      return instance;
    }

    protected override async Task InitializeAsync()
    {
      await base.InitializeAsync();
      _client = _onvifClientFactory.CreateClient<RecordingPortClient, RecordingPort>(
        new EndpointAddress(_url), _binding, _username, _password);
      await _client.OpenAsync();
    }

    public async Task<(bool DynamicRecordings, bool DynamicTracks, int MaxRecordings)> GetServiceCapabilitiesAsync()
    {
      if (_client == null) return (false, false, 0);
      var resp = await _client.GetServiceCapabilitiesAsync(new GetServiceCapabilitiesRequest());
      var caps = resp?.Capabilities;
      if (caps == null) return (false, false, 0);
      return (
        caps.DynamicRecordingsSpecified && caps.DynamicRecordings,
        caps.DynamicTracksSpecified && caps.DynamicTracks,
        caps.MaxRecordingsSpecified ? (int)caps.MaxRecordings : 0);
    }

    public async Task<List<OnvifEdgeRecordingConfiguration>> GetRecordingsAsync()
    {
      if (_client == null) return [];
      var resp = await _client.GetRecordingsAsync(new GetRecordingsRequest());
      return (resp?.RecordingItem ?? [])
        .Select(item => ToConfiguration(item.RecordingToken ?? string.Empty, item.Configuration))
        .ToList();
    }

    public async Task<List<OnvifEdgeRecordingJob>> GetRecordingJobsAsync()
    {
      if (_client == null) return [];
      var resp = await _client.GetRecordingJobsAsync(new GetRecordingJobsRequest());
      return (resp?.JobItem ?? [])
        .Select(item => new OnvifEdgeRecordingJob(
          item.JobToken ?? string.Empty,
          item.JobConfiguration?.RecordingToken ?? string.Empty,
          item.JobConfiguration?.Mode ?? string.Empty,
          item.JobConfiguration?.Source?.FirstOrDefault()?.SourceToken?.Token ?? string.Empty))
        .ToList();
    }

    public async Task<OnvifEdgeRecordingConfiguration?> GetRecordingConfigurationAsync(string recordingToken)
    {
      if (_client == null) return null;
      var resp = await _client.GetRecordingConfigurationAsync(
        new GetRecordingConfigurationRequest { RecordingToken = recordingToken });
      return resp?.RecordingConfiguration == null
        ? null
        : ToConfiguration(recordingToken, resp.RecordingConfiguration);
    }

    /// <summary>
    /// Updates only <c>Content</c> and <c>MaximumRetentionTime</c>; the rest of the configuration is
    /// read back and echoed so a partial write cannot wipe the device's source description.
    /// </summary>
    public async Task SetRecordingConfigurationAsync(string recordingToken, string content, string maximumRetentionTime)
    {
      if (_client == null)
        throw new InvalidOperationException("Recording client not initialized");

      var current = await _client.GetRecordingConfigurationAsync(
        new GetRecordingConfigurationRequest { RecordingToken = recordingToken });
      var config = current?.RecordingConfiguration
        ?? throw new InvalidOperationException($"Camera has no recording configuration for '{recordingToken}'");

      config.Content = content;
      config.MaximumRetentionTime = maximumRetentionTime;

      await _client.SetRecordingConfigurationAsync(new SetRecordingConfigurationRequest
      {
        RecordingToken = recordingToken,
        RecordingConfiguration = config,
      });
    }

    /// <summary>Mode is <c>Active</c> (start) or <c>Idle</c> (stop).</summary>
    public async Task SetRecordingJobModeAsync(string jobToken, string mode)
    {
      if (_client == null)
        throw new InvalidOperationException("Recording client not initialized");
      await _client.SetRecordingJobModeAsync(new SetRecordingJobModeRequest
      {
        JobToken = jobToken,
        Mode = mode,
      });
    }

    public async Task DeleteRecordingAsync(string recordingToken)
    {
      if (_client == null)
        throw new InvalidOperationException("Recording client not initialized");
      await _client.DeleteRecordingAsync(new DeleteRecordingRequest { RecordingToken = recordingToken });
    }

    public async Task DeleteTrackAsync(string recordingToken, string trackToken)
    {
      if (_client == null)
        throw new InvalidOperationException("Recording client not initialized");
      await _client.DeleteTrackAsync(new DeleteTrackRequest
      {
        RecordingToken = recordingToken,
        TrackToken = trackToken,
      });
    }

    public async Task DeleteRecordingJobAsync(string jobToken)
    {
      if (_client == null)
        throw new InvalidOperationException("Recording client not initialized");
      await _client.DeleteRecordingJobAsync(new DeleteRecordingJobRequest { JobToken = jobToken });
    }

    public async Task<OnvifRecordingOptions?> GetRecordingOptionsAsync(string recordingToken)
    {
      if (_client == null) return null;
      var resp = await _client.GetRecordingOptionsAsync(new GetRecordingOptionsRequest { RecordingToken = recordingToken });
      var o = resp?.Options;
      if (o == null) return null;
      return new OnvifRecordingOptions(
        o.Job is { SpareSpecified: true } job ? job.Spare : null,
        (o.Job?.CompatibleSources ?? []).ToList(),
        o.Track is { SpareTotalSpecified: true } t1 ? t1.SpareTotal : null,
        o.Track is { SpareVideoSpecified: true } t2 ? t2.SpareVideo : null,
        o.Track is { SpareAudioSpecified: true } t3 ? t3.SpareAudio : null,
        o.Track is { SpareMetadataSpecified: true } t4 ? t4.SpareMetadata : null);
    }

    /// <summary>Creates an empty recording and returns its token.</summary>
    /// <remarks>
    /// Only cameras whose capabilities report dynamic recordings accept this; most edge cameras
    /// have a fixed set. The source fields are required strings in the schema, so absent ones go
    /// out empty rather than missing.
    /// </remarks>
    /// <param name="maximumRetentionTime">ISO 8601 duration; "PT0S" keeps data until space runs out.</param>
    /// <param name="sourceName">Name of the source the recording describes.</param>
    /// <param name="content">Free-text description of the content.</param>
    /// <param name="sourceId">Source identifier, usually a URI.</param>
    /// <param name="description">Source description.</param>
    /// <param name="location">Source location.</param>
    /// <param name="address">Source address, usually a URI.</param>
    public async Task<string> CreateRecordingAsync(
      string sourceName, string content, string maximumRetentionTime,
      string? sourceId = null, string? description = null, string? location = null, string? address = null)
    {
      if (_client == null)
        throw new InvalidOperationException("Recording client not initialized");
      var resp = await _client.CreateRecordingAsync(new CreateRecordingRequest
      {
        RecordingConfiguration = new RecordingConfiguration
        {
          Source = new RecordingSourceInformation
          {
            SourceId = sourceId ?? string.Empty,
            Name = sourceName,
            Location = location ?? string.Empty,
            Description = description ?? string.Empty,
            Address = address ?? string.Empty,
          },
          Content = content,
          MaximumRetentionTime = maximumRetentionTime,
        },
      });
      return resp?.RecordingToken ?? string.Empty;
    }

    /// <param name="trackType">"Video", "Audio", "Metadata" or "Extended".</param>
    /// <remarks>Requires a camera that reports dynamic tracks.</remarks>
    /// <param name="recordingToken">The recording to add the track to.</param>
    /// <param name="description">Free-text track description.</param>
    public async Task<string> CreateTrackAsync(string recordingToken, string trackType, string description)
    {
      if (_client == null)
        throw new InvalidOperationException("Recording client not initialized");
      var type = Enum.TryParse<TrackType>(trackType, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
        ? parsed
        : throw new ArgumentException($"'{trackType}' is not a track type; expected Video, Audio, Metadata or Extended", nameof(trackType));
      var resp = await _client.CreateTrackAsync(new CreateTrackRequest
      {
        RecordingToken = recordingToken,
        TrackConfiguration = new TrackConfiguration { TrackType = type, Description = description },
      });
      return resp?.TrackToken ?? string.Empty;
    }

    /// <summary>Creates a job that records <paramref name="sourceToken"/> into a recording.</summary>
    /// <param name="mode">"Active" starts writing immediately; "Idle" creates it stopped.</param>
    /// <param name="sourceType">Defaults to a media profile; see <see cref="SourceTypeProfile"/>.</param>
    /// <param name="tracks">Null lets the camera map source tracks to recording tracks itself.</param>
    /// <returns>The job token and the configuration as the camera actually stored it.</returns>
    /// <param name="recordingToken">The recording the job writes into.</param>
    /// <param name="priority">Higher wins when jobs compete for the same recording.</param>
    /// <param name="sourceToken">What to record — a media profile token by default.</param>
    public async Task<(string JobToken, OnvifRecordingJobConfiguration Configuration)> CreateRecordingJobAsync(
      string recordingToken, string mode, int priority, string sourceToken,
      string sourceType = SourceTypeProfile, IReadOnlyList<OnvifRecordingJobTrack>? tracks = null)
    {
      if (_client == null)
        throw new InvalidOperationException("Recording client not initialized");
      var config = ToProxy(new OnvifRecordingJobConfiguration(recordingToken, mode, priority,
        [new OnvifRecordingJobSource(sourceToken, sourceType, null, tracks ?? [])]));
      var resp = await _client.CreateRecordingJobAsync(new CreateRecordingJobRequest { JobConfiguration = config });
      return (resp?.JobToken ?? string.Empty, ToJobConfiguration(resp?.JobConfiguration ?? config));
    }

    public async Task<OnvifRecordingJobConfiguration?> GetRecordingJobConfigurationAsync(string jobToken)
    {
      if (_client == null) return null;
      var resp = await _client.GetRecordingJobConfigurationAsync(new GetRecordingJobConfigurationRequest { JobToken = jobToken });
      return resp?.JobConfiguration == null ? null : ToJobConfiguration(resp.JobConfiguration);
    }

    /// <summary>Replaces a job's mode, priority, sources and schedule; returns what the camera stored.</summary>
    /// <remarks>
    /// The current configuration is read first and only those members replaced, so an event
    /// filter or vendor extension on the job survives the write.
    /// </remarks>
    public async Task<OnvifRecordingJobConfiguration> SetRecordingJobConfigurationAsync(string jobToken, OnvifRecordingJobConfiguration configuration)
    {
      if (_client == null)
        throw new InvalidOperationException("Recording client not initialized");
      var current = (await _client.GetRecordingJobConfigurationAsync(new GetRecordingJobConfigurationRequest { JobToken = jobToken }))?.JobConfiguration
        ?? throw new InvalidOperationException($"Camera has no recording job '{jobToken}'");
      var update = ToProxy(configuration);
      current.RecordingToken = update.RecordingToken;
      current.Mode = update.Mode;
      current.Priority = update.Priority;
      current.Source = update.Source;
      current.ScheduleToken = update.ScheduleToken;
      var resp = await _client.SetRecordingJobConfigurationAsync(new SetRecordingJobConfigurationRequest
      {
        JobToken = jobToken,
        JobConfiguration = current,
      });
      return ToJobConfiguration(resp?.JobConfiguration ?? current);
    }

    public async Task<OnvifRecordingJobState?> GetRecordingJobStateAsync(string jobToken)
    {
      if (_client == null) return null;
      var resp = await _client.GetRecordingJobStateAsync(new GetRecordingJobStateRequest { JobToken = jobToken });
      var s = resp?.State;
      if (s == null) return null;
      return new OnvifRecordingJobState(
        s.State ?? string.Empty,
        s.RecordingToken ?? string.Empty,
        (s.Sources ?? []).Select(src => new OnvifRecordingJobSourceState(
          src.SourceToken?.Token ?? string.Empty,
          src.State ?? string.Empty,
          (src.Tracks?.Track ?? []).Select(t => new OnvifRecordingJobTrackState(
            t.SourceTag ?? string.Empty,
            t.Destination ?? string.Empty,
            t.State ?? string.Empty,
            string.IsNullOrEmpty(t.Error) ? null : t.Error)).ToList())).ToList());
    }

    private static OnvifRecordingJobConfiguration ToJobConfiguration(RecordingJobConfiguration c) => new(
      c.RecordingToken ?? string.Empty,
      c.Mode ?? string.Empty,
      c.Priority,
      (c.Source ?? []).Select(src => new OnvifRecordingJobSource(
        src.SourceToken?.Token ?? string.Empty,
        src.SourceToken?.Type,
        src.AutoCreateReceiverSpecified ? src.AutoCreateReceiver : null,
        (src.Tracks ?? []).Select(t => new OnvifRecordingJobTrack(t.SourceTag ?? string.Empty, t.Destination ?? string.Empty)).ToList())).ToList(),
      string.IsNullOrEmpty(c.ScheduleToken) ? null : c.ScheduleToken);

    private static RecordingJobConfiguration ToProxy(OnvifRecordingJobConfiguration c) => new()
    {
      RecordingToken = c.RecordingToken,
      Mode = c.Mode,
      Priority = c.Priority,
      ScheduleToken = c.ScheduleToken,
      Source = c.Sources.Select(src => new RecordingJobSource
      {
        SourceToken = new SourceReference { Token = src.SourceToken, Type = src.SourceType },
        AutoCreateReceiver = src.AutoCreateReceiver ?? false,
        AutoCreateReceiverSpecified = src.AutoCreateReceiver.HasValue,
        // An empty list goes out as no element at all: the camera then maps tracks itself.
        Tracks = src.Tracks.Count == 0 ? null : src.Tracks.Select(t => new RecordingJobTrack { SourceTag = t.SourceTag, Destination = t.Destination }).ToArray(),
      }).ToArray(),
    };

    private static OnvifEdgeRecordingConfiguration ToConfiguration(string recordingToken, RecordingConfiguration? config) => new(
      recordingToken,
      config?.Source?.SourceId ?? string.Empty,
      config?.Source?.Name ?? string.Empty,
      config?.Source?.Location ?? string.Empty,
      config?.Source?.Description ?? string.Empty,
      config?.Source?.Address ?? string.Empty,
      config?.Content ?? string.Empty,
      config?.MaximumRetentionTime ?? string.Empty);

    public override void Dispose()
    {
      try { _client?.Close(); } catch { }
      base.Dispose();
    }
  }
}
