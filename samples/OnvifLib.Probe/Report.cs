using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnvifLib.Probe;

public enum StepStatus { Ok, Fail, Skip }

public sealed record StepResult(
  string Section,
  string Name,
  [property: JsonConverter(typeof(JsonStringEnumConverter))] StepStatus Status,
  long Ms,
  string? Message);

public sealed record SectionSummary(string Section, int Ok, int Fail, int Skip, long Ms);

/// <summary>
/// Everything the run produced, in a shape that survives being written to disk — so two runs
/// (two cameras, or the same camera across a firmware upgrade) can be diffed.
/// </summary>
public sealed class Report
{
  public string Target { get; set; } = "";
  public string User { get; set; } = "";
  public DateTime StartedUtc { get; } = DateTime.UtcNow;
  public bool AllowWrites { get; set; }
  public bool Connected { get; set; }

  public List<StepResult> Steps { get; } = [];
  /// <summary>Services the device advertised and the library could resolve.</summary>
  public List<string> AvailableServices { get; } = [];
  public List<string> UnavailableServices { get; } = [];

  public int Ok => Steps.Count(s => s.Status == StepStatus.Ok);
  public int Fail => Steps.Count(s => s.Status == StepStatus.Fail);
  public int Skipped => Steps.Count(s => s.Status == StepStatus.Skip);

  public IEnumerable<SectionSummary> Sections =>
    Steps.GroupBy(s => s.Section)
         .Select(g => new SectionSummary(
           g.Key,
           g.Count(s => s.Status == StepStatus.Ok),
           g.Count(s => s.Status == StepStatus.Fail),
           g.Count(s => s.Status == StepStatus.Skip),
           g.Sum(s => s.Ms)));

  public void Write(string path)
    => File.WriteAllText(path, JsonSerializer.Serialize(new
    {
      Target,
      User,
      StartedUtc,
      AllowWrites,
      Connected,
      Totals = new { Ok, Fail, Skip = Skipped },
      AvailableServices,
      UnavailableServices,
      Sections = Sections.ToList(),
      Steps,
    }, JsonOptions));

  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    // The report is read by people and diffed by tools, never embedded in HTML, so the default
    // escaping of '+', '<' and non-ASCII only makes it harder to read.
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  };
}
