namespace OnvifLib
{
  /// <summary>
  /// How often a run of consecutive failures reaches the log: the first at once, then each report
  /// waits twice as long, up to a cap. The attempts themselves keep their own cadence — this only
  /// decides when one of them is worth a line.
  /// </summary>
  /// <remarks>
  /// A camera that stays off for a day costs one line every few minutes instead of three every two
  /// seconds. The shape is the one video_srv's rtsp_reader uses for its reconnect failures.
  /// Pure logic with "now" passed in, so the schedule is testable without a camera.
  /// </remarks>
  internal sealed class FailureBackoff
  {
    private readonly TimeSpan _initialInterval;
    private readonly TimeSpan _maxInterval;
    private TimeSpan _interval;
    private DateTime _nextReportAt = DateTime.MinValue;

    public FailureBackoff(TimeSpan initialInterval, TimeSpan maxInterval)
    {
      _initialInterval = initialInterval;
      _maxInterval = maxInterval;
      _interval = initialInterval;
    }

    /// <summary>Consecutive failures since the last successful pull, reported or not.</summary>
    public int Attempts { get; private set; }

    /// <summary>True when the next failure is one the log should show.</summary>
    public bool Due(DateTime now) => now >= _nextReportAt;

    /// <summary>
    /// Counts one failed attempt. A due attempt moves the next report out by the current interval
    /// and doubles it; a suppressed one only counts.
    /// </summary>
    public void Record(DateTime now)
    {
      ++Attempts;
      if (!Due(now))
        return;

      _nextReportAt = now + _interval;
      _interval = TimeSpan.FromTicks(Math.Min(_interval.Ticks * 2, _maxInterval.Ticks));
    }

    /// <summary>
    /// Starts the streak over. Returns whether there was one — that is the line the log gets when
    /// the pull point comes back.
    /// </summary>
    public bool Reset()
    {
      var hadFailures = Attempts > 0;
      Attempts = 0;
      _interval = _initialInterval;
      _nextReportAt = DateTime.MinValue;
      return hadFailures;
    }
  }
}
