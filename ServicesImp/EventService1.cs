using EventServiceReference1;
using System.ServiceModel.Channels;
using System.ServiceModel;
using System.Xml;
using DateTime = System.DateTime;

namespace OnvifLib
{
  public class EventService1 : OnvifServiceBase, IOnvifServiceFactory<EventService1>
  {
    public const string WSDL_EVENTS = "http://www.onvif.org/ver10/events/wsdl";
    public const string WSDL_MEDIA  = "http://www.onvif.org/ver10/media/wsdl";

    private EventPortTypeClient? _eventClient1;
    private PullPointSubscriptionClient? _pullClient;
    private CancellationTokenSource? _cts;
    private EndpointAddress? _pullPointAddress;
    private TimeSpan _terminationTime = TimeSpan.FromSeconds(30);
    // ONVIF long-poll duration: the camera holds each PullMessages request up to this long
    // waiting for an event (returns immediately when one arrives). Must stay well below the
    // WCF ReceiveTimeout (CustomBindingProvider) and the subscription TTL above.
    private readonly TimeSpan _pullTimeout = TimeSpan.FromSeconds(5);
    private SubscriptionManagerClient? _subscriptionManagerClient;
    private DateTime _subscriptionExpiry;

    public event Action<NotificationMessageHolderType[]>? OnEventReceived;

    protected EventService1(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    public static string[] GetSupportedWsdls()
    {
      // Try dedicated events endpoint first; fall back to media URL for cameras
      // that advertise the event service under the media namespace.
      return [WSDL_EVENTS, WSDL_MEDIA];
    }

    public static async Task<EventService1?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      var instance1 = new EventService1(url, binding, username, password, profile, tokenFactory, logger);
      await instance1.InitializeAsync();
      return instance1;
    }

    protected async override Task InitializeAsync()
    {
      await base.InitializeAsync();

      _eventClient1 = _onvifClientFactory.CreateClient<EventPortTypeClient, EventPortType>(
        new EndpointAddress(_url),
        _binding,
        _username,
        _password);
      await _eventClient1.OpenAsync();

      await SetupPullPointAsync();
    }

