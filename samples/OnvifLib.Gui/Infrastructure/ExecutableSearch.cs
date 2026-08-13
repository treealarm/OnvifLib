namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Finds an executable by walking PATH. Shared by the external player and the ffmpeg locator —
/// spawning <c>which</c>/<c>where</c> just to locate a process is slower and drags in shell quoting.
/// </summary>
internal static class ExecutableSearch
{
  public static string? Find(string executable)
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
}
