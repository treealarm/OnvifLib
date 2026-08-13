using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OnvifLib.Gui.ViewModels;

namespace OnvifLib.Gui.Views;

public partial class VideoView : UserControl
{
  private Image? _surface;
  private VideoPlayerViewModel? _player;

  public VideoView()
  {
    InitializeComponent();
    DataContextChanged += (_, _) =>
    {
      if (VisualRoot is null) return;
      BindPlayer();
    };
  }

  private void InitializeComponent()
  {
    AvaloniaXamlLoader.Load(this);
    _surface = this.FindControl<Image>("Surface");
  }

  protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
  {
    base.OnAttachedToVisualTree(e);
    BindPlayer();
  }

  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
  {
    UnbindPlayer();
    base.OnDetachedFromVisualTree(e);
  }

  private void BindPlayer()
  {
    UnbindPlayer();
    _player = DataContext as VideoPlayerViewModel;
    if (_player is not null) _player.PropertyChanged += OnPlayerPropertyChanged;
  }

  private void UnbindPlayer()
  {
    if (_player is null) return;
    _player.PropertyChanged -= OnPlayerPropertyChanged;
    _player = null;
  }

  private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
  {
    if (e.PropertyName is nameof(VideoPlayerViewModel.Frame))
      _surface?.InvalidateVisual();
  }
}
