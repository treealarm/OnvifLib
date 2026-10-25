using EventServiceReference1;
using System.ServiceModel.Channels;
using System.ServiceModel;
using System.Text.RegularExpressions;
using System.Xml;
using DateTime = System.DateTime;

namespace OnvifLib
{
  public class EventService1 : OnvifServiceBase, IOnvifServiceFactory<EventService1>
  {
    public const string WSDL_EVENTS = "http://www.onvif.org/ver10/events/wsdl";
    public const string WSDL_MEDIA  = "http://www.onvif.org/ver10/media/wsdl";

    /// <summary>The ONVIF topic-expression dialect every conformant camera must accept.</summary>
    public const string ConcreteSetDialect = "http://www.onvif.org/ver10/tev/topicExpression/ConcreteSet";
    public const string OnvifTopicsNamespace = "http://www.onvif.org/ver10/topics";
    private const string WsTopicsNamespace = "http://docs.oasis-open.org/wsn/t-1";
    private const string WsNotificationNamespace = "http://docs.oasis-open.org/wsn/b-2";

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

    private Task? _loopTask;
    // True between a successful CreatePullPointSubscription and the Unsubscribe the receive loop
    // sends when it stops. Lets a restart and a filter change know whether there is a live pull
    // point to reuse or to release.
    private volatile bool _subscriptionActive;

    // Prefix → namespace, learned from GetEventProperties, so a filter naming a vendor topic
    // (tnsaxis:, tns1: …) can declare the namespace its prefix stands for.
    private readonly Dictionary<string, string> _topicNamespaces = new() { ["tns1"] = OnvifTopicsNamespace };
    private readonly object _topicNamespacesGate = new();

    private sealed record FilterChange(string? Previous, TaskCompletionSource Done);
    private FilterChange? _pendingFilterChange;

    /// <summary>
    /// The topic expression the current subscription was created with, or null for every topic.
    /// </summary>
    public string? TopicFilter { get; private set; }

    /// <summary>The receive loop is running.</summary>
    public bool IsReceiving => _cts is { IsCancellationRequested: false } && _loopTask is { IsCompleted: false };

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
      // scheme survives across re-subscribes that return the same address — but some cameras
      // mint a fresh URI per subscription, so rebuild the provider whenever the key changes
      // instead of pinning it to whatever URI the first subscription happened to return.
      var pullCacheKey = subscriptionUri.ToString();
      if (_pullBindingProvider == null || _pullBindingProvider.CacheKey != pullCacheKey)
      {
        var pullInitialScheme = AuthSchemeCache.TryGet(pullCacheKey) ?? _pullBindingProvider?.Scheme ?? _deviceBindingProvider.Scheme;
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
      _subscriptionActive = true;
    }

    /// <summary>
    /// Tears down the (likely invalid) pull point and subscription manager, then runs the
    /// full subscribe process again from scratch.
    /// </summary>
    /// <param name="unsubscribeFirst">
    /// Release the current pull point before creating the next one. Wanted when the old one is
    /// still alive (a filter change): cameras keep only a few pull points, and abandoning one per
    /// change runs them out. Not wanted on the fault path — the endpoint is already dead, and an
    /// Unsubscribe sent to it would only sit out the binding timeout.
    /// </param>
    private async Task ResubscribeAsync(bool unsubscribeFirst = false)
    {
      _logger?.Info($"Re-subscribing pull point for {_url}");

      if (unsubscribeFirst && _subscriptionActive && _subscriptionManagerClient?.State == CommunicationState.Opened)
      {
        try { await _subscriptionManagerClient.UnsubscribeAsync(new Unsubscribe()); }
        catch (Exception ex) { _logger?.Warning($"Unsubscribe before re-subscribe failed for {_url}: {ex.Message}"); }
      }
      _subscriptionActive = false;

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
      var filter = BuildFilter();

      // Try 1: relative duration (most cameras)
      try
      {
        return await _eventClient1!.CreatePullPointSubscriptionAsync(new CreatePullPointSubscriptionRequest
        {
          Filter = filter,
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
          Filter = filter,
          InitialTerminationTime = XmlConvert.ToString(absTime, XmlDateTimeSerializationMode.Utc)
        });
      }
      catch (FaultException<UnacceptableInitialTerminationTimeFaultType>)
      {
        _logger?.Warning("Absolute InitialTerminationTime also rejected, retrying without time");
      }

      // Try 3: no time — let camera pick its own TTL
      return await _eventClient1!.CreatePullPointSubscriptionAsync(new CreatePullPointSubscriptionRequest { Filter = filter });
    }

