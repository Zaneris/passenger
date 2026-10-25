using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The IDE's own state, in one store in its state folder: a file that does not read is set aside, said in
/// the status bar and logged, never written over; a later build's values and a hand edit out of range stay in the file while the window
/// uses what it can; two windows' changes are both kept; what a person kept before the store is there on the first
/// run; and a folder's tabs (whatever was open before it), layout, breakpoints and bookmarks come back with it.</summary>
public sealed class StateTests : IDisposable
{
    private readonly string folder = TestFolders.Create("folder-");
    private readonly string settings = TestFolders.Create("settings-");

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    [AvaloniaFact]
    public void A_settings_file_a_crash_cut_short_is_set_aside_said_in_the_status_bar_and_logged_and_never_written_over()
    {
        using IDisposable theme = TestsTheme();
        string file = Path.Combine(settings, "settings.json");
        string cut = "{\n  \"version\": 1,\n  \"theme\": \"Light\",\n  \"editorFontSize\": 17,\n  \"shortcuts\": {\n    \"app.editor.spl";
        File.WriteAllText(file, cut);

        MainWindow window = Keeping();
        window.KeepSettings();
        Ui.Settle();
        Assert.Equal(13, window.Settings.EditorFontSize);
        Assert.True(File.Exists(file + ".bad"), "The file that did not read was not set aside: the defaults went over it");
        Assert.Equal(cut, File.ReadAllText(file + ".bad"));
        Assert.False(File.Exists(file), "The defaults were written in place of the file that did not read");
        string log = Ui.IdeLog();
        Assert.Contains("settings.json did not read", log, StringComparison.Ordinal);
        Assert.Contains("set aside as settings.json.bad", log, StringComparison.Ordinal);
        Assert.True(window.Status?.Contains($"settings.json did not read, so its defaults stand: what it held is kept as {file}.bad", StringComparison.Ordinal) == true,
            $"Only the log says the settings were set aside, so the person finds them gone with nothing saying where they went: '{window.Status}'");
        Assert.True(window.StatusPlace?.Name == $"show settings.json.bad in {Passenger.App.FileManager.Name}",
            $"The status bar's word on the settings set aside goes to '{window.StatusPlace?.Name}', not to the file they are kept in");

        window.ApplySettings(window.Settings with { EditorFontSize = 15 });
        window.Close();
        JsonObject written = Read(file);
        Assert.Equal(AppSettings.Upgrades.Count, (int)written["version"]!);
        Assert.Equal(15, (double)written["editorFontSize"]!);
        Assert.Equal(cut, File.ReadAllText(file + ".bad"));
        Assert.Empty(Directory.GetFiles(settings, "*.tmp"));
    }

    [AvaloniaFact]
    public void A_settings_file_a_hand_edit_left_not_reading_is_set_aside_by_the_next_change_and_the_status_bar_says_where_it_went()
    {
        using IDisposable theme = TestsTheme();
        string file = Path.Combine(settings, "settings.json");
        MainWindow window = Keeping();
        window.KeepSettings();
        window.ApplySettings(window.Settings with { EditorFontSize = 15 });
        Ui.WaitFor(() => File.Exists(file), "the settings to be written");

        // Edited by hand while the window is open, a comma too many: the window keeps what it has.
        string broken = "{\n  \"version\": 1,\n  \"editorFontSize\": 18,,\n  \"theme\": \"Light\"\n}\n";
        File.WriteAllText(file, broken);
        Ui.Settle();
        Assert.Equal(15, window.Settings.EditorFontSize);

        window.ApplySettings(window.Settings with { EditorFontSize = 16 });
        Ui.WaitFor(() => File.Exists(file + ".bad"), "the file that did not read to be set aside");
        Assert.Equal(broken, File.ReadAllText(file + ".bad"));
        Ui.WaitFor(() => window.Status?.Contains($"what it held is kept as {file}.bad", StringComparison.Ordinal) == true,
            "the status bar to say the settings were set aside and where");
        window.Close();
    }

