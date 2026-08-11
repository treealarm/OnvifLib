using System.Globalization;
using System.Xml;

namespace OnvifLib
{
  public static class OnvifDuration
  {
    // ONVIF DelayTime/DelayTimes are spec'd as xs:duration (e.g. "PT5S"), but some cameras
    // (e.g. F-IC-2642C2MSZ4) return a bare number of seconds ("5") instead. Parse the spec form
    // first, then fall back to a plain numeric-seconds value so those cameras don't blow up with
    // "The string '5' is not a valid TimeSpan value."
    public static int ToMs(string? duration)
    {
      if (string.IsNullOrWhiteSpace(duration)) return 0;
      try
      {
        return (int)XmlConvert.ToTimeSpan(duration).TotalMilliseconds;
      }
      catch (FormatException)
      {
        return double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
          ? (int)(seconds * 1000)
          : 0;
      }
    }

    public static string FromMs(int ms) => XmlConvert.ToString(TimeSpan.FromMilliseconds(ms));
  }

  // Settings only — ONVIF's GetRelayOutputs has no field for the relay's current logical
  // (active/inactive) state, only its configured Mode/DelayTime/IdleState (see RelayOutputSettings
  // in onvif.xsd). Live relay state cannot be polled; only tracked client-side after a command.
  public record OnvifRelayOutput(string Token, string Mode, string IdleState, int DelayMs);

  public record OnvifRelayOutputOptions(string Token, List<string> SupportedModes, bool Discrete, int MinDelayMs, int MaxDelayMs, List<int>? DiscreteDelaysMs);

  // IdleState is null when the camera didn't report it (IdleStateSpecified == false) — GetDigitalInputs
  // has no live logical-state field either (per onvif.xsd DigitalInput), only the idle-state config.
  public record OnvifDigitalInput(string Token, string? IdleState);

  public record OnvifDigitalInputOptions(string Token, bool IdleStateConfigurable);
}
