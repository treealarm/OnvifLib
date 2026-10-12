namespace OnvifLib
{
  // Profile G ("edge recording") — what the camera itself keeps on its SD card, as opposed to the
  // archive this VMS records. Everything here is read from the device; nothing is persisted.

  public record OnvifEdgeCapabilities(
    bool Recording,
    bool Search,
    bool Replay,
    bool DynamicRecordings,
    bool DynamicTracks,
    bool StorageConfiguration,
    bool AuxiliaryCommands,
    int MaxRecordings,
    List<string> SupportedAuxiliaryCommands);

  public record OnvifEdgeRecordingSummary(DateTime? DataFrom, DateTime? DataUntil, int NumberRecordings);

  // TrackType is the ONVIF enum name lowercased ("video"/"audio"/"metadata"/"extended").
  // DataFrom/DataTo are null when the camera left them at the .NET default — the ONVIF schema
  // makes them required, so there is no *Specified flag to consult.
  public record OnvifEdgeTrack(
    string TrackToken,
    string TrackType,
    string Description,
    DateTime? DataFrom,
    DateTime? DataTo);

  public record OnvifEdgeRecording(
    string RecordingToken,
    string SourceName,
    string SourceDescription,
    DateTime? EarliestRecording,
    DateTime? LatestRecording,
    string Content,
    string RecordingStatus,
    List<OnvifEdgeTrack> Tracks);

  // One contiguous recorded span the device reports. Until is null for a span the camera is still
  // writing; callers close it at their search window end so it stays importable.
  public record OnvifEdgeInterval(string RecordingToken, string TrackToken, DateTime From, DateTime? Until);

  public record OnvifReplayCapabilities(
    bool ReversePlayback,
    int SessionTimeoutMinSec,
    int SessionTimeoutMaxSec,
    bool RtpRtspTcp);

  public record OnvifEdgeRecordingJob(
    string JobToken,
    string RecordingToken,
    string Mode,
    string SourceToken);

  public record OnvifEdgeRecordingConfiguration(
    string RecordingToken,
    string SourceId,
    string SourceName,
    string SourceLocation,
    string SourceDescription,
    string SourceAddress,
    string Content,
    // ISO 8601 duration; "PT0S" means unlimited retention on the device.
    string MaximumRetentionTime);

  // Reported by Device Management, not by a Profile G service: whether the camera exposes storage
  // configuration at all, and which vendor auxiliary commands it advertises (one of which may
  // format the card).
  public record OnvifDeviceStorageSupport(
    bool StorageConfiguration,
    int MaxStorageConfigurations,
    List<string> AuxiliaryCommands);

  public record OnvifEdgeStorageConfiguration(
    string Token,
    string Type,
    string LocalPath,
    string StorageUri,
    string User);

  /// <summary>What can still be added to a recording: spare jobs, spare tracks, and which sources fit.</summary>
  /// <param name="CompatibleSources">Tokens (usually media profiles) a job for this recording may record from.</param>
  /// <param name="JobSpare">How many more jobs the recording can take; null when not stated.</param>
  /// <param name="TrackSpareTotal">How many more tracks it can take in all.</param>
  /// <param name="TrackSpareVideo">Spare video tracks.</param>
  /// <param name="TrackSpareAudio">Spare audio tracks.</param>
  /// <param name="TrackSpareMetadata">Spare metadata tracks.</param>
  public record OnvifRecordingOptions(
    int? JobSpare,
    IReadOnlyList<string> CompatibleSources,
    int? TrackSpareTotal,
    int? TrackSpareVideo,
    int? TrackSpareAudio,
    int? TrackSpareMetadata);

  /// <summary>Which source track feeds which recording track. Empty values let the camera map them itself.</summary>
  public record OnvifRecordingJobTrack(string SourceTag, string Destination);

  /// <param name="SourceType">
  /// What <paramref name="SourceToken"/> names: <see cref="RecordingService.SourceTypeProfile"/> for
  /// a media profile, <see cref="RecordingService.SourceTypeReceiver"/> for a receiver. Null means
  /// the schema default, which is Receiver.
  /// </param>
  /// <param name="SourceToken">The source — a media profile token for a camera recording itself.</param>
  /// <param name="AutoCreateReceiver">Let the device create a receiver for the source; null leaves it unsaid.</param>
  /// <param name="Tracks">Source-to-recording track mapping; empty lets the camera map them.</param>
  public record OnvifRecordingJobSource(
    string SourceToken,
    string? SourceType,
    bool? AutoCreateReceiver,
    IReadOnlyList<OnvifRecordingJobTrack> Tracks);

  /// <param name="Mode">"Active" (recording) or "Idle".</param>
  /// <param name="Priority">Higher wins when jobs compete for the same recording.</param>
  /// <param name="RecordingToken">The recording the job writes into.</param>
  /// <param name="Sources">What the job records from.</param>
  /// <param name="ScheduleToken">The schedule that drives the job, if any.</param>
  public record OnvifRecordingJobConfiguration(
    string RecordingToken,
    string Mode,
    int Priority,
    IReadOnlyList<OnvifRecordingJobSource> Sources,
    string? ScheduleToken = null);

  public record OnvifRecordingJobTrackState(string SourceTag, string Destination, string State, string? Error);

  public record OnvifRecordingJobSourceState(string SourceToken, string State, IReadOnlyList<OnvifRecordingJobTrackState> Tracks);

  /// <summary>What a job is actually doing, as opposed to what it is configured to do.</summary>
  /// <param name="State">"Idle", "Active", "PartiallyActive" or "Error".</param>
  /// <param name="RecordingToken">The recording the job writes into.</param>
  /// <param name="Sources">Per-source state, with per-track state inside.</param>
  public record OnvifRecordingJobState(string State, string RecordingToken, IReadOnlyList<OnvifRecordingJobSourceState> Sources);

  /// <summary>An event stored with a recording, as FindEvents reports it.</summary>
  /// <param name="Time">In the camera's clock, UTC.</param>
  /// <param name="StartStateEvent">
  /// The state the property was already in when the search window opened, rather than a change
  /// inside it — only returned when the search asked for start states.
  /// </param>
  /// <param name="Summary">The message's Source and Data items as <c>name=value</c> pairs, for display.</param>
  /// <param name="RecordingToken">The recording the event is stored with.</param>
  /// <param name="TrackToken">The track inside that recording.</param>
  /// <param name="Topic">The event topic, e.g. tns1:VideoSource/MotionAlarm.</param>
  /// <param name="MessageXml">The raw tt:Message, or null.</param>
  public record OnvifEdgeEvent(
    string RecordingToken,
    string TrackToken,
    DateTime Time,
    string Topic,
    bool StartStateEvent,
    string? MessageXml,
    string Summary);
}
