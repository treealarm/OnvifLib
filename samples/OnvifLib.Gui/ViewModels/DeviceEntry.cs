using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

public enum DeviceConnectionState
{
  Disconnected,
  Connecting,
  Connected,
  Error,
}

/// <summary>One row in the device list: identity, credentials, optional live session, JPEG thumb.</summary>
public sealed partial class DeviceEntry : ObservableObject
{
  [ObservableProperty] private string _displayName = "";
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(AddressText))]
  [NotifyPropertyChangedFor(nameof(Key))]
  private string _ip = "";
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(AddressText))]
  [NotifyPropertyChangedFor(nameof(Key))]
  private int _port = 80;
  [ObservableProperty] private string? _xaddr;
  [ObservableProperty] private string _user = "admin";
  [ObservableProperty] private string _password = "";
  [ObservableProperty] private bool _rememberPassword;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(StateText))]
  private DeviceConnectionState _state;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(StateText))]
  private string _errorText = "";
  [ObservableProperty] private Bitmap? _thumbnail;

  /// <summary>
  /// True after a successful Login until the user hits Disconnect/Remove. The watchdog reconnects
  /// only while this is set, so an intentional drop is not fought.
  /// </summary>
  public bool WantConnected { get; set; }

  /// <summary>True once a session was established; failed first logins do not start the reconnect loop.</summary>
  public bool EverConnected { get; set; }

  private CameraSession? _session;

  public CameraSession? Session
  {
    get => _session;
    set
    {
      if (ReferenceEquals(_session, value)) return;
      _session = value;
      OnPropertyChanged();
      OnPropertyChanged(nameof(IsConnected));
      if (value is null)
      {
        if (State == DeviceConnectionState.Connected) State = DeviceConnectionState.Disconnected;
      }
      else
      {
        State = DeviceConnectionState.Connected;
        ErrorText = "";
      }
    }
  }

  public bool IsConnected => Session is not null;

  public string Key => $"{Ip}:{Port}";

  public string AddressText => $"{Ip}:{Port}";

  public string StateText => State switch
  {
    DeviceConnectionState.Connecting => "connecting",
    DeviceConnectionState.Connected => "connected",
    DeviceConnectionState.Error => WantConnected
      ? (string.IsNullOrEmpty(ErrorText) ? "reconnecting…" : ErrorText)
      : string.IsNullOrEmpty(ErrorText) ? "error" : ErrorText,
    _ => "offline",
  };

  public void SetThumbnail(Bitmap? bitmap)
  {
    var previous = Thumbnail;
    Thumbnail = bitmap;
    previous?.Dispose();
  }

  public SavedDevice ToSaved() => new()
  {
    Ip = Ip,
    Port = Port,
    Xaddr = Xaddr,
    User = User,
    DisplayName = DisplayName,
    RememberPassword = RememberPassword,
    Password = RememberPassword ? Password : "",
  };

  public static DeviceEntry FromSaved(SavedDevice saved) => new()
  {
    Ip = saved.Ip,
    Port = saved.Port,
    Xaddr = saved.Xaddr,
    User = saved.User,
    Password = saved.RememberPassword ? saved.Password : "",
    RememberPassword = saved.RememberPassword,
    DisplayName = string.IsNullOrWhiteSpace(saved.DisplayName) ? $"{saved.Ip}:{saved.Port}" : saved.DisplayName,
  };

  public void DisposeThumbnail() => SetThumbnail(null);
}