    [AvaloniaFact]
    public void A_later_builds_values_and_a_hand_edit_out_of_range_stay_in_the_file_while_the_window_uses_what_it_can()
    {
        using IDisposable theme = TestsTheme();
        string file = Path.Combine(settings, "settings.json");
        File.WriteAllText(file, """
            {
              "version": 7,
              "theme": "Sepia",
              "editorFontSize": 1e30,
              "terminalFontSize": 0,
              "minimap": { "side": "right" }
            }
            """);

        MainWindow window = Keeping();
        window.KeepSettings();
        Assert.Equal(ThemeChoice.System, window.Settings.Theme);
        Assert.Equal(AppSettings.MaxFontSize, window.EditorArea.CodeEditor.FontSize);
        Assert.Equal(AppSettings.MinFontSize, window.TerminalPanel.View.FontSize);
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Change(s => s with { EditorLineSpacing = 1.5 });
        dialog.Ok();
        Ui.Settle();
        window.Close();

        JsonObject written = Read(file);
        Assert.Equal(7, (int)written["version"]!);
        Assert.Equal("Sepia", (string)written["theme"]!);
        Assert.Equal("right", (string)written["minimap"]!["side"]!);
        Assert.Equal(1e30, (double)written["editorFontSize"]!);
        Assert.Equal(1.5, (double)written["editorLineSpacing"]!);
    }

    [AvaloniaFact]
    public void Settings_kept_in_a_dotfiles_folder_through_a_symlink_take_each_change_there_and_the_link_stays()
    {
        using IDisposable theme = TestsTheme();
        string file = Path.Combine(settings, "settings.json");
        string kept = Path.Combine(folder, "dotfiles", "passenger.json");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "{\n  \"version\": 1,\n  \"editorFontSize\": 15\n}\n");
        TestFolders.Link(file, kept);
        MainWindow window = Keeping();
        window.KeepSettings();
        Assert.Equal(15, window.Settings.EditorFontSize);

