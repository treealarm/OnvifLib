using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OnvifLib.Gui.Views;

public partial class AnalyticsView : UserControl
{
  public AnalyticsView() => InitializeComponent();

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
