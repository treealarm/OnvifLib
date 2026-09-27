using System.Text.Json.Serialization;

namespace OnvifLib.Gui.Models;

/// <summary>One remembered camera in <c>settings.json</c>.</summary>
public sealed class SavedDevice
{
  public string Ip { get; set; } = "";
  public int Port { get; set; } = 80;
  public string? Xaddr { get; set; }
  public string User { get; set; } = "admin";
  public string DisplayName { get; set; } = "";
  public bool RememberPassword { get; set; }

  /// <summary>In memory only; on disk it is <see cref="ProtectedPassword"/>.</summary>
  [JsonIgnore]
  public string Password { get; set; } = "";

  /// <summary>See <see cref="Infrastructure.CredentialProtector"/>.</summary>
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? ProtectedPassword { get; set; }

  /// <summary>Clear-text password written by 1.2.0 and earlier. Read once to migrate, never written.</summary>
  [JsonPropertyName("Password")]
  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
  public string? LegacyPassword { get; set; }
}