    /// <summary>
    /// The wsnt:TopicExpression for <see cref="TopicFilter"/>, or null for no filter.
    /// </summary>
    /// <remarks>
    /// Every prefix the expression uses is declared on the element itself. The expression is text,
    /// so the serializer has no way to know <c>tns1:</c> needs a namespace in scope, and a camera
    /// that resolves prefixes strictly rejects an undeclared one as an invalid expression.
    /// </remarks>
    private FilterType? BuildFilter()
    {
      if (TopicFilter is not { Length: > 0 } expression)
        return null;

      var doc = new XmlDocument();
      var element = doc.CreateElement("wsnt", "TopicExpression", WsNotificationNamespace);
      element.SetAttribute("Dialect", ConcreteSetDialect);

      var prefixes = Regex.Matches(expression, @"([A-Za-z_][\w.-]*):")
        .Select(m => m.Groups[1].Value)
        .Distinct();
      lock (_topicNamespacesGate)
      {
        foreach (var prefix in prefixes)
        {
          if (!_topicNamespaces.TryGetValue(prefix, out var ns))
          {
            _logger?.Warning($"Topic filter prefix '{prefix}:' is not one the camera reported; sending it undeclared");
            continue;
          }
          var declaration = doc.CreateAttribute("xmlns", prefix, "http://www.w3.org/2000/xmlns/");
          declaration.Value = ns;
          element.Attributes.Append(declaration);
        }
      }

      element.InnerText = expression;
      return new FilterType { Any = [element] };
    }

    private static bool IsFilterFault(Exception ex) =>
      ex is FaultException<InvalidTopicExpressionFaultType>
         or FaultException<TopicNotSupportedFaultType>
         or FaultException<InvalidFilterFaultType>;

