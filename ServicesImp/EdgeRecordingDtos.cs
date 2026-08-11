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
}
