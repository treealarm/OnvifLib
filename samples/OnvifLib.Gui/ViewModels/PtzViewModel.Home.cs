using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OnvifLib.Gui.ViewModels;

/// <summary>Home position and the node's auxiliary commands (wiper, IR lamp, heater …).</summary>
public sealed partial class PtzViewModel
{
  [ObservableProperty] private string _nodeText = "";
  [ObservableProperty] private bool _homeSupported;

  /// <summary>Home can be stored only on a node that has one and does not fix it at the factory.</summary>
  [ObservableProperty] private bool _canSetHome;

  public ObservableCollection<string> AuxiliaryCommands { get; } = [];
  [ObservableProperty] private string _auxiliaryCommand = "";
  [ObservableProperty] private string _auxiliaryResponse = "";

  partial void OnSelectedAuxiliaryCommandChanged(string? value)
  {
    if (value is not null) AuxiliaryCommand = value;
  }

  [ObservableProperty] private string? _selectedAuxiliaryCommand;

  private void ClearNode()
  {
    NodeText = "";
    HomeSupported = CanSetHome = false;
    AuxiliaryCommands.Clear();
    AuxiliaryCommand = "";
    AuxiliaryResponse = "";
  }

  [RelayCommand]
  private async Task LoadNodesAsync()
  {
    if (Session?.Ptz is not { } ptz) return;
    var (ok, nodes) = await Runner.RunAsync("GetNodes", ptz.GetNodesAsync);
    if (!ok) return;

    ClearNode();
    if (nodes is not { Count: > 0 })
    {
      NodeText = "the camera reported no PTZ nodes";
      return;
    }

    // Single-headed cameras are all this tab addresses (every move uses the profile's one
    // configuration), so the first node is the one that matters.
    var node = nodes[0];
    HomeSupported = node.HomeSupported;
    CanSetHome = node.HomeSupported && !node.FixedHomePosition;
    foreach (var c in node.AuxiliaryCommands) AuxiliaryCommands.Add(c);
    NodeText = $"{(string.IsNullOrWhiteSpace(node.Name) ? node.Token : node.Name)}: " +
               $"home {(node.HomeSupported ? node.FixedHomePosition ? "fixed" : "settable" : "not supported")}, " +
               $"up to {node.MaximumNumberOfPresets} presets, " +
               $"{(node.AuxiliaryCommands.Count == 0 ? "no" : node.AuxiliaryCommands.Count.ToString())} auxiliary command(s)";
  }

  [RelayCommand]
  private async Task GotoHomeAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken)) return;
    await Runner.RunAsync("GotoHomePosition", () => ptz.GotoHomePositionAsync(profileToken, (float)Speed, (float)Speed));
  }

  [RelayCommand]
  private async Task SetHomeAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken)) return;
    if (!await dialogs.ConfirmAsync("Store home position",
          "The current position replaces the camera's stored home. The old home cannot be read back first, so there is no undo.\n\nContinue?"))
      return;
    if (await Runner.RunAsync("SetHomePosition", () => ptz.SetHomePositionAsync(profileToken)))
      Runner.Report("Home position stored");
  }

  [RelayCommand]
  private async Task SendAuxiliaryCommandAsync()
  {
    if (!TryGetTarget(out var ptz, out var profileToken)) return;
    if (AuxiliaryCommand is not { Length: > 0 } command)
    {
      Runner.Report("Pick or type an auxiliary command first", isError: true);
      return;
    }
    if (!await dialogs.ConfirmAsync("Send auxiliary command",
          $"\"{command}\" drives hardware on the camera — a wiper, washer, IR lamp or heater. What it does is up to the manufacturer.\n\nContinue?"))
      return;
    var (ok, reply) = await Runner.RunAsync($"SendAuxiliaryCommand [{command}]",
      () => ptz.SendAuxiliaryCommandAsync(profileToken, command));
    if (ok) AuxiliaryResponse = string.IsNullOrEmpty(reply) ? "(empty reply)" : reply!;
  }
}
