using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Fetches a pinned LGPL ffmpeg build into the app data directory. The archive is not committed
/// and never packed into the library NuGet — the repository stays MIT.
/// </summary>
public static class FfmpegDownloader
{
  // Dated BtbN autobuild, LGPL (not GPL), n7.1.x. SHA256 is of the archive, not the extracted binary.
  // Windows URL/SHA must match samples/OnvifLib.Gui/ffmpeg.props (MSI + win-x64 publish).
  private static readonly Build LinuxX64 = new(
    "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-12-13-15/ffmpeg-n7.1.5-12-g1fdbca85aa-linux64-lgpl-7.1.tar.xz",
    "2fc7aa2eb6e75807170a34fec11af8eea3bc39875cf001d26eabc1605de99a87",
    "ffmpeg-n7.1.5-12-g1fdbca85aa-linux64-lgpl-7.1.tar.xz");

  private static readonly Build WindowsX64 = new(
    "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-12-13-15/ffmpeg-n7.1.5-12-g1fdbca85aa-win64-lgpl-7.1.zip",
    "8fdbe7f03b64134fecf26166a22d4b4f5be0756901461d01fe5ad7dbc03b5ce7",
    "ffmpeg-n7.1.5-12-g1fdbca85aa-win64-lgpl-7.1.zip");

  public static bool IsCurrentRidSupported =>
    (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
    && RuntimeInformation.OSArchitecture is Architecture.X64;

  public static string? UnsupportedRidMessage => IsCurrentRidSupported
    ? null
    : $"No download is published for {RuntimeInformation.RuntimeIdentifier}. Put an ffmpeg binary on PATH or in the path box.";

  /// <summary>
  /// Downloads, verifies SHA256, extracts <c>ffmpeg</c> into <see cref="FfmpegLocator.CacheDirectory"/>.
  /// </summary>
  public static async Task<string> DownloadAsync(IProgress<string>? progress, CancellationToken cancellation)
  {
    var build = SelectBuild()
      ?? throw new InvalidOperationException(UnsupportedRidMessage);

    Directory.CreateDirectory(FfmpegLocator.CacheDirectory);

    var archivePath = Path.Combine(FfmpegLocator.CacheDirectory, build.FileName);
    var staging = Path.Combine(FfmpegLocator.CacheDirectory, "staging");

    try
    {
      progress?.Report("Downloading ffmpeg (LGPL)…");
      await DownloadFileAsync(build.Url, archivePath, progress, cancellation).ConfigureAwait(false);

      progress?.Report("Verifying SHA256…");
      var actual = await HashFileAsync(archivePath, cancellation).ConfigureAwait(false);
      if (!actual.Equals(build.Sha256, StringComparison.OrdinalIgnoreCase))
      {
        throw new InvalidOperationException(
          $"ffmpeg archive checksum mismatch (expected {build.Sha256}, got {actual}). " +
          "The published build may have changed — set a path to ffmpeg, or install it with the OS package manager.");
      }

      if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
      Directory.CreateDirectory(staging);

      progress?.Report("Extracting ffmpeg…");
      if (build.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        ExtractZip(archivePath, staging);
      else
        await ExtractTarXzAsync(archivePath, staging, cancellation).ConfigureAwait(false);

      var extracted = FindExtractedBinary(staging)
        ?? throw new InvalidOperationException("The archive downloaded, but it did not contain an ffmpeg binary.");

      Directory.CreateDirectory(FfmpegLocator.CacheDirectory);
      File.Copy(extracted, FfmpegLocator.CachedBinaryPath, overwrite: true);

      if (!OperatingSystem.IsWindows())
      {
        File.SetUnixFileMode(FfmpegLocator.CachedBinaryPath,
          UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
          UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
          UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
      }

      if (!File.Exists(FfmpegLocator.CachedBinaryPath))
        throw new InvalidOperationException("ffmpeg was extracted but the cached binary is missing.");

      progress?.Report($"ffmpeg ready: {FfmpegLocator.CachedBinaryPath}");
      return FfmpegLocator.CachedBinaryPath;
    }
    finally
    {
      TryDelete(archivePath);
      TryDeleteDirectory(staging);
    }
  }

  private static Build? SelectBuild()
  {
    if (!IsCurrentRidSupported) return null;
    return OperatingSystem.IsWindows() ? WindowsX64 : LinuxX64;
  }

  private static async Task DownloadFileAsync(string url, string destination, IProgress<string>? progress, CancellationToken cancellation)
  {
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
    response.EnsureSuccessStatusCode();

    var total = response.Content.Headers.ContentLength;
    await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
    await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 82_000, useAsync: true);

    var buffer = new byte[82_000];
    long copied = 0;
    var lastReport = 0L;
    while (true)
    {
      var read = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false);
      if (read == 0) break;
      await output.WriteAsync(buffer.AsMemory(0, read), cancellation).ConfigureAwait(false);
      copied += read;
      if (total is > 0 && copied - lastReport > total.Value / 20)
      {
        lastReport = copied;
        progress?.Report($"Downloading ffmpeg… {copied / 1_000_000.0:0.0}/{total.Value / 1_000_000.0:0.0} MB");
      }
    }
  }

  private static async Task<string> HashFileAsync(string path, CancellationToken cancellation)
  {
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 82_000, useAsync: true);
    var hash = await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false);
    return Convert.ToHexString(hash).ToLowerInvariant();
  }

  private static void ExtractZip(string archivePath, string destination)
  {
    ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
  }

  /// <summary>
  /// BtbN Linux builds are <c>.tar.xz</c>. .NET has no xz decoder in the box; <c>tar</c> is on
  /// every Linux we care about.
  /// </summary>
  private static async Task ExtractTarXzAsync(string archivePath, string destination, CancellationToken cancellation)
  {
    var start = new System.Diagnostics.ProcessStartInfo("tar")
    {
      ArgumentList = { "-xJf", archivePath, "-C", destination },
      UseShellExecute = false,
      RedirectStandardError = true,
      RedirectStandardOutput = true,
      CreateNoWindow = true,
    };

    using var process = System.Diagnostics.Process.Start(start)
      ?? throw new InvalidOperationException("Could not start tar to extract ffmpeg.");

    var stderr = await process.StandardError.ReadToEndAsync(cancellation).ConfigureAwait(false);
    await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
    if (process.ExitCode != 0)
      throw new InvalidOperationException($"tar failed ({process.ExitCode}): {stderr.Trim()}");
  }

  private static string? FindExtractedBinary(string root)
  {
    var name = FfmpegLocator.BinaryFileName;
    return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
      .FirstOrDefault(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase));
  }

  private static void TryDelete(string path)
  {
    try { if (File.Exists(path)) File.Delete(path); }
    catch (Exception) { /* leftover archives are harmless */ }
  }

  private static void TryDeleteDirectory(string path)
  {
    try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
    catch (Exception) { /* leftover staging is harmless */ }
  }

  private sealed record Build(string Url, string Sha256, string FileName);
}
