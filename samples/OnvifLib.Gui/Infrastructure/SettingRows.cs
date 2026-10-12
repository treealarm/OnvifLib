using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// One numeric camera setting as a slider row: the value, the range the camera reported, and the
/// tick that says whether to send it.
/// </summary>
/// <remarks>
/// Exists so a group of nine exposure values is one collection bound to one template rather than
/// forty-five flattened view-model properties. A row the camera reports no range for stays
/// disabled instead of inventing 0..100 the camera would reject.
/// </remarks>
public sealed partial class SliderSetting(string label) : ObservableObject
{
  public string Label { get; } = label;

  [ObservableProperty] private double _value;
  [ObservableProperty] private double _min;
  [ObservableProperty] private double _max = 100;
  [ObservableProperty] private bool _isAvailable;

  /// <summary>Send this value on Apply. Moving the slider ticks it, so a change is never silently dropped.</summary>
  [ObservableProperty] private bool _change;

  private bool _loading;

  partial void OnValueChanged(double value)
  {
    if (!_loading) Change = true;
  }

  public void SetRange(OnvifFloatRange? range)
  {
    IsAvailable = range is not null;
    if (range is null) return;
    _loading = true;
    // Max first when growing, so the slider never clamps Min against a stale Max.
    Max = Math.Max(range.Max, range.Min);
    Min = range.Min;
    _loading = false;
  }

  /// <summary>Shows what the camera reports, without marking it as a change.</summary>
  public void Load(float? value)
  {
    _loading = true;
    if (value is { } v) Value = v;
    Change = false;
    _loading = false;
  }

  public float? Requested => Change && IsAvailable ? (float)Value : null;

  public void Reset()
  {
    _loading = true;
    IsAvailable = false;
    Change = false;
    Value = 0;
    _loading = false;
  }
}

/// <summary>
/// One mode setting as a combo row: the modes the camera offers, the current one, and the tick.
/// </summary>
public sealed partial class ModeSetting(string label) : ObservableObject
{
  public string Label { get; } = label;
  public ObservableCollection<string> Modes { get; } = [];

  [ObservableProperty] private string? _mode;
  [ObservableProperty] private bool _change;

  private bool _loading;

  public bool IsAvailable => Modes.Count > 0;

  partial void OnModeChanged(string? value)
  {
    if (!_loading) Change = true;
  }

  public void SetModes(IEnumerable<string>? modes)
  {
    _loading = true;
    var keep = Mode;
    Modes.Clear();
    foreach (var m in modes ?? []) Modes.Add(m);
    // A camera reporting a current mode it does not list as an option still shows it, so the
    // combo never reads blank for a value the camera is actually in.
    if (keep is not null && !Modes.Contains(keep)) Modes.Add(keep);
    Mode = keep;
    _loading = false;
    OnPropertyChanged(nameof(IsAvailable));
  }

  public void Load(string? mode)
  {
    _loading = true;
    if (mode is not null && Modes.Count > 0 && !Modes.Contains(mode)) Modes.Add(mode);
    Mode = mode;
    Change = false;
    _loading = false;
    OnPropertyChanged(nameof(IsAvailable));
  }

  public string? Requested => Change ? Mode : null;

  public void Reset()
  {
    _loading = true;
    Modes.Clear();
    Mode = null;
    Change = false;
    _loading = false;
    OnPropertyChanged(nameof(IsAvailable));
  }
}
