using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EventServiceReference1;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

public sealed record EventRow(DateTime Received, string Topic, string Producer, string Xml)
{
  public string TimeText => Received.ToString("HH:mm:ss.fff");
}

/// <summary>Events: the pull-point subscription, and the raw notifications it produces.</summary>
public sealed partial class EventsViewModel(OperationRunner runner, UiLogger logger)
  : TabViewModelBase("Events", runner, logger)
{
  private EventService1? _subscribed;

  public ObservableCollection<EventRow> Events { get; } = [];

  [ObservableProperty] private bool _isReceiving;
  [ObservableProperty] private double _pullTimeoutSeconds = 5;
  [ObservableProperty] private int _messageLimit = 1024;
  [ObservableProperty] private int _maxRows = 500;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(SelectedXml))]
  private EventRow? _selected;

  public string SelectedXml => Selected is { } row ? XmlPretty.Format(row.Xml) : "";

  /// <summary>The topic expression typed into the filter box. Empty means every topic.</summary>
  [ObservableProperty] private string _topicFilter = "";

  /// <summary>What the subscription is actually filtered on, as the service last reported it.</summary>
  [ObservableProperty] private string _activeFilterText = "all topics";

  [ObservableProperty] private string _capabilitiesText = "";

  public ObservableCollection<OnvifEventTopic> Topics { get; } = [];

  [ObservableProperty] private OnvifEventTopic? _selectedTopic;

  private bool _topicsLoaded;

  [ObservableProperty] private string _parseInput = "";
  [ObservableProperty] private string _parseResult = "";

  protected override string? DescribeUnavailability(CameraSession session) => session.Events is null
    ? session.Advertises(EventService1.GetSupportedWsdls())
      ? "The camera advertises events, but the library could not create a client for it — check the Log tab."
      : "This camera does not advertise an event service."
    : null;

  protected override void OnCleared()
  {
    Unsubscribe();
    Events.Clear();
    IsReceiving = false;
    Topics.Clear();
    SelectedTopic = null;
    TopicFilter = "";
    ActiveFilterText = "all topics";
    CapabilitiesText = "";
    _topicsLoaded = false;
  }

  /// <summary>
  /// Topics and capabilities are fixed for a camera, so they are read once per connection rather
  /// than on every visit to the tab.
  /// </summary>
  public override async Task ActivateAsync()
  {
    if (Session?.Events is null || _topicsLoaded) return;
    await LoadCapabilitiesAsync();
    await LoadTopicsAsync();
  }

  private async Task LoadCapabilitiesAsync()
  {
    if (Session?.Events is not { } events) return;
    var (ok, caps) = await Runner.RunAsync("GetServiceCapabilities (events)", events.GetServiceCapabilitiesAsync);
    if (!ok) return;
    CapabilitiesText = caps is null
      ? "the camera reported no event capabilities"
      : $"max pull points {(caps.MaxPullPoints == 0 ? "—" : caps.MaxPullPoints)}, " +
        $"WS subscription policy {(caps.WsSubscriptionPolicySupport ? "yes" : "no")}, " +
        $"pausable {(caps.WsPausableSubscriptionManagerInterfaceSupport ? "yes" : "no")}";
  }

  [RelayCommand]
  private async Task LoadTopicsAsync()
  {
    if (Session?.Events is not { } events) return;
    var (ok, properties) = await Runner.RunAsync("GetEventProperties", events.GetEventPropertiesAsync);
    if (!ok) return;
    Topics.Clear();
    foreach (var topic in properties?.Topics ?? []) Topics.Add(topic);
    _topicsLoaded = true;
    if (properties is { Topics.Count: 0 }) Runner.Report("The camera reported no event topics");
  }

  /// <summary>Copies the selected topic into the filter box, or appends it as an alternative.</summary>
  [RelayCommand]
  private void UseTopic()
  {
    if (SelectedTopic is not { } topic) return;
    TopicFilter = string.IsNullOrWhiteSpace(TopicFilter) ? topic.Path : $"{TopicFilter.Trim()}|{topic.Path}";
  }

  /// <summary>
  /// Re-subscribes with the typed filter. Works whether or not the list is receiving: the service
  /// swaps the subscription either way, and a rejected expression leaves the previous one in place.
  /// </summary>
  [RelayCommand]
  private async Task ApplyFilterAsync()
  {
    if (Session?.Events is not { } events) return;
    var expression = string.IsNullOrWhiteSpace(TopicFilter) ? null : TopicFilter.Trim();
    await Runner.RunAsync(expression is null ? "SetTopicFilter (all topics)" : $"SetTopicFilter [{expression}]",
      () => events.SetTopicFilterAsync(expression));
    ActiveFilterText = events.TopicFilter ?? "all topics";
  }

  [RelayCommand]
  private void ClearFilter() => TopicFilter = "";

  /// <summary>
  /// Asks the camera to re-send the current state of every property event, so the list shows
  /// whether motion is active now instead of waiting for it to change.
  /// </summary>
  [RelayCommand]
  private async Task SyncPointAsync()
  {
    if (Session?.Events is not { } events || !IsReceiving) return;
    await Runner.RunAsync("SetSynchronizationPoint", events.SetSynchronizationPointAsync);
  }

  public override async Task ShutdownAsync()
  {
    if (_subscribed is { } events && IsReceiving)
    {
      try { await events.StopReceivingAsync(); }
      catch (Exception ex) { Logger.Warning($"stopping the event subscription failed: {ex.Message}"); }
    }
    Unsubscribe();
    IsReceiving = false;
  }

  [RelayCommand]
  private async Task StartAsync()
  {
    if (Session?.Events is not { } events || IsReceiving) return;

    events.PullTimeout = TimeSpan.FromSeconds(Math.Max(1, PullTimeoutSeconds));
    events.MessageLimit = MessageLimit;

    Subscribe(events);
    if (await Runner.RunAsync("StartReceiving", events.StartReceivingAsync)) IsReceiving = true;
    else Unsubscribe();
  }

  [RelayCommand]
  private async Task StopAsync()
  {
    if (Session?.Events is not { } events || !IsReceiving) return;
    await Runner.RunAsync("StopReceiving", events.StopReceivingAsync);
    Unsubscribe();
    IsReceiving = false;
  }

  [RelayCommand]
  private void Clear() => Events.Clear();

  private void Subscribe(EventService1 events)
  {
    Unsubscribe();
    _subscribed = events;
    events.OnEventReceived += OnEventReceived;
  }

  private void Unsubscribe()
  {
    if (_subscribed is null) return;
    _subscribed.OnEventReceived -= OnEventReceived;
    _subscribed = null;
  }

  /// <summary>
  /// Raised from the service's own pull loop on a threadpool thread, so this marshals rather than
  /// touching the collection directly. Post, not InvokeAsync().Wait(): the pull loop must not be
  /// stalled by UI work, and blocking on the dispatcher from a pool thread invites a deadlock.
  /// </summary>
  private void OnEventReceived(NotificationMessageHolderType[] messages)
  {
    var rows = messages.Select(Map).ToList();
    Dispatcher.UIThread.Post(() =>
    {
      foreach (var row in rows) Events.Insert(0, row);
      while (Events.Count > MaxRows) Events.RemoveAt(Events.Count - 1);
    });
  }

  private static EventRow Map(NotificationMessageHolderType message) => new(
    DateTime.Now,
    Topic(message),
    message.ProducerReference?.Address?.Value ?? "",
    message.Message?.OuterXml ?? "");

  /// <summary>
  /// Topic is mixed content — text nodes and elements in one array — so both kinds are joined
  /// rather than assuming which dialect the camera used.
  /// </summary>
  private static string Topic(NotificationMessageHolderType message)
  {
    if (message.Topic?.Any is not { } nodes) return "";
    return string.Concat(nodes.Select(n => n.Value ?? n.InnerText)).Trim();
  }

  /// <summary>
  /// Camera.ParseEvent is what a host with its own notification endpoint would use on a raw SOAP
  /// envelope. Nothing on the pull path reaches it, so this panel is the only way to exercise it.
  /// </summary>
  [RelayCommand]
  private void ParseEvent()
  {
    var xml = ParseInput is { Length: > 0 } input ? input : BuildEnvelopeFromSelection();
    if (xml is null) { ParseResult = "paste a SOAP envelope, or select a received event first"; return; }

    try
    {
      var notify = Camera.ParseEvent(xml);
      ParseResult = notify is null
        ? "ParseEvent returned null"
        : $"parsed {notify.NotificationMessage?.Length ?? 0} notification message(s): " +
          string.Join(", ", (notify.NotificationMessage ?? []).Select(Topic));
    }
    catch (Exception ex)
    {
      ParseResult = OnvifError.Describe(ex);
    }
  }

  private string? BuildEnvelopeFromSelection()
  {
    if (Selected is not { Xml.Length: > 0 } row) return null;
    return $"""
      <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope">
        <s:Body>
          <wsnt:Notify xmlns:wsnt="http://docs.oasis-open.org/wsn/b-2">
            <wsnt:NotificationMessage>{row.Xml}</wsnt:NotificationMessage>
          </wsnt:Notify>
        </s:Body>
      </s:Envelope>
      """;
  }
}
