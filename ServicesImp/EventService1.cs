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
    public TimeSpan PullTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public int MessageLimit { get; set; } = 1024;
    private SubscriptionManagerClient? _subscriptionManagerClient;
    private DateTime _subscriptionExpiry;

    // Bindings used for the device-service endpoint (CreatePullPointSubscription) and the
    // pull-point endpoint (PullMessages/Renew/Unsubscribe) — the two can require different
    // HTTP auth schemes (e.g. device service via Digest, events via Basic). Each remembers
    // the working scheme in AuthSchemeCache so re-subscribes don't repeat a failed scheme.
    private readonly CustomBindingProvider _deviceBindingProvider;
    private CustomBindingProvider? _pullBindingProvider;
    private readonly double _bindingTimeoutSeconds;
    private bool _authSchemeConfirmed;

    public event Action<NotificationMessageHolderType[]>? OnEventReceived;

    protected EventService1(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
      _bindingTimeoutSeconds = binding.OpenTimeout.TotalSeconds;
      var initialScheme = AuthSchemeCache.TryGet(url) ?? CustomBindingProvider.SchemeOf(binding);
      _deviceBindingProvider = new CustomBindingProvider(_bindingTimeoutSeconds, initialScheme, url);
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
        _deviceBindingProvider.Current,
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

      // The pull-point endpoint may require a different auth scheme than the device
      // service (e.g. Basic vs Digest). Cache key is the pull-point URI so a known-working
      // scheme survives across re-subscribes that return the same address.
      var pullCacheKey = subscriptionUri.ToString();
      if (_pullBindingProvider == null)
      {
        var pullInitialScheme = AuthSchemeCache.TryGet(pullCacheKey) ?? _deviceBindingProvider.Scheme;
        _pullBindingProvider = new CustomBindingProvider(_bindingTimeoutSeconds, pullInitialScheme, pullCacheKey);
      }

      _pullClient = _onvifClientFactory.CreateClient<PullPointSubscriptionClient, PullPointSubscription>(
        _pullPointAddress,
        _pullBindingProvider.Current,
        _username,
        _password);

      _subscriptionManagerClient = _onvifClientFactory.CreateClient<SubscriptionManagerClient, SubscriptionManager>(
        _pullPointAddress,
        _pullBindingProvider.Current,
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
        _deviceBindingProvider.Current,
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
      await Task.CompletedTask;

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
            Timeout = XmlConvert.ToString(PullTimeout),
            MessageLimit = MessageLimit
          };

          var pullStarted = DateTime.UtcNow;
          var response = await _pullClient!.PullMessagesAsync(request);

          if (!_authSchemeConfirmed)
          {
            _pullBindingProvider?.RememberWorking();
            _deviceBindingProvider.RememberWorking();
            _authSchemeConfirmed = true;
          }

          if (response?.NotificationMessage == null)
          {
            // Empty long-poll result (timed out with no events) is normal — not a warning.
            _logger?.Debug($"PullMessages returned no notifications for {_url}");
          }
          else
          {
            // Dispatch immediately, before the throttle delay below — alarms/events must not
            // wait out PullTimeout before CheckAndTriggerAlarmAsync records AlarmStartTs.
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

          // Some firmware ignores the long-poll Timeout above and returns immediately
          // regardless of whether events are pending. Without a floor here, such a camera
          // drives this loop at full speed (thousands of iterations/sec), starving the
          // process' thread pool. Enforce PullTimeout as a minimum cadence regardless of
          // how fast the camera actually responds — applied after dispatch, so it only
          // delays the next pull, never the event we just received.
          var elapsed = DateTime.UtcNow - pullStarted;
          if (elapsed < PullTimeout)
            await Task.Delay(PullTimeout - elapsed, token);

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
          // If the camera challenged with a different auth scheme (e.g. Basic instead of
          // Anonymous/Digest), switch to it now so the re-subscribe below uses it — and
          // it gets remembered for next time via AuthSchemeCache.
          var switchedScheme =
            (_pullBindingProvider?.TrySwitchToChallenged(ex) ?? false) |
            _deviceBindingProvider.TrySwitchToChallenged(ex);

          if (switchedScheme)
          {
            _authSchemeConfirmed = false;
            _logger?.Warning($"Auth scheme rejected for {_url}, switching and re-subscribing: {ex.Message}");
          }
          else
          {
            _logger?.Error($"Pull/Renew failed for {_url}, will re-subscribe: {ex.Message}");
          }

          // Force a full re-subscribe on the next iteration instead of hammering a dead endpoint.
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
      // Stop the background receive loop first, otherwise it keeps running on the
      // closed clients, faults, and tries to re-subscribe to a disposed service.
      try { _cts?.Cancel(); } catch { }
      try { _eventClient1?.Close(); } catch { }
      try { _pullClient?.Close(); } catch { }
      try { _subscriptionManagerClient?.Close(); } catch { }
      base.Dispose();
    }
  }
}
