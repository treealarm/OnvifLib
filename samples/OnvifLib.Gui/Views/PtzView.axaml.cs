using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using OnvifLib.Gui.ViewModels;

namespace OnvifLib.Gui.Views;

public partial class PtzView : UserControl
{
  public PtzView()
  {
    InitializeComponent();

    // Attached here rather than with PointerPressed="…" in XAML, and this is not a style choice.
    // Button handles both pointer events itself — it captures the pointer on press and raises
    // Click on release — and marks them handled. A XAML attribute subscribes with
    // handledEventsToo: false, so those handlers are never called and pressing a direction button
    // does nothing at all, with no error anywhere. handledEventsToo: true is what makes the pad work.
    foreach (var button in Pads().SelectMany(p => p.GetLogicalChildren().OfType<Button>())
                                 .Where(b => b.Tag is string))
    {
      button.AddHandler(PointerPressedEvent, OnMovePressed, RoutingStrategies.Bubble, handledEventsToo: true);
      button.AddHandler(PointerReleasedEvent, OnMoveReleased, RoutingStrategies.Bubble, handledEventsToo: true);
      // A pointer dragged off the button, or grabbed by something else, would otherwise never
      // deliver a release — and the camera would keep panning until its own timeout expired.
      button.AddHandler(PointerCaptureLostEvent, OnMoveReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }
  }

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  private IEnumerable<Panel> Pads()
  {
    if (this.FindControl<Grid>("Pad") is { } pad) yield return pad;
    if (this.FindControl<StackPanel>("ZoomPad") is { } zoom) yield return zoom;
  }

  private void OnMovePressed(object? sender, PointerPressedEventArgs e)
  {
    if (DataContext is not PtzViewModel vm) return;
    if (Direction(sender) is not { } direction) return;
    _ = vm.StartMoveAsync(direction.Pan, direction.Tilt, direction.Zoom);
  }

  // Shared by PointerReleased and PointerCaptureLost, which carry different argument types.
  private void OnMoveReleased(object? sender, RoutedEventArgs e)
  {
    if (DataContext is PtzViewModel vm) _ = vm.StopMoveAsync();
  }

  /// <summary>Reads "pan,tilt,zoom" off the button's Tag, so the pad stays declared in XAML.</summary>
  private static (float Pan, float Tilt, float Zoom)? Direction(object? sender)
  {
    if (sender is not Control { Tag: string tag }) return null;

    var parts = tag.Split(',');
    if (parts.Length != 3) return null;

    return float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pan)
        && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var tilt)
        && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var zoom)
      ? (pan, tilt, zoom)
      : null;
  }
}
