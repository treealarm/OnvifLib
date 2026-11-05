using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using EventServiceReference1;

namespace OnvifLib
{
  /// <summary>Which kind of device an I/O state belongs to.</summary>
  public enum DeviceIoKind
  {
    /// <summary>A relay output: its coil (logical state) and its contact (physical state).</summary>
    Relay,

    /// <summary>A digital input: a pulled-up line read as true or false.</summary>
    DigitalInput,
  }

  /// <summary>
  /// The live state of one relay output or digital input, as the camera reported it.
  /// </summary>
  /// <param name="Kind">Whether this is a relay output or a digital input.</param>
  /// <param name="Token">The relay's or input's token, from the event's Source item.</param>
  /// <param name="LogicalState">
  /// The relay's coil is energised ("active") or a digital input reads true. The two kinds spell
  /// it differently on the wire — active/inactive and true/false — but both mean the same boolean.
  /// </param>
  /// <param name="ContactClosed">
  /// A relay's contact: closed or open. Null for a digital input, which has no contact of its own,
  /// and for a relay event that did not carry it.
  /// </param>
  /// <param name="PropertyOperation">"Initialized" for the snapshot a new subscription replays, "Changed" afterwards.</param>
  /// <param name="Time">The moment the camera says the state was, when it says one.</param>
  public sealed record DeviceIoState(
    DeviceIoKind Kind,
    string Token,
    bool LogicalState,
    bool? ContactClosed,
    string PropertyOperation,
    DateTimeOffset? Time);

  /// <summary>
  /// Tracks the live state of a camera's relay outputs and digital inputs.
  /// </summary>
  /// <remarks>
  /// ONVIF has no getter for either state: the relays and inputs return configuration, and the
  /// state itself travels as <c>Device/Trigger</c> property events. Subscribing replays the current
  /// state as <c>Initialized</c>; every change after that arrives as <c>Changed</c>. This watcher
  /// opens a topic-filtered pull point for those two topics, keeps the last state of each device in
  /// a snapshot, and raises <see cref="StateChanged"/> for every one it reads.
  /// <para>
  /// It takes over the event service it is given: the topic filter becomes the device topics and
  /// the receive loop runs until the watcher is disposed. Give it an <see cref="EventService1"/>
  /// that nothing else is using — a <see cref="Camera"/> of its own, or at least its event service.
  /// The service itself is not disposed here; its owner still has to do that.
  /// </para>
  /// <para>
  /// The camera filters the events, so the snapshot holds what the camera chose to report. A device
  /// that never sent an event has no state, and <see cref="TryGetState"/> says so rather than
  /// inventing a value.
  /// </para>
  /// </remarks>
  public sealed class DeviceIoStateWatcher : IAsyncDisposable
  {
    /// <summary>
    /// A ConcreteSet expression for the two Device/Trigger topics. The <c>tns1</c> prefix is the one
    /// <see cref="EventService1"/> declares by default, so the expression is self-contained.
    /// </summary>
    public const string TopicFilter = "tns1:Device/Trigger/Relay|tns1:Device/Trigger/DigitalInput";

    private readonly EventService1 _events;
    private readonly IOnvifLogger? _logger;
    private readonly ConcurrentDictionary<(DeviceIoKind Kind, string Token), DeviceIoState> _states = new();
    private bool _disposed;

    private DeviceIoStateWatcher(EventService1 events, IOnvifLogger? logger)
    {
      _events = events;
      _logger = logger;
    }

    /// <summary>
    /// Filters the event service down to the device topics and starts receiving. The current state
    /// of every relay and input the camera reports arrives as <c>Initialized</c> shortly after.
    /// </summary>
    /// <remarks>A camera that rejects the topic filter faults here; the event service is left as it was.</remarks>
    public static async Task<DeviceIoStateWatcher> CreateAsync(EventService1 events, IOnvifLogger? logger = null)
    {
      ArgumentNullException.ThrowIfNull(events);

      var watcher = new DeviceIoStateWatcher(events, logger);
      await watcher.StartAsync();
      return watcher;
    }

    /// <summary>The last state of every device the camera has reported, in no particular order.</summary>
    public IReadOnlyCollection<DeviceIoState> States => _states.Values.ToArray();

    /// <summary>
    /// Raised for every state the camera reports, <c>Initialized</c> and <c>Changed</c> alike, on
    /// the receive loop's thread. A relay reports its contact as well; a digital input does not.
    /// Handlers must be quick and must not throw.
    /// </summary>
    public event Action<DeviceIoState>? StateChanged;

    /// <summary>
    /// The last reported state of one device. False when the camera has said nothing about it yet.
    /// </summary>
    public bool TryGetState(DeviceIoKind kind, string token, [NotNullWhen(true)] out DeviceIoState? state)
    {
      var found = _states.TryGetValue((kind, token), out var value);
      state = value;
      return found;
    }

