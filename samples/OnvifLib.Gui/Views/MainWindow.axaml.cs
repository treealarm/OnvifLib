using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.ViewModels;

namespace OnvifLib.Gui.Views;

public partial class MainWindow : Window, IDialogService
{
  public MainWindow()
  {
    InitializeComponent();
    // The view models are built before any window exists, so the dialog owner is handed over here.
    DataContextChanged += (_, _) => (DataContext as MainWindowViewModel)?.AttachDialogs(this);
  }

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  public Task<bool> ConfirmAsync(string title, string message) =>
    ConfirmDialog.ShowAsync(this, title, message);
}
