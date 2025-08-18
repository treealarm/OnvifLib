using System.ServiceModel.Channels;
using System.ServiceModel;
using MediaServiceReference1;
using EventServiceReference1;

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
      var endpoint = new EndpointAddress(_url);
      var clientInspector = new CustomMessageInspector();
      var behavior = new CustomEndpointBehavior(clientInspector);

      _mediaClient1 = _onvifClientFactory.
          CreateClient<MediaClient, MediaServiceReference1.Media>(
          endpoint,
          _binding,
          _username,
          _password);

      //_mediaClient1 = new MediaClient(_binding, endpoint);

      //_mediaClient1.ClientCredentials.UserName.UserName = _username;
      //_mediaClient1.ClientCredentials.UserName.Password = _password;
      //_mediaClient1.ClientCredentials.HttpDigest.ClientCredential.UserName = _username;
      //_mediaClient1.ClientCredentials.HttpDigest.ClientCredential.Password = _password;
      //_mediaClient1.Endpoint.EndpointBehaviors.Add(behavior);
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
