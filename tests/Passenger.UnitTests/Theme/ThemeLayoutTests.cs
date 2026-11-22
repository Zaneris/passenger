using System.Text;
using Passenger.App;
using Passenger.App.Docking;

namespace Passenger.UnitTests.Theme;

/// <summary>A theme's default layout as read: where its tool windows dock, in its order at each place, which open at the
/// start and the areas' sizes; what it leaves out stays Passenger's own; a window or place this Passenger lacks is passed
/// over and named, and a layout that cannot be laid as written refuses the theme, saying why.</summary>
public class ThemeLayoutTests
{
    private static ThemeRead<ThemeFile> Theme(string members) =>
        ThemeFiles.ReadTheme("Mine.json", Encoding.UTF8.GetBytes($$"""{"format": 1, "name": "Mine", {{members}}}"""), new Dictionary<string, ThemeTokenKind>());

    private static ThemeLayout Read(string layout)
    {
        ThemeRead<ThemeFile> read = Theme($"\"layout\": {layout}");
        Assert.True(read.Read?.Layout is not null, read.Refused);
        return read.Read.Layout;
    }

    [Fact]
    public void A_themes_layout_places_its_windows_and_arranges_the_window_as_written() => Cases.All(
        ("a layout places its windows in its order at each place opens those it names and sizes the areas", A_layout_places_its_windows_in_its_order_at_each_place_opens_those_it_names_and_sizes_the_areas),
        ("what a layout leaves out is Passengers own and a theme with none has no layout", What_a_layout_leaves_out_is_Passengers_own_and_a_theme_with_none_has_no_layout),
        ("a layout places tool windows among the editors each in one place", A_layout_places_tool_windows_among_the_editors_each_in_one_place),
        ("a layout gives where the editors tabs stand and one that gives none keeps Passengers own", A_layout_gives_where_the_editors_tabs_stand_and_one_that_gives_none_keeps_Passengers_own),
        ("a code colour scheme has no layout and passes one over", A_code_colour_scheme_has_no_layout_and_passes_one_over));

    private void A_layout_places_its_windows_in_its_order_at_each_place_opens_those_it_names_and_sizes_the_areas()
    {
        ThemeLayout layout = Read("""
            {"places": {"rightTop": ["explorer", "structure"], "bottomLeft": ["problems", "build", "run"], "leftTop": ["bookmarks"]},
             "open": ["explorer", "problems"], "sizes": {"left": 240, "right": 320, "bottom": 200}}
            """);
        Assert.Equal([new PlacedWindow(ToolWindowIds.Explorer, ToolAnchor.RightTop), new PlacedWindow(ToolWindowIds.Structure, ToolAnchor.RightTop),
            new PlacedWindow(ToolWindowIds.Problems, ToolAnchor.BottomLeft), new PlacedWindow(ToolWindowIds.Build, ToolAnchor.BottomLeft),
            new PlacedWindow(ToolWindowIds.Run, ToolAnchor.BottomLeft), new PlacedWindow(ToolWindowIds.Bookmarks, ToolAnchor.LeftTop)], layout.Places);
        Assert.Equal([ToolWindowIds.Explorer, ToolWindowIds.Problems], layout.Open);
        Assert.Equal((240.0, 320.0, 200.0), (layout.LeftWidth, layout.RightWidth, layout.BottomHeight));
    }

    private void What_a_layout_leaves_out_is_Passengers_own_and_a_theme_with_none_has_no_layout()
    {
        ThemeLayout layout = Read("""{"sizes": {"bottom": 180}}""");
        Assert.Empty(layout.Places);
        Assert.Null(layout.Open);
        Assert.Equal(((double?)null, (double?)null, (double?)180), (layout.LeftWidth, layout.RightWidth, layout.BottomHeight));
        Assert.Null(Theme("\"tokens\": {}").Read!.Layout);
    }

    [Fact]
    public void A_layout_or_arrangement_that_cannot_be_laid_as_written_refuses_the_theme_saying_why() => Cases.All(
        ("a layout that cannot be laid as written refuses the theme saying why", () => Cases.Each<(string, string)>(
        [
            ("\"layout\": [\"explorer\"]", "layout is {\"places\""),
            ("\"layout\": {\"places\": {\"rightTop\": \"explorer\"}}", "it is a list of tool windows' ids"),
            ("\"layout\": {\"places\": {\"rightTop\": [\"explorer\"], \"leftTop\": [\"explorer\"]}}", "places explorer in layout's rightTop and in leftTop: a tool window docks in one place"),
            ("\"layout\": {\"sizes\": {\"left\": 20}}", "an area's size is a number of pixels, 100 to 2000"),
            ("\"layout\": {\"sizes\": 200}", "sizes is an object"),
        ], row => A_layout_that_cannot_be_laid_as_written_refuses_the_theme_saying_why(row.Item1, row.Item2))),
        ("an arrangement word a layout does not know refuses the theme naming the words it takes", () => Cases.Each<(string, string)>(
        [
            ("\"layout\": {\"editorTabs\": \"bottom\"}", "gives layout's editorTabs as \"bottom\": it is \"top\", \"left\" or \"right\""),
            ("\"layout\": {\"closedWindows\": true}", "gives layout's closedWindows as true: it is \"keep\" or \"leave\""),
        ], row => An_arrangement_word_a_layout_does_not_know_refuses_the_theme_naming_the_words_it_takes(row.Item1, row.Item2))));

    private void A_layout_that_cannot_be_laid_as_written_refuses_the_theme_saying_why(string members, string why)
    {
        ThemeRead<ThemeFile> read = Theme(members);
        Assert.Null(read.Read);
        Assert.Contains(why, read.Refused, StringComparison.Ordinal);
    }

    private void A_layout_places_tool_windows_among_the_editors_each_in_one_place()
    {
        Assert.Equal([ToolWindowIds.Tests, ToolWindowIds.Problems], Read("""{"places": {"editor": ["tests", "problems"], "bottomLeft": ["build"]}}""").InEditor);
        ThemeRead<ThemeFile> twice = Theme("""
            "layout": {"places": {"bottomLeft": ["tests"], "editor": ["tests"]}}
            """);
        Assert.Contains("places tests in layout's bottomLeft and in editor", twice.Refused, StringComparison.Ordinal);
    }

    private void A_layout_gives_where_the_editors_tabs_stand_and_one_that_gives_none_keeps_Passengers_own()
    {
        Assert.Equal([EditorTabsPlace.Top, EditorTabsPlace.Left, EditorTabsPlace.Right],
            ((string[])["top", "left", "right"]).Select(word => Read($$$"""{"editorTabs": "{{{word}}}"}""").EditorTabs));
        Assert.Null(Read("""{"sizes": {"bottom": 180}}""").EditorTabs);
    }

    private void An_arrangement_word_a_layout_does_not_know_refuses_the_theme_naming_the_words_it_takes(string members, string why)
    {
        ThemeRead<ThemeFile> read = Theme(members);
        Assert.Null(read.Read);
        Assert.Contains(why, read.Refused, StringComparison.Ordinal);
    }

    private void A_code_colour_scheme_has_no_layout_and_passes_one_over()
    {
        ThemeRead<ThemeFile> read = ThemeFiles.ReadCodeColours("Mine.json", Encoding.UTF8.GetBytes("""{"format": 1, "name": "Mine", "layout": {"places": {}}}"""),
            new Dictionary<string, ThemeTokenKind>());
        Assert.True(read.Read is { Layout: null }, read.Refused);
        Assert.Contains(read.Problems, p => p.Contains("layout is no member of a code colour scheme", StringComparison.Ordinal));
    }
}
