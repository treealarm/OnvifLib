using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OnvifLib.Gui.Infrastructure;

public sealed record PlayerCandidate(string Name, string ExecutablePath)
{
  public override string ToString() => $"{Name} ({ExecutablePath})";

  public IEnumerable<string> BuildArguments(string uri) => Name switch
  {
    // TCP transport across the board: UDP loses packets on any congested link and the resulting
    // smearing looks like a camera fault rather than a network one.
    "vlc" => ["--no-video-title-show", "--network-caching=300", "--rtsp-tcp", uri],
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
  private static readonly string[] Names = ["vlc", "ffplay", "mpv"];

  public static IReadOnlyList<PlayerCandidate> Discover()
  {
    var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    var found = new List<PlayerCandidate>();

    foreach (var name in Names)
    {
      var executable = windows ? name + ".exe" : name;
      // Walking PATH directly rather than shelling out to `which`/`where`: spawning a process to
      // find a process is slower and drags in shell quoting for no benefit.
      var path = SearchPath(executable) ?? (windows ? SearchWellKnownWindowsPaths(name) : null);
      if (path is not null) found.Add(new PlayerCandidate(name, path));
    }

    return found;
  }

  private static string? SearchPath(string executable)
  {
    var path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrEmpty(path)) return null;

    foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
      try
      {
        var candidate = Path.Combine(directory, executable);
        if (File.Exists(candidate)) return candidate;
      }
      catch (ArgumentException) { /* PATH entries with invalid characters are not worth failing over */ }
    }

    return null;
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

  /// <summary>Starts the player. Throws on failure so the caller's operation wrapper reports it.</summary>
  public static void Launch(PlayerCandidate player, string uri)
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

    // Fire and forget. stdout/stderr are deliberately not redirected: redirecting without
    // draining deadlocks the child as soon as it fills the pipe buffer.
    using var process = Process.Start(startInfo);
  }
}
