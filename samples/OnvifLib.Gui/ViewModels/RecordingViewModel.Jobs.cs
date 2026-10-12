using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// Recording jobs and creating recordings/tracks. Everything that writes here claims storage on
/// the camera or changes what it records, so every write asks first.
/// </summary>
public sealed partial class RecordingViewModel
{
  public IReadOnlyList<string> JobModes { get; } = ["Idle", "Active"];
  public IReadOnlyList<string> TrackTypes { get; } = ["Video", "Audio", "Metadata"];

  [ObservableProperty] private bool _dynamicRecordings;
  [ObservableProperty] private bool _dynamicTracks;

  /// <summary>
  /// The recording the job editor works on. Editable, and filled from either list: a camera can
  /// list its recordings only through Search while GetRecordings comes back empty.
  /// </summary>
  [ObservableProperty] private string _editRecordingToken = "";

  [ObservableProperty] private string _editJobMode = "Idle";
  [ObservableProperty] private int _editJobPriority = 1;
  [ObservableProperty] private string? _editJobSource;
  [ObservableProperty] private string _jobStateText = "";
  [ObservableProperty] private string _recordingOptionsText = "";

  [ObservableProperty] private string _newRecordingName = "OnvifLib";
  [ObservableProperty] private string _newRecordingContent = "";
  [ObservableProperty] private string _newRecordingRetention = "PT0S";
  [ObservableProperty] private string _newTrackType = "Video";
  [ObservableProperty] private string _newTrackDescription = "";

  /// <summary>Media profile tokens a job can record from.</summary>
  public IReadOnlyList<string> JobSources =>
    Session?.Media?.GetProfiles().Select(p => p.Token).ToList() ?? [];

  partial void OnSelectedRecordingChanged(OnvifEdgeRecordingConfiguration? value)
  {
    if (value is not null) EditRecordingToken = value.RecordingToken;
  }

  partial void OnSelectedJobChanged(OnvifEdgeRecordingJob? value)
  {
    if (value is null) return;
    EditRecordingToken = value.RecordingToken;
    _ = LoadJobConfigurationAsync();
  }

  partial void OnEditRecordingTokenChanged(string value)
  {
    RecordingOptionsText = "";
    if (value.Length > 0 && Session?.Recording is not null) _ = LoadRecordingOptionsAsync();
  }

  [RelayCommand]
  private async Task LoadRecordingOptionsAsync()
  {
    if (Session?.Recording is not { } recording || EditRecordingToken is not { Length: > 0 } token) return;
    var (ok, o) = await Runner.RunAsync($"GetRecordingOptions [{token}]", () => recording.GetRecordingOptionsAsync(token));
    if (!ok) return;
    RecordingOptionsText = o is null
      ? "the camera reported no options for this recording"
      : $"spare jobs {o.JobSpare?.ToString() ?? "—"}, spare tracks {o.TrackSpareTotal?.ToString() ?? "—"} " +
        $"(video {o.TrackSpareVideo?.ToString() ?? "—"}, audio {o.TrackSpareAudio?.ToString() ?? "—"}, metadata {o.TrackSpareMetadata?.ToString() ?? "—"}); " +
        $"sources: {(o.CompatibleSources.Count == 0 ? "—" : string.Join(", ", o.CompatibleSources))}";
    if (EditJobSource is null && o?.CompatibleSources.FirstOrDefault() is { } source) EditJobSource = source;
  }

  [RelayCommand]
  private async Task LoadJobConfigurationAsync()
  {
    if (Session?.Recording is not { } recording || SelectedJob is not { } job) return;
    var (ok, config) = await Runner.RunAsync($"GetRecordingJobConfiguration [{job.JobToken}]",
      () => recording.GetRecordingJobConfigurationAsync(job.JobToken));
    if (!ok || config is null) return;
    EditJobMode = JobModes.FirstOrDefault(m => m.Equals(config.Mode, StringComparison.OrdinalIgnoreCase)) ?? config.Mode;
    EditJobPriority = config.Priority;
    EditJobSource = config.Sources.FirstOrDefault()?.SourceToken;
    OnPropertyChanged(nameof(JobSources));
    await LoadJobStateAsync();
  }

  [RelayCommand]
  private async Task LoadJobStateAsync()
  {
    if (Session?.Recording is not { } recording || SelectedJob is not { } job) return;
    var (ok, state) = await Runner.RunAsync($"GetRecordingJobState [{job.JobToken}]",
      () => recording.GetRecordingJobStateAsync(job.JobToken));
    if (!ok) return;
    JobStateText = state is null
      ? "the camera reported no state"
      : $"{state.State}" + string.Concat(state.Sources.Select(src =>
          $"\n  {src.SourceToken}: {src.State}" + string.Concat(src.Tracks.Select(t =>
            $"\n    {t.SourceTag} → {t.Destination}: {t.State}{(t.Error is null ? "" : $" ({t.Error})")}"))));
  }

