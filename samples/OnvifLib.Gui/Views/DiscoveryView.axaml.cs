using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using OnvifLib.Gui.ViewModels;

namespace OnvifLib.Gui.Views;

public partial class DiscoveryView : UserControl
{
  public DiscoveryView() => InitializeComponent();

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  // Double-click is a gesture, not state, so it stays in the view and forwards to the command.
  private void OnDeviceDoubleTapped(object? sender, TappedEventArgs e)
    => (DataContext as DiscoveryViewModel)?.UseSelectedDeviceCommand.Execute(null);

  private void OnScannedDoubleTapped(object? sender, TappedEventArgs e)
    => (DataContext as DiscoveryViewModel)?.UseSelectedScannedCommand.Execute(null);
}
