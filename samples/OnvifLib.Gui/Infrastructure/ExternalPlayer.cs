using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OnvifLib.Gui.Infrastructure;

public sealed record PlayerCandidate(string Name, string ExecutablePath)
{
  public override string ToString() => $"{Name} ({ExecutablePath})";

  /// <summary>
  /// TCP transport is asked for across the board: UDP loses packets on any congested link, and
  /// the resulting smearing looks like a camera fault rather than a network one.
  /// </summary>
  public IEnumerable<string> BuildArguments(string uri) => Name switch
  {
    // The transport preference goes after the URI as an MRL option, not as a global "--rtsp-tcp"
    // flag. --rtsp-tcp belongs to the live555 demuxer, and a VLC built without that module — the
    // Ubuntu vlc-plugin-base build is one — rejects the unknown global option and refuses to
    // start at all. An MRL option that no loaded module claims is merely ignored. Only options
    // that exist in the core stay on the command line proper.
    "vlc" => ["--no-video-title-show", "--network-caching=300", uri, ":rtsp-tcp"],
    "ffplay" => ["-rtsp_transport", "tcp", "-fflags", "nobuffer", "-i", uri],
    "mpv" => ["--rtsp-transport=tcp", "--profile=low-latency", uri],
    _ => [uri],
  };
}

/// <summary>
/// Hands an RTSP URI to whatever player is installed. The library decodes nothing, so this is how
/// the sample shows live video without taking on LibVLCSharp and a native dependency.
/// </summary>
public static class ExternalPlayer
{
  /// <summary>
  /// Preference order, best-first. It differs by platform for one empirical reason: the VLC
  /// packaged for current Debian and Ubuntu no longer ships the live555 demuxer, so it cannot
  /// open a plain RTSP stream at all — it falls back to the satip and realrtsp modules and fails
  /// to connect. ffplay carries its own RTSP support in libavformat and just works. On Windows
  /// the official VLC build still has live555 and is the nicer player, so it leads there.
  /// Whatever is found is offered in the drop-down; this only decides the default.
  /// </summary>
  private static string[] Names => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
    ? ["vlc", "mpv", "ffplay"]
    : ["ffplay", "mpv", "vlc"];

  public static IReadOnlyList<PlayerCandidate> Discover()
  {
    var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    var found = new List<PlayerCandidate>();

    foreach (var name in Names)
    {
      var executable = windows ? name + ".exe" : name;
      // Walking PATH directly rather than shelling out to `which`/`where`: spawning a process to
      // find a process is slower and drags in shell quoting for no benefit.
      var path = ExecutableSearch.Find(executable) ?? (windows ? SearchWellKnownWindowsPaths(name) : null);
      if (path is not null) found.Add(new PlayerCandidate(name, path));
    }

    return found;
  }

  private static string? SearchWellKnownWindowsPaths(string name)
  {
    if (name != "vlc") return null;

    // Deliberately no registry lookup: Microsoft.Win32.Registry needs a net10.0-windows target or
    // an extra package, and adding a Windows-only TFM to a cross-platform sample for one lookup
    // is a bad trade. Probing the installer's standard locations covers it.
    string[] roots =
    [
      Environment.GetEnvironmentVariable("ProgramFiles") ?? "",
      Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "",
      Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Programs"),
    ];

    foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
    {
      var candidate = Path.Combine(root, "VideoLAN", "VLC", "vlc.exe");
      if (File.Exists(candidate)) return candidate;
    }

    return null;
  }

  /// <summary>
  /// Starts the player. Throws when it cannot be started at all; a player that starts and then
  /// dies — the usual outcome of an option it does not understand, or a codec it lacks — is
  /// reported through <paramref name="onEarlyExit"/> instead.
  /// </summary>
  public static void Launch(PlayerCandidate player, string uri, Action<string>? onEarlyExit = null)
  {
    var startInfo = new ProcessStartInfo(player.ExecutablePath)
    {
      // No shell: nothing gets a chance to reinterpret the URI, and the child stays ours.
      UseShellExecute = false,
      CreateNoWindow = false,
    };

    // ArgumentList, never Arguments: the URI carries ':', '@', '/', '?' and percent escapes, and
    // hand-quoting one command line is where mis-parsing and injection live. This escapes per
    // platform rules for us.
    foreach (var argument in player.BuildArguments(uri)) startInfo.ArgumentList.Add(argument);

    // stdout/stderr are deliberately not redirected: redirecting without draining deadlocks the
    // child as soon as it fills the pipe buffer, and a player is chatty.
    var process = Process.Start(startInfo);
    if (process is null || onEarlyExit is null) { process?.Dispose(); return; }

    // Without this, a player that prints "unknown option" and quits leaves the app reporting a
    // successful launch — the failure is only visible to whoever started the app from a terminal.
    _ = Task.Run(async () =>
    {
      try
      {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (process.HasExited && process.ExitCode != 0)
          onEarlyExit($"{player.Name} exited immediately with code {process.ExitCode} — run it by hand with the same URI to see why");
      }
      catch (Exception) { /* the process may already be gone; this is only a diagnostic */ }
      finally { process.Dispose(); }
    });
  }
}
