using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OnvifLib.Gui.ViewModels;

/// <summary>Device administration: the clock's mode and zone, user accounts, discovery scopes.</summary>
public sealed partial class DeviceViewModel
{
  // ── Date & time ───────────────────────────────────────────────────────────────

  public IReadOnlyList<string> DateTimeTypes { get; } = ["Manual", "NTP"];

  [ObservableProperty] private string _systemDateTimeText = "not read";
  [ObservableProperty] private string _editDateTimeType = "Manual";
  [ObservableProperty] private bool _editDaylightSavings;
  [ObservableProperty] private string _editTimeZone = "";

  [RelayCommand]
  private async Task LoadSystemDateTimeAsync()
  {
    if (Session is not { } session) return;
    var (ok, t) = await Runner.RunAsync("GetSystemDateAndTime", session.Camera.GetSystemDateAndTimeAsync);
    if (!ok || t is null) return;

    EditDateTimeType = DateTimeTypes.FirstOrDefault(d => d.Equals(t.DateTimeType, StringComparison.OrdinalIgnoreCase)) ?? t.DateTimeType;
    EditDaylightSavings = t.DaylightSavings;
    EditTimeZone = t.TimeZone ?? "";
    SystemDateTimeText =
      $"{t.DateTimeType} mode, zone {t.TimeZone ?? "not set"}, DST {(t.DaylightSavings ? "on" : "off")} — " +
      $"camera UTC {t.Utc:yyyy-MM-dd HH:mm:ss}, camera local {t.Local:yyyy-MM-dd HH:mm:ss}";
  }

  /// <summary>
  /// Applies mode, zone and DST. In Manual mode the time sent is the camera's own current time
  /// carried forward, so changing only the zone does not also move the clock — "Set clock" and
  /// "Sync clock" are the commands that change the time itself.
  /// </summary>
  [RelayCommand]
  private async Task ApplySystemDateTimeAsync()
  {
    if (Session is not { } session) return;
    var zone = string.IsNullOrWhiteSpace(EditTimeZone) ? null : EditTimeZone.Trim();
    if (!await dialogs.ConfirmAsync("Change how the camera keeps time",
          $"Set the camera to {EditDateTimeType} mode, zone {zone ?? "(unchanged)"}, DST {(EditDaylightSavings ? "on" : "off")}?\n\n" +
          (EditDateTimeType == "NTP" ? "NTP mode needs reachable NTP servers (Network page). " : "") +
          "On a camera that records, a different zone changes the local times its archive is labelled with."))
      return;

    var type = EditDateTimeType;
    var dst = EditDaylightSavings;
    if (await Runner.RunAsync("SetSystemDateAndTime", async () =>
        {
          System.DateTime? utc = null;
          if (type == "Manual")
          {
            // The camera's time, carried forward from a fresh offset measurement rather than
            // read and sent back: ONVIF has whole seconds only, and the camera truncates both
            // the reading and the write, so a plain round trip loses about a second each time.
            // The extra second cancels those two truncations on average.
            var reading = await session.Camera.MeasureClockAsync();
            utc = reading is { } r
              ? System.DateTime.UtcNow + r.Offset + TimeSpan.FromSeconds(1)
              : System.DateTime.UtcNow;
          }
          await session.Camera.SetSystemDateAndTimeAsync(type, dst, zone, utc);
        }))
      await LoadSystemDateTimeAsync();
  }

  // ── Users ─────────────────────────────────────────────────────────────────────

  public ObservableCollection<OnvifUser> Users { get; } = [];
  public IReadOnlyList<OnvifUserLevel> UserLevels { get; } = Enum.GetValues<OnvifUserLevel>();

  [ObservableProperty] private OnvifUser? _selectedUser;
  [ObservableProperty] private string _editUsername = "";
  [ObservableProperty] private string _editPassword = "";
  [ObservableProperty] private OnvifUserLevel _editLevel = OnvifUserLevel.User;

  partial void OnSelectedUserChanged(OnvifUser? value)
  {
    if (value is null) return;
    EditUsername = value.Username;
    EditLevel = value.Level;
    EditPassword = "";
  }

  [RelayCommand]
  private async Task LoadUsersAsync()
  {
    if (Session is not { } session) return;
    var (ok, users) = await Runner.RunAsync("GetUsers", session.Camera.GetUsersAsync);
    if (!ok) return;
    Users.Clear();
    foreach (var u in users ?? []) Users.Add(u);
  }

  [RelayCommand]
  private async Task CreateUserAsync()
  {
    if (Session is not { } session) return;
    var name = EditUsername.Trim();
    if (name.Length == 0 || EditPassword.Length == 0)
    {
      Runner.Report("A new user needs a name and a password", isError: true);
      return;
    }
    if (!await dialogs.ConfirmAsync("Create a user", $"Create '{name}' as {EditLevel} on the camera?"))
      return;
    var password = EditPassword;
    var level = EditLevel;
    if (await Runner.RunAsync($"CreateUser [{name}]", () => session.Camera.CreateUserAsync(name, password, level)))
    {
      EditPassword = "";
      await LoadUsersAsync();
    }
  }

