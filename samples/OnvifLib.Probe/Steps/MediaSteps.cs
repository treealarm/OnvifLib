namespace OnvifLib.Probe.Steps;

/// <summary>Media: profiles, stream URIs, snapshots, encoder configuration, and Profile M metadata.</summary>
public static class MediaSteps
{
  public static async Task RunAsync(ProbeContext ctx)
  {
    var r = ctx.Runner;
    r.Section(Sections.Media, "media");

    if (ctx.Media is not { } media)
    {
      r.Skip("media", "service not available");
      return;
    }

    await ProfilesAsync(ctx, media);
    await StreamUrisAsync(ctx, media);
    await SnapshotAsync(ctx, media);
    await VideoEncoderAsync(ctx, media);
    await AudioEncoderAsync(ctx, media);
    await ProfileMAsync(ctx, media);
  }

  private static async Task ProfilesAsync(ProbeContext ctx, MediaService media)
  {
    var r = ctx.Runner;

    await r.StepAsync("RefreshProfiles", media.RefreshProfilesAsync);

    // Synchronous: the profile snapshot was populated during service initialisation.
    await r.StepAsync("GetProfiles", () => Task.FromResult(media.GetProfiles()), profiles =>
    {
      ctx.Profiles.Clear();
      ctx.Profiles.AddRange(profiles);
      r.Table(["token", "name", "resolution", "encoding", "video source"],
        profiles.Select(p => new List<object?> { p.Token, p.Name, $"{p.Width}x{p.Height}", p.Encoding, p.VideoSourceToken }));
    });

    if (ctx.Profiles.Count == 0)
      r.Note("no profiles — every profile-scoped call below will be skipped");
  }

  private static async Task StreamUrisAsync(ProbeContext ctx, MediaService media)
  {
    var r = ctx.Runner;
    if (ctx.Profiles.Count == 0) { r.Skip("GetStreamUri", "no profiles"); return; }

    foreach (var profile in ctx.Profiles)
      await r.StepAsync($"GetStreamUri [{profile.Token}]",
        () => media.GetStreamUri(profile.Token),
        uri => r.Value("uri", uri));

    r.Note("stream URIs come back without credentials — a player needs user:password spliced in");
  }

  private static async Task SnapshotAsync(ProbeContext ctx, MediaService media)
  {
    var r = ctx.Runner;

    // GetImage takes no profile token: the library resolves the snapshot URI for the first
    // profile only. Other profiles' snapshots can still be fetched through DownloadImageAsync.
    var image = await r.StepAsync("GetImage",
      async () => await media.GetImage()
                  ?? throw new ProbeFailure("returned null — the library logs the cause and swallows it; re-run with --verbose"),
      img => r.Values(
        ("bytes", img.Data.Length),
        ("mime", img.MimeType),
        ("extension", img.Extension)));

    if (ctx.Profiles.Count > 1)
      r.Note("the snapshot is always the FIRST profile's — MediaService.GetImage() takes no profile token");

    if (image is null || ctx.Options.SnapshotDir is not { } directory) return;

    await r.StepAsync("save snapshot", () =>
    {
      Directory.CreateDirectory(directory);
      var extension = (image.Extension ?? MediaService.GetExtensionFromMime(image.MimeType) ?? "jpg").TrimStart('.');
      var path = Path.Combine(directory, $"snapshot-{ctx.Options.Ip}-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");
      File.WriteAllBytes(path, image.Data);
      r.Value("saved", path);
      return Task.CompletedTask;
    });
  }

  private static async Task VideoEncoderAsync(ProbeContext ctx, MediaService media)
  {
    var r = ctx.Runner;

    var configs = await r.StepAsync("GetVideoEncoderConfigs", media.GetVideoEncoderConfigsAsync,
      list => r.Table(["token", "name", "encoding", "resolution", "fps", "bitrate", "gov", "profile", "quality"],
        list.Select(c => new List<object?>
          { c.Token, c.Name, c.Encoding, $"{c.Width}x{c.Height}", c.FrameRateLimit, c.BitrateLimit, c.GovLength, c.H264Profile, c.Quality })));

    var first = configs?.FirstOrDefault();
    if (first is null)
    {
      r.Skip("GetVideoEncoderConfigOptions", "no video encoder configurations");
      r.Skip("SetVideoEncoderConfig", "no video encoder configurations");
      return;
    }

    await r.StepAsync($"GetVideoEncoderConfigOptions [{first.Token}]",
      () => media.GetVideoEncoderConfigOptionsAsync(first.Token),
      list => r.Table(["encoding", "resolutions", "fps", "bitrate", "gov", "h264 profiles"],
        list.Select(o => new List<object?>
        {
          o.Encoding,
          string.Join(" ", o.Resolutions.Select(x => $"{x.Width}x{x.Height}")),
          $"{o.MinFrameRate}..{o.MaxFrameRate}",
          $"{o.MinBitrate}..{o.MaxBitrate}",
          $"{o.MinGovLength}..{o.MaxGovLength}",
          string.Join(" ", o.H264Profiles),
        })));

    if (!ctx.Options.AllowWrites) { r.SkipWrites("SetVideoEncoderConfig"); return; }

    // Re-sends exactly what was read, so the camera ends up where it started. It proves the call
    // is accepted without picking settings the camera might not support.
    await r.StepAsync($"SetVideoEncoderConfig [{first.Token}] (no-op re-write)",
      () => media.SetVideoEncoderConfigAsync(first));
  }

