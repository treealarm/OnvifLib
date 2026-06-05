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
    string Encoding
  );

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
    public virtual List<OnvifProfileInfo> GetProfiles()
    {
      return [];
    }
    public virtual async Task<string> GetStreamUri(string profile_token)
    {
      await Task.CompletedTask;
      return string.Empty;
    }
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
    public virtual async Task<ImageResult?> GetImage()
    {
      await Task.CompletedTask;
      return null;
    }
  }
}
