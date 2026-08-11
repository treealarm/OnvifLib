using Avalonia;
using OnvifLib.Gui.ViewModels;

namespace OnvifLib.Gui;

internal static class Program
{
  // Avalonia's initialisation must not be moved into a lambda or reordered: anything that
  // touches a control before the toolkit is set up will fail.
  [STAThread]
  public static int Main(string[] args)
  {
    if (args.Contains("--selftest"))
    {
      // SetupWithoutStarting initialises the toolkit but never enters the message loop, which is
      // what lets the views be constructed and then simply returned from. Calling Shutdown from
      // inside the lifetime instead would tear down a dispatcher that had not started yet.
      BuildAvaloniaApp().SetupWithoutStarting();
      return SelfTest.Run(new MainWindowViewModel());
    }

    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    return 0;
  }

  // Referenced by name by the Avalonia designer and the XAML previewer, so the signature is fixed.
  public static AppBuilder BuildAvaloniaApp()
    => AppBuilder.Configure<App>()
      .UsePlatformDetect()
      .WithInterFont()
      .LogToTrace();
}
