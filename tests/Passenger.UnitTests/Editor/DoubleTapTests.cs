using Avalonia.Input;
using Passenger.Editor;

namespace Passenger.UnitTests.Editor;

/// <summary>A double tap of a modifier alone ("shift , shift"), the shortcut IntelliJ IDEA's keymap gives Search
/// Everywhere: written, read back and shown as menus name it, and run only by the modifier pressed alone and let go twice
/// within half a second, the time given here rather than read from a clock.</summary>
public class DoubleTapTests
{
    private const string Search = "search";

    [Fact]
    public void A_double_tap_reads_back_as_written_and_is_shown_by_its_modifiers_name()
    {
        Shortcut shift = Shortcut.Parse("shift , shift");
        Assert.True(shift.IsDoubleTap, "\"shift , shift\" was read as something other than a double tap of Shift");
        Assert.Equal("shift , shift", shift.Notation());
        Assert.Equal(shift, Shortcut.Parse(shift.Notation()));
        Assert.Equal(OperatingSystem.IsMacOS() ? "⇧⇧" : "Double Shift", shift.Display());
        Assert.False(Shortcut.Parse("shift K , shift K").IsDoubleTap, "A chord of one key pressed twice was read as a double tap");
    }

    [Theory]
    [InlineData("shift")]
    [InlineData("shift , control")]
    [InlineData("control K , shift")]
    public void A_modifier_alone_is_read_only_as_a_double_tap(string written) =>
        Assert.Throws<FormatException>(() => Shortcut.Parse(written));

    [Fact]
    public void A_modifier_tapped_twice_within_half_a_second_runs_its_action_and_nothing_between_or_slower_does()
    {
        long now = 0;
        KeymapDispatcher keys = new(new Keymap("taps").Bind(Search, "shift , shift").Bind("letter", "control A"), () => now);
        string? Down(Key key, KeyModifiers held) => keys.Press(new KeyEventArgs { Key = key, KeyModifiers = held }, out _, out _);
        void Up(Key key) => keys.Release(new KeyEventArgs { Key = key, KeyModifiers = KeyModifiers.None });
        string? Tap(Key key = Key.LeftShift)
        {
            string? ran = Down(key, KeyModifiers.Shift);
            Up(key);
            return ran;
        }

        Assert.Null(Tap());
        now += 200;
        Assert.True(Tap(Key.RightShift) == Search, "Shift tapped twice within half a second, the left then the right, ran nothing");

        now += 1000;
        Assert.Null(Tap());
        now += 600;
        Assert.True(Tap() is null, "Shift tapped again 600 ms after its first tap ran its action, where a double tap's taps come within half a second");
        now += 100;
        Assert.True(Tap() == Search, "A tap too slow to be a second was no first either");

        now += 1000;
        Assert.Null(Tap());
        Assert.Null(Down(Key.A, KeyModifiers.None));
        Assert.True(Tap() is null, "Shift tapped, A, then Shift ran its action, where a key between ends a double tap");

        now += 1000;
        Assert.Null(Tap());
        keys.Heard(new KeyEventArgs { Key = Key.B, KeyModifiers = KeyModifiers.None });
        Assert.True(Tap() is null, "Shift tapped, a key another part of the window took, then Shift ran its action");

        now += 1000;
        Assert.Null(Tap());
        keys.Reset();
        Assert.True(Tap() is null, "Shift tapped, a click, then Shift ran its action, where a click between ends a double tap");

        now += 1000;
        Assert.Null(Down(Key.LeftShift, KeyModifiers.Shift));
        Assert.Null(Down(Key.A, KeyModifiers.Shift));
        Up(Key.A);
        Up(Key.LeftShift);
        Assert.True(Tap() is null, "A capital typed with Shift, then Shift tapped, ran its action, where typing taps nothing");

        now += 1000;
        Assert.Null(Down(Key.LeftCtrl, KeyModifiers.Control));
        Assert.Null(Down(Key.LeftShift, KeyModifiers.Control | KeyModifiers.Shift));
        Up(Key.LeftShift);
        Up(Key.LeftCtrl);
        Assert.True(Tap() is null, "Shift let go with Ctrl held, then Shift tapped, ran its action, where only a modifier alone taps");

        now += 1000;
        Assert.Null(Down(Key.LeftShift, KeyModifiers.Shift));
        Assert.True(Down(Key.LeftShift, KeyModifiers.Shift) is null && Down(Key.LeftShift, KeyModifiers.Shift) is null,
            "A held Shift's repeated presses ran its action, where only a second tap does");
    }
}
