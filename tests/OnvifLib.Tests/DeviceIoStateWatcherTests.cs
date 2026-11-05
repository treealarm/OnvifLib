using System.Xml;
using OnvifLib;

namespace OnvifLib.Tests;

// Device/Trigger property events are the only way ONVIF reports the live state of a relay or an
// input. The parser is where a camera's spelling becomes a boolean, so the cases below are the
// ones that matter: both kinds, both spellings of the logical state, the contact a relay adds, and
// the messages that are not a state at all.
public class DeviceIoStateWatcherTests
{
  private const string RelayTopic = "tns1:Device/Trigger/Relay";
  private const string InputTopic = "tns1:Device/Trigger/DigitalInput";

  private static XmlElement Message(string source, string data, string operation = "Changed",
    string utcTime = "2026-11-05T10:00:00Z") =>
    Element($"""
      <tt:Message xmlns:tt="http://www.onvif.org/ver10/schema"
                  UtcTime="{utcTime}" PropertyOperation="{operation}">
        <tt:Source>{source}</tt:Source>
        <tt:Data>{data}</tt:Data>
      </tt:Message>
      """);

  private static XmlElement Element(string xml)
  {
    var document = new XmlDocument();
    document.LoadXml(xml);
    return document.DocumentElement!;
  }

  private static string Item(string name, string value) =>
    $"""<tt:SimpleItem Name="{name}" Value="{value}"/>""";

  [Fact]
  public void ReadsTheInitialStateOfARelay()
  {
    var message = Message(
      Item("RelayToken", "lamp"),
      Item("LogicalState", "inactive") + Item("PhysicalState", "open"),
      operation: "Initialized");

    var state = DeviceIoStateWatcher.Parse(RelayTopic, message);

    Assert.NotNull(state);
    Assert.Equal(DeviceIoKind.Relay, state.Kind);
    Assert.Equal("lamp", state.Token);
    Assert.False(state.LogicalState);
    Assert.False(state.ContactClosed);
    Assert.Equal("Initialized", state.PropertyOperation);
    Assert.Equal(DateTimeOffset.Parse("2026-11-05T10:00:00Z"), state.Time);
  }

  [Fact]
  public void ReadsAChangedRelayWithItsClosedContact()
  {
    var state = DeviceIoStateWatcher.Parse(RelayTopic, Message(
      Item("RelayToken", "lamp"),
      Item("LogicalState", "active") + Item("PhysicalState", "closed")));

    Assert.NotNull(state);
    Assert.True(state.LogicalState);
    Assert.True(state.ContactClosed);
  }

  [Fact]
  public void ReadsADigitalInput()
  {
    var state = DeviceIoStateWatcher.Parse(InputTopic, Message(
      Item("InputToken", "sensor"),
      Item("LogicalState", "true"),
      operation: "Initialized"));

    Assert.NotNull(state);
    Assert.Equal(DeviceIoKind.DigitalInput, state.Kind);
    Assert.Equal("sensor", state.Token);
    Assert.True(state.LogicalState);
    // An input has no contact of its own.
    Assert.Null(state.ContactClosed);
  }

  [Theory]
  [InlineData("active", true)]
  [InlineData("inactive", false)]
  [InlineData("true", true)]
  [InlineData("false", false)]
  [InlineData("ACTIVE", true)]
  public void BothSpellingsOfTheLogicalStateAreUnderstood(string value, bool expected)
  {
    var state = DeviceIoStateWatcher.Parse(RelayTopic, Message(
      Item("RelayToken", "lamp"),
      Item("LogicalState", value)));

    Assert.NotNull(state);
    Assert.Equal(expected, state.LogicalState);
  }

  [Fact]
  public void ARelayThatDoesNotReportItsContactHasNoContactState()
  {
    var state = DeviceIoStateWatcher.Parse(RelayTopic, Message(
      Item("RelayToken", "lamp"),
      Item("LogicalState", "active")));

    Assert.NotNull(state);
    Assert.Null(state.ContactClosed);
  }

  [Fact]
  public void IgnoresAnUnrelatedTopic()
  {
    var message = Message(Item("RelayToken", "lamp"), Item("LogicalState", "active"));

    Assert.Null(DeviceIoStateWatcher.Parse("tns1:VideoSource/MotionAlarm", message));
  }

  [Fact]
  public void IgnoresAMessageWithNoLogicalState()
  {
    Assert.Null(DeviceIoStateWatcher.Parse(RelayTopic, Message(
      Item("RelayToken", "lamp"), Item("PhysicalState", "closed"))));
    Assert.Null(DeviceIoStateWatcher.Parse(RelayTopic, Message(
      Item("RelayToken", "lamp"), Item("LogicalState", "on"))));
  }

  [Fact]
  public void IgnoresAMessageWithNoToken()
  {
    Assert.Null(DeviceIoStateWatcher.Parse(RelayTopic, Message(
      Item("SomeOtherItem", "lamp"), Item("LogicalState", "active"))));
  }

  [Fact]
  public void IgnoresAMessageWithNoPayload()
  {
    Assert.Null(DeviceIoStateWatcher.Parse(RelayTopic, null));
    Assert.Null(DeviceIoStateWatcher.Parse(null, Message(
      Item("RelayToken", "lamp"), Item("LogicalState", "active"))));
  }

  [Fact]
  public void RecognizesATopicWrittenAsAFullUri()
  {
    var state = DeviceIoStateWatcher.Parse(
      "http://www.onvif.org/ver10/topics/Device/Trigger/DigitalInput",
      Message(Item("InputToken", "sensor"), Item("LogicalState", "false")));

    Assert.NotNull(state);
    Assert.Equal(DeviceIoKind.DigitalInput, state.Kind);
  }

  [Fact]
  public void AMessageWithoutATimeStillParses()
  {
    var message = Element("""
      <tt:Message xmlns:tt="http://www.onvif.org/ver10/schema" PropertyOperation="Changed">
        <tt:Source><tt:SimpleItem Name="RelayToken" Value="lamp"/></tt:Source>
        <tt:Data><tt:SimpleItem Name="LogicalState" Value="active"/></tt:Data>
      </tt:Message>
      """);

    var state = DeviceIoStateWatcher.Parse(RelayTopic, message);

    Assert.NotNull(state);
    Assert.Null(state.Time);
  }
}
