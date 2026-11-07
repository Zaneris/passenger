using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>Godot's themes as Passenger reads them: the carried table of the default theme, a class's items found along its
/// bases as Godot looks them up and the classes a node known as one may be, and the items a project's own Theme resources
/// define and its scenes override on a node.</summary>
public class GodotThemesTests
{
    [Fact]
    public void The_carried_table_gives_a_Label_its_font_color_and_a_CheckBox_its_buttons_items_along_its_bases()
    {
        GodotThemeTable table = GodotThemeTable.Carried;
        Assert.Contains("font_color", table.Items("Label", "colors"));
        Assert.Contains("normal", table.Items("Label", "styles"));
        Assert.Empty(table.Items("Label", "icons"));
        Assert.Equal(["CheckBox", "Button", "BaseButton", "Control", "CanvasItem", "Node", "Object"], table.Chain("CheckBox"));
        Assert.Contains("checked", table.Items("CheckBox", "icons"));
        Assert.Contains("h_separation", table.Chain("CheckBox").SelectMany(t => table.Items(t, "constants")));
        Assert.Contains("CheckBox", table.Below("Button"));
        Assert.DoesNotContain("Label", table.Below("Button"));
        Assert.Equal("Label", table.Variations["HeaderSmall"]);
        // Godot looks a type named in the call up in that type alone where it is no class.
        Assert.Equal(["HeaderSmall"], table.Chain("HeaderSmall"));
        Assert.True(table.Knows("HeaderSmall") && table.Knows("Window") && table.Knows("AcceptDialog"));
        Assert.False(table.Knows("Sprite2D"));
    }

    /// <summary>The default theme differs by Godot: 4.5 gave a Label its focus style and 4.4 took away a ColorPicker's hue icon,
    /// and the table says each Godot's own; older or newer than those it tells, it is no longer sure of what it lacks.</summary>
    [Fact]
    public void The_table_says_each_godots_own_items_and_is_whole_only_for_the_godots_it_tells()
    {
        GodotThemeTable table = GodotThemeTable.Carried;
        Assert.Equal(["normal"], table.For(new Version(4, 4)).Items("Label", "styles"));
        Assert.Equal(["focus", "normal"], table.For(new Version(4, 5)).Items("Label", "styles"));
        Assert.Equal(["focus", "normal"], table.For(new Version(4, 8, 1)).Items("Label", "styles"));
        Assert.Contains("color_okhsl_hue", table.For(new Version(4, 3)).Items("ColorPicker", "icons"));
        Assert.DoesNotContain("color_okhsl_hue", table.For(new Version(4, 4)).Items("ColorPicker", "icons"));
        Assert.Empty(table.For(new Version(4, 4)).Items("FoldableContainer", "styles"));

        Assert.Equal(new Version(4, 5), table.For(new Version(4, 4)).Later("Label", "styles")["focus"]);
        Assert.Empty(table.For(new Version(4, 5)).Later("Label", "styles"));
        Assert.Contains("color_okhsl_hue", table.Removed("ColorPicker", "icons"));
        Assert.Empty(table.For(new Version(4, 7)).Removed("ColorPicker", "icons"));

        Assert.Equal([true, true, true, false, false], new[] { new Version(4, 2), new Version(4, 4), new Version(4, 8) , new Version(4, 1), new Version(4, 9) }.Select(v => table.For(v).IsWhole));
        Assert.True(table.IsWhole);
    }

    [Theory]
    [InlineData("config/features=PackedStringArray(\"4.4\", \"C#\", \"Forward Plus\")", "4.4")]
    [InlineData("config/features=PackedStringArray(\"4.7.2\", \"Mobile\")", "4.7")]
    [InlineData("config/features=PackedStringArray(\"C#\", \"Forward Plus\")", null)]
    [InlineData("config/name=\"4.4\"", null)]
    public void A_project_names_its_godot_in_its_features_and_no_other_value(string line, string? godot)
    {
        Assert.Equal(godot is null ? null : Version.Parse(godot), GodotProject.GodotOf($"config_version=5\n\n[application]\n\n{line}\n"));
    }

