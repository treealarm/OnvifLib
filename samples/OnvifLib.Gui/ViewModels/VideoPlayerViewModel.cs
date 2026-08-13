using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// One live player for the selected camera. The same instance is shown on Live, Media and PTZ;
/// switching tabs must not restart the stream. ONVIF-unaware: the shell hands it profiles and a
/// URI resolver.
/// </summary>
public sealed partial class VideoPlayerViewModel : ObservableObject, IAsyncDisposable
{
  private readonly FfmpegVideoSource _source = new();
  private readonly OperationRunner _runner;
  private WriteableBitmap? _bitmap;
  private int _bitmapWidth;
  private int _bitmapHeight;
  private bool _playing;
  private bool _syncingProfiles;

  public VideoPlayerViewModel(OperationRunner runner)
  {
    _runner = runner;
    _source.FrameArrived += OnFrameArrived;
    _source.Failed += OnFailed;
    RefreshFfmpegStatus();
  }

  public ObservableCollection<OnvifProfileInfo> Profiles { get; } = [];

  /// <summary>Fetches an RTSP URI (credentials already spliced) for the chosen profile token.</summary>
  public Func<string, Task<string?>>? ResolveUri { get; set; }

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
  private OnvifProfileInfo? _selectedProfile;

  [ObservableProperty] private int _frameWidth = 640;
  [ObservableProperty] private int _frameHeight = 360;
  [ObservableProperty] private int _frameRate = 12;

  [ObservableProperty] private string _customFfmpegPath = "";
  [ObservableProperty] private string _resolvedFfmpegPath = "";
  [ObservableProperty] private string _status = "idle";
  [ObservableProperty] private bool _statusIsError;
  [ObservableProperty] private bool _isDownloading;
  [ObservableProperty] private double _actualFps;
  [ObservableProperty] private string _stats = "";

  [ObservableProperty]
  [NotifyCanExecuteChangedFor(nameof(StopCommand))]
  private bool _isPlaying;

  [ObservableProperty] private WriteableBitmap? _frame;

  /// <summary>Live tabs show the profile picker; Profile G only plays archive URIs.</summary>
  [ObservableProperty] private bool _showStreamPicker = true;

  [ObservableProperty] private string _emptyHint = "no video";

  public bool HasProfile => SelectedProfile is not null;

  public void SetProfiles(IEnumerable<OnvifProfileInfo> profiles, OnvifProfileInfo? preferred = null)
  {
    _syncingProfiles = true;
    Profiles.Clear();
    foreach (var profile in profiles) Profiles.Add(profile);
    SelectedProfile = preferred ?? PreferSubstream(Profiles) ?? Profiles.FirstOrDefault();
    _syncingProfiles = false;
  }

  partial void OnSelectedProfileChanged(OnvifProfileInfo? value)
  {
    if (_syncingProfiles || value is null || !IsPlaying) return;
    _ = PlaySelectedAsync();
  }

  public void ClearProfiles()
  {
    Profiles.Clear();
    SelectedProfile = null;
  }

  public void RefreshFfmpegStatus()
  {
    var found = FfmpegLocator.Find(CustomFfmpegPath);
    ResolvedFfmpegPath = found ?? "";
    if (found is not null)
    {
      if (!IsPlaying && !StatusIsError) Status = $"ffmpeg: {found}";
      return;
    }

    Status = FfmpegLocator.DescribeMissing() ?? "ffmpeg not found";
    StatusIsError = false;
  }

  partial void OnCustomFfmpegPathChanged(string value) => RefreshFfmpegStatus();

  [RelayCommand(CanExecute = nameof(CanPlay))]
  private Task PlayAsync() => PlaySelectedAsync();

  private bool CanPlay() => SelectedProfile is not null && !IsPlaying && !IsDownloading;

  public async Task PlaySelectedAsync()
  {
    if (SelectedProfile is null)
    {
      Report("Select a media profile first", isError: true);
      return;
    }

    if (ResolveUri is null)
    {
      Report("No camera is selected", isError: true);
      return;
    }

    string? uri;
    try
    {
      uri = await ResolveUri(SelectedProfile.Token).ConfigureAwait(true);
    }
    catch (Exception ex)
    {
      Report($"GetStreamUri failed: {ex.Message}", isError: true);
      return;
    }

    if (string.IsNullOrWhiteSpace(uri))
    {
      Report("The camera returned no stream URI for this profile", isError: true);
      return;
    }

    await PlayUriAsync(uri).ConfigureAwait(true);
  }

  /// <summary>Plays a ready RTSP URI (live or replay). Stops whatever was already running.</summary>
  public async Task PlayUriAsync(string uri)
  {
    await StopInternalAsync().ConfigureAwait(true);

    var ffmpeg = await EnsureFfmpegAsync().ConfigureAwait(true);
    if (ffmpeg is null) return;

    var width = Math.Clamp(FrameWidth, 160, 1920);
    var height = Math.Clamp(FrameHeight, 90, 1080);
    var fps = Math.Clamp(FrameRate, 1, 30);
    FrameWidth = width;
    FrameHeight = height;
    FrameRate = fps;

    _playing = true;
    IsPlaying = true;
    PlayCommand.NotifyCanExecuteChanged();
    StopCommand.NotifyCanExecuteChanged();
    Report($"starting ffmpeg ({width}×{height} @ {fps} fps)…");

    try
    {
      await _source.StartAsync(ffmpeg, uri, width, height, fps).ConfigureAwait(true);
    }
    catch (Exception ex)
    {
      _playing = false;
      IsPlaying = false;
      PlayCommand.NotifyCanExecuteChanged();
      StopCommand.NotifyCanExecuteChanged();
      Report(ex.Message, isError: true);
    }
  }

