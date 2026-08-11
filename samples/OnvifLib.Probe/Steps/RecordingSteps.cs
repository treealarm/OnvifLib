namespace OnvifLib.Probe.Steps;

/// <summary>
/// Profile G: what the camera keeps on its own storage. Recording configuration, search, replay.
/// </summary>
/// <remarks>
/// Every timestamp here is in the camera's clock. The device section measures the offset; this
/// section prints both the raw camera time and its equivalent in ours, so a wrong clock is
/// visible rather than silently shifting the whole archive.
/// </remarks>
public static class RecordingSteps
{
  public static async Task RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    r.Section(Sections.Recording, "recording (Profile G)");

    if (ctx.Recording is null && ctx.Search is null && ctx.Replay is null)
    {
      r.Skip("recording", "none of the recording, search or replay services are available");
      return;
    }

    r.Value("clock offset", ctx.ClockOffset == TimeSpan.Zero
      ? "0 (not measured — run the device section to measure it)"
      : $"{ctx.ClockOffset:g} (camera − us)");

    var recordingTokens = await RecordingServiceAsync(ctx);
    var searched = await SearchAsync(ctx, recordingTokens);
    await ReplayAsync(ctx, searched ?? recordingTokens);

    r.SkipDestructive("SetRecordingConfiguration", "SetRecordingJobMode", "DeleteRecording", "DeleteTrack", "DeleteRecordingJob");
  }

  private static async Task<List<string>> RecordingServiceAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    var tokens = new List<string>();

    if (ctx.Recording is not { } recording)
    {
      r.Skip("recording service", "not available");
      return tokens;
    }

    await r.StepAsync("GetServiceCapabilities (recording)", recording.GetServiceCapabilitiesAsync, caps => r.Values(
      ("dynamic recordings", caps.DynamicRecordings),
      ("dynamic tracks", caps.DynamicTracks),
      ("max recordings", caps.MaxRecordings)));

    var recordings = await r.StepAsync("GetRecordings", recording.GetRecordingsAsync,
      list => r.Table(["token", "source name", "content", "retention"],
        list.Select(c => new List<object?> { c.RecordingToken, c.SourceName, c.Content, c.MaximumRetentionTime })));
    if (recordings is not null) tokens.AddRange(recordings.Select(x => x.RecordingToken));

    await r.StepAsync("GetRecordingJobs", recording.GetRecordingJobsAsync,
      list => r.Table(["job", "recording", "mode", "source"],
        list.Select(j => new List<object?> { j.JobToken, j.RecordingToken, j.Mode, j.SourceToken })));

    if (tokens.FirstOrDefault() is { } first)
      await r.StepAsync($"GetRecordingConfiguration [{first}]",
        async () => await recording.GetRecordingConfigurationAsync(first)
                    ?? throw new ProbeFailure("the camera returned no configuration for this recording"),
        c => r.Values(
          ("source id", c.SourceId),
          ("source name", c.SourceName),
          ("location", c.SourceLocation),
          ("address", c.SourceAddress),
          ("content", c.Content),
          ("retention", $"{c.MaximumRetentionTime}{(c.MaximumRetentionTime == "PT0S" ? " (unlimited)" : "")}")));
    else
      r.Skip("GetRecordingConfiguration", "no recordings");

    return tokens;
  }

  private static async Task<List<string>?> SearchAsync(ProbeContext ctx, List<string> knownTokens)
  {
    var r = ctx.Runner;
    if (ctx.Search is not { } search)
    {
      r.Skip("search service", "not available");
      return null;
    }

    var summary = await r.StepAsync("GetRecordingSummary",
      async () => await search.GetRecordingSummaryAsync()
                  ?? throw new ProbeFailure("the camera returned no summary"),
      s => r.Values(
        ("data from (camera)", s.DataFrom),
        ("data from (ours)", s.DataFrom is { } f ? ctx.ToLocalClock(f) : null),
        ("data until (camera)", s.DataUntil),
        ("data until (ours)", s.DataUntil is { } u ? ctx.ToLocalClock(u) : null),
        ("recordings", s.NumberRecordings)));

    // Prefer the window the camera itself reports; otherwise ask for the last day, expressed in
    // the camera's clock because that is the index the archive is stored under.
    var to = summary?.DataUntil ?? ctx.ToCameraClock(DateTime.UtcNow);
    var from = summary?.DataFrom ?? to.AddDays(-1);

    var found = await r.StepAsync($"FindRecordings {from:yyyy-MM-dd HH:mm} .. {to:yyyy-MM-dd HH:mm} (camera clock)",
      () => search.FindRecordingsAsync(from, to, null, ctx.Options.MaxResults, ctx.Cancellation),
      list =>
      {
        r.Table(["token", "source", "earliest", "latest", "status", "tracks"],
          list.Select(x => new List<object?>
            { x.RecordingToken, x.SourceName, x.EarliestRecording, x.LatestRecording, x.RecordingStatus, x.Tracks.Count }));

        foreach (var track in list.SelectMany(x => x.Tracks.Select(t => (x.RecordingToken, t))))
          r.Value($"track {track.t.TrackToken}", $"{track.t.TrackType} {track.t.DataFrom:yyyy-MM-dd HH:mm:ss} .. {track.t.DataTo:yyyy-MM-dd HH:mm:ss}");
      });

    if (found is null) return null;
    r.Note("FindRecordings polls the device until it finishes, so a large archive can take tens of seconds");

    // A pure static, and exactly the shape a timeline widget wants: it also closes the open-ended
    // span a camera reports for whatever it is still writing.
    await r.StepAsync("SearchService.ToIntervals",
      () => Task.FromResult(SearchService.ToIntervals(found, to)),
      intervals => r.Table(["recording", "track", "from (camera)", "until (camera)", "from (ours)"],
        intervals.Select(i => new List<object?>
          { i.RecordingToken, i.TrackToken, i.From, i.Until, ctx.ToLocalClock(i.From) })));

    var tokens = found.Select(x => x.RecordingToken).ToList();
    if (tokens.FirstOrDefault() is { } first)
      await r.StepAsync($"GetRecordingInformation [{first}]",
        async () => await search.GetRecordingInformationAsync(first)
                    ?? throw new ProbeFailure("the camera returned no information for this recording"),
        x => r.Values(
          ("source", x.SourceName),
          ("description", x.SourceDescription),
          ("earliest", x.EarliestRecording),
          ("latest", x.LatestRecording),
          ("status", x.RecordingStatus),
          ("tracks", x.Tracks.Count)));
    else
      r.Skip("GetRecordingInformation", "the search returned no recordings");

    return tokens.Count > 0 ? tokens : knownTokens;
  }

  private static async Task ReplayAsync(ProbeContext ctx, List<string> recordingTokens)
  {
    var r = ctx.Runner;
    if (ctx.Replay is not { } replay) { r.Skip("replay service", "not available"); return; }

    await r.StepAsync("GetServiceCapabilities (replay)",
      async () => await replay.GetServiceCapabilitiesAsync()
                  ?? throw new ProbeFailure("the camera returned no replay capabilities"),
      c => r.Values(
        ("reverse playback", c.ReversePlayback),
        ("session timeout", $"{c.SessionTimeoutMinSec}..{c.SessionTimeoutMaxSec} s"),
        ("RTP/RTSP/TCP", c.RtpRtspTcp)));

    if (recordingTokens.FirstOrDefault() is not { } token)
    {
      r.Skip("GetReplayUri", "no recording to play back");
      return;
    }

    await r.StepAsync($"GetReplayUri [{token}]",
      () => replay.GetReplayUriAsync(token),
      uri => r.Value("uri", uri));
    r.Note("replay URIs are frequently single-use — fetch a fresh one immediately before each playback");
  }
}
