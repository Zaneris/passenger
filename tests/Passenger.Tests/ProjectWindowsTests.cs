using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Navigation;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Several solutions open at once in one Passenger, a window each, as Rider's frames: each window has its own
/// Explorer, language server, builds and runs and layout; Open asks This Window or New Window (or follows Settings' Open
/// project in); the Window menu lists the windows and Next and Previous Solution Window step between them; quitting closes
/// them all and a start reopens each, while one closed alone stays closed; and the settings are the process's, a change in
/// one window followed by every other and kept whole in settings.json, another Passenger's too. One debugs through the
/// window, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class ProjectWindowsTests : IDisposable
{
    private readonly string settings = TestFolders.Create("windows-state-");
    private readonly string alpha = TestFolders.Create("alpha-");
    private readonly string beta = TestFolders.Create("beta-");

    public void Dispose()
    {
        TestFolders.Delete(settings);
        TestFolders.Delete(alpha);
        TestFolders.Delete(beta);
    }

    [AvaloniaFact]
    public void Two_solutions_open_in_windows_of_their_own_each_with_its_explorer_language_server_colours_diagnostics_build_and_run()
    {
        Project(alpha, "Alpha");
        Project(beta, "Beta");
        Restore.Run(alpha);
        Restore.Run(beta);
        MainWindow first = Started(LaunchOptions.Empty with { Folder = alpha });
        first.OpenFile("Program.cs");
        MainWindow second = InNewWindow(first, beta);
        second.OpenFile("Program.cs");
        Ui.Settle();

        Assert.Equal([first, second], PassengerApp.Windows);
        Assert.Equal((PathNames.Canonical(alpha), PathNames.Canonical(beta)), (first.Folder, second.Folder));
        Assert.True(first.ExplorerView.Tree.Rows.Any(r => r.Name == "Alpha.csproj") && !first.ExplorerView.Tree.Rows.Any(r => r.Name == "Beta.csproj"),
            $"The first window's Explorer does not show its own project alone: [{string.Join(", ", first.ExplorerView.Tree.Rows.Select(r => r.Name))}]");
        Assert.True(second.ExplorerView.Tree.Rows.Any(r => r.Name == "Beta.csproj") && !second.ExplorerView.Tree.Rows.Any(r => r.Name == "Alpha.csproj"),
            $"The second window's Explorer does not show its own project alone: [{string.Join(", ", second.ExplorerView.Tree.Rows.Select(r => r.Name))}]");

        // A language server each, on its own project, colouring and marking its own file.
        Lsp.RoslynSession alphaServer = first.CSharpService?.Session ?? throw new InvalidOperationException($"No language server for the first window: {first.CSharpService?.State}");
        Lsp.RoslynSession betaServer = second.CSharpService?.Session ?? throw new InvalidOperationException($"No language server for the second window: {second.CSharpService?.State}");
        Assert.NotEqual(alphaServer.Server.ProcessId, betaServer.Server.ProcessId);
        Assert.Equal([PathNames.Canonical(Path.Combine(alpha, "Alpha.csproj"))], alphaServer.Projects);
        Assert.Equal([PathNames.Canonical(Path.Combine(beta, "Beta.csproj"))], betaServer.Projects);
        Coloured(first, "alphaTotal");
        Coloured(second, "betaTotal");
        Ui.WaitFor(() => Marked(first, "alphaUnused") && Marked(second, "betaUnused"), "each window's warning on its own unused local");
        Assert.False(Marked(first, "betaUnused") || Marked(second, "alphaUnused"), "A window marked the other window's file");

        // A build and a run each, in each window's own Build and Run windows.
        BuildOutcome builtAlpha = Ui.Await(first.BuildAsync(), "the first window's build", TimeSpan.FromMinutes(3));
        BuildOutcome builtBeta = Ui.Await(second.BuildAsync(), "the second window's build", TimeSpan.FromMinutes(3));
        Assert.True(builtAlpha.Status == BuildStatus.Succeeded && builtBeta.Status == BuildStatus.Succeeded, $"{builtAlpha.Message}; {builtBeta.Message}");
        Assert.Contains("Alpha", first.BottomPanel.Build.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Beta.csproj", first.BottomPanel.Build.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.True(first.Perform(AppActions.RunProgram) && second.Perform(AppActions.RunProgram), "Run could not run in each window");
        Ui.WaitFor(() => Output(first).Contains("Process finished", StringComparison.Ordinal) && Output(second).Contains("Process finished", StringComparison.Ordinal),
            "both programs to run and finish", TimeSpan.FromMinutes(3));
        Assert.True(Output(first).Contains("alpha runs 6", StringComparison.Ordinal) && !Output(first).Contains("beta runs", StringComparison.Ordinal),
            $"The first window's Run window shows:\n{Output(first)}");
        Assert.True(Output(second).Contains("beta runs 6", StringComparison.Ordinal) && !Output(second).Contains("alpha runs", StringComparison.Ordinal),
            $"The second window's Run window shows:\n{Output(second)}");
        second.Close();
        first.Close();
    }

    /// <summary>A folder's program running, another of its programs paused at a breakpoint, its build's warning in
    /// Problems; another folder opened in the same window: both programs are gone, the Run, Debug and Problems windows show
    /// nothing of the first folder, and Run runs the second folder's own configuration rather than saying one runs.</summary>
    [AvaloniaFact]
    public void Opening_another_folder_in_this_window_ends_what_the_first_ran_and_Run_runs_the_seconds_own()
    {
        File.WriteAllText(Path.Combine(alpha, "Alpha.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n"
            + "  <Target Name=\"Warn\" BeforeTargets=\"Build\">\n    <Warning Code=\"ALPHA1\" Text=\"alpha warns at every build\" />\n  </Target>\n</Project>\n");
        string program = Path.Combine(alpha, "Program.cs");
        File.WriteAllText(program, "string name = System.Diagnostics.Debugger.IsAttached ? \"debugged.pid\" : \"run.pid\";\n"
            + $"File.WriteAllText(Path.Combine(@\"{alpha}\", name), Environment.ProcessId.ToString());\n"
            + "Console.WriteLine(\"alpha runs\");\n"
            + "Thread.Sleep(Timeout.Infinite);\n");
        Project(beta, "Beta");
        Restore.Run(alpha);
        Restore.Run(beta);
        MainWindow window = Started(LaunchOptions.Empty with { Folder = alpha });
        int running = 0;
        int debugged = 0;
        try
        {
            Assert.True(window.Perform(AppActions.RunProgram), "Run could not run in the first folder");
            running = Processes.WrittenId(Path.Combine(alpha, "run.pid"), "the first folder's program to run");
            Ui.WaitFor(() => window.BottomPanel.Problems.Problems.Any(p => p.Message.Contains("alpha warns", StringComparison.Ordinal)), "the first folder's build warning in Problems");
            Ui.WaitFor(() => Output(window).Contains("alpha runs", StringComparison.Ordinal), "the first folder's program's output in the Run window");
            window.Debugger.Breakpoints.Toggle(PathNames.Canonical(program), 3);
            Assert.True(window.Perform(AppActions.Debug), "Debug could not run in the first folder");
            debugged = Processes.WrittenId(Path.Combine(alpha, "debugged.pid"), "the first folder's program to start under the debugger");
            Ui.WaitFor(() => window.Debugger.Session is { State: Passenger.App.Debugging.DebugState.Paused } && window.BottomPanel.Debug.Frames.Count > 0,
                "the debugged program to stop at its breakpoint", TimeSpan.FromMinutes(2));

            // This Window: the first folder's session ends before the second's begins.
            Assert.Null(window.Open(beta));
            Press(Asked(window), OpenProjectDialog.ThisWindow);
            Ui.WaitFor(() => window.Folder == PathNames.Canonical(beta), "the second folder to open in the window");
            Assert.True(Processes.Gone(running), "The first folder's program still runs after the second folder opened in its window");
            Assert.True(Processes.Gone(debugged), "The first folder's debugged program still runs after the second folder opened in its window");
            Assert.False(window.Runner.IsRunning || window.Debugger.IsDebugging, "The window still runs or debugs the first folder's program");
            Assert.Equal(string.Empty, Output(window));
            Assert.Equal(string.Empty, window.BottomPanel.Debug.Console.Document.Snapshot.ToString());
            Assert.Empty(window.BottomPanel.Debug.Frames);
            Assert.Empty(window.BottomPanel.Problems.Problems);

            // Run is the second folder's, which runs at once rather than saying the first's still does.
            Assert.Equal("Beta", window.Configuration?.Name);
            Assert.True(window.Perform(AppActions.RunProgram), "Run could not run in the second folder");
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the second folder's program to run", TimeSpan.FromMinutes(3));
            Assert.True(Output(window).Contains("beta runs 6", StringComparison.Ordinal), $"Run ran something other than the second folder's program:\n{Output(window)}");
        }
        finally
        {
            Processes.Kill(running);
            Processes.Kill(debugged);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Open_asks_This_Window_or_New_Window_and_Dont_ask_again_makes_the_answer_the_setting_every_window_follows()
    {
        string gamma = TestFolders.Create("gamma-");
        MainWindow window = Started(LaunchOptions.Empty with { Folder = alpha });

        // Rider's question, its default New Window; Cancel leaves the window as it was.
        Assert.Null(window.Open(beta));
        OpenProjectDialog asked = Asked(window);
        Assert.Equal($"Where would you like to open the solution '{Path.GetFileName(beta)}'?", asked.Question);
        Assert.Equal(OperatingSystem.IsMacOS()
            ? ["Cancel", OpenProjectDialog.ThisWindow, OpenProjectDialog.NewWindow]
            : [OpenProjectDialog.NewWindow, OpenProjectDialog.ThisWindow, "Cancel"], Buttons(asked).Select(b => (string?)b.Content));
        DialogButtonsTests.Check(asked);
        Assert.Equal(OpenProjectDialog.NewWindow, (string?)Assert.Single(Buttons(asked), b => b.IsDefault).Content);
        Press(asked, "Cancel");
        Assert.True(window.Folder == PathNames.Canonical(alpha) && PassengerApp.Windows.Count == 1, "Cancel opened the folder anyway");

        // This Window: the folder takes this one's place.
        window.Open(beta);
        Press(Asked(window), OpenProjectDialog.ThisWindow);
        Assert.Equal(PathNames.Canonical(beta), window.Folder);
        Assert.Equal([window], PassengerApp.Windows);

        // New Window: the folder in a window of its own, in front, this one keeping its own.
        window.Open(alpha);
        Press(Asked(window), OpenProjectDialog.NewWindow);
        MainWindow other = Assert.Single(PassengerApp.Windows, w => w != window);
        Assert.Equal((PathNames.Canonical(beta), PathNames.Canonical(alpha)), (window.Folder, other.Folder));
        Assert.Equal(other, PassengerApp.Front);

        // Opened again from either, a folder another window has is brought forward there, unasked.
        PassengerApp.BringForward(window);
        Assert.Null(window.Open(alpha));
        Assert.Null(window.Asking);
        Assert.Equal(other, PassengerApp.Front);
        Assert.Equal(2, PassengerApp.Windows.Count);

        // Don't ask again: the answer is Settings' Open project in, which every window follows, asking no more.
        window.Open(gamma);
        OpenProjectDialog again = Asked(window);
        again.GetVisualDescendants().OfType<CheckBox>().Single(c => (string?)c.Content == "Don't ask again").IsChecked = true;
        Press(again, OpenProjectDialog.ThisWindow);
        Assert.Equal(PathNames.Canonical(gamma), window.Folder);
        Assert.Equal((OpenProjectIn.CurrentWindow, OpenProjectIn.CurrentWindow), (window.Settings.OpenProjectIn, other.Settings.OpenProjectIn));
        Assert.Null(other.Open(beta));
        Assert.Null(other.Asking);
        Assert.Equal(PathNames.Canonical(beta), other.Folder);
        Assert.Equal(2, PassengerApp.Windows.Count);

        // Settings' System Settings shows it, as Rider's Project section does; New window there opens each in its own.
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "open project";
        Ui.Settle();
        Assert.Equal(SettingsDialog.SystemPage, dialog.Page);
        Assert.Contains("Open project in", dialog.Shown);
        RadioButton[] choices = [.. dialog.GetVisualDescendants().OfType<RadioButton>()];
        Assert.Equal(["New window", "Current window", "Ask"], choices.Select(r => (string?)r.Content));
        Assert.True(choices[1].IsChecked == true, "Settings does not show the answer kept, Current window");
        choices[0].IsChecked = true;
        dialog.Ok();
        Ui.Settle();
        Assert.Null(window.Open(alpha));
        Assert.Equal(3, PassengerApp.Windows.Count);
        Assert.Equal(PathNames.Canonical(alpha), PassengerApp.Front?.Folder);
        foreach (MainWindow each in PassengerApp.Windows.ToList())
        {
            each.Close();
        }
        Assert.Equal("NewWindow", (string?)Read(Path.Combine(settings, "settings.json"))["openProjectIn"]);
        TestFolders.Delete(gamma);
    }

    [AvaloniaFact]
    public void The_Window_menu_lists_the_solution_windows_and_Next_and_Previous_Solution_Window_step_between_them()
    {
        MainWindow first = Started(LaunchOptions.Empty with { Folder = alpha });
        MainWindow second = InNewWindow(first, beta);
        string alphaName = Path.GetFileName(PathNames.Canonical(alpha));
        string betaName = Path.GetFileName(PathNames.Canonical(beta));

        // Rider's keys: those of its Visual Studio keymaps' parents, Mac OS X 10.5+ and $default.
        Assert.Equal(Shortcut.Parse(OperatingSystem.IsMacOS() ? "meta alt BACK_QUOTE" : "control alt CLOSE_BRACKET"), AppActions.Keymap().ShortcutsFor(AppActions.NextProjectWindow)[0]);
        Assert.Equal(Shortcut.Parse(OperatingSystem.IsMacOS() ? "meta shift alt BACK_QUOTE" : "control alt OPEN_BRACKET"), AppActions.Keymap().ShortcutsFor(AppActions.PreviousProjectWindow)[0]);

        // The Window menu ends with Rider's group: the steps, then each window open, this one checked.
        NativeMenuItemBase[] items = [.. Ui.Submenu(first.MenuBar, "Window").Items];
        string[] tail = [.. items.TakeLast(5).Select(i => (i as NativeMenuItem)?.Header ?? "-")];
        Assert.Equal(["Next Solution Window", "Previous Solution Window", "-", alphaName, betaName], tail);
        NativeMenuItem[] listed = [.. items.TakeLast(2).Cast<NativeMenuItem>()];
        Assert.True(listed[0].IsChecked && !listed[1].IsChecked, "The Window menu does not check the window it is in");

        // Chosen there, the other window comes in front, and its own menu checks it.
        listed[1].Command!.Execute(null);
        Ui.Settle();
        Assert.Equal(second, PassengerApp.Front);
        NativeMenuItem[] secondListed = [.. Ui.Submenu(second.MenuBar, "Window").Items.TakeLast(2).Cast<NativeMenuItem>()];
        Assert.True(!secondListed[0].IsChecked && secondListed[1].IsChecked, "The second window's Window menu does not check it");

        // Next Solution Window's keys go on round to the first, Previous's back.
        Ui.Run(second, AppActions.NextProjectWindow);
        Assert.Equal(first, PassengerApp.Front);
        Ui.Run(first, AppActions.NextProjectWindow);
        Assert.Equal(second, PassengerApp.Front);
        Ui.Run(second, AppActions.PreviousProjectWindow);
        Assert.Equal(first, PassengerApp.Front);

        // The toolbar's project widget lists them too, as Rider's Open Solutions.
        ContextMenu widget = first.ShowProjectMenu();
        Ui.Settle();
        string[] shown = [.. widget.Items.OfType<MenuItem>().Select(i => i.Header as string ?? string.Empty)];
        Assert.True(shown.SkipWhile(h => h != "Open Solutions").Skip(1).Take(2).SequenceEqual([alphaName, betaName]),
            $"The project widget does not list the two windows under Open Solutions: [{string.Join(", ", shown)}]");
        widget.Close();

        // With one window left, there is nothing to step to.
        second.Close();
        Assert.False(first.Perform(AppActions.NextProjectWindow));
        first.Close();
    }

    [AvaloniaFact]
    public void Quitting_closes_every_window_and_a_start_reopens_each_on_its_own_layout_but_not_one_closed_alone()
    {
        MainWindow first = Started(LaunchOptions.Empty with { Folder = alpha });
        MainWindow second = InNewWindow(first, beta);
        first.DockHost.Move(first.DockHost["tests"]!, ToolAnchor.RightBottom);
        first.DockHost.Show("tests");
        second.DockHost.Hide("explorer");
        second.DockHost.Show("build");
        Ui.Settle();

        // File, Exit (the application menu's Quit on macOS) closes them all.
        Assert.True(first.Perform(AppActions.Exit));
        Ui.Settle();
        Assert.False(first.IsVisible || second.IsVisible, "Exit left a window open");
        Assert.Empty(PassengerApp.Windows);

        // A start that names nothing reopens both, each in a window of its own on the layout it left.
        MainWindow again = Started(LaunchOptions.Empty);
        MainWindow other = Assert.Single(PassengerApp.Windows, w => w != again);
        Assert.Equal((PathNames.Canonical(alpha), PathNames.Canonical(beta)), (again.Folder, other.Folder));
        Assert.True(again.DockHost["tests"]!.Anchor == ToolAnchor.RightBottom && again.DockHost.IsShown("tests") && again.DockHost.IsShown("explorer"),
            "The first window's layout did not come back with its folder");
        Assert.True(other.DockHost["tests"]!.Anchor != ToolAnchor.RightBottom && !other.DockHost.IsShown("explorer") && other.DockHost.IsShown("build"),
            "The second window's layout did not come back with its folder, or took the first's");
        Assert.Equal(other, PassengerApp.Front);

        // Closed alone while another stays, a window's solution is closed; the last one's is reopened.
        other.Close();
        again.Close();
        MainWindow last = Started(LaunchOptions.Empty);
        Assert.Equal([last], PassengerApp.Windows);
        Assert.Equal(PathNames.Canonical(alpha), last.Folder);
        last.Close();

        // A solution the system hands Passenger as it starts (the Finder's Open) opens in place of what it would reopen.
        PassengerApp.OpenFromSystem([beta]);
        MainWindow handed = Started(LaunchOptions.Empty);
        Assert.Equal([handed], PassengerApp.Windows);
        Assert.Equal(PathNames.Canonical(beta), handed.Folder);

        // Handed one while it runs, the window in front asks where, as Open does.
        PassengerApp.OpenFromSystem([alpha]);
        Press(Asked(handed), OpenProjectDialog.NewWindow);
        Assert.Equal(PathNames.Canonical(alpha), PassengerApp.Front?.Folder);
        Assert.Equal(2, PassengerApp.Windows.Count);
        PassengerApp.Quit();
    }

    [AvaloniaFact]
    public void A_setting_changed_in_one_window_shows_in_the_other_and_both_changes_are_kept_in_settings_json()
    {
        using IDisposable theme = TestsTheme();
        MainWindow first = Started(LaunchOptions.Empty with { Folder = alpha });
        MainWindow second = InNewWindow(first, beta);
        string file = Path.Combine(settings, "settings.json");
        try
        {
            // The second window's Settings, open before the first changed the font, changes only the theme.
            SettingsDialog later = second.ShowSettings();
            SettingsDialog font = first.ShowSettings();
            Ui.Settle();
            font.Change(s => s with { EditorFontSize = 17 });
            font.Ok();
            Ui.Settle();
            Assert.Equal(17, second.EditorArea.CodeEditor.FontSize);
            later.Change(s => s with { Theme = ThemeChoice.Light });
            later.Ok();
            Ui.Settle();
            Assert.True(first.Settings is { EditorFontSize: 17, Theme: ThemeChoice.Light } && second.Settings is { EditorFontSize: 17, Theme: ThemeChoice.Light },
                $"The windows hold {first.Settings.EditorFontSize}, {first.Settings.Theme} and {second.Settings.EditorFontSize}, {second.Settings.Theme}");
            Assert.Equal((17d, 17d), (first.EditorArea.CodeEditor.FontSize, second.EditorArea.CodeEditor.FontSize));
            Assert.Equal(Avalonia.Styling.ThemeVariant.Light, Avalonia.Application.Current!.RequestedThemeVariant);

            // A shortcut changed in one is the other's keymap at once.
            first.ApplySettings(first.Settings with { Shortcuts = new() { [AppActions.GoToFile] = ["meta shift Y"] } });
            Ui.Press(second, "meta shift Y");
            Assert.IsType<GoToFilePopup>(second.Popup);
            Ui.Press(second, "ESCAPE");

            // Written whole: both changes in settings.json.
            Ui.WaitFor(() => File.Exists(file) && Read(file) is { } kept && (double?)kept["editorFontSize"] == 17 && (string?)kept["theme"] == "Light",
                "both changes in settings.json", TimeSpan.FromSeconds(10));

            // Another Passenger's change to the file is followed by every window here.
            JsonObject changed = Read(file);
            changed["terminalFontSize"] = 21;
            string written = Path.Combine(settings, "settings.json.other.tmp");
            File.WriteAllText(written, changed.ToJsonString());
            File.Move(written, file, overwrite: true);
            Ui.WaitFor(() => first.TerminalPanel.View.FontSize == 21 && second.TerminalPanel.View.FontSize == 21,
                "both windows to follow the change another Passenger made to settings.json", TimeSpan.FromSeconds(30));
            Assert.Equal(17, first.Settings.EditorFontSize);
        }
        finally
        {
            first.ApplySettings(first.Settings with { Shortcuts = [] });
            second.Close();
            first.Close();
        }
        JsonObject final = Read(file);
        Assert.True((double?)final["editorFontSize"] == 17 && (string?)final["theme"] == "Light" && (double?)final["terminalFontSize"] == 21,
            $"settings.json lost a change: {final.ToJsonString()}");
    }

    /// <summary>The window a person starts, as the app makes it: its layout and settings kept in the test's state folder,
    /// then the command line taken.</summary>
    private MainWindow Started(LaunchOptions options)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        window.KeepSettings();
        window.Start(options);
        Ui.Settle();
        return window;
    }

    /// <summary>A folder opened from a window over another, answered New Window: the window it opened in.</summary>
    private static MainWindow InNewWindow(MainWindow from, string folder)
    {
        Assert.Null(from.Open(folder));
        Press(Asked(from), OpenProjectDialog.NewWindow);
        return PassengerApp.Windows.Single(w => w.Folder == PathNames.Canonical(folder));
    }

    private static OpenProjectDialog Asked(MainWindow window)
    {
        Ui.Settle();
        return window.Asking ?? throw new InvalidOperationException($"Opening a folder over {window.Folder} did not ask where");
    }

    private static Button[] Buttons(Window dialog) => [.. dialog.GetVisualDescendants().OfType<Button>().Where(b => b is not Avalonia.Controls.Primitives.ToggleButton)];

    private static void Press(Window dialog, string button)
    {
        Buttons(dialog).First(b => (string?)b.Content == button).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Ui.Settle();
    }

    /// <summary>A console project whose Program.cs prints its name with a total, has a local it never uses (a warning) and
    /// a local only the language server colours as one.</summary>
    private static void Project(string folder, string name)
    {
        File.WriteAllText(Path.Combine(folder, $"{name}.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n</Project>\n");
        string lower = name.ToLowerInvariant();
        File.WriteAllText(Path.Combine(folder, "Program.cs"),
            $"int {lower}Total = 2 * 3;\nint {lower}Unused = 1;\nConsole.WriteLine($\"{lower} runs {{{lower}Total}}\");\n");
    }

    /// <summary>Waits for the server's colour on the local's use in the window's file.</summary>
    private static void Coloured(MainWindow window, string local)
    {
        OpenDocument document = window.EditorArea.Active!;
        int use = document.Text.Snapshot.ToString().IndexOf("{" + local, StringComparison.Ordinal) + 1;
        Ui.WaitFor(() => document.Highlighter!.GetSpans(document.Text.Snapshot, use, 1).Any(s => s.Start == use && s.Kind == TokenKind.Local),
            $"the language server's colour on {local} in {window.Folder}");
    }

    private static bool Marked(MainWindow window, string local) =>
        window.EditorArea.Active!.Markers.Any(m => m.Message.Contains($"'{local}'", StringComparison.Ordinal));

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    private static JsonObject Read(string file) => (JsonObject)JsonNode.Parse(File.ReadAllText(file))!;

    private static IDisposable TestsTheme() => new ThemeBack(Avalonia.Application.Current!.RequestedThemeVariant);

    private sealed class ThemeBack(Avalonia.Styling.ThemeVariant? theme) : IDisposable
    {
        public void Dispose() => Avalonia.Application.Current!.RequestedThemeVariant = theme;
    }
}
