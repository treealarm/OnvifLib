namespace OnvifLib
{
  /// <summary>One topic the camera says it can raise.</summary>
  /// <param name="Path">
  /// The topic in ConcreteSet form, e.g. <c>tns1:VideoSource/MotionAlarm</c> — the exact string a
  /// topic filter takes, so a caller can hand it straight back to
  /// <see cref="EventService1.SetTopicFilterAsync"/>.
  /// </param>
  /// <param name="NamespaceUri">The namespace the prefix of <paramref name="Path"/> stands for.</param>
  /// <param name="MessageDescriptionXml">
  /// The topic's MessageDescription (which Source/Data items it carries), raw, or null when the
  /// camera did not describe it.
  /// </param>
  public record OnvifEventTopic(string Path, string NamespaceUri, string? MessageDescriptionXml);

  /// <summary>What GetEventProperties reports: the topic tree and the filter dialects accepted.</summary>
  public record OnvifEventProperties(
    IReadOnlyList<OnvifEventTopic> Topics,
    bool FixedTopicSet,
    IReadOnlyList<string> TopicExpressionDialects,
    IReadOnlyList<string> MessageContentFilterDialects,
    IReadOnlyList<string> TopicNamespaceLocations);

  /// <summary>
  /// The event service's own capabilities. Zero/false means the camera did not say, not that it
  /// said no — the schema makes every one of these optional.
  /// </summary>
  /// <param name="MaxPullPoints">
  /// How many pull points the camera keeps at once. Small numbers (2–4) are common, which is why a
  /// filter change unsubscribes the old pull point before creating the new one.
  /// </param>
  /// <param name="WsSubscriptionPolicySupport">The camera accepts WS-Notification subscription policies.</param>
  /// <param name="WsPausableSubscriptionManagerInterfaceSupport">Subscriptions can be paused and resumed.</param>
  /// <param name="MaxNotificationProducers">How many push (Basic Notification) subscriptions it keeps at once.</param>
  /// <param name="PersistentNotificationStorage">Notifications survive a reboot.</param>
  public record OnvifEventCapabilities(
    bool WsSubscriptionPolicySupport,
    bool WsPausableSubscriptionManagerInterfaceSupport,
    int MaxNotificationProducers,
    int MaxPullPoints,
    bool PersistentNotificationStorage);
}
