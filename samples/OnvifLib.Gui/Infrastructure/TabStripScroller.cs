using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace OnvifLib.Gui.Infrastructure;

/// <summary>
/// Wires the left/right buttons on a TabControl strip to a hidden ScrollViewer, so tabs stay on
/// one line without a scrollbar.
/// </summary>
public static class TabStripScroller
{
  private static readonly ConditionalWeakTable<TabControl, Hook> Hooks = new();

  public static void Register() =>
    Control.LoadedEvent.AddClassHandler<TabControl>(OnLoaded, handledEventsToo: true);

  private static void OnLoaded(TabControl control, RoutedEventArgs _)
  {
    if (Hooks.TryGetValue(control, out var existing) && existing is not null) return;

    var scroll = Find<ScrollViewer>(control, "PART_TabScroll");
    var left = Find<Button>(control, "PART_ScrollLeft");
    var right = Find<Button>(control, "PART_ScrollRight");
    if (scroll is null || left is null || right is null) return;

    var hook = new Hook(scroll, left, right);
    Hooks.Add(control, hook);

    left.Click += (_, _) => hook.ScrollBy(-hook.Step());
    right.Click += (_, _) => hook.ScrollBy(hook.Step());
    scroll.ScrollChanged += (_, _) => hook.UpdateButtons();
    control.SizeChanged += (_, _) => hook.UpdateButtons();
    control.LayoutUpdated += (_, _) => hook.UpdateButtons();
    hook.UpdateButtons();
  }

  private static T? Find<T>(Control root, string name) where T : Control =>
    root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

  private sealed class Hook(ScrollViewer scroll, Button left, Button right)
  {
    public double Step() => Math.Max(120, scroll.Viewport.Width * 0.6);

    public void ScrollBy(double delta)
    {
      var max = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
      var x = Math.Clamp(scroll.Offset.X + delta, 0, max);
      scroll.Offset = scroll.Offset.WithX(x);
      UpdateButtons();
    }

    public void UpdateButtons()
    {
      var max = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
      var overflow = max > 2;
      left.IsVisible = right.IsVisible = overflow;
      left.IsEnabled = overflow && scroll.Offset.X > 1;
      right.IsEnabled = overflow && scroll.Offset.X < max - 1;
    }
  }
}
