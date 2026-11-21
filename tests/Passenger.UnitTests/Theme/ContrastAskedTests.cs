using Passenger.App;

namespace Passenger.UnitTests.Theme;

/// <summary>What each system says of high contrast, read into one answer: Windows' contrast theme dark or light by the
/// window colour it gives, GNOME's switch as gsettings says it, and a stand-in's words, any other refused.</summary>
public sealed class ContrastAskedTests
{
    [Theory]
    [InlineData("none", false, null)]
    [InlineData("high", true, null)]
    [InlineData("high-dark", true, true)]
    [InlineData(" high-light ", true, false)]
    public void A_stand_ins_words_say_whether_high_contrast_is_asked_and_in_what(string words, bool high, bool? dark) =>
        Assert.Equal(new ContrastAsked(high, dark), ContrastAsked.Parse(words));

    [Theory]
    [InlineData("")]
    [InlineData("on")]
    [InlineData("High")]
    public void A_stand_in_of_other_words_is_refused(string words) =>
        Assert.Throws<FormatException>(() => ContrastAsked.Parse(words));

    [Fact]
    public void Windows_contrast_themes_are_dark_on_a_black_window_and_light_on_a_white_one()
    {
        Assert.Equal(ContrastAsked.None, ContrastAsked.OfWindows(false, 0x000000));
        // COLORREF is 0x00BBGGRR: Aquatic's black window, Desert's warm white (#FFFAEF).
        Assert.Equal(new ContrastAsked(true, true), ContrastAsked.OfWindows(true, 0x000000));
        Assert.Equal(new ContrastAsked(true, false), ContrastAsked.OfWindows(true, 0xEFFAFF));
    }

    [Theory]
    [InlineData("true\n", true)]
    [InlineData("false\n", false)]
    [InlineData("No such schema “org.gnome.desktop.a11y.interface”", false)]
    public void GNOMEs_high_contrast_is_asked_only_where_gsettings_says_true(string said, bool high) =>
        Assert.Equal(high, ContrastAsked.OfGnome(said).High);
}
