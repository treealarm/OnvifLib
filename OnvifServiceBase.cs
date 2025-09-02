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
    protected OnvifServiceBase(string url, CustomBinding binding, string username, string password, string profile)
    {
      _url = url;
      _binding = binding;
      _username = username;
      _password = password;
      _profile = profile;
      _onvifClientFactory = new OnvifClientFactory();
    }
    protected virtual async Task InitializeAsync()
    {
      try
      {
        System.DateTime deviceTime = await GetDeviceTimeAsync();

        if (!string.IsNullOrEmpty(_username))
        {
          byte[] nonceBytes = new byte[20];
          var random = new Random();
          random.NextBytes(nonceBytes);

          var token = new SecurityToken(deviceTime, nonceBytes);

          _onvifClientFactory.SetSecurityToken(token);
        }
      }
      catch (Exception ex)
      {
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
