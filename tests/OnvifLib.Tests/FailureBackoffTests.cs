using OnvifLib;

namespace OnvifLib.Tests;

// The schedule that keeps a camera which is off for a day from costing a line every two seconds:
// the first failure is reported at once, each later report waits twice as long, and the wait stops
// growing at the cap. "Now" is passed in, so the whole contract is testable without a camera.
public class FailureBackoffTests
{
  private static readonly TimeSpan Initial = TimeSpan.FromSeconds(2);
  private static readonly TimeSpan Maximum = TimeSpan.FromMinutes(5);
  private static readonly TimeSpan Tick = TimeSpan.FromTicks(1);
  private static readonly DateTime Start = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

  private static FailureBackoff NewBackoff(TimeSpan? maximum = null) =>
    new(Initial, maximum ?? Maximum);

  [Fact]
  public void ReportsTheFirstFailureAtOnce()
  {
    var backoff = NewBackoff();

    Assert.True(backoff.Due(Start));
    backoff.Record(Start);

    Assert.Equal(1, backoff.Attempts);
  }

  [Fact]
  public void WaitsTheInitialIntervalBeforeTheSecondReport()
  {
    var backoff = NewBackoff();
    backoff.Record(Start);

    Assert.False(backoff.Due(Start + Initial - Tick));
    Assert.True(backoff.Due(Start + Initial));
  }

  [Fact]
  public void EachReportWaitsTwiceAsLongAsTheOneBefore()
  {
    var backoff = NewBackoff();
    var now = Start;
    backoff.Record(now);            // the first failure is reported at once

    var waits = new List<TimeSpan>();
    var wait = Initial;
    for (var i = 0; i < 4; i++)
    {
      now += wait;
      Assert.True(backoff.Due(now));
      backoff.Record(now);
      waits.Add(wait);
      wait *= 2;
    }

    Assert.Equal([Initial, Initial * 2, Initial * 4, Initial * 8], waits);
  }

  [Fact]
  public void SuppressedFailuresStillCount()
  {
    var backoff = NewBackoff();
    const int failures = 3;

    for (var i = 0; i < failures; i++)
      backoff.Record(Start + TimeSpan.FromTicks(i));

    Assert.Equal(failures, backoff.Attempts);
  }

  [Fact]
  public void TheWaitStopsGrowingAtTheCap()
  {
    var cap = TimeSpan.FromSeconds(8);
    var backoff = NewBackoff(cap);
    var now = Start;
    backoff.Record(now);

    var waits = new List<TimeSpan>();
    var wait = Initial;
    for (var i = 0; i < 5; i++)
    {
      now += wait;
      backoff.Record(now);
      waits.Add(wait);
      wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, cap.Ticks));
    }

    Assert.Equal(cap, waits[^2]);
    Assert.Equal(cap, waits[^1]);
  }

  [Fact]
  public void ResetStartsTheStreakOver()
  {
    var backoff = NewBackoff();
    backoff.Record(Start);

    Assert.True(backoff.Reset());
    Assert.Equal(0, backoff.Attempts);
    Assert.True(backoff.Due(Start));      // the first failure of the new streak reports at once
    Assert.False(backoff.Reset());        // and there was nothing to report as recovered
  }
}
