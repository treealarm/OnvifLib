using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;

namespace OnvifLib.Gui.ViewModels;

/// <summary>One stored event, with its time in both clocks.</summary>
public sealed record EdgeEventRow(OnvifEdgeEvent Event, DateTime OurTime)
{
  public string CameraTimeText => Event.Time.ToString("yyyy-MM-dd HH:mm:ss");
  public string OurTimeText => OurTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>FindEvents: the events the camera stored alongside its recordings.</summary>
public sealed partial class RecordingViewModel
{
  public ObservableCollection<EdgeEventRow> EdgeEvents { get; } = [];

  [ObservableProperty] private bool _includeStartState = true;
  [ObservableProperty] private bool _onlySelectedRecording;
  [ObservableProperty] private string _eventsSummary = "";

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(SelectedEventXml))]
  private EdgeEventRow? _selectedEvent;

  public string SelectedEventXml => SelectedEvent?.Event.MessageXml is { } xml ? XmlPretty.Format(xml) : "";

  /// <summary>Uses the same window and limit as the recording search, converted into the camera's clock.</summary>
  [RelayCommand]
  private async Task SearchEventsAsync()
  {
    if (Session?.Search is not { } search || IsSearching) return;

    _searchCancellation?.Cancel();
    _searchCancellation = new CancellationTokenSource();
    var token = _searchCancellation.Token;

    var from = ToCameraClock(FromDate.UtcDateTime);
    var to = ToCameraClock(ToDate.UtcDateTime);
    var recording = OnlySelectedRecording ? SelectedFound?.RecordingToken ?? SelectedRecording?.RecordingToken : null;
    if (OnlySelectedRecording && recording is null)
    {
      Runner.Report("Select a recording first, or untick \"only the selected recording\"", isError: true);
      return;
    }

    IsSearching = true;
    try
    {
      var (ok, events) = await Runner.RunAsync($"FindEvents {from:yyyy-MM-dd HH:mm} … {to:yyyy-MM-dd HH:mm} (camera clock)",
        () => search.FindEventsAsync(from, to, recording, IncludeStartState, MaxResults, token));
      if (!ok || events is null) return;
      EdgeEvents.Clear();
      foreach (var e in events) EdgeEvents.Add(new EdgeEventRow(e, ToOurClock(e.Time)));
      EventsSummary = events.Count == 0
        ? "no events stored in this window"
        : $"{events.Count} event(s), {events.Select(e => e.Topic).Distinct().Count()} topic(s)";
    }
    finally { IsSearching = false; }
  }

  private void ClearEvents()
  {
    EdgeEvents.Clear();
    EventsSummary = "";
    SelectedEvent = null;
  }
}
