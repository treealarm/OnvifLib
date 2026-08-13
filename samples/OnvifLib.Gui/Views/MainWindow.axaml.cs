using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.ViewModels;

namespace OnvifLib.Gui.Views;

public partial class MainWindow : Window, IDialogService
{
  private ListBox? _activity;
  private bool _scrollQueued;

  public MainWindow()
  {
    InitializeComponent();
    _activity = this.FindControl<ListBox>("ActivityList");
    // The view models are built before any window exists, so the dialog owner is handed over here.
    DataContextChanged += (_, _) =>
    {
      if (DataContext is not MainWindowViewModel shell) return;
      shell.AttachDialogs(this);
      shell.Runner.Activity.CollectionChanged += (_, _) => QueueActivityScroll();
    };
  }

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  private void QueueActivityScroll()
  {
    if (_scrollQueued) return;
    _scrollQueued = true;
    Dispatcher.UIThread.Post(() =>
    {
      _scrollQueued = false;
      if (_activity is null || DataContext is not MainWindowViewModel { Runner.Activity.Count: > 0 } shell)
        return;
      try { _activity.ScrollIntoView(shell.Runner.Activity[^1]); }
      catch (Exception) { /* the row can be gone already */ }
    }, DispatcherPriority.Background);
  }

  public Task<bool> ConfirmAsync(string title, string message) =>
    ConfirmDialog.ShowAsync(this, title, message);
}
