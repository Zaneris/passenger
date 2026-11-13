using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>The window's arrangement as a person meets it: each a setting whose default a theme may give and the person's
/// own choice wins over. The editor's tabs down a side, gone between by a click, a drag and the keys.</summary>
public sealed class WindowArrangementTests : IDisposable
{
    private readonly string folder = TestFolders.Create("arrangement-");

    public WindowArrangementTests()
    {
        foreach (string name in (string[])["One.cs", "Two.cs", "Three.cs"])
        {
            File.WriteAllText(Path.Combine(folder, name), $"class {Path.GetFileNameWithoutExtension(name)} {{ }}\n");
        }
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Editor_tabs_set_down_a_side_stand_in_a_column_there_where_a_click_a_drag_and_the_next_tabs_keys_go_between_them()
    {
        MainWindow window = Open();
        try
        {
            window.ApplySettings(window.Settings with { EditorTabs = EditorTabsPlace.Left });
            Ui.Settle();
            EditorGroup group = window.EditorArea.ActiveGroup;
            Rect strip = Placed(window, group.TabStrip);
            Rect editor = Placed(window, group.Editor);
            Assert.True(strip.Right <= editor.X + 1 && strip.Height >= editor.Height - 1,
                $"Set to the left, the tabs stand at {strip}, beside the editor at {editor} rather than down its left side");
            Assert.True(Tabs(group).Select(t => Placed(window, t).Y).Order().SequenceEqual(Tabs(group).Select(t => Placed(window, t).Y)) && Tabs(group).Select(t => Placed(window, t).X).Distinct().Count() == 1,
                $"The tabs down the side are not one column: {string.Join(", ", Tabs(group).Select(t => Placed(window, t)))}");

            Ui.Click(Tab(group, "One.cs"));
            Assert.Equal("One.cs", group.Active?.Name);
            Ui.Press(window, window.Actions.Keymap.ShortcutsFor(AppActions.NextTab)[0].Notation());
            Assert.Equal("Two.cs", group.Active?.Name);

            Rect one = Placed(window, Tab(group, "One.cs"));
            Rect three = Placed(window, Tab(group, "Three.cs"));
            Ui.Drag(window, one.Center, new Point(three.Center.X, three.Bottom - 2));
            Assert.True(group.Tabs.Select(t => t.Document.Name).SequenceEqual(["Two.cs", "Three.cs", "One.cs"]),
                $"One.cs dragged down past Three.cs left the column as {string.Join(", ", group.Tabs.Select(t => t.Document.Name))}");

            window.ApplySettings(window.Settings with { EditorTabs = EditorTabsPlace.Right });
            Ui.Settle();
            strip = Placed(window, group.TabStrip);
            editor = Placed(window, group.Editor);
            Assert.True(strip.X >= editor.Right - 1, $"Set to the right, the tabs stand at {strip}, not right of the editor at {editor}");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_themes_editor_tabs_place_is_the_default_the_Settings_row_overrides_and_Theme_default_follows_again()
    {
        string name = Kept("""{"editorTabs": "right"}""");
        MainWindow window = Open();
        try
        {
            window.ApplySettings(window.Settings with { Look = new SourcedTheme(ThemeSource.Added.Name, name) });
            Ui.Settle();
            EditorGroup group = window.EditorArea.ActiveGroup;
            Assert.True(group.TabsPlace == EditorTabsPlace.Right && Placed(window, group.TabStrip).X >= Placed(window, group.Editor).Right - 1,
                $"The theme gives the tabs the right, where they stand {group.TabsPlace} at {Placed(window, group.TabStrip)}");

            SettingsDialog dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            ComboBox row = SettingsTests.Editor<ComboBox>(dialog, "Editor tabs");
            Assert.Equal("Theme default (Right)", row.SelectedItem as string);
            row.SelectedItem = "Top";
            dialog.Ok();
            Ui.Settle();
            Assert.True(window.Settings.EditorTabs == EditorTabsPlace.Top && group.TabsPlace == EditorTabsPlace.Top,
                $"The person chose Top in Settings over the theme's right, and the tabs stand {group.TabsPlace}");

            dialog = window.ShowSettings();
            dialog.Page = SettingsDialog.Appearance;
            Ui.Settle();
            SettingsTests.Editor<ComboBox>(dialog, "Editor tabs").SelectedIndex = 0;
            dialog.Ok();
            Ui.Settle();
            Assert.True(window.Settings.EditorTabs is null && group.TabsPlace == EditorTabsPlace.Right,
                $"Theme default chosen again, the tabs stand {group.TabsPlace} where the theme gives the right");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>A theme kept as one a person added, its layout as given; its name.</summary>
    private static string Kept(string layout)
    {
        string name = $"Arranged {Guid.NewGuid():N}";
        string json = $$$"""{"format": 1, "name": "{{{name}}}", "layout": {{{layout}}} }""";
        Assert.Null(KeptThemes.Keep(ThemeSource.Added, false, name, Encoding.UTF8.GetBytes(json)));
        return name;
    }

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder);
        foreach (string name in (string[])["One.cs", "Two.cs", "Three.cs"])
        {
            window.OpenFile(Path.Combine(folder, name));
        }
        Ui.Settle();
        return window;
    }

    private static Border[] Tabs(EditorGroup group) =>
        [.. group.TabStrip.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains(StyleClasses.Tab) && b.IsEffectivelyVisible)];

    private static Border Tab(EditorGroup group, string name) =>
        Tabs(group).Single(b => b.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == name));

    private static Rect Placed(Visual within, Visual control) => new(control.TranslatePoint(default, within) ?? default, control.Bounds.Size);
}
