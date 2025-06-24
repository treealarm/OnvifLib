using EventServiceReference1;
using System.ServiceModel.Channels;
using System.ServiceModel;
using System.Xml;
using DeviceServiceReference;
using DateTime = System.DateTime;

namespace OnvifLib
{
  public class EventService1 : OnvifServiceBase, IOnvifServiceFactory<EventService1>
  {
    public static string WSDL_V10 = "http://www.onvif.org/ver10/media/wsdl";

    private EventPortTypeClient? _eventClient1;
    private PullPointSubscriptionClient? _pullClient;
    private CancellationTokenSource? _cts;
    private EndpointAddress? _pullPointAddress;
    private TimeSpan _terminationTime = TimeSpan.FromSeconds(30);
    private SubscriptionManagerClient? _subscriptionManagerClient;

    public event Action<NotificationMessageHolderType[]>? OnEventReceived;

    protected EventService1(string url, CustomBinding binding, string username, string password, string profile) :
      base(url, binding, username, password, profile)
    {
    }
    public static string[] GetSupportedWsdls()
    {
      return new[] { WSDL_V10 };
    }
    public static async Task<EventService1?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile)
    {
      var instance1 = new EventService1(url, binding, username, password, profile);
      await instance1.InitializeAsync();

      return instance1;
    }
    protected async Task InitializeAsync()
    {
      _eventClient1 = _onvifClientFactory.CreateClient<EventPortTypeClient, EventPortType>(
        new EndpointAddress(_url), 
        _binding, 
        _username, 
        _password);
      await _eventClient1.OpenAsync();


      var subscriptionResponse = await _eventClient1.CreatePullPointSubscriptionAsync(new CreatePullPointSubscriptionRequest
      {
        InitialTerminationTime = XmlConvert.ToString(_terminationTime)
      });

      var subscriptionUri = new Uri(subscriptionResponse.SubscriptionReference.Address.Value);

      var headers = new List<AddressHeader>();

      if (subscriptionResponse.SubscriptionReference.ReferenceParameters?.Any is { Length: > 0 } elements)
      {
        foreach (XmlElement element in elements)
          headers.Add(new CustomAddressHeader(element));
      }

      _pullPointAddress = new EndpointAddress(subscriptionUri, headers.ToArray());


      DateTime deviceTime = await GetDeviceTimeAsync();

      if (!string.IsNullOrEmpty(_username))
      {
        byte[] nonceBytes = new byte[20];
        var random = new Random();
        random.NextBytes(nonceBytes);

        var token = new SecurityToken(deviceTime, nonceBytes);

        _onvifClientFactory.SetSecurityToken(token);
      }

      _pullClient = _onvifClientFactory.
        CreateClient<PullPointSubscriptionClient, PullPointSubscription>(
        _pullPointAddress, 
        _binding, 
        _username, 
        _password);      

      _subscriptionManagerClient = _onvifClientFactory.CreateClient<SubscriptionManagerClient, SubscriptionManager>(
          _pullPointAddress, 
          _binding, 
          _username, 
          _password);
      
      await _pullClient.OpenAsync();
      await _subscriptionManagerClient.OpenAsync();
    }

    protected async Task<DateTime> GetDeviceTimeAsync()
    {
      var deviceClient = _onvifClientFactory.CreateClient<DeviceClient, Device>(
        new EndpointAddress(_url), 
        _binding, 
        _username, 
        _password);
      await deviceClient.OpenAsync();

      var deviceSystemDateTime = await deviceClient.GetSystemDateAndTimeAsync();

      if (deviceSystemDateTime.UTCDateTime == null)
        return DateTime.UtcNow;

      return new DateTime(
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

    public async Task StartReceiving()
    {
      await Task.Delay(0);

      if (_pullClient == null)
        throw new InvalidOperationException("Pull client not initialized");

      _cts = new CancellationTokenSource();
      _ = Task.Run(async () =>
      {
        await ReceiveLoopAsync(_cts.Token);
      });
    }

    public void StopReceiving()
    {
      _cts?.Cancel();
    }

    private async Task<bool> ReceiveLoopAsync(CancellationToken token)
    {
      int renewIntervalMs = (int)(_terminationTime.TotalMilliseconds / 2);
      int lastRenewTime = Environment.TickCount;

      while (!token.IsCancellationRequested)
      {
        try
        {
          if (_pullClient!.State != CommunicationState.Opened)
          {
            await _pullClient.OpenAsync();
          }

          var request = new PullMessagesRequest
          {
            Timeout = "PT1S",
            MessageLimit = 1024
          };

          var response = await _pullClient.PullMessagesAsync(request);

           OnEventReceived?.Invoke(response.NotificationMessage);


          if (Environment.TickCount - lastRenewTime > renewIntervalMs && _subscriptionManagerClient != null)
          {
            if (_subscriptionManagerClient!.State != CommunicationState.Opened)
            {
              await _subscriptionManagerClient!.OpenAsync();
            }
            lastRenewTime = Environment.TickCount;

            var renew = new Renew
            {
              TerminationTime = XmlConvert.ToString(_terminationTime)
            };
            await _subscriptionManagerClient!.RenewAsync(renew);
          }
        }
        catch (Exception ex)
        {
          Console.WriteLine("Pull/Renew failed: " + ex.Message);
          await Task.Delay(2000);
        }
      }

      if (_subscriptionManagerClient != null)
      {
        try
        {
          await _subscriptionManagerClient.UnsubscribeAsync(new Unsubscribe());
        }
        catch (Exception ex)
        {
          Console.WriteLine("Unsubscribe failed: " + ex.Message);
        }
      }
      return true;
    }

    public override void Dispose()
    {
      try { _eventClient1?.Close(); } catch { }
      try { _pullClient?.Close(); } catch { }
      base.Dispose();
    }
  }
}