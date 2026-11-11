using Passenger.Terminal;

namespace Passenger.UnitTests.Terminal;

/// <summary>The kitty keyboard protocol's flags as a program sets them: pushed and popped on a stack each screen keeps
/// apart, set in its three modes, asked for and answered with only the enhancement the keys follow, and gone with the
/// terminal's reset, so a program that never asks, or one that asks and ends, leaves the keys as xterm sends them.</summary>
public class KeyboardFlagsTests
{
    [Fact]
    public void A_push_answers_the_query_with_only_the_flags_the_keys_follow_and_a_pop_puts_back_the_ones_before()
    {
        (TerminalScreen screen, List<string> replies) = Screen();
        screen.Feed("\e[?u");
        screen.Feed("\e[>5u\e[?u");
        Assert.True(screen.KeyboardFlags == 1, $"Asking for disambiguation and alternate keys left the flags at {screen.KeyboardFlags}, not disambiguation alone");
        screen.Feed("\e[>31u\e[?u\e[<u\e[?u\e[<u\e[?u");
        Assert.Equal(["\e[?0u", "\e[?1u", "\e[?1u", "\e[?1u", "\e[?0u"], replies);
    }

    [Fact]
    public void A_set_replaces_adds_to_or_takes_from_the_flags_in_force()
    {
        (TerminalScreen screen, _) = Screen();
        screen.Feed("\e[=1u");
        Assert.True(screen.KeyboardFlags == 1, $"CSI = 1 u left the flags at {screen.KeyboardFlags}");
        screen.Feed("\e[=1;3u");
        Assert.True(screen.KeyboardFlags == 0, $"CSI = 1 ; 3 u, which takes the flag away, left the flags at {screen.KeyboardFlags}");
        screen.Feed("\e[=1;2u");
        Assert.True(screen.KeyboardFlags == 1, $"CSI = 1 ; 2 u, which adds the flag, left the flags at {screen.KeyboardFlags}");
        screen.Feed("\e[=0;1u");
        Assert.True(screen.KeyboardFlags == 0, $"CSI = 0 ; 1 u, which sets no flag, left the flags at {screen.KeyboardFlags}");
    }

    [Fact]
    public void Each_screen_keeps_its_own_flags_and_the_alternate_starts_without_any()
    {
        (TerminalScreen screen, _) = Screen();
        screen.Feed("\e[>1u\e[?1049h");
        Assert.True(screen.KeyboardFlags == 0, $"The alternate screen took the main screen's flags: {screen.KeyboardFlags}");
        screen.Feed("\e[>1u\e[?1049h");
        Assert.True(screen.KeyboardFlags == 1, $"Asking for the alternate screen again while it is up lost its program's flags: {screen.KeyboardFlags}");
        screen.Feed("\e[?1049l");
        Assert.True(screen.KeyboardFlags == 1, $"Leaving the alternate screen lost the main screen's flags: {screen.KeyboardFlags}");
        screen.Feed("\e[<u");
        Assert.True(screen.KeyboardFlags == 0, $"Popping the main screen's one push left {screen.KeyboardFlags}");
        screen.Feed("\e[?1049h");
        Assert.True(screen.KeyboardFlags == 0, $"A program that left the alternate screen without popping left its flags there for the next: {screen.KeyboardFlags}");
    }

    [Fact]
    public void A_pop_of_more_than_was_pushed_and_the_terminals_reset_clear_every_flag()
    {
        (TerminalScreen screen, _) = Screen();
        screen.Feed("\e[=1u\e[>1u\e[<5u");
        Assert.True(screen.KeyboardFlags == 0, $"A pop past the stack's foot kept the flags set beneath it: {screen.KeyboardFlags}");
        screen.Feed("\e[>1u\e[?1049h\e[>1u\ec");
        Assert.True(screen.KeyboardFlags == 0 && !screen.IsAlternate, $"The terminal's reset (ESC c) kept the flags: {screen.KeyboardFlags}");
        screen.Feed("\e[?1049l");
        Assert.True(screen.KeyboardFlags == 0, $"The terminal's reset kept the main screen's flags: {screen.KeyboardFlags}");
    }

    [Fact]
    public void A_push_past_the_stacks_room_drops_the_oldest_and_pops_still_reach_the_rest()
    {
        (TerminalScreen screen, _) = Screen();
        screen.Feed(string.Concat(Enumerable.Repeat("\e[>0u\e[>1u", 20)));
        Assert.True(screen.KeyboardFlags == 1, $"After 40 pushes the last's flags are not in force: {screen.KeyboardFlags}");
        screen.Feed("\e[<u");
        Assert.True(screen.KeyboardFlags == 0, $"A pop did not put back the push before: {screen.KeyboardFlags}");
        screen.Feed("\e[<100u");
        Assert.True(screen.KeyboardFlags == 0, $"Popping everything left {screen.KeyboardFlags}");
    }

    private static (TerminalScreen Screen, List<string> Replies) Screen()
    {
        TerminalScreen screen = new(80, 24);
        List<string> replies = [];
        screen.Reply += replies.Add;
        return (screen, replies);
    }
}
