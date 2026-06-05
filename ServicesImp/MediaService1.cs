using System.ServiceModel.Channels;
using System.ServiceModel;
using MediaServiceReference1;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace OnvifLib
{
  public class MediaService1 : MediaService
  {
    private MediaClient? _mediaClient1;
    private List<Profile> _profiles = new();
    private readonly Dictionary<string, string> _streamUriCache = new();

    public MediaService1(
      string url,
      CustomBinding binding,
      string username,
      string password,
      string profile,
      Func<SecurityToken>? tokenFactory = null,
      IOnvifLogger? logger = null)
      : base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    protected override async Task InitializeAsync()
    {
      await base.InitializeAsync();

      _mediaClient1 = _onvifClientFactory.CreateClient<MediaClient, MediaServiceReference1.Media>(
        new EndpointAddress(_url),
        _binding,
        _username,
        _password);
      await _mediaClient1.OpenAsync();

      var profilesResponse = await _mediaClient1.GetProfilesAsync();

      _profiles = profilesResponse.Profiles.ToList();
    }
    public override async Task<string> GetStreamUri(string profile_token)
    {
      if (_streamUriCache.TryGetValue(profile_token, out var cached))
        return cached;

      if (_mediaClient1 == null)
        return string.Empty;

      var profile = _profiles.FirstOrDefault(p => p.token == profile_token);
      if (profile == null)
        return string.Empty;

      var streamUriRequest = new StreamSetup
      {
        Transport = new Transport { Protocol = TransportProtocol.RTSP },
        Stream = MediaServiceReference1.StreamType.RTPUnicast
      };
      var streamResponse = await _mediaClient1.GetStreamUriAsync(streamUriRequest, profile.token);
      var uri = streamResponse.Uri ?? string.Empty;
      if (!string.IsNullOrEmpty(uri))
        _streamUriCache[profile_token] = uri;
      return uri;
    }
    public override List<OnvifProfileInfo> GetProfiles()
    {
      return _profiles.Select(p => new OnvifProfileInfo(
        Token: p.token,
        Name: p.Name ?? p.token,
        Width: p.VideoEncoderConfiguration?.Resolution?.Width ?? 0,
        Height: p.VideoEncoderConfiguration?.Resolution?.Height ?? 0,
        Encoding: p.VideoEncoderConfiguration?.Encoding.ToString() ?? string.Empty
      )).ToList();
    }
    public override async Task<ImageResult?> GetImage()
    {
      var profile = _profiles.FirstOrDefault();
      if (profile == null || _mediaClient1 == null)
        return null;

      try
      {
        var snapShotUriResponse = await _mediaClient1.GetSnapshotUriAsync(profile.token);
        var data = await DownloadImageAsync(snapShotUriResponse.Uri, _username, _password, _logger);
        return data;
      }
      catch (Exception ex)
      {
        _logger?.Error($"ONVIF GetImage (media1) failed for {_url}: {ex}");
        return null;
      }
    }
  }
}