  [RelayCommand]
  private async Task ApplyJobConfigurationAsync()
  {
    if (Session?.Recording is not { } recording) return;
    if (SelectedJob is not { } job) { Runner.Report("Select a job first", isError: true); return; }
    if (EditJobSource is not { Length: > 0 } source) { Runner.Report("Pick the profile the job records from", isError: true); return; }
    var config = new OnvifRecordingJobConfiguration(EditRecordingToken, EditJobMode, EditJobPriority,
      [new OnvifRecordingJobSource(source, RecordingService.SourceTypeProfile, null, [])]);
    if (!await dialogs.ConfirmAsync("Change a recording job",
          $"Set job {job.JobToken} to {config.Mode}, priority {config.Priority}, recording {source} into {config.RecordingToken}?" +
          (config.Mode == "Idle" ? "\n\nIdle stops recording — footage from now on will not exist." : "")))
      return;
    var (ok, _) = await Runner.RunAsync($"SetRecordingJobConfiguration [{job.JobToken}]",
      () => recording.SetRecordingJobConfigurationAsync(job.JobToken, config));
    if (ok)
    {
      await LoadRecordingsAsync();
      await LoadJobStateAsync();
    }
  }

  [RelayCommand]
  private async Task CreateJobAsync()
  {
    if (Session?.Recording is not { } recording) return;
    if (EditRecordingToken is not { Length: > 0 } target) { Runner.Report("Pick or type the recording the job writes to", isError: true); return; }
    if (EditJobSource is not { Length: > 0 } source) { Runner.Report("Pick the profile the job records from", isError: true); return; }
    var mode = EditJobMode;
    var priority = EditJobPriority;
    if (!await dialogs.ConfirmAsync("Create a recording job",
          $"Create a job recording {source} into {target}, {mode}, priority {priority}?" +
          (mode == "Active" ? "\n\nActive starts writing to the camera's storage immediately." : "")))
      return;
    var (ok, created) = await Runner.RunAsync("CreateRecordingJob",
      () => recording.CreateRecordingJobAsync(target, mode, priority, source));
    if (!ok) return;
    Runner.Report($"Created job {created.JobToken}" +
                  (created.Configuration.Sources.FirstOrDefault()?.SourceToken is { } stored && stored != source
                    ? $" — the camera chose {stored} instead of {source}"
                    : ""));
    await LoadRecordingsAsync();
    SelectedJob = Jobs.FirstOrDefault(j => j.JobToken == created.JobToken);
  }

  [RelayCommand]
  private async Task DeleteJobAsync()
  {
    if (Session?.Recording is not { } recording) return;
    if (SelectedJob is not { } job) { Runner.Report("Select a job first", isError: true); return; }
    if (!await dialogs.ConfirmAsync("Delete a recording job",
          $"Delete job {job.JobToken}? The camera stops recording through it; footage already recorded stays."))
      return;
    if (await Runner.RunAsync($"DeleteRecordingJob [{job.JobToken}]", () => recording.DeleteRecordingJobAsync(job.JobToken)))
    {
      JobStateText = "";
      await LoadRecordingsAsync();
    }
  }

  [RelayCommand]
  private async Task CreateRecordingAsync()
  {
    if (Session?.Recording is not { } recording) return;
    var name = NewRecordingName.Trim();
    var content = NewRecordingContent.Trim();
    var retention = NewRecordingRetention.Trim();
    if (!await dialogs.ConfirmAsync("Create a recording",
          $"Create recording '{name}' with retention {retention} on the camera's storage?"))
      return;
    var (ok, token) = await Runner.RunAsync("CreateRecording", () => recording.CreateRecordingAsync(name, content, retention));
    if (!ok) return;
    Runner.Report($"Created recording {token}");
    EditRecordingToken = token ?? "";
    await LoadRecordingsAsync();
  }

  [RelayCommand]
  private async Task CreateTrackAsync()
  {
    if (Session?.Recording is not { } recording) return;
    if (EditRecordingToken is not { Length: > 0 } target) { Runner.Report("Pick or type the recording to add a track to", isError: true); return; }
    var type = NewTrackType;
    var description = NewTrackDescription.Trim();
    if (!await dialogs.ConfirmAsync("Add a track", $"Add a {type} track to {target}?"))
      return;
    var (ok, token) = await Runner.RunAsync($"CreateTrack [{target}]", () => recording.CreateTrackAsync(target, type, description));
    if (ok) Runner.Report($"Created track {token}");
  }

  private void ClearJobs()
  {
    EditRecordingToken = "";
    EditJobSource = null;
    JobStateText = RecordingOptionsText = "";
    DynamicRecordings = DynamicTracks = false;
  }
}
