using Avalonia.Controls;
using OnvifLib.Gui.ViewModels;
using OnvifLib.Gui.Views;

namespace OnvifLib.Gui;

/// <summary>
/// Builds every view once and reports whether it loaded.
/// </summary>
/// <remarks>
/// A TabControl only realises the selected tab, so a XAML error in any other tab stays invisible
/// until someone clicks it. Compiled bindings catch the property-name mistakes at build time, but
/// not a bad template type or a missing style — this does, in one run, with no camera and no
/// clicking. Invoked with <c>--selftest</c>, which exits non-zero if anything failed.
/// </remarks>
public static class SelfTest
{
  public static int Run(MainWindowViewModel shell)
  {
    (string Name, Func<Control> Build, object DataContext)[] views =
    [
      ("MainWindow", () => new MainWindow(), shell),
      ("DiscoveryView", () => new DiscoveryView(), shell.Discovery),
      ("DeviceView", () => new DeviceView(), shell.Device),
      ("MediaView", () => new MediaView(), shell.Media),
      ("PtzView", () => new PtzView(), shell.Ptz),
      ("ImagingView", () => new ImagingView(), shell.Imaging),
      ("EventsView", () => new EventsView(), shell.Events),
      ("AnalyticsView", () => new AnalyticsView(), shell.Analytics),
      ("RecordingView", () => new RecordingView(), shell.Recording),
      ("DeviceIoView", () => new DeviceIoView(), shell.DeviceIo),
      ("LogView", () => new LogView(), shell.Log),
    ];

    var failures = 0;
    foreach (var (name, build, dataContext) in views)
    {
      try
      {
        var control = build();
        control.DataContext = dataContext;
        Console.WriteLine($"  ok    {name}");
      }
      catch (Exception ex)
      {
        failures++;
        Console.WriteLine($"  FAIL  {name}: {ex.Message}");
        Console.WriteLine(ex.ToString());
      }
    }

    Console.WriteLine(failures == 0
      ? $"self-test passed — {views.Length} view(s) loaded"
      : $"self-test FAILED — {failures} of {views.Length} view(s) did not load");
    return failures == 0 ? 0 : 1;
  }
}
