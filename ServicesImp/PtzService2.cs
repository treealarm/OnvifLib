using PtzServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  public class PtzService2 : OnvifServiceBase, IOnvifServiceFactory<PtzService2>
  {
    public const string WSDL_V20 = "http://www.onvif.org/ver20/ptz/wsdl";
    private PTZClient? _ptzClient;
    private Capabilities? _caps;
    protected PtzService2(string url, CustomBinding binding, string username, string password, string profile) :
      base(url, binding, username, password, profile)
    {
    }

    public static string[] GetSupportedWsdls()
    {
      return new[] { WSDL_V20 };
    }
    public static async Task<PtzService2?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile)
    {
      var instance = new PtzService2(url, binding, username, password, profile);
      await instance.InitializeAsync();
      return instance;
    }

    protected async Task InitializeAsync()
    {
      _ptzClient = _onvifClientFactory.CreateClient<PTZClient, PTZ>(
        new EndpointAddress(_url), 
        _binding, 
        _username, 
        _password);
      await _ptzClient.OpenAsync();
      _caps = await _ptzClient.GetServiceCapabilitiesAsync();
    }

    public bool SupportedCaps()
    {
      return true;
    }
    public async Task AbsoluteMoveAsync(string profileToken, float panTiltX, float panTiltY, float zoom = 0f, float speedPanTilt = 0.5f, float speedZoom = 0.5f)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");

      var position = new PTZVector
      {
        PanTilt = new Vector2D { x = panTiltX, y = panTiltY },
        Zoom = new Vector1D { x = zoom }
      };

      var speed = new PTZSpeed
      {
        PanTilt = new Vector2D { x = speedPanTilt, y = speedPanTilt },
        Zoom = new Vector1D { x = speedZoom }
      };

      await _ptzClient.AbsoluteMoveAsync(profileToken, position, speed);
    }

    public async Task ContinuousMoveAsync(
      string profileToken, 
      float panTiltX, 
      float panTiltY, 
      float zoom = 0f, 
      string timeout = "PT1S")
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");

      var velocity = new PTZSpeed
      {
        PanTilt = new Vector2D { x = panTiltX, y = panTiltY },
        Zoom = new Vector1D { x = zoom }
      };

      await _ptzClient.ContinuousMoveAsync(profileToken, velocity, timeout);
    }
    public async Task RelativeMoveAsync(
      string profileToken, 
      float panTiltX, 
      float panTiltY, 
      float zoom = 0f, 
      float speedPanTilt = 0.5f, 
      float speedZoom = 0.5f)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");

      var translation = new PTZVector
      {
        PanTilt = new Vector2D { x = panTiltX, y = panTiltY },
        Zoom = new Vector1D { x = zoom }
      };

      var speed = new PTZSpeed
      {
        PanTilt = new Vector2D { x = speedPanTilt, y = speedPanTilt },
        Zoom = new Vector1D { x = speedZoom }
      };

      await _ptzClient.RelativeMoveAsync(profileToken, translation, speed);
    }


    public async Task StopAsync(string profileToken, bool panTilt = true, bool zoom = true)
    {
      if (_ptzClient == null)
        throw new InvalidOperationException("PTZ client not initialized");

      await _ptzClient.StopAsync(profileToken, panTilt, zoom);
    }

    public override void Dispose()
    {
      try { _ptzClient?.Close(); } catch { }
      base.Dispose();
    }
  }
}
