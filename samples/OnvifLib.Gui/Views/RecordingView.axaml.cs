using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace OnvifLib.Gui.Views;

public partial class RecordingView : UserControl
{
  public RecordingView() => InitializeComponent();

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
