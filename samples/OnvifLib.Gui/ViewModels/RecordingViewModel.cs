using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// Profile G: what the camera keeps on its own storage — recordings, search, replay.
/// </summary>
/// <remarks>
/// Every timestamp here is in the camera's clock, which is frequently not ours. Both are shown
/// side by side rather than silently converted, because a wrong clock is the difference between
/// finding the footage and searching the wrong hour.
/// </remarks>
public sealed partial class RecordingViewModel(OperationRunner runner, UiLogger logger, IDialogService dialogs)
  : TabViewModelBase("Profile G", runner, logger)
{
  private CancellationTokenSource? _searchCancellation;

  public ObservableCollection<OnvifEdgeRecordingConfiguration> Recordings { get; } = [];
  public ObservableCollection<OnvifEdgeRecordingJob> Jobs { get; } = [];
  public ObservableCollection<OnvifEdgeRecording> Found { get; } = [];
  public ObservableCollection<OnvifEdgeInterval> Intervals { get; } = [];
  public ObservableCollection<OnvifEdgeTrack> Tracks { get; } = [];

  [ObservableProperty] private OnvifEdgeRecordingConfiguration? _selectedRecording;
  [ObservableProperty] private OnvifEdgeRecordingJob? _selectedJob;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(SelectedFoundToken))]
  private OnvifEdgeRecording? _selectedFound;

  public string SelectedFoundToken => SelectedFound?.RecordingToken ?? "";

  [ObservableProperty] private string _clockText = "clock offset not measured — measure it on the Device tab";
  [ObservableProperty] private string _capabilitiesText = "not read";
  [ObservableProperty] private string _summaryText = "not read";
  [ObservableProperty] private string _replayCapabilitiesText = "not read";
  [ObservableProperty] private string _replayUri = "";

  [ObservableProperty] private DateTimeOffset _fromDate = DateTimeOffset.Now.AddDays(-1);
  [ObservableProperty] private DateTimeOffset _toDate = DateTimeOffset.Now;
  [ObservableProperty] private int _maxResults = 100;
  [ObservableProperty] private bool _isSearching;

  public IReadOnlyList<string> Transports { get; } = ["RTSP", "HTTP", "TCP", "UDP"];
  [ObservableProperty] private string _transport = "RTSP";

  /// <summary>Set by the shell so playback can reuse the Media tab's player and credentials.</summary>
  public MediaViewModel? Media { get; set; }

  protected override string? DescribeUnavailability(CameraSession session) =>
    session.Recording is null && session.Search is null && session.Replay is null
      ? "This camera advertises none of the Profile G services (recording, search, replay), so it keeps no archive we can reach."
      : null;

  protected override void OnConnected(CameraSession session) => ClockText = Describe(session.ClockOffset);

  protected override void OnCleared()
  {
    _searchCancellation?.Cancel();
    Recordings.Clear();
    Jobs.Clear();
    Found.Clear();
    Intervals.Clear();
    Tracks.Clear();
    ReplayUri = "";
    CapabilitiesText = SummaryText = ReplayCapabilitiesText = "not read";
  }

  public override Task ShutdownAsync()
  {
    _searchCancellation?.Cancel();
    return Task.CompletedTask;
  }

  private static string Describe(TimeSpan offset) => offset == TimeSpan.Zero
    ? "clock offset is zero (or was never measured) — searches use this machine's clock"
    : $"camera clock is {offset:g} {(offset > TimeSpan.Zero ? "ahead of" : "behind")} ours; search windows are converted into it";

  /// <summary>A moment of ours in the camera's clock, which is how the archive is indexed.</summary>
  private DateTime ToCameraClock(DateTime ours) => ours + (Session?.ClockOffset ?? TimeSpan.Zero);

  private DateTime ToOurClock(DateTime camera) => camera - (Session?.ClockOffset ?? TimeSpan.Zero);

  // ── recording service ──────────────────────────────────────────────────────────

  [RelayCommand]
  private async Task LoadRecordingsAsync()
  {
    if (Session?.Recording is not { } recording) return;

    var (okCaps, caps) = await Runner.RunAsync("GetServiceCapabilities (recording)", recording.GetServiceCapabilitiesAsync);
    if (okCaps)
      CapabilitiesText = $"dynamic recordings: {(caps.DynamicRecordings ? "yes" : "no")}, " +
                         $"dynamic tracks: {(caps.DynamicTracks ? "yes" : "no")}, max {caps.MaxRecordings}";

    var (ok, list) = await Runner.RunAsync("GetRecordings", recording.GetRecordingsAsync);
    if (ok && list is not null)
    {
      Recordings.Clear();
      foreach (var item in list) Recordings.Add(item);
    }

    var (okJobs, jobs) = await Runner.RunAsync("GetRecordingJobs", recording.GetRecordingJobsAsync);
    if (okJobs && jobs is not null)
    {
      Jobs.Clear();
      foreach (var job in jobs) Jobs.Add(job);
    }
  }

  [RelayCommand]
  private async Task SetJobActiveAsync() => await SetJobModeAsync("Active");

  [RelayCommand]
  private async Task SetJobIdleAsync() => await SetJobModeAsync("Idle");

  private async Task SetJobModeAsync(string mode)
  {
    if (Session?.Recording is not { } recording || SelectedJob is not { } job) return;

    if (!await dialogs.ConfirmAsync($"Set the recording job to {mode}",
          $"Job {job.JobToken} controls whether the camera records to its own storage.\n\n" +
          $"Setting it to {mode} {(mode == "Idle" ? "stops recording — footage from now on will not exist" : "starts recording")}."))
      return;

    if (await Runner.RunAsync($"SetRecordingJobMode [{job.JobToken}] = {mode}",
          () => recording.SetRecordingJobModeAsync(job.JobToken, mode)))
      await LoadRecordingsAsync();
  }

  [RelayCommand]
  private async Task DeleteRecordingAsync()
  {
    if (Session?.Recording is not { } recording || SelectedRecording is not { } target) return;

    if (!await dialogs.ConfirmAsync("Delete a recording",
          $"Delete recording {target.RecordingToken} from the camera's storage?\n\n" +
          "The footage is erased on the device. There is no undo."))
      return;

    if (await Runner.RunAsync($"DeleteRecording [{target.RecordingToken}]",
          () => recording.DeleteRecordingAsync(target.RecordingToken)))
      await LoadRecordingsAsync();
  }

  // ── search service ─────────────────────────────────────────────────────────────

  [RelayCommand]
  private async Task LoadSummaryAsync()
  {
    if (Session?.Search is not { } search) return;

    var (ok, summary) = await Runner.RunAsync("GetRecordingSummary", search.GetRecordingSummaryAsync);
    if (!ok) return;
    if (summary is null) { SummaryText = "the camera returned no summary"; return; }

    SummaryText = $"{summary.NumberRecordings} recording(s); camera clock " +
                  $"{summary.DataFrom:yyyy-MM-dd HH:mm} … {summary.DataUntil:yyyy-MM-dd HH:mm}";

    // The camera's own window is a far better starting point than an arbitrary "last day",
    // converted back into our clock because that is what the pickers show.
    if (summary.DataFrom is { } from) FromDate = new DateTimeOffset(ToOurClock(from), TimeSpan.Zero).ToLocalTime();
    if (summary.DataUntil is { } until) ToDate = new DateTimeOffset(ToOurClock(until), TimeSpan.Zero).ToLocalTime();
  }

  [RelayCommand]
  private async Task SearchAsync()
  {
    if (Session?.Search is not { } search || IsSearching) return;

    _searchCancellation?.Cancel();
    _searchCancellation = new CancellationTokenSource();
    var token = _searchCancellation.Token;

    var from = ToCameraClock(FromDate.UtcDateTime);
    var to = ToCameraClock(ToDate.UtcDateTime);

    IsSearching = true;
    try
    {
      // One of the few library calls that takes a cancellation token, and it needs one: it polls
      // the device until it finishes, which on a full SD card is tens of seconds.
      var (ok, results) = await Runner.RunAsync($"FindRecordings {from:yyyy-MM-dd HH:mm} … {to:yyyy-MM-dd HH:mm} (camera clock)",
        () => search.FindRecordingsAsync(from, to, null, MaxResults, token));
      if (!ok || results is null) return;

      Found.Clear();
      foreach (var item in results) Found.Add(item);

      // A pure static, and exactly the shape a timeline wants: it also closes the open-ended
      // span a camera reports for whatever it is still writing.
      Intervals.Clear();
      foreach (var interval in SearchService.ToIntervals(results, to)) Intervals.Add(interval);
    }
    finally { IsSearching = false; }
  }

  [RelayCommand]
  private void CancelSearch() => _searchCancellation?.Cancel();

  partial void OnSelectedFoundChanged(OnvifEdgeRecording? value)
  {
    Tracks.Clear();
    if (value is null) return;
    foreach (var track in value.Tracks) Tracks.Add(track);
  }

  [RelayCommand]
  private async Task LoadRecordingInformationAsync()
  {
    if (Session?.Search is not { } search || SelectedFound is not { } found) return;

    var (ok, info) = await Runner.RunAsync($"GetRecordingInformation [{found.RecordingToken}]",
      () => search.GetRecordingInformationAsync(found.RecordingToken));
    if (!ok) return;

    SummaryText = info is null
      ? "the camera returned no information for that recording"
      : $"{info.SourceName}: {info.EarliestRecording:yyyy-MM-dd HH:mm} … {info.LatestRecording:yyyy-MM-dd HH:mm} " +
        $"({info.RecordingStatus}, {info.Tracks.Count} track(s))";
  }

  // ── replay service ─────────────────────────────────────────────────────────────

  [RelayCommand]
  private async Task LoadReplayCapabilitiesAsync()
  {
    if (Session?.Replay is not { } replay) return;

    var (ok, caps) = await Runner.RunAsync("GetServiceCapabilities (replay)", replay.GetServiceCapabilitiesAsync);
    if (!ok) return;

    ReplayCapabilitiesText = caps is null
      ? "the camera returned no replay capabilities"
      : $"reverse playback: {(caps.ReversePlayback ? "yes" : "no")}, " +
        $"session timeout {caps.SessionTimeoutMinSec}–{caps.SessionTimeoutMaxSec} s, " +
        $"RTP/RTSP/TCP: {(caps.RtpRtspTcp ? "yes" : "no")}";
  }

  [RelayCommand]
  private async Task PlayReplayAsync()
  {
    if (Session?.Replay is not { } replay) return;
    if (RecordingTokenForReplay is not { } token) { Runner.Report("Select a recording first", isError: true); return; }

    // Always fetched fresh. Replay URIs are frequently single-use, so there is deliberately no
    // path in this app that plays a cached one.
    var (ok, uri) = await Runner.RunAsync($"GetReplayUri [{token}]", () => replay.GetReplayUriAsync(token, Transport));
    if (!ok || uri is not { Length: > 0 }) return;

    ReplayUri = RtspCredentials.Mask(Media?.WithCredentials(uri) ?? uri);
    Media?.PlayReplay(uri);
  }

  private string? RecordingTokenForReplay =>
    SelectedFound?.RecordingToken ?? SelectedRecording?.RecordingToken;
}
