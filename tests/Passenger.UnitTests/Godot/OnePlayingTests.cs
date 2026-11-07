using Passenger.App;

namespace Passenger.UnitTests.Godot;

/// <summary>A sound's preview takes a while to start, and a person may click the next before it has: of the sounds asked for
/// in turn only the last runs, whichever start lands first, and no player is left running out of Stop's reach.</summary>
public sealed class OnePlayingTests
{
    private readonly List<string> ended = [];

    private OnePlaying<string> Playing() => new(ended.Add);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_sounds_asked_before_either_started_leave_the_second_running_and_in_reach(bool firstLandsFirst)
    {
        OnePlaying<string> playing = Playing();
        int first = playing.Ask();
        int second = playing.Ask();
        Assert.True(playing.Overtaken(first), "A start a later sound overtook went on to start its player");
        Assert.False(playing.Overtaken(second), "The last sound asked for was taken for overtaken, so it never plays");
        if (firstLandsFirst)
        {
            playing.Started(first, "first");
            playing.Started(second, "second");
        }
        else
        {
            playing.Started(second, "second");
            playing.Started(first, "first");
        }
        Assert.True(ended.SequenceEqual(["first"]), $"The overtaken player should end as it lands, the last play on; ended: {string.Join(", ", ended)}");

        playing.Stop();
        Assert.True(ended.Order(StringComparer.Ordinal).SequenceEqual(["first", "second"]), $"Stop left a player running with nothing to end it; ended: {string.Join(", ", ended)}");
    }

    [Fact]
    public void A_sound_running_ends_as_the_next_is_asked_for()
    {
        OnePlaying<string> playing = Playing();
        playing.Started(playing.Ask(), "first");
        Assert.Empty(ended);

        int next = playing.Ask();
        Assert.Equal(["first"], ended);
        playing.Started(next, "second");
        playing.Stop();
        playing.Stop();
        Assert.Equal(["first", "second"], ended);
    }

    [Fact]
    public void A_sound_starting_as_Stop_is_pressed_ends_as_it_lands()
    {
        OnePlaying<string> playing = Playing();
        int ask = playing.Ask();
        playing.Stop();
        Assert.True(playing.Overtaken(ask), "A start stopped under way went on to start its player");

        playing.Started(ask, "late");
        Assert.Equal(["late"], ended);
    }
}
