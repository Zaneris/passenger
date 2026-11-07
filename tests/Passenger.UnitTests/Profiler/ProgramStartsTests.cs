using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A live reading (the Stats tab's metrics, an agent's telemetry, the Requests window's requests) of a program on a
/// runtime that hangs when a session starts as the program starts waits until the program is past its start, and only that
/// long: a program past it, or on a runtime that takes sessions as it starts, is read at once.</summary>
public sealed class ProgramStartsTests
{
    private static readonly TimeSpan Settling = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("10.0.14-servicing.26522.126+0427b9abbead3fa64c6655eb4290c537397131fa")]
    [InlineData("10.0.14+5ad4f9a1c2b3")]
    [InlineData("9.0.11+0c4b5d6e7f80")]
    [InlineData("8.0.22")]
    [InlineData("11.0.0-preview.7.26381.103+a1b2c3")]
    [InlineData("11.0.0-alpha.1.26101.1")]
    [InlineData(null)]
    [InlineData("not a version")]
    public void A_young_program_on_a_runtime_that_can_hang_waits_out_the_rest_of_its_start(string? version)
    {
        Assert.Equal(Settling, ProgramStarts.Due(version, TimeSpan.Zero, Settling));
        Assert.Equal(TimeSpan.FromSeconds(3.5), ProgramStarts.Due(version, TimeSpan.FromSeconds(1.5), Settling));
    }

    [Theory]
    [InlineData("10.0.14+5ad4f9a1c2b3")]
    [InlineData(null)]
    public void A_program_past_its_start_is_read_at_once(string? version)
    {
        Assert.Equal(TimeSpan.Zero, ProgramStarts.Due(version, Settling, Settling));
        Assert.Equal(TimeSpan.Zero, ProgramStarts.Due(version, TimeSpan.FromMinutes(30), Settling));
    }

    [Theory]
    [InlineData("11.0.0-rc.1.26425.128+a1b2c3")]
    [InlineData("11.0.0-rc.3.26522.148+5254d8bcf59917edc1e1abc33bdf2442b77b1c70")]
    [InlineData("11.0.0-rtm.26560.101")]
    [InlineData("11.0.1-servicing.26600.104+a1b2c3")]
    [InlineData("11.0.0+a1b2c3")]
    [InlineData("11.0.2")]
    [InlineData("12.0.0-preview.1.27100.1")]
    public void A_runtime_that_takes_sessions_as_its_program_starts_is_read_at_once_however_young(string version)
    {
        Assert.Equal(TimeSpan.Zero, ProgramStarts.Due(version, TimeSpan.Zero, Settling));
    }

    [Fact]
    public void A_start_the_clock_puts_after_now_waits_the_whole_of_its_start_never_longer()
    {
        Assert.Equal(Settling, ProgramStarts.Due("10.0.14", TimeSpan.FromSeconds(-2), Settling));
    }
}
