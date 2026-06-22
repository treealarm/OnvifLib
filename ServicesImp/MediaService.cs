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

  public record OnvifCapabilities(bool HasPtz, bool HasImaging, bool HasEvents);

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
