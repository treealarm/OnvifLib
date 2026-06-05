using MediaServiceReference;
using System.ServiceModel.Channels;
using System.ServiceModel;
using MediaServiceReference1;
using System;
using PtzServiceReference;

namespace OnvifLib
{
  public class MediaService2 : MediaService
  {
    private Media2Client? _mediaClient2;
    private List<MediaProfile> _profiles = new();
    private readonly Dictionary<string, string> _streamUriCache = new();

    public MediaService2(
      string url,
      CustomBinding binding,
      string username,
      string password,
      string profile,
      Func<SecurityToken>? tokenFactory = null,
      IOnvifLogger? logger = null) : base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    protected override async Task InitializeAsync()
    {
      await base.InitializeAsync();

      _mediaClient2 = _onvifClientFactory.CreateClient<Media2Client, MediaServiceReference.Media2>(
        new EndpointAddress(_url),
        _binding,
        _username,
        _password);
      await _mediaClient2.OpenAsync();

      var request = new MediaServiceReference.GetProfilesRequest { Type = ["All"] };
      var profilesResponse = await _mediaClient2.GetProfilesAsync(request);

      _profiles = profilesResponse.Profiles.ToList();
    }

    public override async Task<string> GetStreamUri(string profile_token)
    {
      if (_streamUriCache.TryGetValue(profile_token, out var cached))
        return cached;

      if (_mediaClient2 == null)
        return string.Empty;

      var profile = _profiles.FirstOrDefault(p => p.token == profile_token);
      if (profile == null)
        return string.Empty;

      var streamUriRequest = new GetStreamUriRequest
      {
        Protocol = MediaServiceReference.StreamType.RTPUnicast.ToString(),
        ProfileToken = profile.token
      };
      var streamResponse = await _mediaClient2.GetStreamUriAsync(streamUriRequest);
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
        Width: p.Configurations?.VideoEncoder?.Resolution?.Width ?? 0,
        Height: p.Configurations?.VideoEncoder?.Resolution?.Height ?? 0,
        Encoding: p.Configurations?.VideoEncoder?.Encoding ?? string.Empty
      )).ToList();
    }
    public override async Task<ImageResult?> GetImage()
    {
      var profile = _profiles.FirstOrDefault();
      if (profile == null || _mediaClient2 == null)
        return null;

      var snapShotUriRequest = new GetSnapshotUriRequest
      {
        ProfileToken = profile.token
      };

      try
      {
        var snapShotUriResponse = await _mediaClient2.GetSnapshotUriAsync(snapShotUriRequest);
        var data = await DownloadImageAsync(snapShotUriResponse.Uri, _username, _password, _logger);
        return data;
      }
      catch (Exception ex)
      {
        _logger?.Error($"ONVIF GetImage (media2) failed for {_url}: {ex}");
        return null;
      }
    }
  }
}
