using Passenger.App;

namespace Passenger.UnitTests.Theme;

/// <summary>What each system says of high contrast, read into one answer: Windows' contrast theme dark or light by the
/// window colour it gives, GNOME's switch as gsettings says it, and a stand-in's words, any other refused.</summary>
public sealed class ContrastAskedTests
{
    [Fact]
    public void High_contrast_is_asked_as_each_system_says_and_a_stand_in_names() => Cases.All(
        ("a stand ins words say whether high contrast is asked and in what", () => Cases.Each<(string, bool, bool?)>(
        [
            ("none", false, null),
            ("high-dark", true, true),
            (" high-light ", true, false),
        ], row => A_stand_ins_words_say_whether_high_contrast_is_asked_and_in_what(row.Item1, row.Item2, row.Item3))),
        ("a stand in of other words is refused", () => Cases.Each<string>(
        [
            "",
            "High",
        ], A_stand_in_of_other_words_is_refused)),
        ("windows contrast themes are dark on a black window and light on a white one", Windows_contrast_themes_are_dark_on_a_black_window_and_light_on_a_white_one),
        ("GNOMEs high contrast is asked only where gsettings says true", () => Cases.Each<(string, bool)>(
        [
            ("true\n", true),
            ("No such schema “org.gnome.desktop.a11y.interface”", false),
        ], row => GNOMEs_high_contrast_is_asked_only_where_gsettings_says_true(row.Item1, row.Item2))));

    private void A_stand_ins_words_say_whether_high_contrast_is_asked_and_in_what(string words, bool high, bool? dark) =>
        Assert.Equal(new ContrastAsked(high, dark), ContrastAsked.Parse(words));

    private void A_stand_in_of_other_words_is_refused(string words) =>
        Assert.Throws<FormatException>(() => ContrastAsked.Parse(words));

    private void Windows_contrast_themes_are_dark_on_a_black_window_and_light_on_a_white_one()
    {
        Assert.Equal(ContrastAsked.None, ContrastAsked.OfWindows(false, 0x000000));
        // COLORREF is 0x00BBGGRR: Aquatic's black window, Desert's warm white (#FFFAEF).
        Assert.Equal(new ContrastAsked(true, true), ContrastAsked.OfWindows(true, 0x000000));
        Assert.Equal(new ContrastAsked(true, false), ContrastAsked.OfWindows(true, 0xEFFAFF));
    }

    private void GNOMEs_high_contrast_is_asked_only_where_gsettings_says_true(string said, bool high) =>
        Assert.Equal(high, ContrastAsked.OfGnome(said).High);
}
