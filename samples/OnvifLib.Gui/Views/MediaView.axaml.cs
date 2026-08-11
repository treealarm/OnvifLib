using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using OnvifLib.Gui.ViewModels;

namespace OnvifLib.Gui.Views;

public partial class MediaView : UserControl
{
  public MediaView() => InitializeComponent();

  private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

  private async void OnCopyStreamUri(object? sender, RoutedEventArgs e)
  {
    if (DataContext is not MediaViewModel vm || vm.StreamUri.Length == 0) return;
    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
      // The real URI, credentials and all — copying a masked one would be useless.
      await clipboard.SetTextAsync(vm.WithCredentials(vm.StreamUri));
  }

  private async void OnSaveSnapshot(object? sender, RoutedEventArgs e)
  {
    if (DataContext is not MediaViewModel { Frame: { } frame }) return;
    if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

    var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
    {
      Title = "Save the snapshot",
      SuggestedFileName = $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
      DefaultExtension = "png",
    });
    if (file is null) return;

    // Saved from the decoded bitmap rather than the original bytes, so what lands on disk is
    // exactly what is on screen even when the frame came from the manual URL box.
    await using var stream = await file.OpenWriteAsync();
    frame.Save(stream);
  }
}
