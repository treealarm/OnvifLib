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

    public MediaService2(
      string url, 
      CustomBinding binding, 
      string username, 
      string password, 
      string profile) : base(url, binding, username, password, profile)
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
      if (_mediaClient2 == null)
      {
        return string.Empty;
      }
      var profile = _profiles.Where(p => p.token == profile_token).FirstOrDefault();
      if (profile == null)
      {
        return string.Empty;
      }
      var streamUriRequest = new GetStreamUriRequest
      {
        Protocol = MediaServiceReference.StreamType.RTPUnicast.ToString(),
        ProfileToken = profile.token
      };
      var streamResponse = await _mediaClient2.GetStreamUriAsync(streamUriRequest);
      return streamResponse.Uri;
    }
    public override List<string> GetProfiles()
    {
      return _profiles.Select(p=>p.token).ToList();
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

      var snapShotUriResponse = await _mediaClient2.GetSnapshotUriAsync(snapShotUriRequest);
      var data = await DownloadImageAsync(snapShotUriResponse.Uri, _username, _password);
      return data;
    }
  }
}
