using System.Diagnostics;
using System.Net.Sockets;
using System.ServiceModel;
using System.ServiceModel.Security;
using System.Xml.Linq;

namespace OnvifLib.Probe;

/// <summary>
/// A failure the step itself detected rather than one thrown by the library — printed as-is,
/// with no exception type prefix and no stack trace.
/// </summary>
public sealed class ProbeFailure(string message) : Exception(message);

/// <summary>
/// Runs one library call per step, times it, catches everything, and prints a single aligned
/// line plus whatever detail the caller asked for. Also accumulates the machine-readable report.
/// </summary>
public sealed class ProbeRunner(ProbeOptions options, Report report)
{
  private const int NameWidth = 44;
  private const int Indent = 12;      // where detail lines start, under the step name

  private string _section = "";

  public Report Report => report;

  public void Section(string id, string title)
  {
    _section = id;
    Console.WriteLine();
    Con.Line(ConsoleColor.Cyan, $"── {title} {new string('─', Math.Max(4, 68 - title.Length))}");
  }

  public async Task<T?> StepAsync<T>(string name, Func<Task<T>> op, Action<T>? print = null)
  {
    var sw = Stopwatch.StartNew();
    try
    {
      var value = await op();
      sw.Stop();
      Record(name, StepStatus.Ok, sw.ElapsedMilliseconds, null);
      if (value is not null) print?.Invoke(value);
      return value;
    }
    catch (Exception ex)
    {
      sw.Stop();
      Fail(name, sw.ElapsedMilliseconds, ex);
      return default;
    }
  }

  public async Task<bool> StepAsync(string name, Func<Task> op)
  {
    var sw = Stopwatch.StartNew();
    try
    {
      await op();
      sw.Stop();
      Record(name, StepStatus.Ok, sw.ElapsedMilliseconds, null);
      return true;
    }
    catch (Exception ex)
    {
      sw.Stop();
      Fail(name, sw.ElapsedMilliseconds, ex);
      return false;
    }
  }

  public void Skip(string name, string reason)
  {
    report.Steps.Add(new StepResult(_section, name, StepStatus.Skip, 0, reason));
    Con.Write(ConsoleColor.DarkGray, "  [SKIP] ");
    Console.WriteLine($"{Pad(name)}  {reason}");
  }

  /// <summary>Marks every write in a group as skipped, with one shared reason.</summary>
  public void SkipWrites(params string[] names)
  {
    foreach (var n in names) Skip(n, "requires --allow-writes");
  }

  public void SkipDestructive(params string[] names)
  {
    foreach (var n in names) Skip(n, "destructive — never run by the probe");
  }

  /// <summary>A remark that is not a step: a caveat, a limitation, an explanation of a SKIP.</summary>
  public void Note(string text) => Con.Line(ConsoleColor.DarkGray, $"{new string(' ', Indent)}note: {text}");

  private void Record(string name, StepStatus status, long ms, string? message)
  {
    report.Steps.Add(new StepResult(_section, name, status, ms, message));
    var (color, label) = status switch
    {
      StepStatus.Ok => (ConsoleColor.Green, "[ OK ]"),
      StepStatus.Fail => (ConsoleColor.Red, "[FAIL]"),
      _ => (ConsoleColor.DarkGray, "[SKIP]"),
    };
    Con.Write(color, $"  {label} ");
    Console.WriteLine($"{Pad(name)}  {ms,6} ms");
  }

  private void Fail(string name, long ms, Exception ex)
  {
    var message = Describe(ex);
    Record(name, StepStatus.Fail, ms, message);
    Con.Line(ConsoleColor.Red, $"{new string(' ', Indent)}{message}");
    if (options.Verbose) Console.Error.WriteLine(ex.ToString());
  }

