using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnvifLib.Gui.Models;

/// <summary>
/// Last connection defaults, remembered devices, and video-player preferences.
/// </summary>
/// <remarks>
/// Stored as plain JSON under the user's config directory. Passwords are only written when the
/// user asks for it, and then in clear text — there is nowhere to hide them in a sample that has
/// to run unchanged on Windows and Linux, so the checkbox says so rather than implying otherwise.
/// </remarks>
public sealed class AppSettings
{
  public string Ip { get; set; } = "192.168.1.10";
  public int Port { get; set; } = 80;
  public string User { get; set; } = "admin";
  public double TimeoutSeconds { get; set; } = 15;
  public bool CaptureSoap { get; set; } = true;

  public bool RememberPassword { get; set; }
  public string Password { get; set; } = "";

  public string FfmpegPath { get; set; } = "";
  public int VideoWidth { get; set; } = 640;
  public int VideoHeight { get; set; } = 360;
  public int VideoFps { get; set; } = 12;
  public bool AutoPlayLive { get; set; } = true;

  public List<SavedDevice> Devices { get; set; } = [];

  [JsonIgnore]
  public static string Path { get; } = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
    "OnvifLib.Gui", "settings.json");

  public static AppSettings Load()
  {
    try
    {
      // A corrupt or hand-edited file must not stop the app from starting; defaults are fine.
      return File.Exists(Path)
        ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings()
        : new AppSettings();
    }
    catch (Exception)
    {
      return new AppSettings();
    }
  }

  /// <summary>Returns the failure rather than throwing: losing settings must not lose a session.</summary>
  public string? Save()
  {
    try
    {
      var directory = System.IO.Path.GetDirectoryName(Path);
      if (directory is not null) Directory.CreateDirectory(directory);

      var toWrite = StripUnrememberedPasswords();
      File.WriteAllText(Path, JsonSerializer.Serialize(toWrite, new JsonSerializerOptions { WriteIndented = true }));
      return null;
    }
    catch (Exception ex)
    {
      return ex.Message;
    }
  }

  private AppSettings StripUnrememberedPasswords() => new()
  {
    Ip = Ip,
    Port = Port,
    User = User,
    TimeoutSeconds = TimeoutSeconds,
    CaptureSoap = CaptureSoap,
    RememberPassword = RememberPassword,
    Password = RememberPassword ? Password : "",
    FfmpegPath = FfmpegPath,
    VideoWidth = VideoWidth,
    VideoHeight = VideoHeight,
    VideoFps = VideoFps,
    AutoPlayLive = AutoPlayLive,
    Devices = Devices.Select(d => new SavedDevice
    {
      Ip = d.Ip,
      Port = d.Port,
      Xaddr = d.Xaddr,
      User = d.User,
      DisplayName = d.DisplayName,
      RememberPassword = d.RememberPassword,
      Password = d.RememberPassword ? d.Password : "",
    }).ToList(),
  };
}
