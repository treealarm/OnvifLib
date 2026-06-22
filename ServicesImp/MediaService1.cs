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
    protected override async Task<string> ResolveStreamUriAsync(string profile_token)
    {
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
      return streamResponse.Uri ?? string.Empty;
    }
    public override List<OnvifProfileInfo> GetProfiles()
    {
      return _profiles.Select(p => new OnvifProfileInfo(
        Token: p.token,
        Name: p.Name ?? p.token,
        Width: p.VideoEncoderConfiguration?.Resolution?.Width ?? 0,
        Height: p.VideoEncoderConfiguration?.Resolution?.Height ?? 0,
        Encoding: p.VideoEncoderConfiguration?.Encoding.ToString() ?? string.Empty,
        VideoSourceToken: p.VideoSourceConfiguration?.token ?? string.Empty
      )).ToList();
    }
    public override async Task<List<OnvifVideoEncoderConfig>> GetVideoEncoderConfigsAsync()
    {
      if (_mediaClient1 == null) return [];
      var resp = await _mediaClient1.GetVideoEncoderConfigurationsAsync();
      return (resp.Configurations ?? []).Select(c => new OnvifVideoEncoderConfig(
        Token:         c.token ?? string.Empty,
        Name:          c.Name  ?? string.Empty,
        Encoding:      c.Encoding.ToString(),
        Width:         c.Resolution?.Width  ?? 0,
        Height:        c.Resolution?.Height ?? 0,
        FrameRateLimit: c.RateControl?.FrameRateLimit ?? 0,
        BitrateLimit:   c.RateControl?.BitrateLimit   ?? 0,
        GovLength:     c.H264?.GovLength ?? 0,
        H264Profile:   c.H264?.H264Profile.ToString() ?? string.Empty,
        Quality:       c.Quality
      )).ToList();
    }

    public override async Task<List<OnvifVideoEncoderOptions>> GetVideoEncoderConfigOptionsAsync(string configToken)
    {
      if (_mediaClient1 == null) return [];
      var opts = await _mediaClient1.GetVideoEncoderConfigurationOptionsAsync(configToken, string.Empty);
      if (opts == null) return [];

      var result = new List<OnvifVideoEncoderOptions>();

      if (opts.H264 != null)
      {
        var h264    = opts.H264;
        var h264v2  = h264 as MediaServiceReference1.H264Options2;
        var resolutions = (h264.ResolutionsAvailable ?? [])
          .Select(r => new OnvifResolutionOption(r.Width, r.Height)).ToList();
        result.Add(new OnvifVideoEncoderOptions(
          Encoding:     "H264",
          Resolutions:  resolutions,
          MinFrameRate: h264.FrameRateRange?.Min ?? 0,
          MaxFrameRate: h264.FrameRateRange?.Max ?? 0,
          MinBitrate:   h264v2?.BitrateRange?.Min ?? 0,
          MaxBitrate:   h264v2?.BitrateRange?.Max ?? 0,
          MinGovLength: h264.GovLengthRange?.Min ?? 0,
          MaxGovLength: h264.GovLengthRange?.Max ?? 0,
          H264Profiles: (h264.H264ProfilesSupported ?? []).Select(p => p.ToString()).ToList()
        ));
      }

      if (opts.JPEG != null)
      {
        var jpeg = opts.JPEG;
        var resolutions = (jpeg.ResolutionsAvailable ?? [])
          .Select(r => new OnvifResolutionOption(r.Width, r.Height)).ToList();
        result.Add(new OnvifVideoEncoderOptions(
          Encoding:     "JPEG",
          Resolutions:  resolutions,
          MinFrameRate: jpeg.FrameRateRange?.Min ?? 0,
          MaxFrameRate: jpeg.FrameRateRange?.Max ?? 0,
          MinBitrate: 0, MaxBitrate: 0,
          MinGovLength: 0, MaxGovLength: 0,
          H264Profiles: []
        ));
      }

      if (opts.MPEG4 != null)
      {
        var mpeg4 = opts.MPEG4;
        var resolutions = (mpeg4.ResolutionsAvailable ?? [])
          .Select(r => new OnvifResolutionOption(r.Width, r.Height)).ToList();
        result.Add(new OnvifVideoEncoderOptions(
          Encoding:     "MPEG4",
          Resolutions:  resolutions,
          MinFrameRate: mpeg4.FrameRateRange?.Min ?? 0,
          MaxFrameRate: mpeg4.FrameRateRange?.Max ?? 0,
          MinBitrate: 0, MaxBitrate: 0,
          MinGovLength: mpeg4.GovLengthRange?.Min ?? 0,
          MaxGovLength: mpeg4.GovLengthRange?.Max ?? 0,
          H264Profiles: []
        ));
      }

      return result;
    }

    public override async Task SetVideoEncoderConfigAsync(OnvifVideoEncoderConfig config)
    {
      if (_mediaClient1 == null) return;

      // Fetch existing to preserve unmanaged fields (multicast, session timeout, etc.)
      var resp = await _mediaClient1.GetVideoEncoderConfigurationsAsync();
      var existing = FindConfigOrThrow(resp.Configurations ?? [], config.Token, c => c.token, "VideoEncoderConfiguration");

      existing.Resolution = new MediaServiceReference1.VideoResolution { Width = config.Width, Height = config.Height };

      if (existing.RateControl != null)
      {
        existing.RateControl.FrameRateLimit = config.FrameRateLimit;
        existing.RateControl.BitrateLimit   = config.BitrateLimit;
      }

      if (existing.H264 != null)
      {
        if (config.GovLength > 0)
          existing.H264.GovLength = config.GovLength;
        if (!string.IsNullOrEmpty(config.H264Profile)
            && Enum.TryParse<MediaServiceReference1.H264Profile>(config.H264Profile, ignoreCase: true, out var profile))
          existing.H264.H264Profile = profile;
      }

      await _mediaClient1.SetVideoEncoderConfigurationAsync(existing, true);
    }

    public override async Task<List<OnvifAudioEncoderConfig>> GetAudioEncoderConfigsAsync()
    {
      if (_mediaClient1 == null) return [];
      var resp = await _mediaClient1.GetAudioEncoderConfigurationsAsync();
      return (resp.Configurations ?? []).Select(c => new OnvifAudioEncoderConfig(
        Token:      c.token      ?? string.Empty,
        Name:       c.Name       ?? string.Empty,
        Encoding:   c.Encoding.ToString(),
        Bitrate:    c.Bitrate,
        SampleRate: c.SampleRate
      )).ToList();
    }

    public override async Task<List<OnvifAudioEncoderOption>> GetAudioEncoderConfigOptionsAsync(string configToken)
    {
      if (_mediaClient1 == null) return [];
      var opts = await _mediaClient1.GetAudioEncoderConfigurationOptionsAsync(configToken, string.Empty);
      if (opts?.Options == null) return [];
      return opts.Options.Select(o => new OnvifAudioEncoderOption(
        Encoding:    o.Encoding.ToString(),
        Bitrates:    (o.BitrateList    ?? []).ToList(),
        SampleRates: (o.SampleRateList ?? []).ToList()
      )).ToList();
    }

    public override async Task SetAudioEncoderConfigAsync(OnvifAudioEncoderConfig config)
    {
      if (_mediaClient1 == null) return;
      var resp = await _mediaClient1.GetAudioEncoderConfigurationsAsync();
      var existing = FindConfigOrThrow(resp.Configurations ?? [], config.Token, c => c.token, "AudioEncoderConfiguration");
      if (Enum.TryParse<MediaServiceReference1.AudioEncoding>(config.Encoding, ignoreCase: true, out var enc))
        existing.Encoding = enc;
      existing.Bitrate    = config.Bitrate;
      existing.SampleRate = config.SampleRate;
      await _mediaClient1.SetAudioEncoderConfigurationAsync(existing, true);
    }

    protected override async Task<string?> GetSnapshotUriAsync()
    {
      var profile = _profiles.FirstOrDefault();
      if (profile == null || _mediaClient1 == null)
        return null;

      var snapShotUriResponse = await _mediaClient1.GetSnapshotUriAsync(profile.token);
      return snapShotUriResponse.Uri;
    }
  }
}
