using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.ViewModels;
using OnvifLib.Gui.Views;

namespace OnvifLib.Gui;

public partial class App : Application
{
  private MainWindowViewModel? _shell;
  private bool _teardownStarted;

  public override void Initialize()
  {
    AvaloniaXamlLoader.Load(this);
    TabStripScroller.Register();
  }

  public override void OnFrameworkInitializationCompleted()
  {
    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
    {
      _shell = new MainWindowViewModel();
      desktop.MainWindow = new MainWindow { DataContext = _shell };
      desktop.ShutdownRequested += OnShutdownRequested;
    }

    base.OnFrameworkInitializationCompleted();
  }

  /// <summary>
  /// Closes the session before the process exits. The handler is synchronous, so the first pass
  /// cancels the shutdown, runs the async teardown, and asks for shutdown again — otherwise the
  /// event service's in-flight pull request keeps the process alive for the full receive timeout.
  /// </summary>
  private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
  {
    if (_teardownStarted || _shell is null) return;

    _teardownStarted = true;
    e.Cancel = true;

    _ = TeardownAsync();

    async Task TeardownAsync()
    {
      try { await _shell.ShutdownAsync(); }
      catch { /* nothing useful can be reported once the window is going away */ }
      finally
      {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
          desktop.Shutdown();
      }
    }
  }
}
