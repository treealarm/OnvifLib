using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// The single path from a button to a library call: it moves the work off the UI thread, times
/// it, catches everything, and reports the outcome in the status bar and the log.
/// </summary>
/// <remarks>
/// <para>
/// The Task.Run is not decoration. OnvifLib never calls ConfigureAwait(false), so every await
/// inside it captures the caller's SynchronizationContext — awaiting a library call directly from
/// the dispatcher would route every internal continuation (retry backoffs, the recording-search
/// poll loop, the scanner's parallel workers) back through the UI thread. Starting on the pool
/// gives those continuations no context to capture; the single ConfigureAwait(true) here is what
/// brings the result back.
/// </para>
/// <para>
/// It never rethrows. Callers branch on the returned flag, and
/// <c>Ok == true</c> with a null value is a real outcome for the several library methods that
/// return null to mean "unsupported" or "failed, and I logged it".
/// </para>
/// </remarks>
public sealed partial class OperationRunner(UiLogger logger) : ObservableObject
{
  [ObservableProperty] private bool _isBusy;
  [ObservableProperty] private string _status = "Ready";
  [ObservableProperty] private bool _statusIsError;

  // A counter, not a flag: two tabs running at once must not have the first one to finish clear
  // the busy indicator for both.
  private int _busyCount;

  public async Task<(bool Ok, T? Value)> RunAsync<T>(string what, Func<Task<T>> operation)
  {
    Begin(what);
    var stopwatch = Stopwatch.StartNew();
    try
    {
      var value = await Task.Run(operation).ConfigureAwait(true);
      Succeed(what, stopwatch.ElapsedMilliseconds);
      return (true, value);
    }
    catch (Exception ex)
    {
      ReportFailure(what, ex);
      return (false, default);
    }
    finally { End(); }
  }

  public async Task<bool> RunAsync(string what, Func<Task> operation)
  {
    Begin(what);
    var stopwatch = Stopwatch.StartNew();
    try
    {
      await Task.Run(operation).ConfigureAwait(true);
      Succeed(what, stopwatch.ElapsedMilliseconds);
      return true;
    }
    catch (Exception ex)
    {
      ReportFailure(what, ex);
      return false;
    }
    finally { End(); }
  }

  /// <summary>Reports an outcome the caller determined itself, without running anything.</summary>
  public void Report(string message, bool isError = false)
  {
    Status = message;
    StatusIsError = isError;
    if (isError) logger.Warning(message); else logger.Info(message);
  }

  private void Begin(string what)
  {
    _busyCount++;
    IsBusy = true;
    Status = what + "…";
    StatusIsError = false;
    logger.Info($"→ {what}");
  }

  private void Succeed(string what, long ms)
  {
    Status = $"{what} — OK ({ms} ms)";
    StatusIsError = false;
    logger.Info($"✓ {what} ({ms} ms)");
  }

  private void ReportFailure(string what, Exception ex)
  {
    var message = OnvifError.Describe(ex);
    // Cancellation is an outcome the user asked for, not a fault.
    var cancelled = ex is OperationCanceledException;
    Status = cancelled ? $"{what} — cancelled" : $"{what} — {message}";
    StatusIsError = !cancelled;
    if (cancelled) logger.Info($"· {what} cancelled");
    else logger.Error($"✗ {what}: {message}{Environment.NewLine}{ex}");
  }

  private void End()
  {
    _busyCount = Math.Max(0, _busyCount - 1);
    IsBusy = _busyCount > 0;
  }
}
