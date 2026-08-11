using System.Threading.Channels;

namespace OnvifLib.Gui.Infrastructure;

public enum LogLevel { Debug, Info, Warning, Error }

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Message)
{
  public string TimeText => Time.ToString("HH:mm:ss.fff");
  public string LevelText => Level.ToString().ToLowerInvariant();

  /// <summary>The grid shows one line; the detail pane below it shows the whole thing.</summary>
  public string FirstLine
  {
    get
    {
      var end = Message.IndexOfAny(['\r', '\n']);
      var line = end < 0 ? Message : Message[..end];
      return line.Length <= 300 ? line : line[..300] + "…";
    }
  }
}

/// <summary>
/// The library's log, on its way to the Log tab.
/// </summary>
/// <remarks>
/// Ingestion has to be free: with SOAP capture on, CustomMessageInspector emits two large strings
/// per call, and the event pull loop logs every few seconds from its own thread. So the four
/// interface methods do nothing but TryWrite into a bounded channel — lock-free, never blocking,
/// never throwing, dropping the oldest entry when full. A single reader drains it in batches, so
/// a flood becomes a few dispatcher posts instead of ten thousand.
/// </remarks>
public sealed class UiLogger : IOnvifLogger
{
  private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(
    new BoundedChannelOptions(10_000)
    {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
    });

  /// <summary>
  /// Applied at ingestion, so filtering Debug out actually removes the cost rather than hiding it.
  /// </summary>
  public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

  public void Debug(string message) => Write(LogLevel.Debug, message);
  public void Info(string message) => Write(LogLevel.Info, message);
  public void Warning(string message) => Write(LogLevel.Warning, message);
  public void Error(string message) => Write(LogLevel.Error, message);

  private void Write(LogLevel level, string message)
  {
    if (level < MinimumLevel) return;
    _channel.Writer.TryWrite(new LogEntry(DateTime.Now, level, message));
  }

  /// <summary>Reads batches until the application exits. One consumer only.</summary>
  public async IAsyncEnumerable<List<LogEntry>> ReadBatchesAsync(
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
  {
    var reader = _channel.Reader;
    while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
    {
      var batch = new List<LogEntry>(200);
      while (batch.Count < 200 && reader.TryRead(out var entry)) batch.Add(entry);
      if (batch.Count > 0) yield return batch;
    }
  }
}
