using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A live reading (the Stats tab's metrics, an agent's telemetry, the Requests window's requests) of a program on a
/// runtime that hangs when a session starts as the program starts waits until the program is past its start, and only that
/// long: a program past it, or on a runtime that takes sessions as it starts, is read at once.</summary>
public sealed class ProgramStartsTests
{
    private static readonly TimeSpan Settling = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_program_on_a_runtime_that_can_hang_is_read_once_past_its_start_and_only_that_late() => Cases.All(
        ("a young program on a runtime that can hang waits out the rest of its start", () => Cases.Each(
            ["10.0.14+5ad4f9a1c2b3", "11.0.0-preview.7.26381.103+a1b2c3", null, "not a version"], version =>
        {
            Assert.Equal(Settling, ProgramStarts.Due(version, TimeSpan.Zero, Settling));
            Assert.Equal(TimeSpan.FromSeconds(3.5), ProgramStarts.Due(version, TimeSpan.FromSeconds(1.5), Settling));
        })),
        ("a program past its start is read at once", () => Assert.Equal(TimeSpan.Zero, ProgramStarts.Due("10.0.14+5ad4f9a1c2b3", TimeSpan.FromMinutes(30), Settling))),
        ("a runtime that takes sessions as its program starts is read at once however young", () => Cases.Each(
            ["11.0.0-rc.1.26425.128+a1b2c3", "11.0.0-rtm.26560.101", "12.0.0-preview.1.27100.1"],
            version => Assert.Equal(TimeSpan.Zero, ProgramStarts.Due(version, TimeSpan.Zero, Settling)))),
        ("a start the clock puts after now waits the whole of its start, never longer", () => Assert.Equal(Settling, ProgramStarts.Due("10.0.14", TimeSpan.FromSeconds(-2), Settling))));
}