  /// <summary>
  /// Turns a WCF/ONVIF exception into one human line. WCF wraps aggressively, so the whole
  /// inner chain is searched for the most specific thing worth reporting.
  /// </summary>
  public static string Describe(Exception ex)
  {
    foreach (var e in Chain(ex))
    {
      switch (e)
      {
        case ProbeFailure:
          return e.Message;

        case OperationCanceledException:
          return "cancelled";

        // ONVIF puts the real reason (ter:NotAuthorized, ter:ActionNotSupported, …) in the
        // fault, not in the exception message, which is a generic "The creator of this fault…".
        case FaultException fault:
          var reason = fault.CreateMessageFault().Reason?.ToString() ?? fault.Message;
          var code = fault.Code?.SubCode?.Name ?? fault.Code?.Name;
          return code is null ? $"SOAP fault: {reason}" : $"SOAP fault {code}: {reason}";

        case MessageSecurityException:
          return $"authentication failed — check user/password ({e.Message})";

        // Cameras report a rejected credential in wildly different ways: a 401, a SOAP
        // ter:NotAuthorized fault, or — commonly — an HTTP 400 whose body says "Not Authorized".
        // Without this the last one surfaces as a generic communication error and reads like a
        // network problem. Same detection the library itself uses in Camera.IsAuthError.
        case not null when LooksLikeAuthFailure(e.Message):
          return $"not authorized — check user/password ({e.Message})";

        case EndpointNotFoundException:
          return $"endpoint not found — the service is not served at that address ({e.Message})";

        case TimeoutException:
          return $"timed out ({e.Message})";

        case SocketException socket:
          return $"network error: {socket.SocketErrorCode} — {socket.Message}";

        case HttpRequestException http:
          return http.StatusCode is { } status
            ? $"HTTP {(int)status} {status}"
            : $"network error: {http.Message}";

        // The base of the WCF family, so it must come after the specific ones above.
        case CommunicationException:
          return $"communication error: {e.Message}";

        // The library uses this for "client not initialized" and for refusing a read-modify-write.
        case InvalidOperationException:
          return e.Message;
      }
    }

    return $"{ex.GetType().Name}: {ex.Message}";
  }

  private static IEnumerable<Exception> Chain(Exception ex)
  {
    for (Exception? e = ex; e != null; e = e.InnerException) yield return e;
  }

  public static bool LooksLikeAuthFailure(string message) =>
    message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)
    || message.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase)
    || message.Contains("not Authorized", StringComparison.OrdinalIgnoreCase);

  private static string Pad(string name) => name.Length >= NameWidth ? name : name.PadRight(NameWidth);

  // ── detail printing ────────────────────────────────────────────────────────────

  public void Value(string label, object? value)
  {
    var text = Format(value);
    Console.Write(new string(' ', Indent));
    Con.Write(ConsoleColor.DarkGray, label.PadRight(22));
    Console.WriteLine(text);
  }

  public void Values(params (string Label, object? Value)[] pairs)
  {
    foreach (var (label, value) in pairs) Value(label, value);
  }

  /// <summary>A column-aligned table, or a "(none)" line when there is nothing to show.</summary>
  public void Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<object?>> rows)
  {
    var data = rows.Select(r => r.Select(Format).ToArray()).ToList();
    if (data.Count == 0) { Con.Line(ConsoleColor.DarkGray, $"{new string(' ', Indent)}(none)"); return; }

    var widths = headers.Select((h, i) =>
      Math.Min(48, Math.Max(h.Length, data.Max(r => i < r.Length ? r[i].Length : 0)))).ToArray();

    var pad = new string(' ', Indent);
    Con.Line(ConsoleColor.DarkGray, pad + string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))));
    foreach (var row in data)
      Console.WriteLine(pad + string.Join("  ", row.Select((c, i) => Truncate(c, widths[i]).PadRight(widths[i]))).TrimEnd());
  }

  /// <summary>Multi-line text (raw XML, a long URI) indented under the step.</summary>
  public void Block(string text, int maxLines = 40)
  {
    var pad = new string(' ', Indent);
    var lines = text.Replace("\r\n", "\n").Split('\n');
    foreach (var line in lines.Take(maxLines)) Con.Line(ConsoleColor.DarkGray, pad + line);
    if (lines.Length > maxLines)
      Con.Line(ConsoleColor.DarkGray, $"{pad}… {lines.Length - maxLines} more line(s)");
  }

  /// <summary>Indents XML for reading. Returns the input unchanged when it will not parse.</summary>
  public static string PrettyXml(string xml)
  {
    try { return XDocument.Parse(xml).ToString(); }
    catch (System.Xml.XmlException) { return xml; }
  }

  private static string Format(object? value) => value switch
  {
    null => "—",
    string s => s.Length == 0 ? "—" : s,
    bool b => b ? "yes" : "no",
    DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss"),
    TimeSpan t => t.ToString(@"hh\:mm\:ss\.fff"),
    float f => f.ToString("0.###"),
    double d2 => d2.ToString("0.###"),
    System.Collections.IEnumerable e and not string => string.Join(", ", e.Cast<object?>().Select(Format)) is { Length: > 0 } j ? j : "—",
    _ => value.ToString() ?? "—",
  };

  private static string Truncate(string s, int width) =>
    s.Length <= width ? s : s[..Math.Max(1, width - 1)] + "…";
}

/// <summary>Colour that turns itself off when the output is piped or NO_COLOR is set.</summary>
public static class Con
{
  private static readonly bool Enabled =
    !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;

  public static void Write(ConsoleColor color, string text)
  {
    if (!Enabled) { Console.Write(text); return; }
    var previous = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.Write(text);
    Console.ForegroundColor = previous;
  }

  public static void Line(ConsoleColor color, string text)
  {
    Write(color, text);
    Console.WriteLine();
  }
}
