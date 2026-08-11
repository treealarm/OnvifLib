using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OnvifLib.Gui.Views;

/// <summary>
/// A yes/no dialog, built in code rather than XAML because it is one panel and adding a whole
/// dialog package to a sample for it would be out of proportion.
/// </summary>
public static class ConfirmDialog
{
  public static async Task<bool> ShowAsync(Window owner, string title, string message)
  {
    var result = false;

    var confirm = new Button { Content = "Continue", MinWidth = 100 };
    var cancel = new Button { Content = "Cancel", MinWidth = 100, IsDefault = true };

    var dialog = new Window
    {
      Title = title,
      SizeToContent = SizeToContent.Height,
      Width = 460,
      CanResize = false,
      WindowStartupLocation = WindowStartupLocation.CenterOwner,
      ShowInTaskbar = false,
    };

    confirm.Click += (_, _) => { result = true; dialog.Close(); };
    cancel.Click += (_, _) => dialog.Close();

    dialog.Content = new StackPanel
    {
      Margin = new Avalonia.Thickness(20),
      Spacing = 16,
      Children =
      {
        new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = 15 },
        new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
        new StackPanel
        {
          Orientation = Orientation.Horizontal,
          HorizontalAlignment = HorizontalAlignment.Right,
          Spacing = 8,
          Children = { cancel, confirm },
        },
      },
    };

    await dialog.ShowDialog(owner);
    return result;
  }
}
