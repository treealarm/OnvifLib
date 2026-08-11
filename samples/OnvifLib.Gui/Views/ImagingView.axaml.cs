using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OnvifLib.Gui.Views;

public partial class ImagingView : UserControl
{
  public ImagingView() => InitializeComponent();

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
