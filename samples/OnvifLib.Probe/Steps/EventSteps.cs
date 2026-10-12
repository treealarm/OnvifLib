using EventServiceReference1;

namespace OnvifLib.Probe.Steps;

/// <summary>Events: subscribe to the pull point, listen for a while, report what arrived.</summary>
public static class EventSteps
{
  /// <summary>How many notifications get printed in full; the rest are counted only.</summary>
  private const int DetailedNotifications = 5;

  /// <summary>How many topics GetEventProperties lists before the rest are only counted.</summary>
  private const int ListedTopics = 40;

  public static async Task RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    r.Section(Sections.Events, "events");

    if (ctx.Events is not { } events) { r.Skip("events", "service not available"); return; }
    if (ctx.Options.EventsSeconds == 0) { r.Skip("events", "--events-seconds 0"); return; }

    await r.StepAsync("GetServiceCapabilities (events)",
      async () => await events.GetServiceCapabilitiesAsync()
                  ?? throw new ProbeFailure("the camera returned no capabilities"),
      c => r.Values(
        ("max pull points", c.MaxPullPoints == 0 ? "—" : c.MaxPullPoints),
        ("max producers", c.MaxNotificationProducers == 0 ? "—" : c.MaxNotificationProducers),
        ("WS subscription policy", c.WsSubscriptionPolicySupport),
        ("pausable subscriptions", c.WsPausableSubscriptionManagerInterfaceSupport),
        ("persistent storage", c.PersistentNotificationStorage)));

    var properties = await r.StepAsync("GetEventProperties",
      async () => await events.GetEventPropertiesAsync()
                  ?? throw new ProbeFailure("the camera returned no event properties"),
      p =>
      {
        r.Values(
          ("topics", p.Topics.Count),
          ("fixed topic set", p.FixedTopicSet),
          ("topic dialects", p.TopicExpressionDialects.Count == 0 ? "—" : string.Join(", ", p.TopicExpressionDialects)));
        r.Table(["topic", "namespace"],
          p.Topics.Take(ListedTopics).Select(t => new List<object?> { t.Path, t.NamespaceUri }));
        if (p.Topics.Count > ListedTopics)
          r.Note($"{p.Topics.Count - ListedTopics} further topic(s) not shown");
      });

    events.PullTimeout = TimeSpan.FromSeconds(5);
    events.MessageLimit = 1024;
    r.Values(("pull timeout", events.PullTimeout), ("message limit", events.MessageLimit));

    var received = new List<NotificationMessageHolderType>();
    var gate = new object();

    // Raised from the service's own pull loop on a threadpool thread, so the handler must be
    // thread-safe. It must also never throw: the loop logs consumer exceptions but they would
    // pollute the output for no benefit.
    void OnEvent(NotificationMessageHolderType[] messages)
    {
      try { lock (gate) received.AddRange(messages); }
      catch { }
    }

    events.OnEventReceived += OnEvent;
    try
    {
      if (!await r.StepAsync("StartReceiving", events.StartReceivingAsync)) return;

      // Asked for before listening, so the window below sees the current state of every property
      // (motion off, input open …) even on a camera where nothing changes while we wait.
      await r.StepAsync("SetSynchronizationPoint", events.SetSynchronizationPointAsync);

      await r.StepAsync($"listen for {ctx.Options.EventsSeconds}s", async () =>
      {
        try { await Task.Delay(TimeSpan.FromSeconds(ctx.Options.EventsSeconds), ctx.Cancellation); }
        catch (OperationCanceledException) { /* Ctrl+C during the wait is not a failure */ }

        NotificationMessageHolderType[] snapshot;
        lock (gate) snapshot = received.ToArray();

        // Silence is a normal answer: most cameras only emit on motion or an I/O change.
        r.Value("notifications", snapshot.Length);
        foreach (var message in snapshot.Take(DetailedNotifications))
        {
          r.Value("topic", Topic(message));
          if (message.SubscriptionReference?.Address?.Value is { Length: > 0 } subscription)
            r.Value("subscription", subscription);
          if (message.Message is { } payload)
            r.Block(ProbeRunner.PrettyXml(payload.OuterXml), maxLines: 20);
        }
        if (snapshot.Length > DetailedNotifications)
          r.Note($"{snapshot.Length - DetailedNotifications} further notification(s) not shown");
      });

      await FilteredListenAsync(ctx, events, properties, received, gate);
    }
    finally
    {
      events.OnEventReceived -= OnEvent;
      await r.StepAsync("StopReceiving", events.StopReceivingAsync);
    }

