using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OnvifLib.Gui.ViewModels;

namespace OnvifLib.Gui.Views;

public partial class LogView : UserControl
{
  private DataGrid? _grid;
  private CheckBox? _autoScroll;

  private bool _scrollQueued;

  public LogView()
  {
    InitializeComponent();
    _grid = this.FindControl<DataGrid>("Grid");
    _autoScroll = this.FindControl<CheckBox>("AutoScroll");

    // Auto-scroll is view behaviour, not view-model state: it depends on the realised rows.
    DataContextChanged += (_, _) =>
    {
      if (DataContext is LogViewModel vm)
        vm.Entries.CollectionChanged += (_, _) => QueueScroll();
    };
  }

  /// <summary>
  /// Scrolls to the newest row, but never from inside a layout pass.
  /// </summary>
  /// <remarks>
  /// This used to hang off DataGrid.LoadingRow, which fires while the grid is realising rows
  /// inside MeasureOverride. Calling ScrollIntoView from there re-enters that realisation and the
  /// grid ends up parenting one DataGridRow twice, which it reports as
  /// "already has a visual parent DataGridRowsPresenter" — a crash during rendering, far from the
  /// handler that caused it. Posting at Background priority runs the scroll after the current
  /// layout pass has finished. The flag coalesces a burst of appends into one scroll, which
  /// matters because the log arrives in batches of up to 200.
  /// </remarks>
  private void QueueScroll()
  {
    if (_scrollQueued || _autoScroll?.IsChecked != true) return;
    _scrollQueued = true;

    Dispatcher.UIThread.Post(() =>
    {
      _scrollQueued = false;
      if (_autoScroll?.IsChecked != true) return;
      if (_grid is null || DataContext is not LogViewModel { Entries.Count: > 0 } vm) return;

      try { _grid.ScrollIntoView(vm.Entries[^1], null); }
      catch (Exception) { /* the row can be gone again already; scrolling is never worth a crash */ }
    }, DispatcherPriority.Background);
  }

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  private async void OnCopyAll(object? sender, RoutedEventArgs e)
  {
    if (DataContext is not LogViewModel vm) return;
    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
      await clipboard.SetTextAsync(vm.ToText());
  }

  private async void OnSave(object? sender, RoutedEventArgs e)
  {
    if (DataContext is not LogViewModel vm) return;
    if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

    var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
    {
      Title = "Save the log",
      SuggestedFileName = $"onvif-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
      DefaultExtension = "txt",
    });
    if (file is null) return;

    // The whole captured buffer, not the filtered view: a log saved for someone else should not
    // silently omit what the filter box happened to be hiding.
    await using var stream = await file.OpenWriteAsync();
    await using var writer = new StreamWriter(stream);
    await writer.WriteAsync(vm.ToText());
  }
}
