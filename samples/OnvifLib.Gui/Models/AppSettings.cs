using System.Text.Json;
using System.Text.Json.Serialization;
using OnvifLib.Gui.Infrastructure;

namespace OnvifLib.Gui.Models;

/// <summary>
/// Last connection defaults, remembered devices, and video-player preferences.
/// </summary>
/// <remarks>
/// Stored as JSON under the user's config directory. A password is written only when the user
/// ticks Remember, and then only encrypted by <see cref="CredentialProtector"/> — never in clear
/// text. Where the OS offers no secret store, passwords are not persisted at all.
/// </remarks>
public sealed class AppSettings
{
  public string Ip { get; set; } = "192.168.1.10";
  public int Port { get; set; } = 80;
  public string User { get; set; } = "admin";
  public double TimeoutSeconds { get; set; } = 15;
  public bool CaptureSoap { get; set; } = true;

  public bool RememberPassword { get; set; }

  /// <summary>In memory only; on disk it is <see cref="ProtectedPassword"/>.</summary>
  [JsonIgnore]
  public string Password { get; set; } = "";

  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? ProtectedPassword { get; set; }

  /// <summary>Clear-text password written by 1.2.0 and earlier. Read once to migrate, never written.</summary>
  [JsonPropertyName("Password")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? LegacyPassword { get; set; }

  public string FfmpegPath { get; set; } = "";
  public int VideoWidth { get; set; } = 640;
  public int VideoHeight { get; set; } = 360;
  public int VideoFps { get; set; } = 12;
  public bool AutoPlayLive { get; set; } = true;

  /// <summary>"Light" or "Dark". Applied to <c>Application.RequestedThemeVariant</c>.</summary>
  public string Theme { get; set; } = "Light";

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
      var settings = File.Exists(Path)
        ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new AppSettings()
        : new AppSettings();

      // Rewrite at once rather than on exit, so a clear-text password from an older version does
      // not stay on disk for the whole session — or forever, if the app is killed.
      if (settings.DecryptPasswords()) settings.Save();
      return settings;
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

      var toWrite = ForDisk();
      File.WriteAllText(Path, JsonSerializer.Serialize(toWrite, new JsonSerializerOptions { WriteIndented = true }));
      return null;
    }
    catch (Exception ex)
    {
      return ex.Message;
    }
  }

  /// <summary>Fills <see cref="Password"/> from disk. True when a legacy clear-text field was found.</summary>
  private bool DecryptPasswords()
  {
    var migrated = false;
    (Password, RememberPassword) = Reveal(ProtectedPassword, LegacyPassword, RememberPassword, ref migrated);
    foreach (var device in Devices)
      (device.Password, device.RememberPassword) = Reveal(device.ProtectedPassword, device.LegacyPassword, device.RememberPassword, ref migrated);
    return migrated;
  }

  private static (string Password, bool Remember) Reveal(string? protectedPassword, string? legacy, bool remember, ref bool migrated)
  {
    migrated |= legacy is not null;
    if (!remember) return ("", false);
    var password = legacy is { Length: > 0 } ? legacy : CredentialProtector.Unprotect(protectedPassword);
    // No secret store here: keep the password for this session, but do not claim it will be kept.
    return (password, CredentialProtector.IsAvailable);
  }

  private static string? ProtectIfRemembered(bool remember, string password) =>
    remember ? CredentialProtector.Protect(password) : null;

  private AppSettings ForDisk() => new()
  {
    Ip = Ip,
    Port = Port,
    User = User,
    TimeoutSeconds = TimeoutSeconds,
    CaptureSoap = CaptureSoap,
    RememberPassword = RememberPassword,
    ProtectedPassword = ProtectIfRemembered(RememberPassword, Password),
    FfmpegPath = FfmpegPath,
    VideoWidth = VideoWidth,
    VideoHeight = VideoHeight,
    VideoFps = VideoFps,
    AutoPlayLive = AutoPlayLive,
    Theme = Theme,
    Devices = Devices.Select(d => new SavedDevice
    {
      Ip = d.Ip,
      Port = d.Port,
      Xaddr = d.Xaddr,
      User = d.User,
      DisplayName = d.DisplayName,
      RememberPassword = d.RememberPassword,
      ProtectedPassword = ProtectIfRemembered(d.RememberPassword, d.Password),
    }).ToList(),
  };
}
