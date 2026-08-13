using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OnvifLib.Gui.Infrastructure;
using OnvifLib.Gui.Models;

namespace OnvifLib.Gui.ViewModels;

/// <summary>
/// The Log tab: the first place to look when anything else in the app reports a failure.
/// Always available, with or without a session.
/// </summary>
public sealed partial class LogViewModel : TabViewModelBase
{
  private readonly UiLogger _uiLogger;
  private readonly CancellationTokenSource _cancellation = new();
  private readonly List<LogEntry> _all = [];

  public override bool RequiresConnection => false;
  public override bool IsSessionScoped => false;

  public LogViewModel(OperationRunner runner, UiLogger logger) : base("Log", runner, logger)
  {
    _uiLogger = logger;
    IsAvailable = true;
    _ = PumpAsync();
  }

  public ObservableCollection<LogEntry> Entries { get; } = [];

  public IReadOnlyList<LogLevel> Levels { get; } = [LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error];

  /// <summary>
  /// Applied at ingestion inside the logger, so lowering it below Debug genuinely stops paying
  /// for the SOAP dumps rather than collecting and hiding them.
  /// </summary>
  [ObservableProperty] private LogLevel _minimumLevel = LogLevel.Info;

  [ObservableProperty] private string _filter = "";
  [ObservableProperty] private bool _isPaused;
  [ObservableProperty] private int _maxEntries = 5000;
  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(SelectedMessage))]
  private LogEntry? _selected;

  /// <summary>
  /// Projected rather than bound through Selected directly: LogEntry is a struct, so the
  /// selection is a Nullable&lt;LogEntry&gt; that compiled bindings cannot see through.
  /// </summary>
  public string SelectedMessage => Selected?.Message ?? "";

  partial void OnMinimumLevelChanged(LogLevel value) => _uiLogger.MinimumLevel = value;
  partial void OnFilterChanged(string value) => Rebuild();

  protected override string? DescribeUnavailability(CameraSession session) => null;

  private async Task PumpAsync()
  {
    try
    {
      await foreach (var batch in _uiLogger.ReadBatchesAsync(_cancellation.Token))
      {
        if (IsPaused) continue;   // still drained, so the channel does not back up while paused
        // One dispatcher post per batch, not per entry: with SOAP capture on the logger can
        // produce thousands of entries in a second and per-entry posting would starve the UI.
        await Dispatcher.UIThread.InvokeAsync(() => Append(batch));
      }
    }
    catch (OperationCanceledException) { /* the expected way out */ }
  }

  private void Append(List<LogEntry> batch)
  {
    _all.AddRange(batch);
    if (_all.Count > MaxEntries) _all.RemoveRange(0, _all.Count - MaxEntries);

    // A batch large enough to blow the cap is cheaper to rebuild than to trim one item at a
    // time: removing from the front of an ObservableCollection is O(n) per removal.
    if (batch.Count >= MaxEntries) { Rebuild(); return; }

    foreach (var entry in batch.Where(Matches)) Entries.Add(entry);

    // Removing from the front is O(n) per item and makes the grid re-realise its rows each time;
    // past a handful it is cheaper to rebuild once.
    var excess = Entries.Count - MaxEntries;
    if (excess > 32) { Rebuild(); return; }
    for (var i = 0; i < excess; i++) Entries.RemoveAt(0);
  }

  private void Rebuild()
  {
    Entries.Clear();
    foreach (var entry in _all.Where(Matches)) Entries.Add(entry);
  }

  private bool Matches(LogEntry entry) =>
    string.IsNullOrWhiteSpace(Filter)
    || entry.Message.Contains(Filter, StringComparison.OrdinalIgnoreCase);

  [RelayCommand]
  private void Clear()
  {
    _all.Clear();
    Entries.Clear();
  }

  /// <summary>The whole captured buffer, unfiltered — what "save to file" and "copy all" use.</summary>
  public string ToText() =>
    string.Join(Environment.NewLine, _all.Select(e => $"{e.TimeText} {e.LevelText,-7} {e.Message}"));

  public override Task ShutdownAsync()
  {
    _cancellation.Cancel();
    return Task.CompletedTask;
  }
}
