using System.ServiceModel.Channels;
using System.ServiceModel;
using MediaServiceReference1;

namespace OnvifLib
{
  public class MediaService1 : MediaService
  {
    private MediaClient? _mediaClient1;
    private List<Profile> _profiles = new();

    public MediaService1(
      string url, 
      CustomBinding binding, 
      string username, 
      string password, 
      string profile) 
      : base(url, binding,username,password,profile)
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

      foreach (var profile in _profiles)
      {
        var streamUriRequest = new StreamSetup
        {
          Transport  = new Transport()
          {
            Protocol = TransportProtocol.UDP
          },

          Stream = MediaServiceReference1.StreamType.RTPUnicast
        };

        var streamResponse = await _mediaClient1.GetStreamUriAsync(streamUriRequest, profile.token);
        Console.WriteLine($"Profile: {profile.Name}, RTSP URL: {streamResponse.Uri}");
      }
    }
    public override List<string> GetProfiles()
    {
      return _profiles.Select(p => p.token).ToList();
    }
    public override async Task<ImageResult?> GetImage()
    {
      var profile = _profiles.FirstOrDefault();
      if (profile == null || _mediaClient1 == null)
        return null;

      var snapShotUriResponse = await _mediaClient1.GetSnapshotUriAsync(profile.token);
      var data = await DownloadImageAsync(snapShotUriResponse.Uri, _username, _password);
       // e.g. "jpg", "png", etc.
      return data;
    }
  }
}