    /// <summary>A node's type variation leads its class's chain, through each type it varies (a project theme's declaration
    /// before the default theme's); a type named alone is looked in alone; and the class reference describes a Label's
    /// font_color, not a project's own item.</summary>
    [Fact]
    public void A_variation_leads_its_nodes_class_and_the_class_reference_describes_a_labels_font_color()
    {
        const string theme = "[gd_resource type=\"Theme\" format=3]\n\n[resource]\nTitle/base_type = &\"Label\"\nTitle/colors/glow = Color(1, 1, 0, 1)\nBig/base_type = &\"Title\"\n";
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile> { ["res://ui/theme.tres"] = GodotFile.Parse(theme) }, texts: new Dictionary<string, string>());
        GodotDefinitions defined = scenes.Defined;
        Assert.Equal([("Big", "Title"), ("Title", "Label")], defined.ThemeVariations.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => (v.Key, v.Value)));
        Assert.Equal(["Big", "Title", "Label", "Control", "CanvasItem", "Node", "Object"], defined.ThemeChain("Label", "Big"));
        Assert.Equal(["HeaderSmall", "Label", "Control", "CanvasItem", "Node", "Object"], defined.ThemeChain("Label", "HeaderSmall"));
        Assert.Equal(["Title"], defined.ThemeChain("Title"));
        Assert.Equal(["glow"], defined.ThemeItems.Where(i => i.Type == "Title").Select(i => i.Name));
        Assert.Equal("Default text Color of the Label.", GodotThemeTable.Carried.Described("Label", "colors", "font_color"));
        Assert.Null(GodotThemeTable.Carried.Described("Label", "colors", "glow"));
    }

    [Fact]
    public void A_project_s_themes_and_its_scenes_overrides_are_read_with_the_places_of_their_names()
    {
        const string theme = "[gd_resource type=\"Theme\" format=3]\n\n[resource]\nButton/colors/font_color = Color(1, 0, 0, 1)\nScore/base_type = &\"Label\"\nScore/colors/glow = Color(1, 1, 0, 1)\n";
        const string scene = """
            [gd_scene format=3]

            [sub_resource type="Theme" id="Theme_1"]
            Panel/styles/panel = null

            [node name="Hud" type="Control"]
            theme = SubResource("Theme_1")

            [node name="Score" type="Label" parent="."]
            theme_override_colors/font_outline_color = Color(0, 0, 0, 1)
            theme_override_constants/outline_size = 4

            """;
        GodotScenes scenes = new("/game", new Dictionary<string, GodotFile>
        {
            ["res://hud.tscn"] = GodotFile.Parse(scene),
            ["res://ui/theme.tres"] = GodotFile.Parse(theme),
        }, texts: new Dictionary<string, string> { ["res://project.godot"] = "[gui]\n\ntheme/custom=\"res://ui/theme.tres\"\n" });
        GodotDefinitions defined = scenes.Defined;

        Assert.Equal("res://ui/theme.tres", defined.CustomTheme);
        Assert.Equal([("Button", "colors", "font_color", "theme.tres:4"), ("Score", "colors", "glow", "theme.tres:6"), ("Panel", "styles", "panel", "hud.tscn:4")],
            defined.ThemeItems.Select(i => (i.Type, i.Kind, i.Name, i.Place.Shown)));
        GodotThemeItem first = defined.ThemeItems[0];
        Assert.Equal("font_color", theme[first.Place.Start..first.Place.End]);
        Assert.True(first.Place.Defines);
        Assert.Equal([("Label", "colors", "font_outline_color"), ("Label", "constants", "outline_size")], defined.ThemeOverrides.Select(i => (i.Type, i.Kind, i.Name)));
        Assert.False(defined.ThemeOverrides[0].Place.Defines);
    }
}
