using System.Collections.Concurrent;
using System.Net;
using System.ServiceModel;
using System.ServiceModel.Channels;
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

  private readonly CustomBinding _binding;
  private readonly OnvifClientFactory _onvifClientFactory = new OnvifClientFactory();
  private Task<Dictionary<string, string>?>? _servicesTask;
  private System.DateTime _tokenExpiry = System.DateTime.UtcNow;
  public Task InitTask { get; private set; } = Task.CompletedTask;

  private readonly OnvifServiceCache _serviceCache;

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

      byte[] nonceBytes = new byte[20];
      var random = new Random();
      random.NextBytes(nonceBytes);

      var token = new SecurityToken(deviceTime ?? System.DateTime.UtcNow, nonceBytes);

      _onvifClientFactory.SetSecurityToken(token);   
    }

    var deviceClient = _onvifClientFactory.
      CreateClient<DeviceClient, DeviceServiceReference.Device>(
        endpoint,
        _binding,
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
        _binding,
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

  public string Url { get { return _url; } }
  public string User { get { return _username; } }
  public string Password { get { return _password; } }
  public string Ip { get { return _ip; } }
  public int Port { get { return _port; } }

  //http://192.168.1.150:8899/onvif/device_service
  public static  string CreateUrl(string ip, int port)
  {
    return $"http://{ip}:{port}/onvif/device_service";
  }

  public static Camera Create(string ip, int port, string username, string password, double timeout = 15)
  {
    var cam = new Camera(ip, port, username, password, timeout);
    cam.InitTask = cam.InitAsync(); // запускаем в фоне
    return cam;
  }
  private Camera(string ip, int port, string username, string password, double timeout)
  {
    _url = CreateUrl(ip, port);
    _ip = ip;
    _username = username;
    _password = password;
    _port = port;

    _binding = new CustomBinding(
      new TextMessageEncodingBindingElement(MessageVersion.Soap12WSAddressing10, Encoding.UTF8), // SOAP 1.2
      new HttpTransportBindingElement()
      {
        AuthenticationScheme = AuthenticationSchemes.Digest
      }
    );

    _binding.OpenTimeout = TimeSpan.FromSeconds(timeout);
    _binding.CloseTimeout = TimeSpan.FromSeconds(timeout);
    _binding.SendTimeout = TimeSpan.FromSeconds(timeout);
    _binding.ReceiveTimeout = TimeSpan.FromSeconds(timeout);

    var endpoint = new EndpointAddress(_url);

    var clientInspector = new CustomMessageInspector();
    var behavior = new CustomEndpointBehavior(clientInspector);

    _serviceCache = new OnvifServiceCache(_binding, username, password);
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
      // сбрасываем, чтобы в следующий раз попробовать снова
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
      return await OnvifServiceSelector.TryCreateService<EventService1>(services, _binding, _username, _password);
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

  async public Task<List<string>?> GetProfiles()
  {
    var service = await GetMediaService();
    if (service == null)
    {
      return null;
    }
    return service.GetProfiles();
  }
  public async Task<bool> IsAlive()
  {
    var services = await GetServicesAsync();
    return (services != null && services.Count > 0);
  }
  private async Task<Dictionary<string, string>?> DoGetServices()
  {
    try
    {
      using var client = await GetDevice();
      var result = await client.GetServicesAsync(true);
      return result.Service.ToDictionary(s => s.Namespace, s => s.XAddr);
    }
    catch (Exception ex)
    {
      Console.WriteLine(ex);
      return null;
    }
  }

  public static void ParseEvent(string soapXml)
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
    var notify =  serializer.Deserialize(reader) as Notify;

    if (notify != null && (notify).NotificationMessage != null)
    {
      var n = (notify).NotificationMessage.FirstOrDefault();

      if (n != null)
        Console.WriteLine("Topic: " + (n).Topic.Any);
    }    
  }
}
