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
  private readonly DeviceClient _deviceClient;
  private readonly CustomBinding _binding;

  private Task<Dictionary<string, string>?>? _servicesTask;
  public Task InitTask { get; private set; } = Task.CompletedTask;

  public string Url
  {
    get
    {
      return _url;
    }
  }
  public string User
  {
    get
    {
      return _username;
    }
  }
  public string Password
  {
    get
    {
      return _password;
    }
  }
  public string Ip
  {
    get
    {
      return _ip;
    }
  }
  public int Port
  {
    get
    {
      return _port;
    }
  }
  //http://192.168.1.150:8899/onvif/device_service
  public static  string CreateUrl(string ip, int port)
  {
    return $"http://{ip}:{port}/onvif/device_service";
  }

  public static Camera Create(string ip, int port, string username, string password)
  {
    var cam = new Camera(ip, port, username, password);
    cam.InitTask = cam.InitAsync(); // запускаем в фоне
    return cam;
  }
  private Camera(string ip, int port, string username, string password)
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

    var timeout = 5;
    _binding.OpenTimeout = TimeSpan.FromSeconds(timeout);
    _binding.CloseTimeout = TimeSpan.FromSeconds(timeout);
    _binding.SendTimeout = TimeSpan.FromSeconds(timeout);
    _binding.ReceiveTimeout = TimeSpan.FromSeconds(timeout);

    var endpoint = new EndpointAddress(_url);

    _deviceClient = new DeviceClient(_binding, endpoint);

    _deviceClient.ClientCredentials.UserName.UserName = _username;
    _deviceClient.ClientCredentials.UserName.Password = _password;
    _deviceClient.ClientCredentials.HttpDigest.ClientCredential.UserName = _username;
    _deviceClient.ClientCredentials.HttpDigest.ClientCredential.Password = _password;
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
      return await OnvifServiceSelector.TryCreateService<MediaService>(services, _binding, _username, _password);
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
      return await OnvifServiceSelector.TryCreateService<PtzService2>(services, _binding, _username, _password);
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
      var result = await _deviceClient.GetServicesAsync(true);
      return result.Service.ToDictionary(s => s.Namespace, s => s.XAddr);
    }
    catch (Exception ex)
    {
      Console.WriteLine(ex);
      return null;
    }
  }

  /// <summary>
  /// Закрыть соединение
  /// </summary>
  public void Close()
  {
    try
    {
      _deviceClient.Close();
    }
    catch
    {
      _deviceClient.Abort();
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
