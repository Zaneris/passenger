using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App.Editors;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Docking;
using Passenger.App.Settings;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The IDE's own state, in one store in its state folder: a file that does not read is set aside, said in
/// the status bar and logged, never written over, and one read again that does not read said too; a value of the wrong
/// kind is passed over alone; a later build's values and a hand edit out of range stay in the file while the window
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

    [Gate]
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
    public void A_settings_value_of_the_wrong_kind_is_passed_over_alone_and_a_hand_edit_that_does_not_read_is_said_as_it_is_read_again()
    {
        string file = Path.Combine(settings, "settings.json");
        File.WriteAllText(file, $"{{\n  \"version\": {AppSettings.Upgrades.Count},\n  \"editorFontSize\": \"16\",\n  \"terminalFontSize\": 15,\n  \"editorLineSpacing\": 1.5\n}}\n");
        MainWindow window = Keeping();
        window.KeepSettings();
        Ui.Settle();
        Assert.True((window.Settings.EditorFontSize, window.Settings.TerminalFontSize, window.Settings.EditorLineSpacing) == (13, 15, 1.5),
            $"A font size written as text took the file's other settings with it: the editor's font {window.Settings.EditorFontSize}, the terminal's {window.Settings.TerminalFontSize}, the line spacing {window.Settings.EditorLineSpacing}");
        Assert.False(File.Exists(file + ".bad"), "settings.json was set aside for one value of the wrong kind");
        Assert.True(window.Status == "settings.json's editorFontSize is \"16\", where a number goes: its default stands",
            $"The status bar says \"{window.Status}\", not which value was passed over and why");
        Assert.True(window.StatusPlace?.Name == $"show settings.json in {Passenger.App.FileManager.Name}",
            $"The status bar's word on the value passed over goes to '{window.StatusPlace?.Name}', not to settings.json");

        // Edited by hand while the window is open, a comma left out: the window keeps what it has, and says why.
        string broken = "{\n  \"version\": 1,\n  \"terminalFontSize\": 17\n  \"editorLineSpacing\": 1.5\n}\n";
        File.WriteAllText(file, broken);
        Ui.WaitFor(() => window.Status?.StartsWith("settings.json does not read (", StringComparison.Ordinal) == true,
            () => $"the status bar to say settings.json, read again, does not read (it says \"{window.Status}\")");
        Assert.True(window.Status!.EndsWith(", at line 4): the values in force stand until it reads", StringComparison.Ordinal),
            $"The status bar says \"{window.Status}\", not where settings.json stops reading and that the values in force stand");
        Assert.Equal(15, window.Settings.TerminalFontSize);

        window.ApplySettings(window.Settings with { EditorFontSize = 16 });
        Ui.WaitFor(() => File.Exists(file + ".bad"), "the file that did not read to be set aside");
        Ui.WaitFor(() => window.Status?.Contains($"settings.json did not read, so what this Passenger has in force is written in its place: what it held is kept as {file}.bad", StringComparison.Ordinal) == true,
            () => $"the status bar to say the values in force were written in place of the file set aside (it says \"{window.Status}\")");
        window.Close();
        JsonObject written = Read(file);
        Assert.True(((double)written["editorFontSize"]!, (double)written["terminalFontSize"]!, (double)written["editorLineSpacing"]!) == (16, 15, 1.5),
            $"settings.json written in place of the one set aside does not hold the values in force: {written.ToJsonString()}");
        Assert.Equal(broken, File.ReadAllText(file + ".bad"));
    }

    [Gate]
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

    [Gate]
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

    /// <summary>A change to the settings made while another program holds settings.json without sharing its deletion (on
    /// Windows another Passenger's read or a scanner, which a move over the file waits out; elsewhere, where an open file is
    /// moved over all the same, its folder closed to writes) is tried again until it lands, reaching the file once let go.</summary>
    [AvaloniaFact]
    public void A_change_made_while_settings_json_is_held_reaches_the_file_once_it_is_let_go()
    {
        using IDisposable theme = TestsTheme();
        string file = Path.Combine(settings, "settings.json");
        MainWindow window = Keeping();
        window.KeepSettings();
        window.ApplySettings(window.Settings with { EditorFontSize = 15 });
        Ui.WaitFor(() => FontKept(file) == 15, "the settings to be written");

        using (IDisposable held = Held(file))
        {
            window.ApplySettings(window.Settings with { EditorFontSize = 16 });
            Ui.WaitFor(() => Ui.IdeLog().Contains($"{file} could not be written", StringComparison.Ordinal),
                () => $"the change's write to fail while settings.json is held (it holds font {FontKept(file)})");
            Assert.Equal(15, FontKept(file));
        }
        Ui.WaitFor(() => FontKept(file) == 16,
            () => $"the change made while settings.json was held to reach it once let go: it holds font {FontKept(file)}, so the next start comes up without the change");
        window.Close();
        Assert.Equal(16, FontKept(file));
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

    [Gate]
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

    /// <summary>A folder's documents are held in the store only while a window has the folder open: left again and again, by
    /// Close Solution and by another folder's open, the store keeps none of them, what the window kept of the folder written
    /// as it goes; two windows on the folder share them until the last lets them go.</summary>
    [AvaloniaFact]
    public void A_folders_documents_leave_the_store_once_no_window_has_the_folder_open()
    {
        string other = TestFolders.Create("other-");
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        string open = PathNames.Canonical(folder);
        string workspace = Path.Combine(ProjectState.FolderOf(settings, open), "workspace.json");
        MainWindow first = Keeping();
        StateStore store = StateStore.Open(settings);
        try
        {
            for (int round = 1; round <= 4; round++)
            {
                first.OpenFolder(folder);
                Ui.Settle();
                Assert.True(store.Folders.Contains(open), $"The folder open in round {round} has no documents in the store");
                first.OpenFile(Path.Combine(folder, "One.cs"));
                Ui.Settle();
                string leaving = round % 2 == 1 ? "Close Solution" : "another folder's open";
                if (round % 2 == 1)
                {
                    Assert.Null(first.CloseSolution());
                }
                else
                {
                    first.OpenFolder(other);
                }
                Assert.True(Ui.Shared(workspace).Contains("One.cs", StringComparison.Ordinal),
                    $"The folder left by {leaving} in round {round} had its editors written a moment later at best, so a close then loses them:\n{Ui.Shared(workspace)}");
                Ui.Settle();
                Assert.True(!store.Folders.Contains(open),
                    $"The folder left by {leaving} in round {round} still has its documents in the store: [{string.Join(", ", store.Folders)}]");
            }

            MainWindow second = Keeping(folder);
            first.OpenFolder(folder);
            Ui.Settle();
            Assert.Null(first.CloseSolution());
            Ui.Settle();
            Assert.True(store.Folders.Contains(open), "The folder's documents left the store while another window still has it open");
            second.Close();
            Ui.Settle();
            Assert.True(!store.Folders.Contains(open), $"The folder's documents stay in the store with no window left on it: [{string.Join(", ", store.Folders)}]");
        }
        finally
        {
            store.Release();
            first.Close();
        }
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
    [Gate]
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

    [Gate]
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

    [Gate]
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

    /// <summary>The editor's font settings.json holds, read beside a program holding it; none while it holds none.</summary>
    private static double? FontKept(string file) =>
        Ui.Shared(file) is { Length: > 0 } text ? (double?)JsonNode.Parse(text)?["editorFontSize"] : null;

    /// <summary>settings.json held as another program holds it: on Windows open without its deletion shared, so a move over
    /// it is refused; elsewhere, where a move over an open file goes ahead, its folder closed to writes.</summary>
    private IDisposable Held(string file)
    {
        return OperatingSystem.IsWindows() ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : ClosedToWrites(settings);
    }

    /// <summary>A folder closed to writes until let go.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static IDisposable ClosedToWrites(string folder)
    {
        UnixFileMode was = File.GetUnixFileMode(folder);
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Opened opened = new(folder, was);
        string probe = Path.Combine(folder, "probe");
        try
        {
            File.WriteAllText(probe, string.Empty);
        }
        catch (UnauthorizedAccessException)
        {
            return opened;
        }
        File.Delete(probe);
        opened.Dispose();
        Assert.Skip("A folder closed to writes takes them all the same here (the tests run as root)");
        return opened;
    }

    /// <summary>A folder's mode put back as it is let go.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private sealed class Opened(string folder, UnixFileMode was) : IDisposable
    {
        public void Dispose() => File.SetUnixFileMode(folder, was);
    }

    /// <summary>Every tab's file name, group after group, as the strips show them.</summary>
    private static string[] Tabs(MainWindow window) => [.. window.EditorArea.Groups.SelectMany(g => g.Tabs).Select(t => t.Document.Name)];

    /// <summary>Puts the tests' theme back once a window that keeps its settings has put the person's in force.</summary>
    private static IDisposable TestsTheme() => new ThemeBack(Avalonia.Application.Current!.RequestedThemeVariant);

    private sealed class ThemeBack(Avalonia.Styling.ThemeVariant? theme) : IDisposable
    {
        public void Dispose() => Avalonia.Application.Current!.RequestedThemeVariant = theme;
    }
}
