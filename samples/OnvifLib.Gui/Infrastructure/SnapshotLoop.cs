using Avalonia.Threading;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Polls snapshots on a timer without ever overlapping two requests.
/// </summary>
/// <remarks>
/// The body is sequential — the next tick is not awaited until the previous fetch has finished —
/// and PeriodicTimer drops ticks that elapse while the body runs rather than queuing them. A slow
/// camera therefore degrades to "as fast as it can answer" instead of piling up requests.
/// </remarks>
public sealed class SnapshotLoop : IAsyncDisposable
{
  private CancellationTokenSource? _cancellation;
  private Task? _loop;

  public bool IsRunning => _loop is { IsCompleted: false };

  public void Start(int intervalMs, Func<Task<ImageResult?>> fetch, Action<ImageResult?> onFrame, Action<Exception> onError)
  {
    Stop();

    var cancellation = new CancellationTokenSource();
    _cancellation = cancellation;

    // On the pool for the same reason every other library call is: OnvifLib never calls
    // ConfigureAwait(false), so a loop started on the dispatcher would marshal each of its
    // internal continuations back through the UI thread.
    _loop = Task.Run(async () =>
    {
      using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
      try
      {
        while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
        {
          ImageResult? frame = null;
          try { frame = await fetch().ConfigureAwait(false); }
          catch (OperationCanceledException) { break; }
          catch (Exception ex) { Dispatcher.UIThread.Post(() => onError(ex)); continue; }

          if (cancellation.IsCancellationRequested) break;
          Dispatcher.UIThread.Post(() => onFrame(frame));
        }
      }
      catch (OperationCanceledException) { /* the expected way out */ }
    }, CancellationToken.None);
  }

  public void Stop() => _cancellation?.Cancel();

  /// <summary>
  /// Cancels and waits, so no frame can land after the caller has torn down what it would land in.
  /// </summary>
  public async ValueTask DisposeAsync()
  {
    _cancellation?.Cancel();
    if (_loop is { } loop)
    {
      try { await loop.ConfigureAwait(false); }
      catch (OperationCanceledException) { }
    }
    _cancellation?.Dispose();
    _cancellation = null;
    _loop = null;
  }
}
