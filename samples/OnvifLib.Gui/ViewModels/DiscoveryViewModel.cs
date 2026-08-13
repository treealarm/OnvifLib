using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

public sealed record ScannedCamera(string Ip, int Port, string User, string Url);

/// <summary>
/// Finds cameras. The only tab that works without a session, which makes it the place to start
/// when a connection attempt is failing.
/// </summary>
public sealed partial class DiscoveryViewModel : TabViewModelBase
{
  private readonly UiLogger _logger;
  private CancellationTokenSource? _probeCancellation;
  private CancellationTokenSource? _scanCancellation;

  public override bool RequiresConnection => false;
  public override bool IsSessionScoped => false;

  public DiscoveryViewModel(OperationRunner runner, UiLogger logger) : base("Discovery", runner, logger)
  {
    _logger = logger;
    IsAvailable = true;   // no session required
  }

  public ObservableCollection<DiscoveredDevice> Devices { get; } = [];
  public ObservableCollection<ScannedCamera> Scanned { get; } = [];

  [ObservableProperty] private double _probeSeconds = 4;
  [ObservableProperty] private string _probeResultText = "not probed yet";
  [ObservableProperty] private bool _probeResultIsWarning;
  [ObservableProperty] private DiscoveredDevice? _selectedDevice;

  [ObservableProperty] private string _scanFrom = "192.168.1.1";
  [ObservableProperty] private string _scanTo = "192.168.1.254";
  [ObservableProperty] private string _scanPorts = "80,8000,8080,2020";
  [ObservableProperty] private int _scanPercent;
  [ObservableProperty] private string _scanStatus = "idle";
  [ObservableProperty] private bool _isScanning;
  [ObservableProperty] private ScannedCamera? _selectedScanned;

  /// <summary>Raised when a row is chosen, so the shell can fill in the connection bar.</summary>
  public event Action<string, int, string?>? UseRequested;

  protected override string? DescribeUnavailability(CameraSession session) => null;

  public override Task ShutdownAsync()
  {
    _probeCancellation?.Cancel();
    _scanCancellation?.Cancel();
    return Task.CompletedTask;
  }

  [RelayCommand]
  private async Task ProbeAsync()
  {
    _probeCancellation?.Cancel();
    _probeCancellation = new CancellationTokenSource();
    var token = _probeCancellation.Token;

    Devices.Clear();
    var (ok, result) = await Runner.RunAsync("WS-Discovery probe",
      () => WsDiscovery.ProbeAsync(TimeSpan.FromSeconds(ProbeSeconds), token, _logger));

    if (!ok || result is null) { ProbeResultText = "the probe failed — see the Log tab"; ProbeResultIsWarning = true; return; }

    foreach (var device in result.Devices) Devices.Add(device);

    // Three genuinely different outcomes. The library keeps ScanOk separate precisely so that
    // "we could not probe at all" is never reported as "there is nothing out there".
    if (!result.ScanOk)
    {
      ProbeResultText = "Could not probe at all — no network interface could join the multicast group " +
                        "239.255.255.250:3702. Check the firewall, container or VPN networking, and that " +
                        "this machine has a real LAN interface.";
      ProbeResultIsWarning = true;
    }
    else if (result.Devices.Count == 0)
    {
      ProbeResultText = "Probed successfully — no ONVIF device answered.";
      ProbeResultIsWarning = false;
    }
    else
    {
      ProbeResultText = $"{result.Devices.Count} device(s) answered. Double-click one to add it to the device list.";
      ProbeResultIsWarning = false;
    }
  }

  [RelayCommand]
  private void CancelProbe() => _probeCancellation?.Cancel();

  [RelayCommand]
  private void UseSelectedDevice()
  {
    if (SelectedDevice is not { } device) return;
    // The XAddr matters: some cameras serve device_service on a non-standard path, and passing
    // it through is what lets Camera.Create reach them at all.
    UseRequested?.Invoke(device.Ip, device.Port, device.XAddrs.FirstOrDefault());
  }

  [RelayCommand]
  private void UseSelectedScanned()
  {
    if (SelectedScanned is { } camera) UseRequested?.Invoke(camera.Ip, camera.Port, null);
  }

  [RelayCommand]
  private async Task ScanAsync()
  {
    if (IsScanning) return;

    var ports = ScanPorts
      .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
      .Select(p => int.TryParse(p, out var v) ? v : -1)
      .Where(v => v is > 0 and <= 65535)
      .ToList();

    if (ports.Count == 0) { Runner.Report("No valid ports to scan", isError: true); return; }

    _scanCancellation?.Cancel();
    _scanCancellation = new CancellationTokenSource();
    var token = _scanCancellation.Token;

    Scanned.Clear();
    ScanPercent = 0;
    IsScanning = true;

    // Both callbacks arrive on Parallel.ForEachAsync workers, and the scanner invokes onProgress
    // from inside its own lock as a discarded task. So they must never block and never throw —
    // an exception here escapes into the parallel loop and aborts the scan.
    Task OnProgress(int percent, string message)
    {
      Dispatcher.UIThread.Post(() => { ScanPercent = percent; ScanStatus = message; });
      return Task.CompletedTask;
    }

    Task OnDiscovered(Camera camera, object _)
    {
      var row = new ScannedCamera(camera.Ip, camera.Port, camera.User, camera.Url);
      Dispatcher.UIThread.Post(() => Scanned.Add(row));
      return Task.CompletedTask;
    }

    try
    {
      var credentials = new List<(string username, string password)> { (Shell?.User ?? "", Shell?.Password ?? "") };
      await Runner.RunAsync($"Scan {ScanFrom}..{ScanTo}",
        () => CameraScanner.ScanAsync(ScanFrom, ScanTo, ports, credentials,
          OnProgress, OnDiscovered, token, existing: [], context: this));
    }
    finally
    {
      IsScanning = false;
      ScanStatus = token.IsCancellationRequested ? "cancelled" : "finished";
    }
  }

  [RelayCommand]
  private void CancelScan() => _scanCancellation?.Cancel();

  /// <summary>
  /// The scan probes with a credential pair, so it needs whatever is in the connection bar.
  /// Set by the shell after construction.
  /// </summary>
  public MainWindowViewModel? Shell { get; set; }
}
