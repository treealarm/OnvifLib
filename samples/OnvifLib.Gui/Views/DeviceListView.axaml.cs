using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using OnvifLib.Gui.ViewModels;

namespace OnvifLib.Gui.Views;

public partial class DeviceListView : UserControl
{
  public DeviceListView() => InitializeComponent();

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  private void OnDeviceDoubleTapped(object? sender, TappedEventArgs e)
  {
    if (DataContext is DeviceListViewModel vm)
      vm.ConnectCommand.Execute(null);
  }

  private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
  {
    if (sender is ListBox { SelectedItem: { } item } list)
      list.ScrollIntoView(item);
  }
}
