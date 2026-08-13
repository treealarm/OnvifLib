using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

public sealed record ServiceEntry(string Namespace, string XAddr);

/// <summary>
/// Device Management: identity, the service table, the clock, and the destructive operations
/// the probe refuses to run.
/// </summary>
public sealed partial class DeviceViewModel(OperationRunner runner, UiLogger logger, IDialogService dialogs)
  : TabViewModelBase("Device", runner, logger)
{
  [ObservableProperty] private string _manufacturer = "";
  [ObservableProperty] private string _model = "";
  [ObservableProperty] private string _firmware = "";
  [ObservableProperty] private string _serial = "";
  [ObservableProperty] private string _hardwareId = "";

  [ObservableProperty] private bool _hasPtz;
  [ObservableProperty] private bool _hasImaging;
  [ObservableProperty] private bool _hasEvents;
  [ObservableProperty] private bool _hasDigitalInputs;
  [ObservableProperty] private bool _hasEdgeRecording;
  [ObservableProperty] private bool _hasAnalytics;

  [ObservableProperty] private string _aliveText = "not checked";
  [ObservableProperty] private string _clockText = "not measured";
  [ObservableProperty] private string _storageSupportText = "not read";

  [ObservableProperty] private DateTimeOffset _manualDate = DateTimeOffset.Now;
  [ObservableProperty] private TimeSpan _manualTime = DateTime.Now.TimeOfDay;

  [ObservableProperty] private string? _selectedAuxiliaryCommand;
  [ObservableProperty] private string _auxiliaryResponse = "";

  public ObservableCollection<ServiceEntry> Services { get; } = [];
  public ObservableCollection<OnvifEdgeStorageConfiguration> StorageConfigurations { get; } = [];
  public ObservableCollection<string> AuxiliaryCommands { get; } = [];

  protected override string? DescribeUnavailability(CameraSession session) => null;

  protected override void OnConnected(CameraSession session)
  {
    Services.Clear();
    foreach (var (ns, xaddr) in session.Services.OrderBy(kv => kv.Key))
      Services.Add(new ServiceEntry(ns, xaddr));

    ApplyDeviceInfo(session.DeviceInfo);

    HasPtz = session.Capabilities.HasPtz;
    HasImaging = session.Capabilities.HasImaging;
    HasEvents = session.Capabilities.HasEvents;
    HasDigitalInputs = session.Capabilities.HasDigitalInputs;
    HasEdgeRecording = session.Capabilities.HasEdgeRecording;
    HasAnalytics = session.Capabilities.HasAnalytics;

    ClockText = DescribeOffset(session.ClockOffset);
    AliveText = "connected";
  }

  public override async Task ActivateAsync()
  {
    if (Session is null) return;
    await PingAsync();
    await ReadStorageSupportAsync();
    await ReadStorageConfigurationsAsync();
  }

  protected override void OnCleared()
  {
    Services.Clear();
    StorageConfigurations.Clear();
    AuxiliaryCommands.Clear();
    ApplyDeviceInfo(null);
    HasPtz = HasImaging = HasEvents = HasDigitalInputs = HasEdgeRecording = HasAnalytics = false;
    AliveText = "not checked";
    ClockText = "not measured";
    StorageSupportText = "not read";
    AuxiliaryResponse = "";
  }

  private void ApplyDeviceInfo(OnvifDeviceInfo? info)
  {
    Manufacturer = info?.Manufacturer ?? "";
    Model = info?.Model ?? "";
    Firmware = info?.FirmwareVersion ?? "";
    Serial = info?.SerialNumber ?? "";
    HardwareId = info?.HardwareId ?? "";
  }

  private static string DescribeOffset(TimeSpan offset) => offset == TimeSpan.Zero
    ? "0 (or not measured)"
    : $"{offset:g} — the camera runs {(offset > TimeSpan.Zero ? "ahead of" : "behind")} this machine";

  [RelayCommand]
  private async Task RefreshInfoAsync()
  {
    if (Session is not { } session) return;
    var (ok, info) = await Runner.RunAsync("GetDeviceInformation", session.Camera.GetDeviceInformationAsync);
    if (ok) { session.DeviceInfo = info; ApplyDeviceInfo(info); }
  }

  [RelayCommand]
  private async Task PingAsync()
  {
    if (Session is not { } session) return;
    var (ok, alive) = await Runner.RunAsync("IsAlive", session.Camera.IsAlive);
    AliveText = ok ? (alive ? "alive" : "no services returned") : "check failed";
  }

  [RelayCommand]
  private async Task RefreshServicesAsync()
  {
    if (Session is not { } session) return;
    var (ok, services) = await Runner.RunAsync("GetServices", session.Camera.GetServicesAsync);
    if (!ok) return;
    if (services is null) { Runner.Report("GetServices returned null — see the Log tab", isError: true); return; }

    Services.Clear();
    foreach (var (ns, xaddr) in services.OrderBy(kv => kv.Key)) Services.Add(new ServiceEntry(ns, xaddr));
  }

  [RelayCommand]
  private async Task ReadDeviceTimeAsync()
  {
    if (Session is not { } session) return;
    var (ok, utc) = await Runner.RunAsync("GetDeviceTime", session.Camera.GetDeviceTimeAsync);
    if (!ok) return;
    ClockText = utc is { } time
      ? $"device reports {time:yyyy-MM-dd HH:mm:ss} UTC"
      : "the device did not report UTC";
  }

  [RelayCommand]
  private async Task MeasureClockAsync()
  {
    if (Session is not { } session) return;
    var (ok, reading) = await Runner.RunAsync("MeasureClock", session.Camera.MeasureClockAsync);
    if (!ok) return;
    if (reading is null) { ClockText = "the device did not report UTC, so the offset is unknown"; return; }

    // Every Profile G timestamp is interpreted through this, so it is stored on the session
    // rather than only displayed.
    session.ClockOffset = reading.Offset;
    ClockText = $"camera {reading.CameraUtc:HH:mm:ss} / here {reading.ServerUtc:HH:mm:ss} " +
                $"(round trip {reading.RoundTrip.TotalMilliseconds:0} ms) — offset {DescribeOffset(reading.Offset)}";
  }

  [RelayCommand]
  private async Task ReadStorageSupportAsync()
  {
    if (Session is not { } session) return;
    var (ok, support) = await Runner.RunAsync("GetStorageSupport", session.Camera.GetStorageSupportAsync);
    if (!ok || support is null) return;

    StorageSupportText = $"storage configuration: {(support.StorageConfiguration ? "yes" : "no")}, " +
                         $"max {support.MaxStorageConfigurations}";
    AuxiliaryCommands.Clear();
    foreach (var command in support.AuxiliaryCommands) AuxiliaryCommands.Add(command);
  }

  [RelayCommand]
  private async Task ReadStorageConfigurationsAsync()
  {
    if (Session is not { } session) return;
    var (ok, configs) = await Runner.RunAsync("GetStorageConfigurations", session.Camera.GetStorageConfigurationsAsync);
    if (!ok || configs is null) return;

    StorageConfigurations.Clear();
    foreach (var config in configs) StorageConfigurations.Add(config);
  }

  [RelayCommand]
  private async Task SyncTimeAsync()
  {
    if (Session is not { } session) return;
    if (!await dialogs.ConfirmAsync("Set the camera clock",
          "This overwrites the camera's clock with this machine's UTC time.\n\n" +
          "On a camera that records, this shifts how its own archive is indexed from now on."))
      return;

    await Runner.RunAsync("SyncTime", session.Camera.SyncTimeAsync);
  }

  [RelayCommand]
  private async Task SetTimeAsync()
  {
    if (Session is not { } session) return;

    var local = ManualDate.Date + ManualTime;
    var utc = local.ToUniversalTime();
    if (!await dialogs.ConfirmAsync("Set the camera clock",
          $"Set the camera to {utc:yyyy-MM-dd HH:mm:ss} UTC?\n\n" +
          "On a camera that records, this shifts how its own archive is indexed from now on."))
      return;

    await Runner.RunAsync("SetTime", () => session.Camera.SetTimeAsync(utc));
  }

  [RelayCommand]
  private async Task RebootAsync()
  {
    if (Session is not { } session) return;
    if (!await dialogs.ConfirmAsync("Reboot the camera",
          "The camera will restart and be offline for as long as it takes to come back.\n\nContinue?"))
      return;

    var (ok, response) = await Runner.RunAsync("Reboot", session.Camera.RebootAsync);
    if (ok) Runner.Report($"Reboot — the camera replied: {response}");
  }

  [RelayCommand]
  private async Task SendAuxiliaryCommandAsync()
  {
    if (Session is not { } session) return;
    if (SelectedAuxiliaryCommand is not { Length: > 0 } command) return;

    // ONVIF standardises the transport but not the command strings, and on many cameras one of
    // them formats the SD card. There is no way to tell which from here.
    if (!await dialogs.ConfirmAsync("Send an auxiliary command",
          $"Send \"{command}\" to the camera?\n\n" +
          "ONVIF does not define what vendor auxiliary commands do. On many cameras one of them " +
          "formats the storage, and there is no undo."))
      return;

    var (ok, response) = await Runner.RunAsync($"SendAuxiliaryCommand {command}",
      () => session.Camera.SendAuxiliaryCommandAsync(command));
    if (ok) AuxiliaryResponse = response ?? "";
  }
}
