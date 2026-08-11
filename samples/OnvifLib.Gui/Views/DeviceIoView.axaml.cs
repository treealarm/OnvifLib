using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OnvifLib.Gui.Views;

public partial class DeviceIoView : UserControl
{
  public DeviceIoView() => InitializeComponent();

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
