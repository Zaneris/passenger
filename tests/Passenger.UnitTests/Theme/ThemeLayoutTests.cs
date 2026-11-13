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
    public void A_layout_places_its_windows_in_its_order_at_each_place_opens_those_it_names_and_sizes_the_areas()
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

    [Fact]
    public void What_a_layout_leaves_out_is_Passengers_own_and_a_theme_with_none_has_no_layout()
    {
        ThemeLayout layout = Read("""{"sizes": {"bottom": 180}}""");
        Assert.Empty(layout.Places);
        Assert.Null(layout.Open);
        Assert.Equal(((double?)null, (double?)null, (double?)180), (layout.LeftWidth, layout.RightWidth, layout.BottomHeight));
        Assert.Null(Theme("\"tokens\": {}").Read!.Layout);
    }

    [Fact]
    public void Every_place_a_window_docks_is_a_word_a_theme_may_write()
    {
        string[] words = ["leftTop", "leftBottom", "rightTop", "rightBottom", "bottomLeft", "bottomRight"];
        ToolAnchor[] read = [.. words.Select(word => Read($$$"""{"places": {"{{{word}}}": ["terminal"]}}""").Places.Single().Anchor)];
        Assert.Equal(Enum.GetValues<ToolAnchor>(), read);
    }

    [Fact]
    public void A_window_or_place_this_Passenger_lacks_and_a_member_it_does_not_know_are_passed_over_and_named()
    {
        ThemeRead<ThemeFile> read = Theme("""
            "layout": {"places": {"rightTop": ["explorer", "radar"], "floating": ["git"]}, "open": ["radar", "explorer"],
              "sizes": {"bottom": 200, "top": 50}, "splits": 0.5}
            """);
        Assert.True(read.Read?.Layout is not null, read.Refused);
        Assert.Equal([new PlacedWindow(ToolWindowIds.Explorer, ToolAnchor.RightTop)], read.Read.Layout.Places);
        Assert.Equal([ToolWindowIds.Explorer], read.Read.Layout.Open);
        Assert.Equal(5, read.Problems.Count);
        Assert.Contains(read.Problems, p => p.Contains("\"radar\" in layout's places' rightTop is no tool window", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("\"radar\" in layout's open is no tool window", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("floating in layout's places is no place", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("top is no member of layout's sizes", StringComparison.Ordinal));
        Assert.Contains(read.Problems, p => p.Contains("splits is no member of layout", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\"layout\": [\"explorer\"]", "layout is {\"places\"")]
    [InlineData("\"layout\": {\"places\": [\"explorer\"]}", "places is an object")]
    [InlineData("\"layout\": {\"places\": {\"rightTop\": \"explorer\"}}", "it is a list of tool windows' ids")]
    [InlineData("\"layout\": {\"places\": {\"rightTop\": [1]}}", "a tool window is named by its id")]
    [InlineData("\"layout\": {\"places\": {\"rightTop\": [\"explorer\"], \"leftTop\": [\"explorer\"]}}", "places explorer in layout's rightTop and in leftTop: a tool window docks in one place")]
    [InlineData("\"layout\": {\"open\": \"explorer\"}", "gives layout's open as")]
    [InlineData("\"layout\": {\"sizes\": {\"left\": 20}}", "an area's size is a number of pixels, 100 to 2000")]
    [InlineData("\"layout\": {\"sizes\": {\"bottom\": \"tall\"}}", "an area's size is a number of pixels")]
    [InlineData("\"layout\": {\"sizes\": 200}", "sizes is an object")]
    public void A_layout_that_cannot_be_laid_as_written_refuses_the_theme_saying_why(string members, string why)
    {
        ThemeRead<ThemeFile> read = Theme(members);
        Assert.Null(read.Read);
        Assert.Contains(why, read.Refused, StringComparison.Ordinal);
    }

    [Fact]
    public void A_layout_gives_where_the_editors_tabs_stand_and_one_that_gives_none_keeps_Passengers_own()
    {
        Assert.Equal([EditorTabsPlace.Top, EditorTabsPlace.Left, EditorTabsPlace.Right],
            ((string[])["top", "left", "right"]).Select(word => Read($$$"""{"editorTabs": "{{{word}}}"}""").EditorTabs));
        Assert.Null(Read("""{"sizes": {"bottom": 180}}""").EditorTabs);
    }

    [Theory]
    [InlineData("\"layout\": {\"editorTabs\": \"bottom\"}", "gives layout's editorTabs as \"bottom\": it is \"top\", \"left\" or \"right\"")]
    [InlineData("\"layout\": {\"editorTabs\": 1}", "gives layout's editorTabs as 1")]
    public void An_arrangement_word_a_layout_does_not_know_refuses_the_theme_naming_the_words_it_takes(string members, string why)
    {
        ThemeRead<ThemeFile> read = Theme(members);
        Assert.Null(read.Read);
        Assert.Contains(why, read.Refused, StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_colour_scheme_has_no_layout_and_passes_one_over()
    {
        ThemeRead<ThemeFile> read = ThemeFiles.ReadCodeColours("Mine.json", Encoding.UTF8.GetBytes("""{"format": 1, "name": "Mine", "layout": {"places": {}}}"""),
            new Dictionary<string, ThemeTokenKind>());
        Assert.True(read.Read is { Layout: null }, read.Refused);
        Assert.Contains(read.Problems, p => p.Contains("layout is no member of a code colour scheme", StringComparison.Ordinal));
    }
}
