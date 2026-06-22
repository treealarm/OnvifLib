using ImagingServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  public class ImagingService2 : OnvifServiceBase, IOnvifServiceFactory<ImagingService2>
  {
    public const string WSDL_V20 = "http://www.onvif.org/ver20/imaging/wsdl";
    private ImagingPortClient? _imagingClient;

    protected ImagingService2(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    public static string[] GetSupportedWsdls()
    {
      return new[] { WSDL_V20 };
    }

    public static async Task<ImagingService2?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      var instance = new ImagingService2(url, binding, username, password, profile, tokenFactory, logger);
      await instance.InitializeAsync();
      return instance;
    }

    protected async override Task InitializeAsync()
    {
      await base.InitializeAsync();
      _imagingClient = _onvifClientFactory.CreateClient<ImagingPortClient, ImagingPort>(
        new EndpointAddress(_url),
        _binding,
        _username,
        _password);
      await _imagingClient.OpenAsync();
    }

    public async Task<OnvifImagingSettings?> GetImagingSettingsAsync(string videoSourceToken)
    {
      if (_imagingClient == null) return null;
      var resp = await _imagingClient.GetImagingSettingsAsync(
        new GetImagingSettingsRequest { VideoSourceToken = videoSourceToken });
      var s = resp.ImagingSettings;
      if (s == null) return null;
      return new OnvifImagingSettings(
        s.BrightnessSpecified ? s.Brightness : null,
        s.ContrastSpecified ? s.Contrast : null,
        s.ColorSaturationSpecified ? s.ColorSaturation : null,
        s.SharpnessSpecified ? s.Sharpness : null);
    }

    public async Task SetImagingSettingsAsync(string videoSourceToken, OnvifImagingSettings settings)
    {
      if (_imagingClient == null) return;

      // Fetch existing to preserve unmanaged fields (Exposure/Focus/IrCutFilter/WideDynamicRange/WhiteBalance/BacklightCompensation).
      // If the camera doesn't return its current settings, refuse rather than push a blank
      // ImagingSettings20 — some cameras treat absent optional fields in a Set request as
      // "reset to default" rather than "leave unchanged", which would silently clobber settings
      // the caller never asked to change.
      var current = await _imagingClient.GetImagingSettingsAsync(
        new GetImagingSettingsRequest { VideoSourceToken = videoSourceToken });
      var existing = current.ImagingSettings
        ?? throw new InvalidOperationException("Camera did not return current imaging settings; refusing to apply a partial update");

      if (settings.Brightness.HasValue)
      {
        existing.Brightness = settings.Brightness.Value;
        existing.BrightnessSpecified = true;
      }
      if (settings.Contrast.HasValue)
      {
        existing.Contrast = settings.Contrast.Value;
        existing.ContrastSpecified = true;
      }
      if (settings.ColorSaturation.HasValue)
      {
        existing.ColorSaturation = settings.ColorSaturation.Value;
        existing.ColorSaturationSpecified = true;
      }
      if (settings.Sharpness.HasValue)
      {
        existing.Sharpness = settings.Sharpness.Value;
        existing.SharpnessSpecified = true;
      }

      await _imagingClient.SetImagingSettingsAsync(new SetImagingSettingsRequest
      {
        VideoSourceToken = videoSourceToken,
        ImagingSettings = existing,
      });
    }

    public async Task<OnvifImagingOptions?> GetOptionsAsync(string videoSourceToken)
    {
      if (_imagingClient == null) return null;
      var resp = await _imagingClient.GetOptionsAsync(
        new GetOptionsRequest { VideoSourceToken = videoSourceToken });
      var o = resp.ImagingOptions;
      if (o == null) return null;
      return new OnvifImagingOptions(
        o.Brightness != null ? new OnvifFloatRange(o.Brightness.Min, o.Brightness.Max) : null,
        o.Contrast != null ? new OnvifFloatRange(o.Contrast.Min, o.Contrast.Max) : null,
        o.ColorSaturation != null ? new OnvifFloatRange(o.ColorSaturation.Min, o.ColorSaturation.Max) : null,
        o.Sharpness != null ? new OnvifFloatRange(o.Sharpness.Min, o.Sharpness.Max) : null);
    }

    public override void Dispose()
    {
      try { _imagingClient?.Close(); } catch { }
      base.Dispose();
    }
  }

  public record OnvifImagingSettings(float? Brightness, float? Contrast, float? ColorSaturation, float? Sharpness);
  public record OnvifFloatRange(float Min, float Max);
  public record OnvifImagingOptions(OnvifFloatRange? Brightness, OnvifFloatRange? Contrast, OnvifFloatRange? ColorSaturation, OnvifFloatRange? Sharpness);
}