  [RelayCommand(CanExecute = nameof(CanStop))]
  private Task StopAsync() => StopInternalAsync();

  private bool CanStop() => IsPlaying;

  public Task StopInternalAsync()
  {
    _playing = false;
    _source.Stop();
    IsPlaying = false;
    ActualFps = 0;
    Stats = "";
    PlayCommand.NotifyCanExecuteChanged();
    StopCommand.NotifyCanExecuteChanged();
    if (!StatusIsError) Report("stopped");
    return Task.CompletedTask;
  }

  [RelayCommand]
  private async Task DownloadFfmpegAsync()
  {
    if (IsDownloading) return;
    if (!FfmpegDownloader.IsCurrentRidSupported)
    {
      Report(FfmpegDownloader.UnsupportedRidMessage ?? "download not supported", isError: true);
      return;
    }

    IsDownloading = true;
    PlayCommand.NotifyCanExecuteChanged();
    try
    {
      var progress = new Progress<string>(message => Report(message));
      var path = await Task.Run(() => FfmpegDownloader.DownloadAsync(progress, CancellationToken.None)).ConfigureAwait(true);
      CustomFfmpegPath = path;
      RefreshFfmpegStatus();
      Report($"downloaded ffmpeg to {path}");
    }
    catch (Exception ex)
    {
      Report($"ffmpeg download failed: {ex.Message}", isError: true);
    }
    finally
    {
      IsDownloading = false;
      PlayCommand.NotifyCanExecuteChanged();
    }
  }

  public async ValueTask DisposeAsync()
  {
    await StopInternalAsync().ConfigureAwait(true);
    await _source.DisposeAsync().ConfigureAwait(true);
    _bitmap?.Dispose();
    _bitmap = null;
    Frame = null;
  }

  private async Task<string?> EnsureFfmpegAsync()
  {
    RefreshFfmpegStatus();
    if (ResolvedFfmpegPath is { Length: > 0 } existing) return existing;

    if (!FfmpegDownloader.IsCurrentRidSupported)
    {
      Report(FfmpegLocator.DescribeMissing() ?? "ffmpeg not found", isError: true);
      return null;
    }

    await DownloadFfmpegAsync().ConfigureAwait(true);
    RefreshFfmpegStatus();
    if (ResolvedFfmpegPath is { Length: > 0 } downloaded) return downloaded;

    Report(FfmpegLocator.DescribeMissing() ?? "ffmpeg not found", isError: true);
    return null;
  }

  private void OnFrameArrived(byte[] pixels, int width, int height)
  {
    if (!_playing) return;

    EnsureBitmap(width, height);
    if (_bitmap is null) return;

    using (var locked = _bitmap.Lock())
    {
      var dest = locked.RowBytes == width * 4
        ? locked.Address
        : locked.Address; // BGRA tightly packed; Avalonia uses 4 bytes per pixel for Bgra8888.

      if (locked.RowBytes == width * 4)
      {
        Marshal.Copy(pixels, 0, dest, pixels.Length);
      }
      else
      {
        for (var y = 0; y < height; y++)
          Marshal.Copy(pixels, y * width * 4, dest + y * locked.RowBytes, width * 4);
      }
    }

    ActualFps = _source.ActualFps;
    var start = _source.TimeToFirstFrame;
    Stats = start > TimeSpan.Zero
      ? $"{ActualFps:0.0} fps, first frame in {start.TotalSeconds:0.00}s"
      : $"{ActualFps:0.0} fps";
    // Same WriteableBitmap instance: the generated setter would skip, and Image would keep the
    // first pixels until some other property changed (~once a second). Force a redraw every frame.
    OnPropertyChanged(nameof(Frame));
    if (StatusIsError || Status.StartsWith("starting", StringComparison.Ordinal))
      Report($"playing {width}×{height}");
  }

  private void OnFailed(string message)
  {
    _playing = false;
    IsPlaying = false;
    PlayCommand.NotifyCanExecuteChanged();
    StopCommand.NotifyCanExecuteChanged();
    Report(message, isError: true);
    _runner.Report(message, isError: true);
  }

  private void EnsureBitmap(int width, int height)
  {
    if (_bitmap is not null && _bitmapWidth == width && _bitmapHeight == height) return;

    var created = new WriteableBitmap(
      new PixelSize(width, height),
      new Vector(96, 96),
      PixelFormat.Bgra8888,
      AlphaFormat.Opaque);

    var previous = _bitmap;
    _bitmap = created;
    _bitmapWidth = width;
    _bitmapHeight = height;
    Frame = created;
    previous?.Dispose();
  }

  private void Report(string message, bool isError = false)
  {
    Status = message;
    StatusIsError = isError;
  }

  public static OnvifProfileInfo? PreferSubstream(IReadOnlyList<OnvifProfileInfo> profiles)
  {
    if (profiles.Count == 0) return null;

    var sized = profiles.Where(p => p.Width > 0 && p.Height > 0).ToList();
    if (sized.Count > 0)
      return sized.OrderBy(p => (long)p.Width * p.Height).ThenBy(p => p.Width).First();

    // Unknown resolutions: the last profile is usually the substream.
    return profiles[^1];
  }
}