    /// <summary>
    /// Creates a fresh pull-point subscription on the (already opened) event client and
    /// opens the pull / subscription-manager channels for it. Used on first init and on
    /// re-subscribe after the pull point becomes invalid.
    /// </summary>
    private async Task SetupPullPointAsync()
    {
      var subscriptionResponse = await CreateSubscriptionAsync();

      _subscriptionExpiry = subscriptionResponse.TerminationTime ?? DateTime.UtcNow + _terminationTime;

      var subscriptionUri = new Uri(subscriptionResponse.SubscriptionReference.Address.Value);
      var headers = new List<AddressHeader>();

      if (subscriptionResponse.SubscriptionReference.ReferenceParameters?.Any is { Length: > 0 } elements)
      {
        foreach (XmlElement element in elements)
          headers.Add(new CustomAddressHeader(element));
      }

      _pullPointAddress = new EndpointAddress(subscriptionUri, headers.ToArray());

      _pullClient = _onvifClientFactory.CreateClient<PullPointSubscriptionClient, PullPointSubscription>(
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

    /// <summary>
    /// Tears down the (likely invalid) pull point and subscription manager, then runs the
    /// full subscribe process again from scratch.
    /// </summary>
    private async Task ResubscribeAsync()
    {
      _logger?.Info($"Re-subscribing pull point for {_url}");

      try { _pullClient?.Abort(); } catch { }
      try { _subscriptionManagerClient?.Abort(); } catch { }
      _pullClient = null;
      _subscriptionManagerClient = null;

      // The event client may itself be faulted — recreate it so CreateSubscription works.
      try { _eventClient1?.Abort(); } catch { }
      _eventClient1 = _onvifClientFactory.CreateClient<EventPortTypeClient, EventPortType>(
        new EndpointAddress(_url),
        _binding,
        _username,
        _password);
      await _eventClient1.OpenAsync();

      await SetupPullPointAsync();
    }

    private async Task<CreatePullPointSubscriptionResponse> CreateSubscriptionAsync()
    {
      UnacceptableInitialTerminationTimeFaultType? lastFault;

      // Try 1: relative duration (most cameras)
      try
      {
        return await _eventClient1!.CreatePullPointSubscriptionAsync(new CreatePullPointSubscriptionRequest
        {
          InitialTerminationTime = XmlConvert.ToString(_terminationTime)
        });
      }
      catch (FaultException<UnacceptableInitialTerminationTimeFaultType> ex)
      {
        _logger?.Warning("Relative InitialTerminationTime rejected, retrying with absolute time");
        lastFault = ex.Detail;
      }

      // Try 2: absolute datetime, clamped to camera-advertised bounds
      try
      {
        var absTime = DateTime.UtcNow + _terminationTime;
        if (lastFault != null && lastFault.MinimumTime > absTime)
          absTime = lastFault.MinimumTime;
        if (lastFault?.MaximumTimeSpecified == true && lastFault.MaximumTime < absTime)
          absTime = lastFault.MaximumTime;

        return await _eventClient1!.CreatePullPointSubscriptionAsync(new CreatePullPointSubscriptionRequest
        {
          InitialTerminationTime = XmlConvert.ToString(absTime, XmlDateTimeSerializationMode.Utc)
        });
      }
      catch (FaultException<UnacceptableInitialTerminationTimeFaultType>)
      {
        _logger?.Warning("Absolute InitialTerminationTime also rejected, retrying without time");
      }

      // Try 3: no time — let camera pick its own TTL
      return await _eventClient1!.CreatePullPointSubscriptionAsync(new CreatePullPointSubscriptionRequest());
    }

    public async Task StartReceivingAsync()
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

    public async Task StopReceivingAsync()
    {
      if (_cts == null)
      {
        return;
      }
      await _cts.CancelAsync();
    }

    private async Task<bool> ReceiveLoopAsync(CancellationToken token)
    {
      var needsResubscribe = false;
      while (!token.IsCancellationRequested)
      {
        try
        {
          if (needsResubscribe || _pullClient == null || _pullClient.State == CommunicationState.Faulted)
          {
            await ResubscribeAsync();
            needsResubscribe = false;
          }
          else if (_pullClient.State != CommunicationState.Opened)
          {
            await _pullClient.OpenAsync();
          }

          var request = new PullMessagesRequest
          {
            Timeout = XmlConvert.ToString(_pullTimeout),
            MessageLimit = 1024
          };

          var response = await _pullClient!.PullMessagesAsync(request);

          if (response?.NotificationMessage == null)
          {
            // Empty long-poll result (timed out with no events) is normal — not a warning.
            _logger?.Debug($"PullMessages returned no notifications for {_url}");
          }
          else
          {
            // Dispatch separately: a throwing consumer is the caller's bug, not a
            // pull/renew failure, and must not trigger the reconnect backoff below.
            try
            {
              OnEventReceived?.Invoke(response.NotificationMessage);
            }
            catch (Exception ex)
            {
              _logger?.Error($"OnEventReceived handler threw for {_url}: {ex}");
            }
          }

          if (DateTime.UtcNow + _terminationTime / 2 >= _subscriptionExpiry && _subscriptionManagerClient != null)
          {
            if (_subscriptionManagerClient!.State != CommunicationState.Opened)
            {
              await _subscriptionManagerClient!.OpenAsync();
            }
            await RenewSubscriptionAsync();
          }
        }
        catch (Exception ex)
        {
          // Pull or renew failed — the pull point is likely terminated/invalid.
          // Force a full re-subscribe on the next iteration instead of hammering a dead endpoint.
          _logger?.Error($"Pull/Renew failed for {_url}, will re-subscribe: {ex.Message}");
          needsResubscribe = true;
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
          _logger?.Error($"Unsubscribe failed for {_url}: {ex}");
        }
      }
      return true;
    }

    private async Task RenewSubscriptionAsync()
    {
      RenewResponse1? renewResult;

      // Try 1: relative duration
      try
      {
        renewResult = await _subscriptionManagerClient!.RenewAsync(new Renew
        {
          TerminationTime = XmlConvert.ToString(_terminationTime)
        });
      }
      catch (FaultException<UnacceptableTerminationTimeFaultType> ex)
      {
        _logger?.Warning("Relative TerminationTime rejected on renew, retrying with absolute time");

        var absTime = DateTime.UtcNow + _terminationTime;
        if (ex.Detail.MinimumTime > absTime) absTime = ex.Detail.MinimumTime;
        if (ex.Detail.MaximumTimeSpecified && ex.Detail.MaximumTime < absTime) absTime = ex.Detail.MaximumTime;

        // Try 2: absolute datetime
        renewResult = await _subscriptionManagerClient!.RenewAsync(new Renew
        {
          TerminationTime = XmlConvert.ToString(absTime, XmlDateTimeSerializationMode.Utc)
        });
      }

      _subscriptionExpiry = renewResult?.RenewResponse.TerminationTime ?? DateTime.UtcNow + _terminationTime;
    }

    public override void Dispose()
    {
      try { _eventClient1?.Close(); } catch { }
      try { _pullClient?.Close(); } catch { }
      base.Dispose();
    }
  }
}
