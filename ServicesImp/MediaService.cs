using System.Collections.Concurrent;
using System.Net;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  public record OnvifProfileInfo(
    string Token,
    string Name,
    int Width,
    int Height,
    string Encoding,
    string VideoSourceToken = ""
  );

  public record OnvifVideoEncoderConfig(
    string Token,
    string Name,
    string Encoding,
    int Width,
    int Height,
    int FrameRateLimit,
    int BitrateLimit,
    int GovLength,
    string H264Profile,
    float Quality);

  public record OnvifResolutionOption(int Width, int Height);

  public record OnvifVideoEncoderOptions(
    string Encoding,
    List<OnvifResolutionOption> Resolutions,
    int MinFrameRate,
    int MaxFrameRate,
    int MinBitrate,
    int MaxBitrate,
    int MinGovLength,
    int MaxGovLength,
    List<string> H264Profiles);

  public record OnvifAudioEncoderConfig(
    string Token,
    string Name,
    string Encoding,
    int    Bitrate,
    int    SampleRate);

  public record OnvifAudioEncoderOption(
    string    Encoding,
    List<int> Bitrates,
    List<int> SampleRates);

  public record OnvifDeviceInfo(
    string Manufacturer,
    string Model,
    string FirmwareVersion,
    string SerialNumber,
    string HardwareId);

  public record OnvifCapabilities(bool HasPtz, bool HasImaging, bool HasEvents, bool HasDigitalInputs, bool HasEdgeRecording, bool HasAnalytics);

  /// <summary>
  /// A camera's MetadataConfiguration. <c>Analytics</c> is the flag that decides whether the
  /// metadata track carries Scene Description (object boxes) at all; the rest is reported so the
  /// admin UI can show what else the configuration is set to stream.
  /// </summary>
  public record OnvifMetadataConfig(
    string Token,
    string Name,
    bool Analytics,
    bool PtzStatus,
    bool Events,
    bool GeoLocation,
    bool ShapePolygon,
    string? SessionTimeout,
    string? CompressionType,
    // Profiles this configuration is attached to. A configuration with Analytics=true that is
    // attached to nothing changes no stream: the metadata track only appears in an RTSP session
    // pulled from a profile that carries the configuration.
    IReadOnlyList<string> AttachedProfileTokens);

  /// <summary>
  /// What a camera says it will accept for a MetadataConfiguration. Note there is no "supports
  /// analytics" option in the ONVIF schema — whether Scene Description is available is answered by
  /// the presence of the analytics service, not by these options.
  /// </summary>
  public record OnvifMetadataConfigOptions(
    bool SupportsPtzStatus,
    IReadOnlyList<string> CompressionTypes);

  /// <summary>
  /// Requested changes to a MetadataConfiguration. Null means "leave as is": some cameras treat an
  /// absent optional field in a Set request as "reset to default", so unchanged values are read
  /// back and re-sent rather than omitted (same hazard as SetImagingSettingsAsync).
  /// </summary>
  public record OnvifMetadataConfigUpdate(
    string Token,
    bool? Analytics = null,
    bool? PtzStatus = null,
    string? SessionTimeout = null,
    string? CompressionType = null);

  /// <summary>A VideoAnalyticsConfiguration — the engine whose modules/rules produce the objects.</summary>
  public record OnvifAnalyticsConfig(string Token, string Name, IReadOnlyList<string> AttachedProfileTokens);

  public class ImageResult
  {
    public byte[] Data { get; set; } = [];
    public string? Extension { get; set; } // with or without dot, e.g. "jpeg"
    public string? MimeType { get; set; }
  }
  public class MediaService : OnvifServiceBase, IOnvifServiceFactory<MediaService>
  {
    public const string WSDL_V10 = "http://www.onvif.org/ver10/media/wsdl";
    public const string WSDL_V20 = "http://www.onvif.org/ver20/media/wsdl";

    // Shared between Media v1/v2 — stream URIs don't change for the lifetime of a profile.
    private readonly Dictionary<string, string> _streamUriCache = new();

    public virtual List<OnvifProfileInfo> GetProfiles()
    {
      return [];
    }

    public async Task<string> GetStreamUri(string profile_token)
    {
      if (_streamUriCache.TryGetValue(profile_token, out var cached))
        return cached;

      var uri = await ResolveStreamUriAsync(profile_token);
      if (!string.IsNullOrEmpty(uri))
        _streamUriCache[profile_token] = uri;
      return uri;
    }

    protected virtual async Task<string> ResolveStreamUriAsync(string profile_token)
    {
      await Task.CompletedTask;
      return string.Empty;
    }

    public virtual Task<List<OnvifVideoEncoderConfig>> GetVideoEncoderConfigsAsync()
      => Task.FromResult<List<OnvifVideoEncoderConfig>>([]);
    public virtual Task<List<OnvifVideoEncoderOptions>> GetVideoEncoderConfigOptionsAsync(string configToken)
      => Task.FromResult<List<OnvifVideoEncoderOptions>>([]);
    public virtual Task SetVideoEncoderConfigAsync(OnvifVideoEncoderConfig config)
      => Task.CompletedTask;
    public virtual Task<List<OnvifAudioEncoderConfig>> GetAudioEncoderConfigsAsync()
      => Task.FromResult<List<OnvifAudioEncoderConfig>>([]);
    public virtual Task<List<OnvifAudioEncoderOption>> GetAudioEncoderConfigOptionsAsync(string configToken)
      => Task.FromResult<List<OnvifAudioEncoderOption>>([]);
    public virtual Task SetAudioEncoderConfigAsync(OnvifAudioEncoderConfig config)
      => Task.CompletedTask;

    // ---- Metadata / analytics configurations -------------------------------------------------
    // Enabling the camera's own Scene Description takes two steps that both live here: a
    // MetadataConfiguration with Analytics=true has to exist, and it has to be attached to the
    // media profile the stream is pulled from — only then does the RTSP session carry a metadata
    // track. The VideoAnalyticsConfiguration is what actually runs the detector behind it, and it
    // has to be attached to the same profile.

    /// <summary>
    /// Re-reads the camera's profiles. The attachment lists reported below are derived from them,
    /// and the profile snapshot is otherwise taken once at connect and kept for the lifetime of the
    /// cached Camera — so without this, attaching a configuration would keep reading back as
    /// "not attached" for as long as the connection stayed cached.
    /// </summary>
    public virtual Task RefreshProfilesAsync() => Task.CompletedTask;

    public virtual Task<List<OnvifMetadataConfig>> GetMetadataConfigsAsync()
      => Task.FromResult<List<OnvifMetadataConfig>>([]);
    public virtual Task<OnvifMetadataConfigOptions?> GetMetadataConfigOptionsAsync(string configToken)
      => Task.FromResult<OnvifMetadataConfigOptions?>(null);
    public virtual Task SetMetadataConfigAsync(OnvifMetadataConfigUpdate update)
      => Task.CompletedTask;
    public virtual Task<List<OnvifAnalyticsConfig>> GetAnalyticsConfigsAsync()
      => Task.FromResult<List<OnvifAnalyticsConfig>>([]);
    public virtual Task AttachMetadataConfigAsync(string profileToken, string configToken)
      => Task.CompletedTask;
    public virtual Task DetachMetadataConfigAsync(string profileToken, string configToken)
      => Task.CompletedTask;
    public virtual Task AttachAnalyticsConfigAsync(string profileToken, string configToken)
      => Task.CompletedTask;
    public virtual Task DetachAnalyticsConfigAsync(string profileToken, string configToken)
      => Task.CompletedTask;

    protected static T FindConfigOrThrow<T>(IEnumerable<T> configs, string token, Func<T, string?> tokenSelector, string configTypeName)
      => configs.FirstOrDefault(c => tokenSelector(c) == token)
        ?? throw new InvalidOperationException($"{configTypeName} '{token}' not found on camera");

    public static string? GetExtensionFromMime(string? mime)
    {
      if (string.IsNullOrWhiteSpace(mime))
        return null;

      var parts = mime.Split('/');
      if (parts.Length != 2)
        return null;

      return "." + parts[1]; // e.g. "image/jpeg" → ".jpeg"
    }

    protected MediaService(
      string url,
      CustomBinding binding,
      string username,
      string password,
      string profile,
      Func<SecurityToken>? tokenFactory = null,
      IOnvifLogger? logger = null) : base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }


    public static string[] GetSupportedWsdls()
    {
      return new[] { WSDL_V20, WSDL_V10 };
    }

    public static async Task<MediaService?> CreateAsync(
      string url,
      CustomBinding binding,
      string username,
      string password,
      string profile,
      Func<SecurityToken>? tokenFactory = null,
      IOnvifLogger? logger = null)
    {
      if (profile == WSDL_V10)
      {
        var instance1 = new MediaService1(url, binding, username, password, profile, tokenFactory, logger);
        await instance1.InitializeAsync();
        return instance1;
      }

      var instance = new MediaService2(url, binding, username, password, profile, tokenFactory, logger);
      await instance.InitializeAsync();
      return instance;
    }

    // One pooled HttpClient per credential pair. Recreating it per snapshot leaked sockets
    // (each disposed client left connections in TIME_WAIT); these are reused for the app lifetime.
    private static readonly ConcurrentDictionary<(string User, string Password), HttpClient> _imageClients = new();

    private static HttpClient GetImageClient(string username, string password)
      => _imageClients.GetOrAdd((username, password), static key => new HttpClient(new SocketsHttpHandler
      {
        PreAuthenticate = true,
        Credentials = new NetworkCredential(key.User, key.Password),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
      }));

    public static async Task<ImageResult> DownloadImageAsync(string url, string username, string password, IOnvifLogger? logger = null)
    {
      var client = GetImageClient(username, password);

      var response = await client.GetAsync(url);
      if (!response.IsSuccessStatusCode)
      {
        var body = await response.Content.ReadAsStringAsync();
        logger?.Error($"ONVIF snapshot download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {url}. Body: {body}");
      }
      response.EnsureSuccessStatusCode();

      var mime = response.Content.Headers.ContentType?.MediaType;

      var data = await response.Content.ReadAsByteArrayAsync();
      return new ImageResult()
      { Data = data, Extension = GetExtensionFromMime(mime), MimeType = mime};
    }
    public async Task<ImageResult?> GetImage()
    {
      try
      {
        var snapshotUri = await GetSnapshotUriAsync();
        if (string.IsNullOrEmpty(snapshotUri))
          return null;

        return await DownloadImageAsync(snapshotUri, _username, _password, _logger);
      }
      catch (Exception ex)
      {
        _logger?.Error($"ONVIF GetImage failed for {_url}: {ex}");
        return null;
      }
    }

    protected virtual async Task<string?> GetSnapshotUriAsync()
    {
      await Task.CompletedTask;
      return null;
    }
  }
}
