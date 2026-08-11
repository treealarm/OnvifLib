using CommunityToolkit.Mvvm.ComponentModel;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

public abstract partial class TabViewModelBase(string header, OperationRunner runner, UiLogger logger) : ObservableObject
{
  protected OperationRunner Runner { get; } = runner;
  protected UiLogger Logger { get; } = logger;

  public string Header { get; } = header;

  /// <summary>The connected session, or null. Null is the signal to clear everything.</summary>
  [ObservableProperty] private CameraSession? _session;

  /// <summary>
  /// Bound to the tab's IsEnabled. Disabled tabs stay visible on purpose: a test harness has to
  /// show what a camera cannot do, and a hidden tab looks like a missing feature of the app.
  /// </summary>
  [ObservableProperty] private bool _isAvailable;

  /// <summary>Shown in place of the content when the tab is unavailable.</summary>
  [ObservableProperty] private string _unavailableReason = "Connect to a camera first.";

  public void SetSession(CameraSession? session)
  {
    Session = session;
    if (session is null)
    {
      IsAvailable = false;
      UnavailableReason = "Connect to a camera first.";
      OnCleared();
      return;
    }

    var reason = DescribeUnavailability(session);
    IsAvailable = reason is null;
    UnavailableReason = reason ?? string.Empty;
    if (IsAvailable) OnConnected(session); else OnCleared();
  }

  /// <summary>Null when the tab can be used; otherwise the reason it cannot.</summary>
  protected abstract string? DescribeUnavailability(CameraSession session);

  protected virtual void OnConnected(CameraSession session) { }

  /// <summary>Drops everything read from the previous camera: lists, bitmaps, subscriptions.</summary>
  protected virtual void OnCleared() { }

  /// <summary>Stops anything long-running before the session is discarded.</summary>
  public virtual Task ShutdownAsync() => Task.CompletedTask;
}
