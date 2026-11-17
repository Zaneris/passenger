using Passenger.Core;

namespace Passenger.UnitTests.Core;

/// <summary>What each system's answers come to (<see cref="MotionAsked"/>): macOS's Reduce Motion, non-blinking cursor and
/// insertion point periods, Windows' animations and caret blink time (INFINITE for none), GNOME's interface settings as
/// gsettings lists them, and a stand-in's words, any other refused.</summary>
public class MotionAskedTests
{
    private static CaretBlink Blink(int shown, int hidden) => new(TimeSpan.FromMilliseconds(shown), TimeSpan.FromMilliseconds(hidden));

    [Fact]
    public void A_stand_in_names_the_motion_and_the_blink_each_left_out_standing_as_Passengers_own()
    {
        Assert.Equal(new MotionAsked(false, Blink(530, 530)), MotionAsked.Parse("full,blink=530"));
        Assert.Equal(new MotionAsked(true, CaretBlink.Steady), MotionAsked.Parse("reduced, blink=none"));
        Assert.Equal(new MotionAsked(false, Blink(200, 400)), MotionAsked.Parse("blink=200/400"));
        Assert.Equal(new MotionAsked(true, CaretBlink.Default), MotionAsked.Parse("reduced"));
    }

    [Theory]
    [InlineData("slow")]
    [InlineData("blink=")]
    [InlineData("blink=0")]
    [InlineData("blink=-5")]
    [InlineData("blink=1/2/3")]
    [InlineData("full,,blink=530")]
    public void A_stand_in_of_other_words_is_refused(string value)
    {
        FormatException refused = Assert.Throws<FormatException>(() => MotionAsked.Parse(value));
        Assert.Contains(value, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_caret_blinks_its_time_each_way_never_for_INFINITE_and_animations_off_ask_for_less_motion()
    {
        Assert.Equal(new MotionAsked(false, Blink(530, 530)), MotionAsked.OfWindows(animates: true, 530));
        Assert.Equal(new MotionAsked(true, CaretBlink.Steady), MotionAsked.OfWindows(animates: false, MotionAsked.WindowsNeverBlinks));
        Assert.True(MotionAsked.OfWindows(animates: true, 0).Blink == CaretBlink.Default,
            "Windows' caret blink time read as 0, its answer when it could not say, gave a caret no blink of Passenger's own");
    }

    [Fact]
    public void Macs_insertion_point_blinks_its_periods_each_way_never_where_a_steady_cursor_is_preferred()
    {
        Assert.Equal(new MotionAsked(true, Blink(200, 800)), MotionAsked.OfMac(reducesMotion: true, steady: false, 200, 800));
        Assert.Equal(new MotionAsked(false, CaretBlink.Default), MotionAsked.OfMac(reducesMotion: false, steady: false, null, null));
        Assert.Equal(new MotionAsked(false, Blink(530, 300)), MotionAsked.OfMac(reducesMotion: false, steady: false, null, 300));
        Assert.Equal(new MotionAsked(false, CaretBlink.Steady), MotionAsked.OfMac(reducesMotion: false, steady: true, 200, 800));
    }

    [Fact]
    public void GNOMEs_cursor_blinks_half_its_cycle_each_way_never_with_cursor_blink_off_and_animations_off_ask_for_less_motion()
    {
        const string Listed = "org.gnome.desktop.interface color-scheme 'default'\n"
            + "org.gnome.desktop.interface cursor-blink true\n"
            + "org.gnome.desktop.interface cursor-blink-time 1200\n"
            + "org.gnome.desktop.interface cursor-blink-timeout 10\n"
            + "org.gnome.desktop.interface enable-animations false\n";
        Assert.Equal(new MotionAsked(true, Blink(600, 600)), MotionAsked.OfGnome(Listed));
        Assert.Equal(new MotionAsked(true, CaretBlink.Steady), MotionAsked.OfGnome(Listed.Replace("cursor-blink true", "cursor-blink false", StringComparison.Ordinal)));
        Assert.Equal(new MotionAsked(false, Blink(500, 500)),
            MotionAsked.OfGnome("org.gnome.desktop.interface cursor-blink-time uint32 1000\norg.gnome.desktop.interface enable-animations true\n"));
        Assert.Equal(MotionAsked.Default, MotionAsked.OfGnome(string.Empty));
    }
}
