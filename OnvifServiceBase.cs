using DeviceServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  public class OnvifServiceBase : IDisposable
  {
    protected readonly string _url;
    protected readonly string _username;
    protected readonly string _password;
    protected readonly CustomBinding _binding;
    protected readonly string _profile;
    protected readonly OnvifClientFactory _onvifClientFactory;
    protected readonly IOnvifLogger? _logger;
    private readonly Func<SecurityToken>? _tokenFactory;

    protected OnvifServiceBase(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      _url = url;
      _binding = binding;
      _username = username;
      _password = password;
      _profile = profile;
      _logger = logger;
      _onvifClientFactory = new OnvifClientFactory(logger);
      _tokenFactory = tokenFactory;
    }
    protected virtual async Task InitializeAsync()
    {
      try
      {
        if (string.IsNullOrEmpty(_username))
          return;

        SecurityToken token;
        if (_tokenFactory != null)
        {
          token = _tokenFactory();
        }
        else
        {
          var deviceTime = await GetDeviceTimeAsync();
          var nonce = new byte[20];
          Random.Shared.NextBytes(nonce);
          token = new SecurityToken(deviceTime, nonce);
        }
        _onvifClientFactory.SetSecurityToken(token);
      }
      catch (Exception ex)
      {
        _logger?.Error($"ONVIF service initialization failed for {_url}: {ex}");
      }
    }
    protected async Task<System.DateTime> GetDeviceTimeAsync()
    {
      var deviceClient = _onvifClientFactory.CreateClient<DeviceClient, Device>(
        new EndpointAddress(_url),
        _binding,
        _username,
        _password);
      await deviceClient.OpenAsync();

      var deviceSystemDateTime = await deviceClient.GetSystemDateAndTimeAsync();

      if (deviceSystemDateTime.UTCDateTime == null)
        return System.DateTime.UtcNow;

      return new System.DateTime(
          deviceSystemDateTime.UTCDateTime.Date.Year,
          deviceSystemDateTime.UTCDateTime.Date.Month,
          deviceSystemDateTime.UTCDateTime.Date.Day,
          deviceSystemDateTime.UTCDateTime.Time.Hour,
          deviceSystemDateTime.UTCDateTime.Time.Minute,
          deviceSystemDateTime.UTCDateTime.Time.Second,
          0,
          DateTimeKind.Utc
      );
    }
    public virtual void Dispose() { }
  }
}
