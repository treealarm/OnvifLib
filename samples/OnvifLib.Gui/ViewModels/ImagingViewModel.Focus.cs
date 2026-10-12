using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OnvifLib.Gui.ViewModels;

/// <summary>Moving the focus motor itself — the configuration (auto/manual, limits) is in the main file.</summary>
public sealed partial class ImagingViewModel
{
  [ObservableProperty] private bool _canFocusAbsolute;
  [ObservableProperty] private bool _canFocusRelative;
  [ObservableProperty] private bool _canFocusContinuous;

  [ObservableProperty] private double _focusPositionMin;
  [ObservableProperty] private double _focusPositionMax = 1;
  [ObservableProperty] private double _focusTarget;

  [ObservableProperty] private double _focusStepMax = 1;
  [ObservableProperty] private double _focusStep = 0.05;

  [ObservableProperty] private double _focusSpeedMax = 1;
  [ObservableProperty] private double _focusSpeed = 0.5;

  [ObservableProperty] private string _focusOptionsText = "";
  [ObservableProperty] private string _focusStatusText = "";

  private bool _focusMoving;

  private void ClearFocusControl()
  {
    CanFocusAbsolute = CanFocusRelative = CanFocusContinuous = false;
    FocusOptionsText = "";
    FocusStatusText = "";
    _focusMoving = false;
  }

  private bool TryGetFocusTarget(out ImagingService2 imaging, out string source)
  {
    imaging = null!;
    source = VideoSourceToken;
    if (Session?.Imaging is not { } i) { Runner.Report("Imaging service not available", isError: true); return false; }
    if (source is not { Length: > 0 }) { Runner.Report("This stream has no sensor attached — pick another on the Media tab", isError: true); return false; }
    imaging = i;
    return true;
  }

  [RelayCommand]
  private async Task LoadFocusControlAsync()
  {
    if (Session?.Imaging is not { } imaging || VideoSourceToken is not { Length: > 0 } source) return;

    var (ok, moves) = await Runner.RunAsync("GetMoveOptions (focus)", () => imaging.GetMoveOptionsAsync(source));
    if (!ok) return;

    CanFocusAbsolute = moves?.AbsolutePosition is not null;
    CanFocusRelative = moves?.RelativeDistance is not null;
    CanFocusContinuous = moves?.ContinuousSpeed is not null;
    if (moves?.AbsolutePosition is { } position)
    {
      FocusPositionMax = position.Max;
      FocusPositionMin = position.Min;
    }
    if (moves?.RelativeDistance is { } distance)
    {
      FocusStepMax = Math.Max(Math.Abs(distance.Max), Math.Abs(distance.Min));
      FocusStep = FocusStepMax * 0.05;
    }
    // The speed box drives both relative and continuous moves, so it takes whichever range the
    // camera gives — continuous first, since that is the one a missing speed breaks.
    if ((moves?.ContinuousSpeed ?? moves?.RelativeSpeed ?? moves?.AbsoluteSpeed) is { } speed)
    {
      FocusSpeedMax = Math.Max(Math.Abs(speed.Max), Math.Abs(speed.Min));
      FocusSpeed = Math.Min(FocusSpeedMax, Math.Max(FocusSpeedMax * 0.5, 0.01));
    }

    FocusOptionsText = moves is null
      ? "the camera reported no focus move options — it probably has a fixed lens"
      : string.Join(", ", new[]
        {
          CanFocusAbsolute ? $"absolute {moves.AbsolutePosition!.Min:0.###}…{moves.AbsolutePosition.Max:0.###}" : null,
          CanFocusRelative ? $"relative ±{FocusStepMax:0.###}" : null,
          CanFocusContinuous ? $"continuous, speed up to {FocusSpeedMax:0.###}" : null,
        }.Where(t => t is not null)) is { Length: > 0 } list ? list : "the camera offers no kind of focus move";

    await LoadFocusStatusAsync();
  }

  [RelayCommand]
  private async Task LoadFocusStatusAsync()
  {
    if (Session?.Imaging is not { } imaging || VideoSourceToken is not { Length: > 0 } source) return;
    var (ok, status) = await Runner.RunAsync("GetStatus (imaging)", () => imaging.GetStatusAsync(source));
    if (!ok) return;
    FocusStatusText = status is { FocusPosition: null, FocusMoveStatus: null }
      ? "the camera reports no focus status"
      : $"position {status?.FocusPosition:0.###}, {status?.FocusMoveStatus?.ToLowerInvariant()}" +
        (status?.FocusError is { } error ? $", error: {error}" : "");
    if (status?.FocusPosition is { } p && CanFocusAbsolute) FocusTarget = p;
  }

  [RelayCommand]
  private async Task FocusGoToAsync()
  {
    if (!TryGetFocusTarget(out var imaging, out var source)) return;
    if (await Runner.RunAsync($"MoveFocus absolute {FocusTarget:0.###}",
          () => imaging.MoveFocusAbsoluteAsync(source, (float)FocusTarget, (float)FocusSpeed)))
      await LoadFocusStatusAsync();
  }

  [RelayCommand]
  private Task FocusStepNearAsync() => FocusStepAsync(-1);

  [RelayCommand]
  private Task FocusStepFarAsync() => FocusStepAsync(+1);

  private async Task FocusStepAsync(int sign)
  {
    if (!TryGetFocusTarget(out var imaging, out var source)) return;
    var distance = (float)(sign * FocusStep);
    if (await Runner.RunAsync($"MoveFocus relative {distance:+0.###;-0.###}",
          () => imaging.MoveFocusRelativeAsync(source, distance, (float)FocusSpeed)))
      await LoadFocusStatusAsync();
  }

  /// <summary>Press-and-hold: started from the view's code-behind, stopped on release.</summary>
  /// <remarks>
  /// Not a RelayCommand for the same reason as the PTZ pad: a generated async command refuses to
  /// re-enter while running, which would swallow the release that has to stop the motor.
  /// </remarks>
  public async Task StartFocusAsync(int sign)
  {
    if (_focusMoving || !TryGetFocusTarget(out var imaging, out var source)) return;
    _focusMoving = true;
    var speed = (float)(sign * FocusSpeed);
    if (!await Runner.RunAsync($"MoveFocus continuous {speed:+0.###;-0.###}", () => imaging.MoveFocusContinuousAsync(source, speed)))
      _focusMoving = false;
  }

  public async Task StopFocusMoveAsync()
  {
    if (!_focusMoving) return;
    _focusMoving = false;
    await StopFocusAsync();
  }

  [RelayCommand]
  private async Task StopFocusAsync()
  {
    if (!TryGetFocusTarget(out var imaging, out var source)) return;
    if (await Runner.RunAsync("Stop (focus)", () => imaging.StopFocusAsync(source)))
      await LoadFocusStatusAsync();
  }
}
