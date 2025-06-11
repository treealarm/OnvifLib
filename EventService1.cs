using EventServiceReference1;
using OnvifLib;
using System.ServiceModel.Channels;
using System.ServiceModel;
namespace OnvifLib
{
  public class EventService1 : OnvifServiceBase, IOnvifServiceFactory<EventService1>
  {
    public static string WSDL_V10 = "http://www.onvif.org/ver10/media/wsdl";

    private EventPortTypeClient? _eventClient1;
    private PullPointSubscriptionClient? _pullClient;
    private CancellationTokenSource? _cts;
    private string? _pullPointAddress;

    public event Action<NotificationMessageHolderType>? OnEventReceived;

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
      _eventClient1 = OnvifClientFactory.CreateClient<EventPortTypeClient, EventPortType>(_url, _binding, _username, _password);
      await _eventClient1.OpenAsync();


      var subscriptionResponse = await _eventClient1.CreatePullPointSubscriptionAsync(new CreatePullPointSubscriptionRequest
      {
        InitialTerminationTime = "PT1H"
      });

      _pullPointAddress = subscriptionResponse.SubscriptionReference.Address.Value;

      _pullClient = OnvifClientFactory.
        CreateClient<PullPointSubscriptionClient, PullPointSubscription>(_pullPointAddress, _binding, _username, _password);
      await _pullClient.OpenAsync();
    }

    public async Task StartReceiving()
    {
      await Task.Delay(0);

      if (_pullClient == null) 
        throw new InvalidOperationException("Pull client not initialized");

      _cts = new CancellationTokenSource();
      _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public void StopReceiving()
    {
      _cts?.Cancel();
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
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
            Timeout = "PT1S",//XmlConvert.ToString(TimeSpan.FromSeconds(30)),
            MessageLimit = 1024
          };
          var response = await _pullClient!.PullMessagesAsync(request);

          foreach (var msg in response.NotificationMessage)
          {
            OnEventReceived?.Invoke(msg);
          }
        }
        catch (Exception ex)
        {
          Console.WriteLine("Pull failed: " + ex.Message);
          await Task.Delay(2000, token);
        }
      }
    }
    public override void Dispose()
    {
      try { _eventClient1?.Close(); } catch { }
      try { _pullClient?.Close(); } catch { }
      base.Dispose();
    }
  }
}