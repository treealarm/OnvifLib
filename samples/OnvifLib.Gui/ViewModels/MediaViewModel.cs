using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// Media: profiles, stream URIs, snapshots, encoder configuration and the Profile M metadata
/// plumbing (which lives on the media service, not on the analytics one).
/// </summary>
public sealed partial class MediaViewModel : TabViewModelBase, IAsyncDisposable
{
  private readonly SnapshotLoop _snapshots = new();

  public MediaViewModel(OperationRunner runner, UiLogger logger) : base("Media", runner, logger)
  {
    Players = ExternalPlayer.Discover();
    SelectedPlayer = Players.FirstOrDefault();
  }

  // ── profiles and streaming ─────────────────────────────────────────────────────

  public ObservableCollection<OnvifProfileInfo> Profiles { get; } = [];

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(HasProfile))]
  private OnvifProfileInfo? _selectedProfile;

  public bool HasProfile => SelectedProfile is not null;

  [ObservableProperty] private string _streamUri = "";
  [ObservableProperty] private string _maskedStreamUri = "";

  public IReadOnlyList<PlayerCandidate> Players { get; }
  [ObservableProperty] private PlayerCandidate? _selectedPlayer;
  [ObservableProperty] private string _customPlayerPath = "";

  public bool HasPlayer => Players.Count > 0 || CustomPlayerPath.Length > 0;

  /// <summary>Raised when the selected profile changes, so PTZ and Imaging follow along.</summary>
  public event Action<OnvifProfileInfo?>? ProfileSelected;

  partial void OnSelectedProfileChanged(OnvifProfileInfo? value)
  {
    // A URI belongs to the profile it came from; keeping a stale one visible invites playing
    // the wrong stream.
    StreamUri = MaskedStreamUri = "";
    StopLive();
    ProfileSelected?.Invoke(value);
  }

  // ── snapshot ───────────────────────────────────────────────────────────────────

  [ObservableProperty] private Bitmap? _frame;
  [ObservableProperty] private string _snapshotInfo = "no snapshot yet";
  [ObservableProperty] private int _snapshotIntervalMs = 1000;
  [ObservableProperty] private bool _isLive;
  [ObservableProperty] private string _manualSnapshotUrl = "";

  // ── encoders ───────────────────────────────────────────────────────────────────

  public ObservableCollection<OnvifVideoEncoderConfig> VideoConfigs { get; } = [];
  public ObservableCollection<OnvifVideoEncoderOptions> VideoOptions { get; } = [];
  public ObservableCollection<OnvifAudioEncoderConfig> AudioConfigs { get; } = [];
  public ObservableCollection<OnvifAudioEncoderOption> AudioOptions { get; } = [];

  [ObservableProperty] private OnvifVideoEncoderConfig? _selectedVideoConfig;
  [ObservableProperty] private OnvifAudioEncoderConfig? _selectedAudioConfig;

  // Edited separately from the selected record so "Apply" sends a deliberate change rather than
  // whatever the grid happened to leave behind.
  [ObservableProperty] private int _editWidth;
  [ObservableProperty] private int _editHeight;
  [ObservableProperty] private int _editFrameRate;
  [ObservableProperty] private int _editBitrate;
  [ObservableProperty] private int _editGovLength;

  partial void OnSelectedVideoConfigChanged(OnvifVideoEncoderConfig? value)
  {
    if (value is null) return;
    EditWidth = value.Width;
    EditHeight = value.Height;
    EditFrameRate = value.FrameRateLimit;
    EditBitrate = value.BitrateLimit;
    EditGovLength = value.GovLength;
  }

  // ── Profile M ──────────────────────────────────────────────────────────────────

  public ObservableCollection<OnvifMetadataConfig> MetadataConfigs { get; } = [];
  public ObservableCollection<OnvifAnalyticsConfig> AnalyticsConfigs { get; } = [];

  [ObservableProperty] private OnvifMetadataConfig? _selectedMetadataConfig;
  [ObservableProperty] private OnvifAnalyticsConfig? _selectedAnalyticsConfig;
  [ObservableProperty] private string _metadataOptionsText = "";

  // Three-state on purpose: null means "leave as is", which is what the update record documents.
  [ObservableProperty] private bool? _editAnalytics;
  [ObservableProperty] private bool? _editPtzStatus;

  /// <summary>Raised so the Analytics tab can work from the same configuration list.</summary>
  public event Action<IReadOnlyList<OnvifAnalyticsConfig>>? AnalyticsConfigsLoaded;

  // ── lifecycle ──────────────────────────────────────────────────────────────────

  protected override string? DescribeUnavailability(CameraSession session) => session.Media is null
    ? session.Advertises(MediaService.GetSupportedWsdls())
      ? "The camera advertises a media service, but the library could not create a client for it. That is almost always a rejected credential — check the Log tab."
      : "This camera does not advertise a media service."
    : null;

  protected override void OnConnected(CameraSession session)
  {
    Profiles.Clear();
    // Safe synchronously: the profile snapshot is populated while the service initialises.
    foreach (var profile in session.Media!.GetProfiles()) Profiles.Add(profile);
    SelectedProfile = Profiles.FirstOrDefault();
  }

  protected override void OnCleared()
  {
    StopLive();
    Profiles.Clear();
    VideoConfigs.Clear();
    VideoOptions.Clear();
    AudioConfigs.Clear();
    AudioOptions.Clear();
    MetadataConfigs.Clear();
    AnalyticsConfigs.Clear();
    SelectedProfile = null;
    StreamUri = MaskedStreamUri = "";
    SnapshotInfo = "no snapshot yet";
    SetFrame(null);
  }

  public override async Task ShutdownAsync()
  {
    await _snapshots.DisposeAsync();
    IsLive = false;
    SetFrame(null);
  }

  public ValueTask DisposeAsync() => new(ShutdownAsync());

  // ── commands: profiles and streaming ───────────────────────────────────────────

  [RelayCommand]
  private async Task RefreshProfilesAsync()
  {
    if (Session?.Media is not { } media) return;
    if (!await Runner.RunAsync("RefreshProfiles", media.RefreshProfilesAsync)) return;

    var token = SelectedProfile?.Token;
    Profiles.Clear();
    foreach (var profile in media.GetProfiles()) Profiles.Add(profile);
    SelectedProfile = Profiles.FirstOrDefault(p => p.Token == token) ?? Profiles.FirstOrDefault();
  }

  [RelayCommand]
  private async Task GetStreamUriAsync()
  {
    if (Session?.Media is not { } media || SelectedProfile is not { } profile) return;

    var (ok, uri) = await Runner.RunAsync($"GetStreamUri [{profile.Token}]", () => media.GetStreamUri(profile.Token));
    if (!ok || uri is null) return;

    StreamUri = uri;
    // Shown with the password blanked; the copy and play paths use the real one.
    MaskedStreamUri = RtspCredentials.Mask(WithCredentials(uri));
  }

  [RelayCommand]
  private void PlayStream() => Play(StreamUri);

  /// <summary>
  /// Plays a replay URI through the same discovered player and credentials. The caller fetches
  /// it fresh each time: replay URIs are frequently single-use.
  /// </summary>
  public void PlayReplay(string uri) => Play(uri);

  private void Play(string uri)
  {
    if (string.IsNullOrWhiteSpace(uri)) { Runner.Report("Fetch a stream URI first", isError: true); return; }

    var player = ResolvePlayer();
    if (player is null)
    {
      Runner.Report("No external player found — install VLC, mpv or ffmpeg, or set a player path. The URI can still be copied.", isError: true);
      return;
    }

    var full = WithCredentials(uri);
    try
    {
      ExternalPlayer.Launch(player, full);
      // Never the real URI: the log is saved and shared.
      Runner.Report($"Launched {player.Name} with {RtspCredentials.Mask(full)}");
    }
    catch (Exception ex)
    {
      Runner.Report($"Could not launch {player.ExecutablePath}: {ex.Message}", isError: true);
      Logger.Error(ex.ToString());
    }
  }

  private PlayerCandidate? ResolvePlayer()
  {
    if (CustomPlayerPath is { Length: > 0 } path)
      return new PlayerCandidate(Path.GetFileNameWithoutExtension(path), path);
    return SelectedPlayer ?? Players.FirstOrDefault();
  }

  /// <summary>The URI a player can actually open — the library never puts credentials in one.</summary>
  public string WithCredentials(string uri) =>
    Session is { } session ? RtspCredentials.Inject(uri, session.Camera.User, session.Camera.Password) : uri;

  // ── commands: snapshot ─────────────────────────────────────────────────────────

  [RelayCommand]
  private async Task SnapshotAsync()
  {
    if (Session?.Media is not { } media) return;

    var (ok, image) = await Runner.RunAsync("GetImage", media.GetImage);
    if (!ok) return;

    if (image is null)
    {
      // The library catches the exception and returns null, so the only account of what went
      // wrong is in the log.
      SnapshotInfo = "GetImage returned null — the library swallowed the error; see the Log tab";
      Runner.Report("GetImage returned null — see the Log tab", isError: true);
      return;
    }

    ApplyFrame(image);
  }

  [RelayCommand]
  private async Task DownloadSnapshotAsync()
  {
    if (Session is not { } session || ManualSnapshotUrl is not { Length: > 0 } url) return;

    var (ok, image) = await Runner.RunAsync("DownloadImage",
      () => MediaService.DownloadImageAsync(url, session.Camera.User, session.Camera.Password, Logger));
    if (ok && image is not null) ApplyFrame(image);
  }

  [RelayCommand]
  private void ToggleLive()
  {
    if (IsLive) { StopLive(); return; }
    if (Session?.Media is not { } media) return;

    IsLive = true;
    _snapshots.Start(
      Math.Max(100, SnapshotIntervalMs),
      media.GetImage,
      frame =>
      {
        if (frame is null) { SnapshotInfo = "GetImage returned null — see the Log tab"; return; }
        ApplyFrame(frame);
      },
      ex => Logger.Error($"snapshot poll failed: {ex}"));
  }

  private void StopLive()
  {
    _snapshots.Stop();
    IsLive = false;
  }

  private void ApplyFrame(ImageResult image)
  {
    // Some cameras answer a snapshot request with an HTML error page and HTTP 200, which would
    // otherwise reach the Bitmap constructor and throw somewhere far less informative.
    if (image.MimeType is { Length: > 0 } mime && !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
    {
      SnapshotInfo = $"the camera returned {mime}, not an image ({image.Data.Length} bytes)";
      return;
    }

    try
    {
      using var stream = new MemoryStream(image.Data, writable: false);
      SetFrame(new Bitmap(stream));
      SnapshotInfo = $"{image.Data.Length:N0} bytes, {image.MimeType ?? "unknown type"}, extension {image.Extension ?? "?"}";
    }
    catch (Exception ex)
    {
      SnapshotInfo = $"the payload did not decode as an image: {ex.Message}";
      Logger.Warning($"snapshot decode failed: {ex}");
    }
  }

  /// <summary>
  /// Swaps the displayed bitmap and disposes the one it replaced — in that order, so the render
  /// pass is never left holding a dead handle. At a few frames per second a leaked 1080p bitmap
  /// costs megabytes each, which is the fastest way to make this app look like it has a leak.
  /// </summary>
  private void SetFrame(Bitmap? bitmap)
  {
    var previous = Frame;
    Frame = bitmap;
    previous?.Dispose();
  }

  // ── commands: encoders ─────────────────────────────────────────────────────────

  [RelayCommand]
  private async Task LoadVideoConfigsAsync()
  {
    if (Session?.Media is not { } media) return;
    var (ok, configs) = await Runner.RunAsync("GetVideoEncoderConfigs", media.GetVideoEncoderConfigsAsync);
    if (!ok || configs is null) return;

    VideoConfigs.Clear();
    foreach (var config in configs) VideoConfigs.Add(config);
    SelectedVideoConfig = VideoConfigs.FirstOrDefault();
  }

  [RelayCommand]
  private async Task LoadVideoOptionsAsync()
  {
    if (Session?.Media is not { } media || SelectedVideoConfig is not { } config) return;
    var (ok, options) = await Runner.RunAsync($"GetVideoEncoderConfigOptions [{config.Token}]",
      () => media.GetVideoEncoderConfigOptionsAsync(config.Token));
    if (!ok || options is null) return;

    VideoOptions.Clear();
    foreach (var option in options) VideoOptions.Add(option);
  }

  [RelayCommand]
  private async Task ApplyVideoConfigAsync()
  {
    if (Session?.Media is not { } media || SelectedVideoConfig is not { } config) return;

    var edited = config with
    {
      Width = EditWidth,
      Height = EditHeight,
      FrameRateLimit = EditFrameRate,
      BitrateLimit = EditBitrate,
      GovLength = EditGovLength,
    };

    if (await Runner.RunAsync($"SetVideoEncoderConfig [{config.Token}]", () => media.SetVideoEncoderConfigAsync(edited)))
      await LoadVideoConfigsAsync();
  }

  [RelayCommand]
  private async Task LoadAudioConfigsAsync()
  {
    if (Session?.Media is not { } media) return;
    var (ok, configs) = await Runner.RunAsync("GetAudioEncoderConfigs", media.GetAudioEncoderConfigsAsync);
    if (!ok || configs is null) return;

    AudioConfigs.Clear();
    foreach (var config in configs) AudioConfigs.Add(config);
    SelectedAudioConfig = AudioConfigs.FirstOrDefault();
  }

  [RelayCommand]
  private async Task LoadAudioOptionsAsync()
  {
    if (Session?.Media is not { } media || SelectedAudioConfig is not { } config) return;
    var (ok, options) = await Runner.RunAsync($"GetAudioEncoderConfigOptions [{config.Token}]",
      () => media.GetAudioEncoderConfigOptionsAsync(config.Token));
    if (!ok || options is null) return;

    AudioOptions.Clear();
    foreach (var option in options) AudioOptions.Add(option);
  }

  // ── commands: Profile M ────────────────────────────────────────────────────────

  [RelayCommand]
  private async Task LoadMetadataConfigsAsync()
  {
    if (Session?.Media is not { } media) return;
    var (ok, configs) = await Runner.RunAsync("GetMetadataConfigs", media.GetMetadataConfigsAsync);
    if (!ok || configs is null) return;

    MetadataConfigs.Clear();
    foreach (var config in configs) MetadataConfigs.Add(config);
    SelectedMetadataConfig = MetadataConfigs.FirstOrDefault();
  }

  [RelayCommand]
  private async Task LoadMetadataOptionsAsync()
  {
    if (Session?.Media is not { } media || SelectedMetadataConfig is not { } config) return;
    var (ok, options) = await Runner.RunAsync($"GetMetadataConfigOptions [{config.Token}]",
      () => media.GetMetadataConfigOptionsAsync(config.Token));
    if (!ok) return;

    MetadataOptionsText = options is null
      ? "the camera reported no options for this configuration"
      : $"PTZ status supported: {(options.SupportsPtzStatus ? "yes" : "no")}; " +
        $"compression types: {(options.CompressionTypes.Count == 0 ? "—" : string.Join(", ", options.CompressionTypes))}";
  }

  [RelayCommand]
  private async Task ApplyMetadataConfigAsync()
  {
    if (Session?.Media is not { } media || SelectedMetadataConfig is not { } config) return;

    // Null means "leave as is". Some cameras treat an absent optional field in a Set request as
    // "reset to default", so sending only what was deliberately changed is the safe shape.
    var update = new OnvifMetadataConfigUpdate(config.Token, EditAnalytics, EditPtzStatus);

    if (await Runner.RunAsync($"SetMetadataConfig [{config.Token}]", () => media.SetMetadataConfigAsync(update)))
      await LoadMetadataConfigsAsync();
  }

  [RelayCommand]
  private async Task LoadAnalyticsConfigsAsync()
  {
    if (Session?.Media is not { } media) return;
    var (ok, configs) = await Runner.RunAsync("GetAnalyticsConfigs", media.GetAnalyticsConfigsAsync);
    if (!ok || configs is null) return;

    AnalyticsConfigs.Clear();
    foreach (var config in configs) AnalyticsConfigs.Add(config);
    SelectedAnalyticsConfig = AnalyticsConfigs.FirstOrDefault();

    // The analytics tab is keyed by these tokens and has no other source for them.
    AnalyticsConfigsLoaded?.Invoke(configs);
  }

  [RelayCommand]
  private Task AttachMetadataAsync() => AttachDetachAsync("AttachMetadataConfig",
    (media, profile, config) => media.AttachMetadataConfigAsync(profile, config), SelectedMetadataConfig?.Token);

  [RelayCommand]
  private Task DetachMetadataAsync() => AttachDetachAsync("DetachMetadataConfig",
    (media, profile, config) => media.DetachMetadataConfigAsync(profile, config), SelectedMetadataConfig?.Token);

  [RelayCommand]
  private Task AttachAnalyticsAsync() => AttachDetachAsync("AttachAnalyticsConfig",
    (media, profile, config) => media.AttachAnalyticsConfigAsync(profile, config), SelectedAnalyticsConfig?.Token);

  [RelayCommand]
  private Task DetachAnalyticsAsync() => AttachDetachAsync("DetachAnalyticsConfig",
    (media, profile, config) => media.DetachAnalyticsConfigAsync(profile, config), SelectedAnalyticsConfig?.Token);

  private async Task AttachDetachAsync(string what, Func<MediaService, string, string, Task> call, string? configToken)
  {
    if (Session?.Media is not { } media) return;
    if (SelectedProfile is not { } profile) { Runner.Report("Select a profile first", isError: true); return; }
    if (configToken is not { Length: > 0 }) { Runner.Report("Select a configuration first", isError: true); return; }

    if (await Runner.RunAsync($"{what} [{profile.Token} ← {configToken}]", () => call(media, profile.Token, configToken)))
      await LoadMetadataConfigsAsync();
  }
}
