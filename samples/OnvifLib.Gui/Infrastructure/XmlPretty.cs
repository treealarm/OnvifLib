using System.Xml;
using System.Xml.Linq;

namespace OnvifLib.Gui.Infrastructure;

public static class XmlPretty
{
  /// <summary>
  /// Indents XML for reading, returning the input unchanged when it will not parse. Several
  /// library methods hand back raw XML by design (the analytics options and rule calls), and the
  /// event payloads arrive as XmlElement, so this is used on real device output that is not
  /// guaranteed to be well formed.
  /// </summary>
  public static string Format(string? xml)
  {
    if (string.IsNullOrWhiteSpace(xml)) return string.Empty;
    try { return XDocument.Parse(xml).ToString(); }
    catch (XmlException) { return xml; }
  }

  public static string Format(XmlElement? element) => element is null ? string.Empty : Format(element.OuterXml);
}
