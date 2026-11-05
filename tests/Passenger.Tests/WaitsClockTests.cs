using System.Diagnostics;
using System.Runtime.InteropServices;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The clock a test's waits give up by (<see cref="Deadline"/>, <see cref="Stopwatch"/>'s) is the system's clock that
/// stops while the machine sleeps: macOS's uptime (CLOCK_UPTIME_RAW), Linux's monotonic clock, where the wall clock and
/// macOS's own monotonic clock run on. So a test the machine slept through goes on as it wakes, rather than every wait
/// giving up at once. Read, not slept through: the two clocks agree now.</summary>
[Collection(Beside.Name)]
public sealed partial class WaitsClockTests
{
    [Fact]
    public void The_clock_a_wait_gives_up_by_is_the_one_that_stops_while_the_machine_sleeps()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' performance counter, Stopwatch's clock there, counts the time the machine sleeps");
        long before = Deadline.In(TimeSpan.Zero).At;
        double stopping = SleepStoppedSeconds();
        long after = Deadline.In(TimeSpan.Zero).At;
        double waits = (before + after) / 2.0 / Stopwatch.Frequency;
        Assert.True(Math.Abs(waits - stopping) < 1,
            $"A wait's clock read {waits:0.###} s where the clock that stops while the machine sleeps read {stopping:0.###} s, so a wait the machine slept through would "
            + "give up as it woke, every test waiting then failing");
    }

    /// <summary>The seconds of the system's clock that stops while the machine sleeps.</summary>
    private static double SleepStoppedSeconds()
    {
        if (OperatingSystem.IsMacOS())
        {
            return UptimeNanoseconds(UptimeRaw) / 1e9;
        }
        Assert.True(ClockTime(Monotonic, out TimeSpec time) == 0, $"clock_gettime(CLOCK_MONOTONIC) failed: {Marshal.GetLastPInvokeError()}");
        return time.Seconds + time.Nanoseconds / 1e9;
    }

    private const int UptimeRaw = 8;
    private const int Monotonic = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [LibraryImport("libc", EntryPoint = "clock_gettime_nsec_np")]
    private static partial ulong UptimeNanoseconds(int clock);

    [LibraryImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static partial int ClockTime(int clock, out TimeSpec time);
}
