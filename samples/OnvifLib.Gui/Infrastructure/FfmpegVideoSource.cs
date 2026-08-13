using System.Diagnostics;
using System.Buffers;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Runs ffmpeg and reads fixed-size BGRA frames from its stdout.
/// </summary>
/// <remarks>
/// The frame size is chosen by us (<c>scale=WxH</c>), so the reader is a loop of ReadExactly —
/// no container parsing. stderr is drained on a second task: redirecting it and not reading
/// deadlocks the child as soon as the pipe buffer fills.
/// </remarks>
public sealed class FfmpegVideoSource : IAsyncDisposable
{
  private Process? _process;
  private CancellationTokenSource? _cancellation;
  private Task? _stdoutTask;
  private Task? _stderrTask;
  private readonly object _stderrLock = new();
  private readonly Queue<string> _stderrTail = new();
  private byte[]? _latestFrame;
  private int _uiPosted;
  private int _framesThisSecond;
  private long _secondStart;
  private bool _disposed;

  public int Width { get; private set; }
  public int Height { get; private set; }
  public int FrameBytes => Width * Height * 4;
  public bool IsRunning => _process is { HasExited: false };
  public double ActualFps { get; private set; }
  public TimeSpan TimeToFirstFrame { get; private set; }

  /// <summary>Raised on the UI thread with the most recent frame. Older frames are dropped.</summary>
  public event Action<byte[], int, int>? FrameArrived;

  /// <summary>Raised on the UI thread when ffmpeg exits before Stop is called.</summary>
  public event Action<string>? Failed;

  public Task StartAsync(string ffmpegPath, string uri, int width, int height, int fps)
  {
    ObjectDisposedException.ThrowIf(_disposed, this);
    if (width < 16 || height < 16) throw new ArgumentOutOfRangeException(nameof(width), "Frame size is too small.");
    if (fps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(fps));

    Stop();

    Width = width;
    Height = height;
    ActualFps = 0;
    TimeToFirstFrame = TimeSpan.Zero;
    _framesThisSecond = 0;
    _secondStart = Stopwatch.GetTimestamp();

    var cancellation = new CancellationTokenSource();
    _cancellation = cancellation;

    var start = new ProcessStartInfo(ffmpegPath)
    {
      UseShellExecute = false,
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = false,
    };

    // ArgumentList, never Arguments: the RTSP URI carries ':', '@', '/' and percent escapes.
    foreach (var argument in BuildArguments(uri, width, height, fps))
      start.ArgumentList.Add(argument);

    Process process;
    try
    {
      process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {ffmpegPath}.");
    }
    catch (Exception ex)
    {
      throw new InvalidOperationException($"Could not start ffmpeg at {ffmpegPath}: {ex.Message}", ex);
    }

    _process = process;
    var started = Stopwatch.StartNew();

    _stderrTask = Task.Run(() => DrainStderr(process, cancellation.Token), CancellationToken.None);
    _stdoutTask = Task.Run(() => ReadFrames(process, started, cancellation.Token), CancellationToken.None);
    return Task.CompletedTask;
  }

  public void Stop()
  {
    var cancellation = _cancellation;
    var process = _process;
    var stdout = _stdoutTask;
    var stderr = _stderrTask;

    _cancellation = null;
    _process = null;
    _stdoutTask = null;
    _stderrTask = null;
    _latestFrame = null;
    _uiPosted = 0;

    try { cancellation?.Cancel(); }
    catch (ObjectDisposedException) { }

    if (process is not null)
    {
      try
      {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
      }
      catch (Exception) { /* already gone, or we lack permission — either way it is not ours anymore */ }

      try { process.Dispose(); }
      catch (Exception) { }
    }

    // Do not block the UI thread waiting for the reader: Stop is called from Disconnect and from
    // Play (to replace the previous stream). The tasks observe the cancellation and the killed
    // process, and DisposeAsync waits for them at shutdown.
    _ = AwaitReaders(stdout, stderr, cancellation);
  }

  public async ValueTask DisposeAsync()
  {
    if (_disposed) return;
    _disposed = true;

    var cancellation = _cancellation;
    var process = _process;
    var stdout = _stdoutTask;
    var stderr = _stderrTask;

    _cancellation = null;
    _process = null;
    _stdoutTask = null;
    _stderrTask = null;

    try { cancellation?.Cancel(); }
    catch (ObjectDisposedException) { }

    if (process is not null)
    {
      try
      {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
      }
      catch (Exception) { }

      try { process.Dispose(); }
      catch (Exception) { }
    }

    await AwaitReaders(stdout, stderr, cancellation).ConfigureAwait(false);
    _latestFrame = null;
  }

