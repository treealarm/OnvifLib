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
  public string Password { get; set; } = "";
}
