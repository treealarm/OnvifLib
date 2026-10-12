using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OnvifLib.Gui.ViewModels;

namespace OnvifLib.Gui.Views;

public partial class ImagingView : UserControl
{
  public ImagingView()
  {
    InitializeComponent();

    // Press-and-hold focus, wired exactly like the PTZ pad and for the same reason: Button marks
    // its own pointer events handled, so only a handledEventsToo subscription ever sees them, and
    // a lost capture must stop the motor just as a release does.
    foreach (var name in new[] { "FocusNearHold", "FocusFarHold" })
    {
      if (this.FindControl<Button>(name) is not { } button) continue;
      button.AddHandler(PointerPressedEvent, OnFocusPressed, RoutingStrategies.Bubble, handledEventsToo: true);
      button.AddHandler(PointerReleasedEvent, OnFocusReleased, RoutingStrategies.Bubble, handledEventsToo: true);
      button.AddHandler(PointerCaptureLostEvent, OnFocusReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }
  }

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  private void OnFocusPressed(object? sender, PointerPressedEventArgs e)
  {
    if (DataContext is not ImagingViewModel vm || sender is not Control { Tag: string tag }) return;
    if (int.TryParse(tag, out var sign)) _ = vm.StartFocusAsync(sign);
  }

  private void OnFocusReleased(object? sender, RoutedEventArgs e)
  {
    if (DataContext is ImagingViewModel vm) _ = vm.StopFocusMoveAsync();
  }
}
