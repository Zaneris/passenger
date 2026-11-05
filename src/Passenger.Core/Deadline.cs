using System.Diagnostics;

namespace Passenger.Core;

/// <summary>A moment some time from now on <see cref="Stopwatch"/>'s clock, at which a wait gives up. That clock stops
/// while the machine sleeps (macOS's uptime, Linux's monotonic clock), where the wall clock (<see cref="DateTime.UtcNow"/>)
/// runs on, so a wait the machine slept through goes on from where it stood as it wakes, rather than giving up at once.
/// <see cref="At"/> is a timestamp of <see cref="Stopwatch.GetTimestamp"/>'s.</summary>
public readonly record struct Deadline(long At)
{
    /// <summary>The moment <paramref name="time"/> from now; none past the clock's last, and now for no time or less.</summary>
    public static Deadline In(TimeSpan time) => In(time, Stopwatch.GetTimestamp());

    /// <summary>The moment <paramref name="time"/> after <paramref name="now"/>, a timestamp of the clock's.</summary>
    public static Deadline In(TimeSpan time, long now)
    {
        double ticks = Math.Max(0, time.TotalSeconds) * Stopwatch.Frequency;
        return new Deadline(ticks >= long.MaxValue - now ? long.MaxValue : now + (long)ticks);
    }

    /// <summary>The later of two moments.</summary>
    public static Deadline Later(Deadline one, Deadline other) => one.At >= other.At ? one : other;

    /// <summary>Whether the moment has passed.</summary>
    public bool Passed => PassedAt(Stopwatch.GetTimestamp());

    /// <summary>Whether the moment has passed at <paramref name="now"/>, a timestamp of the clock's.</summary>
    public bool PassedAt(long now) => now > At;

    /// <summary>The time left until the moment, none once it has passed.</summary>
    public TimeSpan Left => LeftAt(Stopwatch.GetTimestamp());

    /// <summary>The time left at <paramref name="now"/>, a timestamp of the clock's, until the moment.</summary>
    public TimeSpan LeftAt(long now) => now >= At ? TimeSpan.Zero : Stopwatch.GetElapsedTime(now, At);
}