        window.ApplySettings(window.Settings with { EditorFontSize = 17 });
        window.Close();
        Assert.True(new FileInfo(file).LinkTarget is not null, "Changing a setting replaced the settings' symlink with a file of its own, the dotfiles' copy left behind");
        Assert.Equal(17, (int)Read(kept)["editorFontSize"]!);
    }

    [AvaloniaFact]
    public void What_a_person_kept_before_the_store_is_there_on_the_first_run()
    {
        using IDisposable theme = TestsTheme();
        // As the build before the store left them: no versions, and a folder's workspace in one file of its own.
        string kept = "{\n  \"theme\": \"Light\",\n  \"editorFontSize\": 16\n}";
        File.WriteAllText(Path.Combine(settings, "settings.json"), kept);
        File.WriteAllText(Path.Combine(settings, "layout.json"),
            "{\n  \"windows\": {\n    \"tests\": { \"anchor\": \"RightBottom\", \"order\": 0, \"shown\": true }\n  },\n  \"leftWidth\": 0,\n  \"rightWidth\": 0,\n  \"bottomHeight\": 0\n}");
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n    int x;\n}\n");
        File.WriteAllText(Path.Combine(folder, "Two.cs"), "class Two\n{\n}\n");
        string canonical = PathNames.Canonical(folder);
        string one = Path.Combine(canonical, "One.cs");
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..12];
        string former = Path.Combine(settings, "workspaces", $"{Path.GetFileName(canonical)}-{hash}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(former)!);
        File.WriteAllText(former, new JsonObject
        {
            ["editors"] = new JsonObject { ["tabs"] = new JsonArray(new JsonObject { ["path"] = one, ["line"] = 2, ["column"] = 4 }), ["current"] = true },
            ["recentFiles"] = new JsonArray(one),
            ["unloadedProjects"] = new JsonArray(),
        }.ToJsonString());

        MainWindow window = Keeping();
        window.KeepSettings();
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.Equal((ThemeChoice.Light, 16), (window.Settings.Theme, window.Settings.EditorFontSize));
        Assert.Equal(ToolAnchor.RightBottom, window.DockHost[ToolWindowIds.Tests].Anchor);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Tests));
        Assert.Equal("One.cs", window.EditorArea.Active?.Name);
        Assert.Equal(new TextPosition(2, 4), window.EditorArea.CodeEditor.CaretPosition);
        window.OpenFile(Path.Combine(folder, "Two.cs"));
        window.Close();

        Assert.Equal(kept, File.ReadAllText(Path.Combine(settings, "settings.json")));
        JsonObject workspace = Read(Path.Combine(settings, "workspaces", $"{Path.GetFileName(canonical)}-{hash}", "workspace.json"));
        Assert.Equal(1, (int)workspace["version"]!);
        Assert.Equal(["Two.cs", "One.cs"], workspace["recentFiles"]!.AsArray().Select(f => Path.GetFileName((string)f!)));
        Assert.True(File.Exists(former), "The workspace file an older build reads was taken away");
    }

    [AvaloniaFact]
    public void Breakpoints_and_bookmarks_come_back_when_the_folder_opens_again_and_go_when_another_opens()
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One\n{\n    void M()\n    {\n        int x = 1;\n    }\n}\n");
        File.WriteAllText(Path.Combine(folder, "Two.cs"), "class Two\n{\n}\n");
        MainWindow first = Keeping(folder);
        first.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();
        first.EditorArea.CodeEditor.GoTo(new TextPosition(4, 8));
        Ui.Run(first, AppActions.ToggleBreakpoint);
        LineBreakpoint set = Assert.Single(first.Debugger.Breakpoints.All);
        first.Debugger.Breakpoints.Update(set, enabled: false, suspend: false, condition: "x > 0", hitCondition: null, logMessage: "x is {x}");
        first.EditorArea.CodeEditor.GoTo(new TextPosition(2, 4));
        Ui.Run(first, AppActions.ToggleBookmark);
        first.OpenFile(Path.Combine(folder, "Two.cs"));
        Ui.Settle();
        first.EditorArea.CodeEditor.GoTo(new TextPosition(1, 0));
        Ui.Run(first, AppActions.ToggleBookmark);
        first.Close();

        MainWindow second = Keeping(folder);
        LineBreakpoint kept = Assert.Single(second.Debugger.Breakpoints.All);
        Assert.Equal(("One.cs", 4, false, false, "x > 0", "x is {x}"),
            (Path.GetFileName(kept.Path), kept.Line, kept.Enabled, kept.Suspend, kept.Condition, kept.LogMessage));
        Assert.Equal([("One.cs", 2), ("Two.cs", 1)], second.Bookmarks.All.Select(b => (b.Name, b.Line)));
        second.OpenFile(Path.Combine(folder, "One.cs"));
        Ui.Settle();
        Assert.Equal([2], second.EditorArea.Active!.Bookmarks);
        Assert.Contains(new LineMark(4, LineMarkKind.BreakpointDisabled, Conditional: true, Tracepoint: true), second.EditorArea.CodeEditor.LineMarks);

        string other = TestFolders.Create("other-");
        // A folder's documents are read off the window's thread as it opens, its restore once they land.
        second.OpenFolder(other);
        Ui.Settle();
        Assert.Empty(second.Debugger.Breakpoints.All);
        Assert.Empty(second.Bookmarks.All);
        second.OpenFolder(folder);
        Ui.Settle();
        Assert.Single(second.Debugger.Breakpoints.All);
        Assert.Equal(2, second.Bookmarks.All.Count);
        second.Close();
    }

    /// <summary>A folder's kept documents are read off the window's thread as it opens, so their size never holds the
    /// window: the open's own work on the window's thread allocates no more with 4,000 breakpoints kept than with 40, and
    /// every one comes back once read.</summary>
    [AvaloniaFact]
    public void A_folders_kept_documents_are_read_off_the_windows_thread_as_it_opens_and_all_come_back()
    {
        long Opening(int count)
        {
            string open = PathNames.Canonical(TestFolders.Create("kept-"));
            string kept = Passenger.App.State.ProjectState.FolderOf(settings, open);
            Directory.CreateDirectory(kept);
            JsonArray breakpoints = [.. Enumerable.Range(0, count).Select(i => (JsonNode)new JsonObject
            {
                ["path"] = Path.Combine(open, $"File{i % 100}.cs"),
                ["line"] = i / 100,
                ["condition"] = $"count == {i}",
            })];
            File.WriteAllText(Path.Combine(kept, "breakpoints.json"), new JsonObject { ["breakpoints"] = breakpoints }.ToJsonString());
            MainWindow window = Keeping();
            long before = GC.GetAllocatedBytesForCurrentThread();
            window.OpenFolder(open);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Ui.Settle();
            int restored = window.Debugger.Breakpoints.All.Count();
            Assert.True(restored == count, $"{restored} of the {count} breakpoints kept came back as the folder opened");
            window.Close();
            return allocated;
        }

        // The first open in the process pays for what it loads once, which neither measure should carry.
        Opening(1);
        long few = Opening(40);
        long many = Opening(4000);
        Assert.True(many < few + (256 * 1024),
            $"Opening a folder with 4,000 breakpoints kept allocated {many / 1024} KB on the window's thread, {few / 1024} KB with 40: its documents are read there");
    }

    /// <summary>A file open with no folder never keeps the next folder's tabs from coming back, and leaving that folder keeps
    /// its own tabs, not the file open before it.</summary>
    [AvaloniaFact]
    public void A_folder_opens_with_its_own_tabs_whatever_was_open_before_it_which_stays_beside_them()
    {
        File.WriteAllText(Path.Combine(folder, "Shop.cs"), "class Shop { }\n");
        File.WriteAllText(Path.Combine(folder, "Bank.cs"), "class Bank { }\n");
        MainWindow window = Keeping(folder);
        window.OpenFile(Path.Combine(folder, "Shop.cs"));
        window.OpenFile(Path.Combine(folder, "Bank.cs"));
        Ui.Settle();
        window.Close();
        Ui.Settle();

        window = Keeping();
        AboutDialog about = DialogButtonsTests.Check(window.ShowAbout());
        Ui.ClickButton(about, AboutDialog.LicenseLink);
        string licence = PathNames.Canonical(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt"));
        Assert.Equal(licence, window.EditorArea.Active?.Path);

        window.OpenFolder(folder);
        Ui.Settle();
        string[] tabs = Tabs(window);
        Assert.True(tabs.SequenceEqual(["Shop.cs", "Bank.cs", "LICENSE.txt"]),
            $"The folder opened on [{string.Join(", ", tabs)}], not its own Shop.cs and Bank.cs with the licence open before it after them");
        Assert.Equal(licence, window.EditorArea.Active?.Path);

        Assert.Null(window.CloseSolution());
        Ui.Settle();
        Assert.Empty(window.EditorArea.Documents);
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.True(Tabs(window).SequenceEqual(tabs), $"Opened again, the folder has [{string.Join(", ", Tabs(window))}], not the [{string.Join(", ", tabs)}] it was left with");
        Assert.Equal(licence, window.EditorArea.Active?.Path);
        window.Close();
    }

    [AvaloniaFact]
    public void Each_folder_opens_on_its_own_layout_and_one_without_on_the_one_there_was()
    {
        string other = TestFolders.Create("other-");
        MainWindow first = Keeping(folder);
        first.DockHost.Move(first.DockHost[ToolWindowIds.Tests], ToolAnchor.RightBottom);
        first.DockHost.Show(ToolWindowIds.Tests);
        first.OpenFolder(other);
        Ui.Settle();
        Assert.Equal(ToolAnchor.RightBottom, first.DockHost[ToolWindowIds.Tests].Anchor);
        first.DockHost.Move(first.DockHost[ToolWindowIds.Tests], ToolAnchor.LeftBottom);
        first.OpenFolder(folder);
        Ui.Settle();
        Assert.Equal(ToolAnchor.RightBottom, first.DockHost[ToolWindowIds.Tests].Anchor);
        first.Close();

        MainWindow second = Keeping();
        Assert.Equal(ToolAnchor.RightBottom, second.DockHost[ToolWindowIds.Tests].Anchor);
        second.OpenFolder(other);
        Ui.Settle();
        Assert.Equal(ToolAnchor.LeftBottom, second.DockHost[ToolWindowIds.Tests].Anchor);
        Assert.True(second.DockHost.IsShown(ToolWindowIds.Tests));
        second.Close();
    }

    [AvaloniaFact]
    public void A_layout_kept_for_a_screen_no_longer_there_or_by_a_later_build_opens_every_tool_window_where_it_can_be_reached()
    {
        File.WriteAllText(Path.Combine(settings, "layout.json"), """
            {
              "version": 1,
              "windows": {
                "debug": { "anchor": "BottomLeft", "order": 0, "shown": true, "mode": "Floating", "bounds": [-40000, -40000, 600, 400] },
                "run": { "anchor": "BottomLeft", "order": 1, "shown": true, "mode": "Floating", "bounds": [100, 100, 700, 90000] },
                "tests": { "anchor": "Sideways", "order": 0, "shown": false },
                "problems": { "anchor": 42, "order": 0, "shown": false },
                "structure": { "anchor": "RightTop", "order": 0, "shown": true, "mode": "Tabbed" },
                "later": { "anchor": "LeftBottom", "order": 0, "shown": true }
              }
            }
            """);

        MainWindow window = Keeping();
        Ui.Settle();
        Avalonia.Platform.Screen[] screens = [.. window.Screens.All];
        foreach (Avalonia.Controls.Window floating in window.OwnedWindows)
        {
            Avalonia.PixelRect at = new(floating.Position, Avalonia.PixelSize.FromSize(floating.Bounds.Size, 1));
            Assert.True(screens.Any(s => s.WorkingArea.Contains(at.TopLeft) && floating.Bounds.Height * s.Scaling <= s.WorkingArea.Height),
                $"{floating.Title} opened at {at}, off every screen: [{string.Join(", ", screens.Select(s => s.WorkingArea))}]");
        }
        Assert.Equal(["Debug", "Run"], window.OwnedWindows.Select(w => w.Title).Order());
        // A place or a mode this build does not have is its default: where the window was added, docked.
        Assert.Equal((ToolAnchor.BottomLeft, ToolAnchor.BottomLeft), (window.DockHost[ToolWindowIds.Tests].Anchor, window.DockHost[ToolWindowIds.Problems].Anchor));
        Assert.Equal((ToolAnchor.RightTop, ToolMode.Docked, true), (window.DockHost[ToolWindowIds.Structure].Anchor, window.DockHost[ToolWindowIds.Structure].Mode, window.DockHost.IsShown(ToolWindowIds.Structure)));
        // A window a later build has is passed over, and kept for it.
        Assert.True(window.DockHost.Capture().Windows.ContainsKey("later"), "The later build's window was dropped from the layout kept");
        window.Close();
    }

    /// <summary>The window a person uses, as the app makes it: one that keeps its layout and folders.</summary>
    private MainWindow Keeping(string? open = null)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        if (open is not null)
        {
            window.OpenFolder(open);
        }
        Ui.Settle();
        return window;
    }

    private static JsonObject Read(string file) => (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;

    /// <summary>Every tab's file name, group after group, as the strips show them.</summary>
    private static string[] Tabs(MainWindow window) => [.. window.EditorArea.Groups.SelectMany(g => g.Tabs).Select(t => t.Document.Name)];

    /// <summary>Puts the tests' theme back once a window that keeps its settings has put the person's in force.</summary>
    private static IDisposable TestsTheme() => new ThemeBack(Avalonia.Application.Current!.RequestedThemeVariant);

    private sealed class ThemeBack(Avalonia.Styling.ThemeVariant? theme) : IDisposable
    {
        public void Dispose() => Avalonia.Application.Current!.RequestedThemeVariant = theme;
    }
}
