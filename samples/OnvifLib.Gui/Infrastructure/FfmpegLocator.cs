using System.Runtime.InteropServices;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Resolves the ffmpeg binary used for in-window playback.
/// </summary>
/// <remarks>
/// Order: PATH, then a previously downloaded copy under the app data directory, then a path the
/// user typed. The library NuGet never ships a binary; this sample finds or downloads one.
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
  /// Returns an existing ffmpeg, or null if none of the three sources has one. Does not download.
  /// </summary>
  public static string? Find(string? configuredPath = null)
  {
    var fromPath = ExecutableSearch.Find(BinaryFileName);
    if (fromPath is not null) return fromPath;

    if (File.Exists(CachedBinaryPath)) return CachedBinaryPath;

    if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
      return configuredPath;

    return null;
  }

  public static string? DescribeMissing()
  {
    if (Find() is not null) return null;

    if (!FfmpegDownloader.IsCurrentRidSupported)
    {
      return OperatingSystem.IsLinux()
        ? "ffmpeg was not found. Install it with `sudo apt install ffmpeg`, or set a path to an ffmpeg binary."
        : "ffmpeg was not found. Set a path to an ffmpeg binary, or run on win-x64 / linux-x64 to download one.";
    }

    return "ffmpeg was not found. Press Download to fetch an LGPL build, install it on PATH, or set a path.";
  }
}