    /// <summary>
    /// Changes which topics the subscription delivers. Null or empty means every topic.
    /// </summary>
    /// <remarks>
    /// A filter is part of CreatePullPointSubscription and cannot be changed on a live
    /// subscription, so this replaces the subscription — and that is also why it is a method and
    /// not a setting: the service subscribes as soon as it is created, before a caller could set
    /// anything. While receiving, the swap is handed to the receive loop so only one thread ever
    /// touches the pull point; this call waits for the loop to report the outcome.
    /// <para>
    /// A camera that rejects the expression faults the subscribe call. The previous filter is then
    /// put back and the exception propagates, so a typo never leaves the service unsubscribed.
    /// </para>
    /// </remarks>
    /// <param name="topicExpression">
    /// A ConcreteSet expression, e.g. <c>tns1:VideoSource/MotionAlarm|tns1:RuleEngine//.</c>.
    /// Topic paths from <see cref="GetEventPropertiesAsync"/> can be used as they are.
    /// </param>
    public async Task SetTopicFilterAsync(string? topicExpression)
    {
      var next = string.IsNullOrWhiteSpace(topicExpression) ? null : topicExpression.Trim();
      var previous = TopicFilter;

      if (IsReceiving)
      {
        var change = new FilterChange(previous, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        TopicFilter = next;
        Interlocked.Exchange(ref _pendingFilterChange, change)?.Done.TrySetCanceled();
        try
        {
          // One long-poll plus a full subscribe round trip is the longest the loop can take to
          // reach the change; past that something is wrong and the caller should hear about it.
          await change.Done.Task.WaitAsync(PullTimeout + TimeSpan.FromSeconds(_bindingTimeoutSeconds * 2 + 5));
          return;
        }
        catch (OperationCanceledException) when (!IsReceiving)
        {
          // The loop stopped before it got to the change; apply it the idle way below.
          Interlocked.CompareExchange(ref _pendingFilterChange, null, change);
        }
      }

      // Not receiving. A loop that is still winding down owns the pull point until it exits.
      if (_loopTask is { IsCompleted: false } winding)
      {
        try { await winding; } catch { }
      }

      TopicFilter = next;
      try
      {
        await ResubscribeAsync(unsubscribeFirst: true);
      }
      catch
      {
        TopicFilter = previous;
        try { await ResubscribeAsync(); }
        catch (Exception restoreEx) { _logger?.Error($"Restoring the previous subscription failed for {_url}: {restoreEx.Message}"); }
        throw;
      }
    }

    /// <summary>
    /// The topics the camera can raise, flattened to ConcreteSet paths, plus the dialects it says
    /// it accepts.
    /// </summary>
    /// <remarks>
    /// The topic set arrives as an untyped XML tree: each element is a topic node, and the ones
    /// marked <c>wstop:topic="true"</c> are the ones that actually fire. Nodes without the marker
    /// but carrying a MessageDescription are counted too — firmware exists that describes a leaf
    /// and forgets to flag it.
    /// </remarks>
    public async Task<OnvifEventProperties?> GetEventPropertiesAsync()
    {
      if (_eventClient1 == null) return null;
      var resp = await _eventClient1.GetEventPropertiesAsync(new GetEventPropertiesRequest());
      if (resp == null) return null;

      var topics = new List<OnvifEventTopic>();
      var generated = 0;
      foreach (var root in resp.TopicSet?.Any ?? [])
      {
        var ns = root.NamespaceURI ?? string.Empty;
        string prefix;
        lock (_topicNamespacesGate)
        {
          prefix = !string.IsNullOrEmpty(root.Prefix) ? root.Prefix
            : _topicNamespaces.FirstOrDefault(kv => kv.Value == ns).Key
              ?? $"ns{++generated}";
          _topicNamespaces.TryAdd(prefix, ns);
        }
        Walk(root, $"{prefix}:{root.LocalName}", ns, topics);
      }

      return new OnvifEventProperties(
        topics,
        resp.FixedTopicSet,
        resp.TopicExpressionDialect ?? [],
        resp.MessageContentFilterDialect ?? [],
        resp.TopicNamespaceLocation ?? []);
    }

    private static void Walk(XmlElement node, string path, string ns, List<OnvifEventTopic> topics)
    {
      XmlElement? description = null;
      var children = new List<XmlElement>();
      foreach (var child in node.ChildNodes.OfType<XmlElement>())
      {
        if (child.LocalName == "MessageDescription") description = child;
        else if (child.NamespaceURI != WsTopicsNamespace) children.Add(child);
      }

      var flagged = string.Equals(node.GetAttribute("topic", WsTopicsNamespace), "true", StringComparison.OrdinalIgnoreCase);
      if (flagged || description != null)
        topics.Add(new OnvifEventTopic(path, ns, description?.OuterXml));

      foreach (var child in children)
        Walk(child, $"{path}/{child.LocalName}", ns, topics);
    }

    public async Task<OnvifEventCapabilities?> GetServiceCapabilitiesAsync()
    {
      if (_eventClient1 == null) return null;
      var c = await _eventClient1.GetServiceCapabilitiesAsync();
      if (c == null) return null;
      return new OnvifEventCapabilities(
        c.WSSubscriptionPolicySupportSpecified && c.WSSubscriptionPolicySupport,
        c.WSPausableSubscriptionManagerInterfaceSupportSpecified && c.WSPausableSubscriptionManagerInterfaceSupport,
        c.MaxNotificationProducersSpecified ? c.MaxNotificationProducers : 0,
        c.MaxPullPointsSpecified ? c.MaxPullPoints : 0,
        c.PersistentNotificationStorageSpecified && c.PersistentNotificationStorage);
    }

    /// <summary>
    /// Asks the camera to re-send the current state of every property event (motion on/off, input
    /// levels …) on this subscription.
    /// </summary>
    /// <remarks>
    /// Property events normally fire only on a change, so a fresh subscriber would otherwise not
    /// know whether motion is currently active until it next toggles.
    /// </remarks>
    public async Task SetSynchronizationPointAsync()
    {
      if (_pullClient == null)
        throw new InvalidOperationException("Pull client not initialized");
      if (_pullClient.State != CommunicationState.Opened)
        await _pullClient.OpenAsync();
      await _pullClient.SetSynchronizationPointAsync();
    }

    public async Task StartReceivingAsync()
    {
      await Task.CompletedTask;

      if (_pullClient == null)
        throw new InvalidOperationException("Pull client not initialized");

      _cts = new CancellationTokenSource();
      var token = _cts.Token;
      _loopTask = Task.Run(async () =>
      {
        await ReceiveLoopAsync(token);
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
      // A previous run unsubscribed on its way out; pulling from that pull point would only fault
      // and cost the 2 s back-off below before the inevitable re-subscribe.
      var needsResubscribe = !_subscriptionActive;
      while (!token.IsCancellationRequested)
      {
        var filterChange = Interlocked.Exchange(ref _pendingFilterChange, null);
        if (filterChange != null)
        {
          try
          {
            await ResubscribeAsync(unsubscribeFirst: !needsResubscribe);
            filterChange.Done.TrySetResult();
            needsResubscribe = false;
          }
          catch (Exception ex)
          {
            _logger?.Error($"Applying topic filter '{TopicFilter}' failed for {_url}: {ex.Message}");
            TopicFilter = filterChange.Previous;
            filterChange.Done.TrySetException(ex);
            needsResubscribe = true;
          }
        }

        if (needsResubscribe || _pullClient == null || _pullClient.State == CommunicationState.Faulted)
        {
          try
          {
            await ResubscribeAsync();
          }
          catch (Exception ex) when (IsFilterFault(ex) && TopicFilter != null)
          {
            // The camera accepted this filter once and now refuses it (a firmware restart that
            // dropped a vendor topic, say). Receiving everything beats receiving nothing forever.
            _logger?.Error($"Camera rejected topic filter '{TopicFilter}' on re-subscribe for {_url}; continuing unfiltered: {ex.Message}");
            TopicFilter = null;
            needsResubscribe = true;
            continue;
          }
          catch (Exception ex)
          {
            // Only the device-service binding is in play here (ResubscribeAsync talks to
            // the device service via _deviceBindingProvider) — a challenge on this call
            // must not be attributed to the unrelated pull-point binding.
            if (_deviceBindingProvider.TrySwitchToChallenged(ex))
            {
              _authSchemeConfirmed = false;
              _logger?.Warning($"Device-service auth scheme rejected for {_url}, switching and retrying: {ex.Message}");
            }
            else
            {
              _logger?.Error($"Re-subscribe failed for {_url}, will retry: {ex.Message}");
            }

            needsResubscribe = true;
            await Task.Delay(2000);
            continue;
          }
          needsResubscribe = false;
        }

        try
        {
          if (_pullClient!.State != CommunicationState.Opened)
          {
            await _pullClient.OpenAsync();
          }

          // Snapshot once per iteration — PullTimeout is a public mutable property and is
          // read again below after the SOAP call returns; a concurrent mutation between the
          // two reads must not change the floor-delay calculation mid-iteration.
          var pullTimeout = PullTimeout;
          var request = new PullMessagesRequest
          {
            Timeout = XmlConvert.ToString(pullTimeout),
            MessageLimit = MessageLimit
          };

          var pullStarted = DateTime.UtcNow;
          var response = await _pullClient.PullMessagesAsync(request);

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
          if (elapsed < pullTimeout)
            await Task.Delay(pullTimeout - elapsed, token);

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
          // Pull or renew failed — the pull point is likely terminated/invalid. Only the
          // pull-point binding is in play here (PullMessages/Renew both go through
          // _pullBindingProvider), so only try switching that one — a challenge on this
          // endpoint must not bleed into the device-service binding's scheme.
          if (_pullBindingProvider?.TrySwitchToChallenged(ex) ?? false)
          {
            _authSchemeConfirmed = false;
            _logger?.Warning($"Pull-point auth scheme rejected for {_url}, switching and re-subscribing: {ex.Message}");
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

      Interlocked.Exchange(ref _pendingFilterChange, null)?.Done.TrySetCanceled();

      if (_subscriptionManagerClient != null && _subscriptionActive)
      {
        try
        {
          await _subscriptionManagerClient.UnsubscribeAsync(new Unsubscribe());
        }
        catch (Exception ex)
        {
          _logger?.Error($"Unsubscribe failed for {_url}: {OnvifErrors.Describe(ex)}");
        }
      }
      _subscriptionActive = false;
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
