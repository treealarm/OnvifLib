using System.Runtime.InteropServices;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Resolves the ffmpeg binary used for in-window playback.
/// </summary>
/// <remarks>
/// Order: next to the app (every release package ships one), PATH, a copy under the app data
/// directory, then a path the user typed. Nothing is downloaded: versions up to 1.3.1 could fetch
/// ffmpeg into that directory, and a copy left there is still used. The library NuGet never ships
/// a binary.
/// </remarks>
public static class FfmpegLocator
{
  public static string CacheDirectory { get; } = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
    "OnvifLib.Gui", "ffmpeg");

  public static string CachedBinaryPath { get; } = Path.Combine(
    CacheDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg");

  public static string BinaryFileName { get; } =
    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

  /// <summary>
  /// Returns an existing ffmpeg, or null if none of the sources has one. Does not download.
  /// </summary>
  public static string? Find(string? configuredPath = null)
  {
    foreach (var candidate in SidecarCandidates())
    {
      if (File.Exists(candidate)) return candidate;
    }

    var fromPath = ExecutableSearch.Find(BinaryFileName);
    if (fromPath is not null) return fromPath;

    if (File.Exists(CachedBinaryPath)) return CachedBinaryPath;

    if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
      return configuredPath;

    return null;
  }

  /// <summary>Release layouts put ffmpeg beside the executable or under <c>ffmpeg/</c>.</summary>
  public static IEnumerable<string> SidecarCandidates()
  {
    var root = AppContext.BaseDirectory;
    yield return Path.Combine(root, BinaryFileName);
    yield return Path.Combine(root, "ffmpeg", BinaryFileName);
  }

  public static string? DescribeMissing()
  {
    if (Find() is not null) return null;

    if (OperatingSystem.IsLinux())
      return "ffmpeg was not found. Install it (sudo apt install ffmpeg), or set the path to an ffmpeg binary.";
    if (OperatingSystem.IsWindows())
      return "ffmpeg was not found. Install it (winget install ffmpeg), or set the path to ffmpeg.exe.";
    return "ffmpeg was not found. Install it on PATH, or set the path to an ffmpeg binary.";
  }
}