  /// <summary>Changes the level, and the password when one is typed. A blank password is left as it is.</summary>
  [RelayCommand]
  private async Task UpdateUserAsync()
  {
    if (Session is not { } session) return;
    var name = EditUsername.Trim();
    if (name.Length == 0) { Runner.Report("Pick a user first", isError: true); return; }
    var password = EditPassword.Length == 0 ? null : EditPassword;
    var self = name == session.Camera.User;
    if (!await dialogs.ConfirmAsync("Change a user",
          $"Set '{name}' to {EditLevel}{(password is null ? "" : " with the new password")}?" +
          (self && password is not null
            ? "\n\nThis is the account this app is logged in with: the session stops working until you reconnect with the new password."
            : "") +
          (self && EditLevel != OnvifUserLevel.Administrator
            ? "\n\nLowering the level of the account this app uses can lock it out of the settings it is changing."
            : "")))
      return;
    var level = EditLevel;
    if (await Runner.RunAsync($"SetUser [{name}]", () => session.Camera.SetUserAsync(name, password, level)))
    {
      EditPassword = "";
      await LoadUsersAsync();
    }
  }

  [RelayCommand]
  private async Task DeleteUserAsync()
  {
    if (Session is not { } session) return;
    var name = EditUsername.Trim();
    if (name.Length == 0) { Runner.Report("Pick a user first", isError: true); return; }
    if (!await dialogs.ConfirmAsync("Delete a user",
          $"Delete '{name}' from the camera? There is no undo." +
          (name == session.Camera.User ? "\n\nThis is the account this app is logged in with." : "")))
      return;
    if (await Runner.RunAsync($"DeleteUser [{name}]", () => session.Camera.DeleteUserAsync(name)))
    {
      EditUsername = "";
      await LoadUsersAsync();
    }
  }

  // ── Scopes ────────────────────────────────────────────────────────────────────

  public ObservableCollection<OnvifScope> Scopes { get; } = [];

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanRemoveScope))]
  private OnvifScope? _selectedScope;

  [ObservableProperty] private string _newScope = "onvif://www.onvif.org/location/";

  /// <summary>The configurable scopes, one per line, for "Replace configurable".</summary>
  [ObservableProperty] private string _configurableScopesText = "";

  public bool CanRemoveScope => SelectedScope is { Fixed: false };

  [RelayCommand]
  private async Task LoadScopesAsync()
  {
    if (Session is not { } session) return;
    var (ok, scopes) = await Runner.RunAsync("GetScopes", session.Camera.GetScopesAsync);
    if (!ok) return;
    Scopes.Clear();
    foreach (var s in scopes ?? []) Scopes.Add(s);
    ConfigurableScopesText = string.Join(Environment.NewLine, Scopes.Where(s => !s.Fixed).Select(s => s.ScopeItem));
  }

  [RelayCommand]
  private async Task AddScopeAsync()
  {
    if (Session is not { } session) return;
    var scope = NewScope.Trim();
    if (scope.Length == 0) return;
    if (!await dialogs.ConfirmAsync("Add a discovery scope", $"Add '{scope}'? Discovery tools will see it."))
      return;
    if (await Runner.RunAsync("AddScopes", () => session.Camera.AddScopesAsync([scope])))
      await LoadScopesAsync();
  }

  [RelayCommand]
  private async Task RemoveScopeAsync()
  {
    if (Session is not { } session || SelectedScope is not { Fixed: false } scope) return;
    if (!await dialogs.ConfirmAsync("Remove a discovery scope", $"Remove '{scope.ScopeItem}'?"))
      return;
    if (await Runner.RunAsync("RemoveScopes", () => session.Camera.RemoveScopesAsync([scope.ScopeItem])))
      await LoadScopesAsync();
  }

  /// <summary>Replaces the configurable scopes with the edited list. Fixed scopes are not touched.</summary>
  [RelayCommand]
  private async Task ReplaceScopesAsync()
  {
    if (Session is not { } session) return;
    var scopes = ConfigurableScopesText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    if (scopes.Count == 0)
    {
      // SetScopes with nothing is how a camera is told to drop every configurable scope — almost
      // never what someone who cleared a text box meant.
      Runner.Report("Refusing to replace the scopes with an empty list — remove them one by one instead", isError: true);
      return;
    }
    if (!await dialogs.ConfirmAsync("Replace configurable scopes",
          $"Replace every configurable scope with these {scopes.Count}?\n\n{string.Join("\n", scopes)}"))
      return;
    if (await Runner.RunAsync("SetScopes", () => session.Camera.SetScopesAsync(scopes)))
      await LoadScopesAsync();
  }

  private void ClearAdministration()
  {
    SystemDateTimeText = "not read";
    EditTimeZone = "";
    Users.Clear();
    EditUsername = EditPassword = "";
    Scopes.Clear();
    ConfigurableScopesText = "";
  }
}