  private static async Task AudioEncoderAsync(ProbeContext ctx, MediaService media)
  {
    var r = ctx.Runner;

    var configs = await r.StepAsync("GetAudioEncoderConfigs", media.GetAudioEncoderConfigsAsync,
      list => r.Table(["token", "name", "encoding", "bitrate", "sample rate"],
        list.Select(c => new List<object?> { c.Token, c.Name, c.Encoding, c.Bitrate, c.SampleRate })));

    var first = configs?.FirstOrDefault();
    if (first is null)
    {
      r.Skip("GetAudioEncoderConfigOptions", "no audio encoder configurations");
      r.Skip("SetAudioEncoderConfig", "no audio encoder configurations");
      return;
    }

    await r.StepAsync($"GetAudioEncoderConfigOptions [{first.Token}]",
      () => media.GetAudioEncoderConfigOptionsAsync(first.Token),
      list => r.Table(["encoding", "bitrates", "sample rates"],
        list.Select(o => new List<object?> { o.Encoding, o.Bitrates, o.SampleRates })));

    if (!ctx.Options.AllowWrites) { r.SkipWrites("SetAudioEncoderConfig"); return; }

    await r.StepAsync($"SetAudioEncoderConfig [{first.Token}] (no-op re-write)",
      () => media.SetAudioEncoderConfigAsync(first));
  }

  private static async Task ProfileMAsync(ProbeContext ctx, MediaService media)
  {
    var r = ctx.Runner;

    var metadata = await r.StepAsync("GetMetadataConfigs", media.GetMetadataConfigsAsync,
      list => r.Table(["token", "name", "analytics", "ptz status", "events", "geo", "polygon", "timeout", "compression", "profiles"],
        list.Select(m => new List<object?>
          { m.Token, m.Name, m.Analytics, m.PtzStatus, m.Events, m.GeoLocation, m.ShapePolygon, m.SessionTimeout, m.CompressionType, m.AttachedProfileTokens })));

    var firstMetadata = metadata?.FirstOrDefault();
    if (firstMetadata is null)
    {
      r.Skip("GetMetadataConfigOptions", "no metadata configurations");
      r.Skip("SetMetadataConfig", "no metadata configurations");
    }
    else
    {
      await r.StepAsync($"GetMetadataConfigOptions [{firstMetadata.Token}]",
        async () => await media.GetMetadataConfigOptionsAsync(firstMetadata.Token)
                    ?? throw new ProbeFailure("the camera reported no options for this configuration"),
        o => r.Values(
          ("supports ptz status", o.SupportsPtzStatus),
          ("compression types", o.CompressionTypes)));

      if (ctx.Options.AllowWrites)
        // Null in this record means "leave as is", so re-sending the read values is a true no-op.
        await r.StepAsync($"SetMetadataConfig [{firstMetadata.Token}] (no-op re-write)",
          () => media.SetMetadataConfigAsync(new OnvifMetadataConfigUpdate(
            firstMetadata.Token, firstMetadata.Analytics, firstMetadata.PtzStatus,
            firstMetadata.SessionTimeout, firstMetadata.CompressionType)));
      else
        r.SkipWrites("SetMetadataConfig");
    }

    await r.StepAsync("GetAnalyticsConfigs", media.GetAnalyticsConfigsAsync, list =>
    {
      ctx.AnalyticsConfigs.Clear();
      ctx.AnalyticsConfigs.AddRange(list);
      r.Table(["token", "name", "attached profiles"],
        list.Select(c => new List<object?> { c.Token, c.Name, c.AttachedProfileTokens }));
    });

    // These change which configurations a profile carries. Reverting is not reliably clean —
    // re-attaching does not always restore the original ordering or the vendor's extra state —
    // so they stay out of reach of --allow-writes and belong in the GUI, behind a confirmation.
    foreach (var name in new[] { "AttachMetadataConfig", "DetachMetadataConfig", "AttachAnalyticsConfig", "DetachAnalyticsConfig" })
      r.Skip(name, "changes profile composition — not reversible enough for the probe");
  }
}
