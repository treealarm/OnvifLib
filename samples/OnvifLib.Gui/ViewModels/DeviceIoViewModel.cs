using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// Device I/O: relay outputs and digital inputs.
/// </summary>
/// <remarks>
/// The split is easy to trip over: relays are read and driven through <c>Camera</c>, because they
/// live on Device Management, while their options and the digital inputs come from
/// <c>DeviceIOService2</c>. Both are wired up here.
/// </remarks>
public sealed partial class DeviceIoViewModel(OperationRunner runner, UiLogger logger, IDialogService dialogs)
  : TabViewModelBase("Device I/O", runner, logger)
{
  public ObservableCollection<OnvifRelayOutput> Relays { get; } = [];
  public ObservableCollection<OnvifRelayOutputOptions> RelayOptions { get; } = [];
  public ObservableCollection<OnvifDigitalInput> Inputs { get; } = [];

  public IReadOnlyList<string> Modes { get; } = ["bistable", "monostable"];
  public IReadOnlyList<string> IdleStates { get; } = ["open", "closed"];

  [ObservableProperty] private OnvifRelayOutput? _selectedRelay;
  [ObservableProperty] private OnvifDigitalInput? _selectedInput;

  [ObservableProperty] private string _editMode = "bistable";
  [ObservableProperty] private string _editIdleState = "closed";
  [ObservableProperty] private int _editDelayMs = 1000;
  [ObservableProperty] private string _inputIdleState = "closed";

  [ObservableProperty] private string _lastRelayCommand = "no relay command sent yet";
  [ObservableProperty] private string _inputOptionsText = "not read";

  partial void OnSelectedRelayChanged(OnvifRelayOutput? value)
  {
    if (value is null) return;
    EditMode = value.Mode;
    EditIdleState = value.IdleState;
    EditDelayMs = value.DelayMs;
  }

  protected override string? DescribeUnavailability(CameraSession session) =>
    // Relays hang off Camera rather than off DeviceIOService2, so this tab is still worth opening
    // on a camera that has no Device I/O service at all.
    session.DeviceIo is null && !session.Capabilities.HasDigitalInputs
      ? "This camera advertises no Device I/O service. Relay outputs are read through Device Management, so the relay half below may still work."
      : null;

  protected override void OnCleared()
  {
    Relays.Clear();
    RelayOptions.Clear();
    Inputs.Clear();
    LastRelayCommand = "no relay command sent yet";
    InputOptionsText = "not read";
  }

  [RelayCommand]
  private async Task RefreshRelaysAsync()
  {
    if (Session is not { } session) return;

    var (ok, relays) = await Runner.RunAsync("GetRelayOutputs", session.Camera.GetRelayOutputsAsync);
    if (ok && relays is not null)
    {
      Relays.Clear();
      foreach (var relay in relays) Relays.Add(relay);
      SelectedRelay = Relays.FirstOrDefault();
    }

    if (session.DeviceIo is not { } io) return;

    var (okOptions, options) = await Runner.RunAsync("GetRelayOutputOptions", io.GetRelayOutputOptionsAsync);
    if (!okOptions || options is null) return;

    RelayOptions.Clear();
    foreach (var option in options) RelayOptions.Add(option);
  }

  [RelayCommand]
  private async Task ApplyRelaySettingsAsync()
  {
    if (Session is not { } session || SelectedRelay is not { } relay) return;

    if (await Runner.RunAsync($"SetRelayOutputSettings [{relay.Token}]",
          () => session.Camera.SetRelayOutputSettingsAsync(relay.Token, EditMode, EditIdleState, EditDelayMs)))
      await RefreshRelaysAsync();
  }

  [RelayCommand]
  private Task ActivateRelayAsync() => SetRelayStateAsync(true);

  [RelayCommand]
  private Task DeactivateRelayAsync() => SetRelayStateAsync(false);

  private async Task SetRelayStateAsync(bool active)
  {
    if (Session is not { } session || SelectedRelay is not { } relay) return;

    // Electrically this reverses; physically it may not. A relay output is usually wired to a
    // door strike, a gate or an alarm, so it asks first even though nothing is being erased.
    if (!await dialogs.ConfirmAsync($"Switch relay {relay.Token} {(active ? "on" : "off")}",
          "A relay output is usually wired to real hardware — a door strike, a gate, a siren.\n\n" +
          "Switching it will actuate whatever is connected."))
      return;

    if (await Runner.RunAsync($"SetRelayOutputState [{relay.Token}] = {(active ? "active" : "inactive")}",
          () => session.Camera.SetRelayOutputStateAsync(relay.Token, active)))
      // The only state this tab can honestly show: ONVIF exposes no way to read a relay back.
      LastRelayCommand = $"last command: {(active ? "active" : "inactive")} on {relay.Token} at {DateTime.Now:HH:mm:ss}";
  }

  [RelayCommand]
  private async Task RefreshInputsAsync()
  {
    if (Session?.DeviceIo is not { } io) return;

    var (ok, inputs) = await Runner.RunAsync("GetDigitalInputs", io.GetDigitalInputsAsync);
    if (!ok || inputs is null) return;

    Inputs.Clear();
    foreach (var input in inputs) Inputs.Add(input);
    SelectedInput = Inputs.FirstOrDefault();
  }

  [RelayCommand]
  private async Task LoadInputOptionsAsync()
  {
    if (Session?.DeviceIo is not { } io || SelectedInput is not { } input) return;

    var (ok, options) = await Runner.RunAsync($"GetDigitalInputConfigurationOptions [{input.Token}]",
      () => io.GetDigitalInputConfigurationOptionsAsync(input.Token));
    if (!ok) return;

    InputOptionsText = options is null
      ? "the camera reported no options for this input"
      : $"idle state configurable: {(options.IdleStateConfigurable ? "yes" : "no")}";
  }

  [RelayCommand]
  private async Task ApplyInputIdleStateAsync()
  {
    if (Session?.DeviceIo is not { } io || SelectedInput is not { } input) return;

    if (await Runner.RunAsync($"SetDigitalInputIdleState [{input.Token}] = {InputIdleState}",
          () => io.SetDigitalInputIdleStateAsync(input.Token, InputIdleState)))
      await RefreshInputsAsync();
  }
}
