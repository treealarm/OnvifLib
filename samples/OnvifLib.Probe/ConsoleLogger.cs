namespace OnvifLib.Probe;

public enum LogLevel { Debug, Info, Warning, Error }

/// <summary>
/// The probe's <see cref="IOnvifLogger"/>. Everything goes to stderr, so the report on stdout
/// stays clean and pipeable (<c>probe &gt; report.txt</c> keeps the diagnostics on screen).
/// </summary>
/// <remarks>
/// A non-null logger is always passed to <c>Camera.Create</c>, even at the default level: several
/// library methods swallow their exception and only log it — <c>MediaService.GetImage()</c>
/// returns null, and <c>Camera.DoGetServices()</c> returns null — so without a logger those
/// failures have no explanation anywhere. The cost is that CustomMessageInspector buffers every
/// SOAP message to dump it; at Debug level those dumps are also printed, everything above drops
/// them on the floor.
/// </remarks>
public sealed class ConsoleLogger(LogLevel minimum) : IOnvifLogger
{
  private readonly object _lock = new();

  public void Debug(string message) => Write(LogLevel.Debug, ConsoleColor.DarkGray, message);
  public void Info(string message) => Write(LogLevel.Info, ConsoleColor.DarkGray, message);
  public void Warning(string message) => Write(LogLevel.Warning, ConsoleColor.Yellow, message);
  public void Error(string message) => Write(LogLevel.Error, ConsoleColor.Red, message);

  private void Write(LogLevel level, ConsoleColor color, string message)
  {
    if (level < minimum) return;
    // The library logs from its pull loop and from parallel scan workers, so interleaving is real.
    lock (_lock)
    {
      var previous = Console.ForegroundColor;
      var colored = !Console.IsErrorRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null;
      if (colored) Console.ForegroundColor = color;
      Console.Error.WriteLine($"    {level.ToString().ToLowerInvariant(),-7} {message}");
      if (colored) Console.ForegroundColor = previous;
    }
  }
}