  private static async Task AwaitReaders(Task? stdout, Task? stderr, CancellationTokenSource? cancellation)
  {
    try
    {
      if (stdout is not null) await stdout.ConfigureAwait(false);
    }
    catch (Exception) { }

    try
    {
      if (stderr is not null) await stderr.ConfigureAwait(false);
    }
    catch (Exception) { }

    cancellation?.Dispose();
  }

  private static IEnumerable<string> BuildArguments(string uri, int width, int height, int fps)
  {
    // Decode every frame the camera sends. An fps= filter plus nobuffer/tiny probesize is how
    // RTSP timestamps collapse onto I-frames and the UI looks like a slideshow.
    _ = fps;
    yield return "-hide_banner";
    yield return "-loglevel";
    yield return "error";
    yield return "-nostdin";
    yield return "-rtsp_transport";
    yield return "tcp";
    yield return "-fflags";
    yield return "+genpts";
    yield return "-flags";
    yield return "low_delay";
    yield return "-i";
    yield return uri;
    yield return "-an";
    yield return "-vsync";
    yield return "0";
    yield return "-vf";
    yield return $"scale={width}:{height}:flags=fast_bilinear";
    yield return "-f";
    yield return "rawvideo";
    yield return "-pix_fmt";
    yield return "bgra";
    yield return "-";
  }

  private async Task ReadFrames(Process process, Stopwatch started, CancellationToken cancellation)
  {
    var frameSize = FrameBytes;
    var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
    var first = true;

    try
    {
      var stream = process.StandardOutput.BaseStream;
      while (!cancellation.IsCancellationRequested)
      {
        try
        {
          await stream.ReadExactlyAsync(buffer.AsMemory(0, frameSize), cancellation).ConfigureAwait(false);
        }
        catch (EndOfStreamException) { break; }
        catch (OperationCanceledException) { break; }
        catch (IOException) { break; }

        if (cancellation.IsCancellationRequested) break;

        if (first)
        {
          first = false;
          TimeToFirstFrame = started.Elapsed;
        }

        TickFps();

        var copy = new byte[frameSize];
        Buffer.BlockCopy(buffer, 0, copy, 0, frameSize);
        Volatile.Write(ref _latestFrame, copy);
        ScheduleUiPush();
      }
    }
    finally
    {
      ArrayPool<byte>.Shared.Return(buffer);
    }

    if (cancellation.IsCancellationRequested) return;

    var exit = WaitForExitCode(process);
    var stderr = SnapshotStderr();
    var message = exit is int code
      ? $"ffmpeg exited with code {code}{(stderr.Length == 0 ? "" : $": {stderr}")}"
      : stderr.Length == 0 ? "ffmpeg ended without producing a frame" : stderr;

    Avalonia.Threading.Dispatcher.UIThread.Post(() => Failed?.Invoke(message));
  }

  private void DrainStderr(Process process, CancellationToken cancellation)
  {
    try
    {
      while (!cancellation.IsCancellationRequested)
      {
        var line = process.StandardError.ReadLine();
        if (line is null) break;
        if (line.Length == 0) continue;
        lock (_stderrLock)
        {
          _stderrTail.Enqueue(line);
          while (_stderrTail.Count > 12) _stderrTail.Dequeue();
        }
      }
    }
    catch (Exception) { /* the process is going away; stderr is only diagnostic */ }
  }

  private void TickFps()
  {
    _framesThisSecond++;
    var now = Stopwatch.GetTimestamp();
    var elapsed = Stopwatch.GetElapsedTime(_secondStart, now);
    if (elapsed.TotalSeconds < 1) return;
    ActualFps = _framesThisSecond / elapsed.TotalSeconds;
    _framesThisSecond = 0;
    _secondStart = now;
  }

  private void ScheduleUiPush()
  {
    // One cell, not a queue: if the UI is behind, it draws the latest frame and the rest are
    // dropped. Posting every frame at 12 fps is fine; posting a backlog is how latency grows.
    if (Interlocked.Exchange(ref _uiPosted, 1) != 0) return;

    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
      Interlocked.Exchange(ref _uiPosted, 0);
      var frame = Interlocked.Exchange(ref _latestFrame, null);
      if (frame is null || _disposed) return;
      FrameArrived?.Invoke(frame, Width, Height);
    });
  }

  private string SnapshotStderr()
  {
    lock (_stderrLock) return string.Join(" | ", _stderrTail);
  }

  private static int? WaitForExitCode(Process process)
  {
    try
    {
      if (!process.HasExited) process.WaitForExit(500);
      return process.HasExited ? process.ExitCode : null;
    }
    catch (Exception)
    {
      return null;
    }
  }
}
