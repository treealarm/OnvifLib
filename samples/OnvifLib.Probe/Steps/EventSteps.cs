using EventServiceReference1;

namespace OnvifLib.Probe.Steps;

/// <summary>Events: subscribe to the pull point, listen for a while, report what arrived.</summary>
public static class EventSteps
{
  /// <summary>How many notifications get printed in full; the rest are counted only.</summary>
  private const int DetailedNotifications = 5;

  public static async Task RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    r.Section(Sections.Events, "events");

    if (ctx.Events is not { } events) { r.Skip("events", "service not available"); return; }
    if (ctx.Options.EventsSeconds == 0) { r.Skip("events", "--events-seconds 0"); return; }

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
    }
    finally
    {
      events.OnEventReceived -= OnEvent;
      await r.StepAsync("StopReceiving", events.StopReceivingAsync);
    }

    await ParseEventAsync(ctx, received, gate);
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
