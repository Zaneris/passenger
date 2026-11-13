using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Settings;
using Passenger.App.State;

namespace Passenger.Tests;

/// <summary>A theme's default layout as a person meets it: a window on a fresh state opens with the tool windows where the
/// theme docks them, a layout the person arranged wins at the next start, Restore Default Layout goes back to the theme's,
/// and windows standing where a theme put them follow the next theme chosen.</summary>
public sealed class ThemeDefaultLayoutTests : IDisposable
{
    private readonly string folder = TestFolders.Create("theme-layout-");
    private readonly string settings = TestFolders.Create("theme-layout-settings-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    [AvaloniaFact]
    public void A_themes_layout_opens_on_a_fresh_state_a_persons_own_wins_and_Restore_Default_Layout_goes_back_to_the_themes()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        string name = $"Layout {Guid.NewGuid():N}";
        string json = $$$"""
            {"format": 1, "name": "{{{name}}}", "layout": {"places": {"rightTop": ["explorer"], "bottomRight": ["problems", "build"] },
              "open": ["explorer", "problems"], "sizes": {"right": 260, "bottom": 200} } }
            """;
        Assert.Null(KeptThemes.Keep(ThemeSource.Added, false, name, Encoding.UTF8.GetBytes(json)));
        File.WriteAllText(Path.Combine(settings, StateStore.SettingsFile),
            $$"""{ "version": 5, "look": { "source": "{{ThemeSource.Added.Name}}", "name": "{{name}}" } }""");

        MainWindow window = Keeping();
        try
        {
            DockHost dock = window.DockHost;
            Assert.True(Placed(dock, ToolWindowIds.Explorer) == (ToolAnchor.RightTop, 0, true) && Placed(dock, ToolWindowIds.Problems) == (ToolAnchor.BottomRight, 0, true)
                && Placed(dock, ToolWindowIds.Build) == (ToolAnchor.BottomRight, 1, false),
                $"On a fresh state the theme docks the Explorer open at the right and Problems open with Build below, where the window shows {Shown(dock)}");
            Assert.True(Placed(dock, ToolWindowIds.Structure) == (ToolAnchor.LeftBottom, 0, false), $"Structure, which the theme leaves, is not where Passenger puts it: {Shown(dock)}");
            DockLayout sizes = dock.Capture();
            Assert.True((sizes.LeftWidth, sizes.RightWidth, sizes.BottomHeight) == (300, 260, 200),
                $"The areas are {sizes.LeftWidth}, {sizes.RightWidth} and {sizes.BottomHeight} wide and high, where the theme gives the right 260 and the bottom 200, the left Passenger's 300");
            Point explorer = window.ExplorerView.TranslatePoint(default, window) ?? default;
            Point editor = window.EditorArea.TranslatePoint(new Point(window.EditorArea.Bounds.Width, 0), window) ?? default;
            Assert.True(explorer.X >= editor.X, $"The Explorer is drawn at {explorer.X}, left of the editor's right edge at {editor.X}");

            dock.Move(dock[ToolWindowIds.Explorer], ToolAnchor.LeftTop);
            Ui.Settle();
        }
        finally
        {
            window.Close();
            Ui.Settle();
        }

        MainWindow again = Keeping();
        try
        {
            DockHost dock = again.DockHost;
            Assert.True(dock[ToolWindowIds.Explorer] is { Anchor: ToolAnchor.LeftTop, IsShown: true } && Placed(dock, ToolWindowIds.Problems) == (ToolAnchor.BottomRight, 0, true),
                $"The person moved the Explorer to the left, which the next start lost to the theme's layout: {Shown(dock)}");

            again.Actions.Run(AppActions.RestoreDefaultLayout);
            Ui.Settle();
            Assert.True(Placed(dock, ToolWindowIds.Explorer) == (ToolAnchor.RightTop, 0, true) && dock.Capture().RightWidth == 260,
                $"Restore Default Layout did not go back to the theme's, the Explorer at the right: {Shown(dock)}");

            again.ApplySettings(again.Settings with { Look = null });
            Ui.Settle();
            Assert.True(Placed(dock, ToolWindowIds.Explorer) == (ToolAnchor.LeftTop, 0, true) && Placed(dock, ToolWindowIds.Problems).Anchor == ToolAnchor.BottomLeft
                && !dock.IsShown(ToolWindowIds.Problems) && dock.Capture().RightWidth == 300,
                $"Windows standing where the theme put them did not follow Passenger's own theme, chosen after it: {Shown(dock)}");
        }
        finally
        {
            again.ApplySettings(new AppSettings());
            again.Close();
        }
    }

    /// <summary>A window that keeps its state, settings and layout in the test's folder, over the test's project.</summary>
    private MainWindow Keeping()
    {
        MainWindow window = new() { Width = 1400, Height = 900, StateFolder = settings };
        window.Show();
        window.KeepSettings();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    private static (ToolAnchor Anchor, int Order, bool Shown) Placed(DockHost dock, string id) => (dock[id].Anchor, dock[id].Order, dock[id].IsShown);

    private static string Shown(DockHost dock) =>
        string.Join(", ", dock.Windows.Where(w => w.IsShown || w.Id is ToolWindowIds.Explorer or ToolWindowIds.Problems or ToolWindowIds.Build)
            .Select(w => $"{w.Id} {w.Anchor} {w.Order}{(w.IsShown ? " open" : "")}"));
}
