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

    protected override async Task<string> ResolveStreamUriAsync(string profile_token)
    {
      if (_mediaClient2 == null)
        return string.Empty;

      var profile = _profiles.FirstOrDefault(p => p.token == profile_token);
      if (profile == null)
        return string.Empty;

      var streamUriRequest = new GetStreamUriRequest
      {
        // Media2 GetStreamUri.Protocol is a transport value ("RtspUnicast", "RtspMulticast",
        // "RTSP", "RtspOverHttp") — not the Media1 StreamType enum ("RTPUnicast").
        Protocol = "RtspUnicast",
        ProfileToken = profile.token
      };
      var streamResponse = await _mediaClient2.GetStreamUriAsync(streamUriRequest);
      return streamResponse.Uri ?? string.Empty;
    }
    public override List<OnvifProfileInfo> GetProfiles()
    {
      return _profiles.Select(p => new OnvifProfileInfo(
        Token: p.token,
        Name: p.Name ?? p.token,
        Width: p.Configurations?.VideoEncoder?.Resolution?.Width ?? 0,
        Height: p.Configurations?.VideoEncoder?.Resolution?.Height ?? 0,
        Encoding: p.Configurations?.VideoEncoder?.Encoding ?? string.Empty,
        VideoSourceToken: p.Configurations?.VideoSource?.SourceToken ?? string.Empty
      )).ToList();
    }
    public override async Task<List<OnvifVideoEncoderConfig>> GetVideoEncoderConfigsAsync()
    {
      if (_mediaClient2 == null) return [];
      var resp = await _mediaClient2.GetVideoEncoderConfigurationsAsync(
        new MediaServiceReference.GetVideoEncoderConfigurationsRequest());
      return (resp.Configurations ?? []).Select(c => new OnvifVideoEncoderConfig(
        Token:          c.token         ?? string.Empty,
        Name:           c.Name          ?? string.Empty,
        Encoding:       c.Encoding      ?? string.Empty,
        Width:          c.Resolution?.Width  ?? 0,
        Height:         c.Resolution?.Height ?? 0,
        FrameRateLimit: (int)Math.Round(c.RateControl?.FrameRateLimit ?? 0),
        BitrateLimit:   c.RateControl?.BitrateLimit ?? 0,
        GovLength:      c.GovLengthSpecified ? c.GovLength : 0,
        H264Profile:    c.Profile ?? string.Empty,
        Quality:        c.Quality
      )).ToList();
    }

    public override async Task<List<OnvifVideoEncoderOptions>> GetVideoEncoderConfigOptionsAsync(string configToken)
    {
      if (_mediaClient2 == null) return [];
      var resp = await _mediaClient2.GetVideoEncoderConfigurationOptionsAsync(
        new MediaServiceReference.GetVideoEncoderConfigurationOptionsRequest
        {
          ConfigurationToken = configToken,
        });
      return (resp.Options ?? []).Select(o => {
        var resolutions = (o.ResolutionsAvailable ?? [])
          .Select(r => new OnvifResolutionOption(r.Width, r.Height)).ToList();
        // GovLengthRange is int[] — [min, max]
        var govRange   = o.GovLengthRange ?? [];
        var rates      = o.FrameRatesSupported ?? [];
        return new OnvifVideoEncoderOptions(
          Encoding:     o.Encoding ?? string.Empty,
          Resolutions:  resolutions,
          MinFrameRate: rates.Length > 0 ? (int)Math.Round(rates.Min()) : 0,
          MaxFrameRate: rates.Length > 0 ? (int)Math.Round(rates.Max()) : 0,
          MinBitrate:   o.BitrateRange?.Min ?? 0,
          MaxBitrate:   o.BitrateRange?.Max ?? 0,
          MinGovLength: govRange.Length > 0 ? govRange.Min() : 0,
          MaxGovLength: govRange.Length > 0 ? govRange.Max() : 0,
          H264Profiles: (o.ProfilesSupported ?? []).ToList()
        );
      }).ToList();
    }

    public override async Task SetVideoEncoderConfigAsync(OnvifVideoEncoderConfig config)
    {
      if (_mediaClient2 == null) return;

      // Fetch existing to preserve unmanaged fields
      var resp = await _mediaClient2.GetVideoEncoderConfigurationsAsync(
        new MediaServiceReference.GetVideoEncoderConfigurationsRequest());
      var existing = FindConfigOrThrow(resp.Configurations ?? [], config.Token, c => c.token, "VideoEncoderConfiguration");

      existing.Resolution = new MediaServiceReference.VideoResolution2 { Width = config.Width, Height = config.Height };

      if (existing.RateControl != null)
      {
        existing.RateControl.FrameRateLimit = config.FrameRateLimit;
        existing.RateControl.BitrateLimit   = config.BitrateLimit;
      }

      if (config.GovLength > 0)
      {
        existing.GovLength          = config.GovLength;
        existing.GovLengthSpecified = true;
      }

      if (!string.IsNullOrEmpty(config.H264Profile))
        existing.Profile = config.H264Profile;

      await _mediaClient2.SetVideoEncoderConfigurationAsync(
        new MediaServiceReference.SetVideoEncoderConfigurationRequest { Configuration = existing });
    }

    public override async Task<List<OnvifAudioEncoderConfig>> GetAudioEncoderConfigsAsync()
    {
      if (_mediaClient2 == null) return [];
      var resp = await _mediaClient2.GetAudioEncoderConfigurationsAsync(
        new MediaServiceReference.GetAudioEncoderConfigurationsRequest());
      return (resp.Configurations ?? []).Select(c => new OnvifAudioEncoderConfig(
        Token:      c.token      ?? string.Empty,
        Name:       c.Name       ?? string.Empty,
        Encoding:   c.Encoding   ?? string.Empty,
        Bitrate:    c.Bitrate,
        SampleRate: c.SampleRate
      )).ToList();
    }

    public override async Task<List<OnvifAudioEncoderOption>> GetAudioEncoderConfigOptionsAsync(string configToken)
    {
      if (_mediaClient2 == null) return [];
      var resp = await _mediaClient2.GetAudioEncoderConfigurationOptionsAsync(
        new MediaServiceReference.GetAudioEncoderConfigurationOptionsRequest
        {
          ConfigurationToken = configToken,
        });
      return (resp.Options ?? []).Select(o => new OnvifAudioEncoderOption(
        Encoding:    o.Encoding    ?? string.Empty,
        Bitrates:    (o.BitrateList    ?? []).ToList(),
        SampleRates: (o.SampleRateList ?? []).ToList()
      )).ToList();
    }

    public override async Task SetAudioEncoderConfigAsync(OnvifAudioEncoderConfig config)
    {
      if (_mediaClient2 == null) return;
      var resp = await _mediaClient2.GetAudioEncoderConfigurationsAsync(
        new MediaServiceReference.GetAudioEncoderConfigurationsRequest());
      var existing = FindConfigOrThrow(resp.Configurations ?? [], config.Token, c => c.token, "AudioEncoderConfiguration");
      existing.Encoding   = config.Encoding;
      existing.Bitrate    = config.Bitrate;
      existing.SampleRate = config.SampleRate;
      await _mediaClient2.SetAudioEncoderConfigurationAsync(
        new MediaServiceReference.SetAudioEncoderConfigurationRequest { Configuration = existing });
    }

    protected override async Task<string?> GetSnapshotUriAsync()
    {
      var profile = _profiles.FirstOrDefault();
      if (profile == null || _mediaClient2 == null)
        return null;

      var snapShotUriRequest = new GetSnapshotUriRequest
      {
        ProfileToken = profile.token
      };
      var snapShotUriResponse = await _mediaClient2.GetSnapshotUriAsync(snapShotUriRequest);
      return snapShotUriResponse.Uri;
    }

    public override async Task RefreshProfilesAsync()
    {
      if (_mediaClient2 == null) return;
      var request = new MediaServiceReference.GetProfilesRequest { Type = ["All"] };
      var profilesResponse = await _mediaClient2.GetProfilesAsync(request);
      _profiles = profilesResponse.Profiles.ToList();
    }

    // ---- Metadata / analytics configurations -------------------------------------------------
    // Unlike ver10, Media2 has no per-kind Add/Remove pair. Configurations are attached to and
    // detached from a profile through the generic AddConfiguration/RemoveConfiguration, with the
    // kind named in ConfigurationRef.Type.

    private const string ConfigTypeMetadata = "Metadata";
    private const string ConfigTypeAnalytics = "Analytics";

    public override async Task<List<OnvifMetadataConfig>> GetMetadataConfigsAsync()
    {
      if (_mediaClient2 == null) return [];
      // The attachment list below is read off the profiles, so they have to be current.
      await RefreshProfilesAsync();
      var resp = await _mediaClient2.GetMetadataConfigurationsAsync(new MediaServiceReference.GetMetadataConfigurationsRequest());
      return (resp.Configurations ?? [])
        .Where(c => c != null)
        .Select(c => new OnvifMetadataConfig(
          c.token ?? string.Empty,
          c.Name ?? string.Empty,
          c.AnalyticsSpecified && c.Analytics,
          c.PTZStatus != null,
          c.Events != null,
          c.GeoLocationSpecified && c.GeoLocation,
          c.ShapePolygonSpecified && c.ShapePolygon,
          c.SessionTimeout,
          c.CompressionType,
          _profiles
            .Where(p => p.Configurations?.Metadata?.token == c.token)
            .Select(p => p.token)
            .ToList()))
        .ToList();
    }

    public override async Task<OnvifMetadataConfigOptions?> GetMetadataConfigOptionsAsync(string configToken)
    {
      if (_mediaClient2 == null) return null;
      var resp = await _mediaClient2.GetMetadataConfigurationOptionsAsync(
        new MediaServiceReference.GetMetadataConfigurationOptionsRequest { ConfigurationToken = configToken });
      var opts = resp?.Options;
      if (opts == null) return null;
      return new OnvifMetadataConfigOptions(
        opts.PTZStatusFilterOptions != null,
        opts.Extension?.CompressionType ?? []);
    }

    public override async Task SetMetadataConfigAsync(OnvifMetadataConfigUpdate update)
    {
      if (_mediaClient2 == null) return;

      // Read-modify-write for the same reason as the ver10 implementation: an omitted optional
      // field can be read by the camera as "reset to default".
      var resp = await _mediaClient2.GetMetadataConfigurationsAsync(new MediaServiceReference.GetMetadataConfigurationsRequest());
      var existing = FindConfigOrThrow(
        resp.Configurations ?? [], update.Token, c => c.token, "MetadataConfiguration");

      if (update.Analytics.HasValue)
      {
        existing.Analytics = update.Analytics.Value;
        existing.AnalyticsSpecified = true;
      }
      if (update.PtzStatus.HasValue)
      {
        existing.PTZStatus = update.PtzStatus.Value
          ? existing.PTZStatus ?? new MediaServiceReference.PTZFilter { Status = true, Position = true }
          : null;
      }
      if (update.SessionTimeout != null)
        existing.SessionTimeout = update.SessionTimeout;
      if (update.CompressionType != null)
        existing.CompressionType = update.CompressionType;

      await _mediaClient2.SetMetadataConfigurationAsync(
        new MediaServiceReference.SetMetadataConfigurationRequest { Configuration = existing });
    }

    public override async Task<List<OnvifAnalyticsConfig>> GetAnalyticsConfigsAsync()
    {
      if (_mediaClient2 == null) return [];
      await RefreshProfilesAsync();
      var resp = await _mediaClient2.GetAnalyticsConfigurationsAsync(new MediaServiceReference.GetAnalyticsConfigurationsRequest());
      return (resp.Configurations ?? [])
        .Where(c => c != null)
        .Select(c => new OnvifAnalyticsConfig(
          c.token ?? string.Empty,
          c.Name ?? string.Empty,
          _profiles
            .Where(p => p.Configurations?.Analytics?.token == c.token)
            .Select(p => p.token)
            .ToList()))
        .ToList();
    }

    public override Task AttachMetadataConfigAsync(string profileToken, string configToken)
      => AddConfigAsync(profileToken, ConfigTypeMetadata, configToken);

    public override Task DetachMetadataConfigAsync(string profileToken, string configToken)
      => RemoveConfigAsync(profileToken, ConfigTypeMetadata, configToken);

    public override Task AttachAnalyticsConfigAsync(string profileToken, string configToken)
      => AddConfigAsync(profileToken, ConfigTypeAnalytics, configToken);

    public override Task DetachAnalyticsConfigAsync(string profileToken, string configToken)
      => RemoveConfigAsync(profileToken, ConfigTypeAnalytics, configToken);

    private async Task AddConfigAsync(string profileToken, string type, string configToken)
    {
      if (_mediaClient2 == null) return;
      await _mediaClient2.AddConfigurationAsync(new AddConfigurationRequest
      {
        ProfileToken = profileToken,
        Configuration = [new MediaServiceReference.ConfigurationRef { Type = type, Token = configToken }],
      });
    }

    private async Task RemoveConfigAsync(string profileToken, string type, string configToken)
    {
      if (_mediaClient2 == null) return;
      await _mediaClient2.RemoveConfigurationAsync(new RemoveConfigurationRequest
      {
        ProfileToken = profileToken,
        Configuration = [new MediaServiceReference.ConfigurationRef { Type = type, Token = configToken }],
      });
    }
  }
}