    private async Task StartAsync()
    {
      try
      {
        await _events.SetTopicFilterAsync(TopicFilter);
        _events.OnEventReceived += OnMessages;
        await _events.StartReceivingAsync();
      }
      catch
      {
        _events.OnEventReceived -= OnMessages;
        throw;
      }
    }

    private void OnMessages(NotificationMessageHolderType[] messages)
    {
      foreach (var message in messages)
      {
        var topic = TopicText(message.Topic);
        if (KindOf(topic) is null)
          continue;

        if (Parse(topic, message.Message) is not { } state)
        {
          _logger?.Warning($"Unreadable Device/Trigger event on topic '{topic}' — ignored");
          continue;
        }

        _states[(state.Kind, state.Token)] = state;
        Raise(state);
      }
    }

    private void Raise(DeviceIoState state)
    {
      if (StateChanged is not { } handlers)
        return;

      // Isolated per handler: one consumer's bug must not keep the state from the others, and must
      // not escape into the pull loop, which logs and re-subscribes as if the camera had faulted.
      foreach (var handler in handlers.GetInvocationList().Cast<Action<DeviceIoState>>())
      {
        try { handler(state); }
        catch (Exception ex) { _logger?.Error($"A Device I/O state handler threw: {ex.Message}"); }
      }
    }

    /// <summary>
    /// Reads one Device/Trigger notification. Null when the message is not a readable relay or
    /// input state — an unrelated topic, malformed XML, or no LogicalState in the Data items.
    /// </summary>
    internal static DeviceIoState? Parse(string? topicText, XmlElement? message)
    {
      if (message is null || string.IsNullOrEmpty(topicText))
        return null;

      var kind = KindOf(topicText);
      if (kind is null)
        return null;

      XElement root;
      try
      {
        root = XElement.Parse(message.OuterXml);
      }
      catch (XmlException)
      {
        return null;
      }

      var token = SimpleItem(root, "Source", kind == DeviceIoKind.Relay ? "RelayToken" : "InputToken");
      var logicalText = SimpleItem(root, "Data", "LogicalState");
      if (string.IsNullOrWhiteSpace(token) || logicalText is null)
        return null;

      if (ParseLogical(logicalText) is not { } logical)
        return null;

      // Only a relay has a contact; an input's state is the line itself.
      bool? contact = kind == DeviceIoKind.Relay
        ? SimpleItem(root, "Data", "PhysicalState") switch
        {
          { } s when s.Equals("closed", StringComparison.OrdinalIgnoreCase) => true,
          { } s when s.Equals("open", StringComparison.OrdinalIgnoreCase) => false,
          _ => null,
        }
        : null;

      return new DeviceIoState(
        kind.Value,
        token,
        logical,
        contact,
        message.GetAttribute("PropertyOperation"),
        ParseTime(message.GetAttribute("UtcTime")));
    }

    private static DeviceIoKind? KindOf(string topicText)
    {
      if (topicText.EndsWith("Device/Trigger/Relay", StringComparison.OrdinalIgnoreCase))
        return DeviceIoKind.Relay;
      if (topicText.EndsWith("Device/Trigger/DigitalInput", StringComparison.OrdinalIgnoreCase))
        return DeviceIoKind.DigitalInput;
      return null;
    }

    // A relay writes its logical state as active/inactive, an input as true/false. Both spellings
    // are accepted for either kind: firmware that crosses them is otherwise unreadable, and the
    // value means the same thing either way.
    private static bool? ParseLogical(string text)
    {
      if (bool.TryParse(text, out var state))
        return state;
      if (text.Equals("active", StringComparison.OrdinalIgnoreCase))
        return true;
      if (text.Equals("inactive", StringComparison.OrdinalIgnoreCase))
        return false;
      return null;
    }

    private static string? SimpleItem(XElement root, string parentName, string itemName) =>
      root.Descendants()
        .Where(e => e.Name.LocalName == parentName)
        .SelectMany(parent => parent.Descendants())
        .Where(e => e.Name.LocalName == "SimpleItem"
                    && string.Equals((string?)e.Attribute("Name"), itemName, StringComparison.OrdinalIgnoreCase))
        .Select(e => (string?)e.Attribute("Value"))
        .FirstOrDefault();

    private static DateTimeOffset? ParseTime(string? text) =>
      DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
        ? time
        : null;

    private static string TopicText(TopicExpressionType? topic)
    {
      if (topic?.Any == null) return string.Empty;
      return string.Join(" ", topic.Any.Select(n => n.InnerText ?? n.OuterXml ?? string.Empty)).Trim();
    }

    public async ValueTask DisposeAsync()
    {
      if (_disposed) return;
      _disposed = true;

      _events.OnEventReceived -= OnMessages;
      try
      {
        await _events.StopReceivingAsync();
      }
      catch (Exception ex)
      {
        _logger?.Warning($"Stopping the Device I/O watcher's receive loop failed: {ex.Message}");
      }
    }
  }
}
