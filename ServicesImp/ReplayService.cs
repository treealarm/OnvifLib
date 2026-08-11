using ReplayServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  /// <summary>
  /// ONVIF Profile G Replay — resolves the RTSP URI that plays back the camera's own recording.
  /// </summary>
  public class ReplayService : OnvifServiceBase, IOnvifServiceFactory<ReplayService>
  {
    public const string WSDL_V10 = "http://www.onvif.org/ver10/replay/wsdl";

    private ReplayPortClient? _client;

    protected ReplayService(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    public static string[] GetSupportedWsdls() => new[] { WSDL_V10 };

    public static async Task<ReplayService?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      var instance = new ReplayService(url, binding, username, password, profile, tokenFactory, logger);
      await instance.InitializeAsync();
      return instance;
    }

    protected override async Task InitializeAsync()
    {
      await base.InitializeAsync();
      _client = _onvifClientFactory.CreateClient<ReplayPortClient, ReplayPort>(
        new EndpointAddress(_url), _binding, _username, _password);
      await _client.OpenAsync();
    }

    public async Task<OnvifReplayCapabilities?> GetServiceCapabilitiesAsync()
    {
      if (_client == null) return null;
      var resp = await _client.GetServiceCapabilitiesAsync(new GetServiceCapabilitiesRequest());
      var caps = resp?.Capabilities;
      if (caps == null) return null;

      var range = caps.SessionTimeoutRange ?? [];
      return new OnvifReplayCapabilities(
        caps.ReversePlaybackSpecified && caps.ReversePlayback,
        range.Length > 0 ? (int)range[0] : 0,
        range.Length > 1 ? (int)range[1] : 0,
        caps.RTP_RTSP_TCPSpecified && caps.RTP_RTSP_TCP);
    }

    /// <summary>
    /// Many devices hand out single-use replay URIs, so resolve one immediately before starting a
    /// pull rather than caching it.
    /// </summary>
    public async Task<string> GetReplayUriAsync(string recordingToken, string transport = "RTSP")
    {
      if (_client == null) return string.Empty;

      var protocol = transport.ToUpperInvariant() switch
      {
        "HTTP" => TransportProtocol.HTTP,
        "TCP" => TransportProtocol.TCP,
        "UDP" => TransportProtocol.UDP,
        _ => TransportProtocol.RTSP,
      };

      var resp = await _client.GetReplayUriAsync(new GetReplayUriRequest
      {
        RecordingToken = recordingToken,
        StreamSetup = new StreamSetup
        {
          Stream = StreamType.RTPUnicast,
          Transport = new Transport { Protocol = protocol },
        },
      });
      return resp?.Uri ?? string.Empty;
    }

    public override void Dispose()
    {
      try { _client?.Close(); } catch { }
      base.Dispose();
    }
  }
}
