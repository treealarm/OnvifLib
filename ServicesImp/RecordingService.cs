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
