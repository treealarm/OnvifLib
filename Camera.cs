using System.Net;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Security;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using DeviceServiceReference;
using EventServiceReference1;
using OnvifLib;

public class Camera
{
  private readonly string _url;
  private readonly string _username;
  private readonly string _password;
  private readonly int _port;
  private readonly string _ip;

  private readonly CustomBindingProvider _bindingProvider;
  private readonly IOnvifLogger? _logger;
  private readonly OnvifClientFactory _onvifClientFactory;
  private Task<Dictionary<string, string>?>? _servicesTask;
  private System.DateTime _tokenExpiry = System.DateTime.UtcNow;
  private TimeSpan _deviceClockOffset = TimeSpan.Zero;
  public Task InitTask { get; private set; } = Task.CompletedTask;

  private OnvifServiceCache _serviceCache;

  private async Task<DeviceClient> GetDevice()
  {
    var endpoint = new EndpointAddress(_url);    
    if (System.DateTime.UtcNow >= _tokenExpiry)
    {
      _tokenExpiry = System.DateTime.UtcNow.AddMinutes(1);
      _onvifClientFactory.SetSecurityToken(null);
    }

    if (!string.IsNullOrEmpty(_username) &&
      _onvifClientFactory.GetSecurityToken() == null)
    {
      System.DateTime? deviceTime = await GetDeviceTimeAsync();

      var resolvedTime = deviceTime ?? System.DateTime.UtcNow;
      _deviceClockOffset = resolvedTime - System.DateTime.UtcNow;

      var nonce = new byte[20];
      Random.Shared.NextBytes(nonce);

      var token = new SecurityToken(resolvedTime, nonce);

      _onvifClientFactory.SetSecurityToken(token);
    }

    var deviceClient = _onvifClientFactory.
      CreateClient<DeviceClient, DeviceServiceReference.Device>(
        endpoint,
        _bindingProvider.Current,
        _username,
        _password
      );

    await deviceClient.OpenAsync();

    return deviceClient;
  }
  public async Task<System.DateTime?> GetDeviceTimeAsync()
  {
    _onvifClientFactory.SetSecurityToken(null);
    var endpoint = new EndpointAddress(_url);

    using var deviceClient = _onvifClientFactory.
      CreateClient<DeviceClient, DeviceServiceReference.Device>(
        endpoint,
        _bindingProvider.Current,
        _username,
        _password
      );

    var deviceSystemDateTime = await deviceClient.GetSystemDateAndTimeAsync();

    if (deviceSystemDateTime.UTCDateTime == null)
      return null;

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

  public async Task<string> RebootAsync()
  {
    using var device = await GetDevice();
    return await device.SystemRebootAsync();
  }

  public async Task<OnvifDeviceInfo> GetDeviceInformationAsync()
  {
    using var device = await GetDevice();
    var resp = await device.GetDeviceInformationAsync(new DeviceServiceReference.GetDeviceInformationRequest());
    return new OnvifDeviceInfo(
      resp.Manufacturer ?? string.Empty,
      resp.Model ?? string.Empty,
      resp.FirmwareVersion ?? string.Empty,
      resp.SerialNumber ?? string.Empty,
      resp.HardwareId ?? string.Empty);
  }

  public async Task SyncTimeAsync() => await SetTimeAsync(System.DateTime.UtcNow);

  public async Task SetTimeAsync(System.DateTime utcTime)
  {
    using var device = await GetDevice();
    await device.SetSystemDateAndTimeAsync(
      SetDateTimeType.Manual,
      false,
      null,
      new DeviceServiceReference.DateTime
      {
        Date = new Date { Year = utcTime.Year, Month = utcTime.Month, Day = utcTime.Day },
        Time = new Time { Hour = utcTime.Hour, Minute = utcTime.Minute, Second = utcTime.Second },
      });
  }

  public string Url { get { return _url; } }
  public string User { get { return _username; } }
  public string Password { get { return _password; } }
  public string Ip { get { return _ip; } }
  public int Port { get { return _port; } }

  //http://192.168.1.150:8899/onvif/device_service
  public static string CreateUrl(string ip, int port, string? xaddr = null)
  {
    // Prefer the address actually discovered (WS-Discovery XAddr) over the standard
    // template — some devices serve their device_service at a non-standard path.
    return string.IsNullOrEmpty(xaddr) ? $"http://{ip}:{port}/onvif/device_service" : xaddr;
  }

  public static Camera Create(
    string ip, int port, string username, string password,
    double timeout = 15, IOnvifLogger? logger = null, string? xaddr = null)
  {
    var cam = new Camera(ip, port, username, password, timeout, logger, xaddr);
    cam.InitTask = cam.InitAsync(); // start initialization in the background
    return cam;
  }
  private Camera(
    string ip, int port, string username, string password,
    double timeout, IOnvifLogger? logger = null, string? xaddr = null)
  {
    _url = CreateUrl(ip, port, xaddr);
    _ip = ip;
    _username = username;
    _password = password;
    _port = port;
    _logger = logger;

    _onvifClientFactory = new OnvifClientFactory(logger);
    _bindingProvider = new CustomBindingProvider(timeout, cacheKey: _url);
    _serviceCache = new OnvifServiceCache(_bindingProvider, username, password, MakeSecurityToken, logger: logger);
  }

  private SecurityToken MakeSecurityToken()
  {
    var approxDeviceTime = System.DateTime.UtcNow + _deviceClockOffset;
    var nonce = new byte[20];
    Random.Shared.NextBytes(nonce);
    return new SecurityToken(approxDeviceTime, nonce);
  }

  private async Task InitAsync()
  {
    _servicesTask = DoGetServices();
    await _servicesTask;
  }

  public async Task<Dictionary<string, string>?> GetServicesAsync()
  {
    if (_servicesTask == null)
      _servicesTask = DoGetServices();

    var result = await _servicesTask;

    if (result == null)
    {
      // Reset so the next call retries discovery
      _servicesTask = null;
    }

    return result;
  }

  async public Task<MediaService?> GetMediaService()
  {
    var services = await GetServicesAsync();

    if (services != null)
    {
      return await _serviceCache.GetServiceAsync<MediaService>(services);
    }
    return null;
  }

  async public Task<EventService1?> GetEventService()
  {
    var services = await GetServicesAsync();

    if (services != null)
    {
      return await OnvifServiceSelector.TryCreateService<EventService1>(services, _bindingProvider.Current, _username, _password, MakeSecurityToken, _logger);
    }
    return null;
  }

  async public Task<PtzService2?> GetPtzService()
  {
    var services = await GetServicesAsync();

    if (services != null)
    {
      var service = await _serviceCache.GetServiceAsync<PtzService2>(services);
      return service;
    }
    return null;
  }

  async public Task<ImagingService2?> GetImagingService()
  {
    var services = await GetServicesAsync();

    if (services != null)
    {
      return await _serviceCache.GetServiceAsync<ImagingService2>(services);
    }
    return null;
  }

  async public Task<List<OnvifProfileInfo>?> GetProfiles()
  {
    var service = await GetMediaService();
    if (service == null)
      return null;
    return service.GetProfiles();
  }
  public async Task<bool> IsAlive()
  {
    var services = await GetServicesAsync();
    return (services != null && services.Count > 0);
  }

  private OnvifCapabilities? _capabilitiesCache;
  private System.DateTime _capabilitiesCacheAt = System.DateTime.MinValue;
  private static readonly TimeSpan CapabilitiesCacheTtl = TimeSpan.FromMinutes(5);

  // HasImaging/HasEvents are derived purely from namespace presence in the
  // Device.GetServicesAsync(true) (IncludeCapability) dictionary — no extra SOAP call needed,
  // since that's the same information ONVIF's GetCapabilities call would expose (XAddr per
  // category). HasPtz is the exception: namespace presence alone isn't reliable for PTZ (some
  // cameras advertise the PTZ WSDL without any real move hardware behind it), so it costs one
  // extra GetConfigurations round trip — cached below since this method is polled frequently
  // by UI capability checks.
  public async Task<OnvifCapabilities> GetCapabilitiesSummaryAsync()
  {
    if (_capabilitiesCache != null && System.DateTime.UtcNow - _capabilitiesCacheAt < CapabilitiesCacheTtl)
      return _capabilitiesCache;

    var services = await GetServicesAsync();
    if (services == null)
      return new OnvifCapabilities(false, false, false);

    // GetConfigurations (not GetNodes) is the reliable PTZ signal — same reasoning as
    // PtzService2.GetCapabilitiesAsync's own comment.
    var hasPtz = false;
    if (services.ContainsKey(PtzService2.WSDL_V20))
    {
      var ptz = await GetPtzService();
      if (ptz != null)
      {
        var ptzCaps = await ptz.GetCapabilitiesAsync(string.Empty);
        hasPtz = ptzCaps.AbsoluteMove || ptzCaps.RelativeMove || ptzCaps.ContinuousMove;
      }
    }

    var result = new OnvifCapabilities(
      HasPtz:     hasPtz,
      HasImaging: services.ContainsKey(ImagingService2.WSDL_V20),
      // Mirror GetEventService()'s own resolution (OnvifServiceSelector tries every WSDL in
      // EventService1.GetSupportedWsdls()) — some cameras advertise events only under the
      // media namespace, with no separate dedicated events entry.
      HasEvents:  EventService1.GetSupportedWsdls().Any(services.ContainsKey));

    _capabilitiesCache = result;
    _capabilitiesCacheAt = System.DateTime.UtcNow;
    return result;
  }
  private async Task<Dictionary<string, string>?> DoGetServices()
  {
    try
    {
      using var client = await GetDevice();
      var result = await client.GetServicesAsync(true);
      _bindingProvider.RememberWorking();
      return result.Service.ToDictionary(s => s.Namespace, s => s.XAddr);
    }
    catch (Exception ex) when (IsAuthError(ex) && SwitchAuthScheme(ex))
    {
      // Camera challenged with a different auth scheme (Basic/Digest) — switch and retry once.
      _serviceCache = new OnvifServiceCache(_bindingProvider, _username, _password, MakeSecurityToken, logger: _logger);
      _onvifClientFactory.SetSecurityToken(null);
      _tokenExpiry = System.DateTime.UtcNow;
      try
      {
        using var client = await GetDevice();
        var result = await client.GetServicesAsync(true);
        _bindingProvider.RememberWorking();
        return result.Service.ToDictionary(s => s.Namespace, s => s.XAddr);
      }
      catch (Exception retryEx)
      {
        _logger?.Error(retryEx.ToString());
        return null;
      }
    }
    catch (Exception ex)
    {
      _logger?.Error(ex.ToString());
      return null;
    }
  }

  /// <summary>
  /// Switches the binding to the auth scheme the camera challenged for (Basic/Digest),
  /// or falls back to Digest once if the challenge didn't name a scheme. Returns false
  /// if the binding is already on the requested scheme (nothing to retry with).
  /// </summary>
  private bool SwitchAuthScheme(Exception ex)
  {
    if (_bindingProvider.TrySwitchToChallenged(ex))
      return true;

    return !_bindingProvider.IsDigest && _bindingProvider.SwitchToDigest();
  }

  private static bool IsAuthError(Exception? ex)
  {
    // Walk the inner-exception chain: a 401 surfaces wrapped (WCF -> Http/Web exception),
    // and cameras may also report it as a WS-Security MessageSecurityException or SOAP fault.
    for (var e = ex; e != null; e = e.InnerException)
    {
      switch (e)
      {
        case MessageSecurityException:
        case HttpRequestException { StatusCode: HttpStatusCode.Unauthorized }:
        case WebException { Response: HttpWebResponse { StatusCode: HttpStatusCode.Unauthorized } }:
          return true;
      }

      if (e.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
          || e.Message.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
          || e.Message.Contains("not Authorized", StringComparison.OrdinalIgnoreCase))
        return true;
    }
    return false;
  }

  public static Notify? ParseEvent(string soapXml)
  {
    var xmlDoc = new XmlDocument();
    xmlDoc.LoadXml(soapXml);

    var nsmgr = new XmlNamespaceManager(xmlDoc.NameTable);
    nsmgr.AddNamespace("s", "http://www.w3.org/2003/05/soap-envelope");
    nsmgr.AddNamespace("wsnt", "http://docs.oasis-open.org/wsn/b-2");

    var notifyNode = xmlDoc.SelectSingleNode("//wsnt:Notify", nsmgr);
    if (notifyNode is null)
      throw new Exception("Notify element not found");

    var serializer = new XmlSerializer(typeof(Notify), "http://docs.oasis-open.org/wsn/b-2");
    using var reader = new XmlNodeReader(notifyNode);
    return serializer.Deserialize(reader) as Notify;
  }
}