    await ParseEventAsync(ctx, received, gate);
  }

  /// <summary>
  /// Re-subscribes with a topic filter, listens, and puts the unfiltered subscription back. A
  /// subscription filter changes nothing on the camera itself, so this is a read.
  /// </summary>
  private static async Task FilteredListenAsync(ProbeContext ctx, EventService1 events,
    OnvifEventProperties? properties, List<NotificationMessageHolderType> received, object gate)
  {
    var r = ctx.Runner;
    var topic = properties?.Topics.FirstOrDefault(t => t.Path.Contains("Motion", StringComparison.OrdinalIgnoreCase))
             ?? properties?.Topics.FirstOrDefault();
    if (topic is null)
    {
      r.Skip("SetTopicFilter", "the camera reported no topics to filter on");
      return;
    }

    var seconds = Math.Min(ctx.Options.EventsSeconds, 10);
    try
    {
      if (!await r.StepAsync($"SetTopicFilter [{topic.Path}]", () => events.SetTopicFilterAsync(topic.Path)))
        return;

      int before;
      lock (gate) before = received.Count;
      // The sync point makes the filtered topic report its state now, so a camera with nothing
      // moving in front of it still shows whether the filter lets anything through.
      await r.StepAsync("SetSynchronizationPoint (filtered)", events.SetSynchronizationPointAsync);

      await r.StepAsync($"listen filtered for {seconds}s", async () =>
      {
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), ctx.Cancellation); }
        catch (OperationCanceledException) { }

        NotificationMessageHolderType[] arrived;
        lock (gate) arrived = received.Skip(before).ToArray();
        var outside = arrived.Select(Topic).Where(t => !Matches(t, topic.Path)).Distinct().ToList();
        r.Values(("notifications", arrived.Length), ("outside the filter", outside.Count));
        if (outside.Count > 0)
          throw new ProbeFailure($"the camera delivered topics the filter excludes: {string.Join(", ", outside.Take(3))}");
      });
    }
    finally
    {
      await r.StepAsync("SetTopicFilter (clear)", () => events.SetTopicFilterAsync(null));
    }
  }

  /// <summary>
  /// Whether a received topic is the filtered one. Notifications name the topic with whatever
  /// prefix the camera likes, so the comparison is on the path after the prefix.
  /// </summary>
  private static bool Matches(string received, string filter)
  {
    static string Strip(string t) => t.Contains(':') ? t[(t.IndexOf(':') + 1)..] : t;
    return string.Equals(Strip(received), Strip(filter), StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Exercises the one static the pull path never reaches: Camera.ParseEvent, which is what a
  /// host with its own notification endpoint would use on the raw SOAP envelope.
  /// </summary>
  private static async Task ParseEventAsync(ProbeContext ctx, List<NotificationMessageHolderType> received, object gate)
  {
    var r = ctx.Runner;
    lock (gate)
    {
      if (received.Count == 0)
      {
        r.Skip("Camera.ParseEvent", "no notification arrived to build an envelope from");
        return;
      }
    }

    // Wrapping the notification back into an envelope is exactly the shape ParseEvent expects,
    // and it is the only way to reach it without a real push subscription.
    var envelope = $"""
      <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
        <s:Body>
          <wsnt:Notify xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
            <wsnt:NotificationMessage>{Payload(received, gate)}</wsnt:NotificationMessage>
          </wsnt:Notify>
        </s:Body>
      </s:Envelope>
      """;

    await r.StepAsync("Camera.ParseEvent",
      () => Task.FromResult(Camera.ParseEvent(envelope) ?? throw new ProbeFailure("returned null")),
      notify => r.Value("messages parsed", notify.NotificationMessage?.Length ?? 0));
  }

  private static string Payload(List<NotificationMessageHolderType> received, object gate)
  {
    lock (gate)
    {
      var message = received[0];
      return message.Message?.OuterXml ?? string.Empty;
    }
  }

  /// <summary>
  /// Topic is mixed content — text nodes and elements in one array — so both kinds are joined
  /// rather than assuming the dialect the camera used.
  /// </summary>
  private static string Topic(NotificationMessageHolderType message)
  {
    if (message.Topic?.Any is not { } nodes) return "—";
    return string.Concat(nodes.Select(n => n.Value ?? n.InnerText)).Trim();
  }
}
