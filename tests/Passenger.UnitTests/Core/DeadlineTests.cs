using System.Diagnostics;
using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>A wait's limit on the clock's timestamps: passed only once its time has run, the time left until then, none
/// past the clock's last for a time too long to count, and now for none.</summary>
public class DeadlineTests
{
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void A_deadline_passes_once_its_time_has_run_on_the_clock_and_says_the_time_left_before()
    {
        const long now = 1_000_000;
        Deadline limit = Deadline.In(TimeSpan.FromSeconds(5), now);
        Assert.True(!limit.PassedAt(now) && !limit.PassedAt(now + Ticks(4.9)) && !limit.PassedAt(now + Ticks(5)),
            "A wait of 5 s gave up before its 5 s had run on the clock, as a wait counting a sleep the machine woke from would");
        Assert.True(limit.PassedAt(now + Ticks(5) + 1), "A wait of 5 s went on after its 5 s had run, so it would never give up");
        TimeSpan left = limit.LeftAt(now + Ticks(2));
        Assert.True(Math.Abs((left - TimeSpan.FromSeconds(3)).TotalMilliseconds) < 1, $"With 2 s of a 5 s wait run, {left} was said left, not 3 s");
        Assert.True(limit.LeftAt(now + Ticks(9)) == TimeSpan.Zero, $"A wait past its time said {limit.LeftAt(now + Ticks(9))} left, not none");
    }

    [Fact]
    public void A_time_too_long_to_count_never_passes_and_none_or_less_has_passed_at_once()
    {
        const long now = 1_000_000;
        Deadline forever = Deadline.In(TimeSpan.MaxValue, now);
        Assert.True(forever.At == long.MaxValue && !forever.PassedAt(long.MaxValue - 1),
            $"A wait of TimeSpan.MaxValue ended at {forever.At}: its time overflowed the clock, so it would give up at once or never");
        Assert.True(Deadline.In(TimeSpan.Zero, now).PassedAt(now + 1) && Deadline.In(TimeSpan.FromSeconds(-1), now).PassedAt(now + 1),
            "A wait of no time, or less, did not give up at the next look");
        Deadline sooner = Deadline.In(TimeSpan.FromSeconds(1), now);
        Deadline later = Deadline.In(TimeSpan.FromSeconds(2), now);
        Assert.True(Deadline.Later(sooner, later) == later && Deadline.Later(later, sooner) == later, "The later of two limits was not the one 2 s on");
    }
}
