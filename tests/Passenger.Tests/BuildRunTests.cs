using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Building and running a solution from the window, with the platform's own shortcuts: the
/// build's errors arrive in Problems where they point, and a run's output in the Run window.</summary>
public sealed class BuildRunTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly RestoredFolders folders;

    /// <summary>The test's own solution, once it has taken one.</summary>
    private string? taken;

    public BuildRunTests(RestoredFolders folders) => this.folders = folders;

    public void Dispose()
    {
        if (taken is not null)
        {
            TestFolders.Delete(taken);
        }
    }

    /// <summary>The test's own solution of one console project, Hello, restored: taken the first time it is asked for, so a
    /// test that needs none (one of its own, or an empty folder) restores none.</summary>
    private string Folder => taken ??= folders.Take("build-", made =>
    {
        TestFolders.Write(made, "Sample.slnx", "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/Hello/Hello.csproj\" />\n  </Folder>\n</Solution>\n");
        TestFolders.Write(made, "src/Hello/Hello.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(made, "src/Hello/Program.cs", "Console.WriteLine(\"Hello from the run\");\n");
        return Path.Combine(made, "Sample.slnx");
    });

    /// <summary>A solution opened by name among the folder's several, its project's framework set by the repository,
    /// built before: the window opens that solution and keeps it as it reads it again; the project shows its framework,
    /// unloaded is dimmed and leaves the run configurations until reloaded; and the project host's design-time build of
    /// it writes no apphost, which the SDK makes (and on macOS signs) in Compile's dependencies once the assembly is
    /// there, so a build begun beside it could have both make and sign that one file at once.</summary>
    [AvaloniaFact]
    public void A_solution_opened_by_name_opens_that_one_shows_its_projects_framework_unloads_it_and_its_design_time_build_leaves_the_apphost()
    {
        Write("Zebra.slnx", "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/Hello/Hello.csproj\" />\n  </Folder>\n</Solution>\n");
        Write("Directory.Build.props", $"<Project>\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        Write("src/Hello/Hello.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n</Project>\n");
        string project = Path.Combine(Folder, "src", "Hello");
        Passenger.Core.ChildResult built = Task.Run(() => Passenger.Core.ChildProcesses.RunAsync(
            Passenger.Core.ChildProcesses.DotnetCommand(["build", Path.Combine(project, "Hello.csproj"), "-nologo"]), limit: Restore.Limit)).GetAwaiter().GetResult();
        Assert.True(built.Succeeded, $"The program did not build before the test:\n{built.Output}{built.Error}");
        string apphost = Path.Combine(project, "obj", "Debug", TestProjects.Framework, OperatingSystem.IsWindows() ? "apphost.exe" : "apphost");
        Assert.True(File.Exists(apphost), $"The build made no apphost at {apphost}");
        File.Delete(apphost);

        MainWindow window = Ui.OpenFolder(Path.Combine(Folder, "Zebra.slnx"));
        try
        {
            Assert.Equal("Zebra", window.Solution?.Name);
            Assert.Equal(PathNames.Canonical(Path.Combine(Folder, "Zebra.slnx")), window.CSharpService?.Session?.Solution);
            Ui.WaitFor(() => window.CSharpService?.Session is { IsLoaded: true }, "the language server to load the program's design-time build", TimeSpan.FromMinutes(2));
            Assert.False(File.Exists(apphost), "The design-time build made the program's apphost, which the build makes (and on macOS signs) beside it");
            window.ReloadSolution();
            Assert.Equal("Zebra", window.Solution?.Name);

            ExplorerTree tree = window.ExplorerView.Tree;
            tree.Expand(Assert.Single(tree.Rows, r => r.Kind == NodeKind.SolutionFolder && r.Name == "src"));
            ExplorerNode loaded = Assert.Single(tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Hello");
            Assert.Equal(TestProjects.Framework, loaded.Detail);
            string hello = loaded.Path!;
            Assert.Equal(["Hello"], window.Configurations.Select(c => c.Name));

            window.SetUnloaded(hello, unloaded: true);
            ExplorerNode unloaded = Assert.Single(tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Hello");
            Assert.Equal("(unloaded)", unloaded.Detail);
            Assert.True(unloaded.IsDimmed);
            Assert.False(unloaded.HasChildren);
            Assert.Empty(window.Configurations);
            window.Perform(AppActions.Build);
            Assert.Equal("Nothing to build: every project is unloaded", window.Status);

            window.SetUnloaded(hello, unloaded: false);
            Assert.Equal(["Hello"], window.Configurations.Select(c => c.Name));
            Assert.Equal(TestProjects.Framework, Assert.Single(tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Hello").Detail);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>One program with a compiler's error, in a folder named as Finder and Explorer name a copy, built once: its error
    /// listed in Problems at its file and line, the status bar's word a link there, the Build window's line of it a link to
    /// its file, line and column; and Run, whose build fails on it too, brings the Build window forward with it and runs
    /// nothing.</summary>
    [AvaloniaFact]
    public void A_build_error_is_listed_in_problems_and_a_link_from_the_build_window_and_a_run_whose_build_fails_runs_nothing()
    {
        string shop = Path.Combine(TestFolders.Create("build-error-"), "Shop (copy)");
        TestFolders.Write(shop, "Shop.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
        TestFolders.Write(shop, "Program.cs", "Console.WriteLine(\"Hello\");\nint count = \"three\";\n");
        string program = PathNames.Canonical(Path.Combine(shop, "Program.cs"));
        MainWindow window = Ui.OpenFolder(shop);
        Ui.Press(window, Ui.Shortcut(AppActions.Build));
        Ui.Settle();
        Assert.False(window.IsBuilding);
        Problem? problem = window.BottomPanel.Problems.Problems.SingleOrDefault(p => p.Code == "CS0029");
        Assert.True(problem is not null, $"The build failed but Problems has nothing for its error, listing [{string.Join(", ", window.BottomPanel.Problems.Problems.Select(p => $"{p.Code} {p.Path}"))}]");
        Assert.Equal(ProblemSeverity.Error, problem.Severity);
        Assert.Equal(program, problem.Path);
        Assert.Equal(new TextPosition(1, 12), problem.Position);

        Assert.StartsWith("Build failed: 1 error", window.Status, StringComparison.Ordinal);
        ProblemsModel problems = window.BottomPanel.Problems;
        window.ShowToolWindow(ToolWindowIds.Build);
        problems.Shown = ProblemsTab.File;
        Button said = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StatusMessage");
        Ui.Click(said);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Problems) && problems.Shown == ProblemsTab.Build && problems.Selected == problem,
            $"A click on \"{window.Status}\" in the status bar showed {(window.DockHost.IsShown(ToolWindowIds.Problems) ? $"Problems' {problems.Shown} tab, {problems.Selected?.Code ?? "nothing"} chosen" : "nothing")}, "
            + "not the build's error in Problems' Build");

        window.ShowToolWindow(ToolWindowIds.Build);
        Ui.Settle();
        OutputView build = window.BottomPanel.Build;
        string output = build.Document.Snapshot.ToString();
        int error = output.IndexOf("Program.cs(2,13): error CS0029", StringComparison.Ordinal);
        Assert.True(error >= 0, $"The build window has no error at its place:\n{output}");
        Click(window, build.Editor, error);
        Assert.Equal(program, window.EditorArea.Active?.Path);
        Assert.Equal(new TextPosition(1, 12), window.EditorArea.CodeEditor.CaretPosition);

        window.ShowToolWindow(ToolWindowIds.Problems);
        string? refused = Ui.Await(window.RunAsync(), "the run's build", TimeSpan.FromMinutes(2));
        Assert.StartsWith("Build failed: 1 error", refused, StringComparison.Ordinal);
        Assert.True(window.DockHost.IsShown(ToolWindowIds.Build), "The build before the run failed, but the Build window did not come forward with its error as Rider's does");
        Assert.Contains("Program.cs(2,13): error CS0029", window.BottomPanel.Build.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.False(window.Runner.IsRunning);
        Assert.Equal(string.Empty, Output(window));
        window.Close();
    }

    [AvaloniaFact]
    public void A_build_error_whose_message_runs_over_several_lines_is_one_problem_showing_its_first_line()
    {
        // A message of many lines, as XAML compilers' and NuGet's are: MSBuild writes each line as its own.
        Write("src/Hello/Hello.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <ImplicitUsings>enable</ImplicitUsings>\n  </PropertyGroup>\n" +
            "  <Target Name=\"Unreadable\" BeforeTargets=\"Build\">\n" +
            "    <Warning File=\"Program.cs\" Code=\"NU1608\" Text=\"Detected package version outside of dependency constraint.%0A Hello -&gt; A -&gt; X (&gt;= 2.0)%0A Hello -&gt; X (&gt;= 1.0)\" />\n" +
            "    <Error File=\"Views/Main.axaml\" Code=\"AVLN1001\" Text=\"File doesn't contain valid XAML: 'Hello' is an unexpected token.%0A   at System.Xml.XmlTextReaderImpl.Throw(Exception e)%0A   at System.Xml.XmlTextReaderImpl.ParseAttributes()\" />\n" +
            "  </Target>\n</Project>\n");
        MainWindow window = Open();
        Ui.Press(window, Ui.Shortcut(AppActions.Build));
        Ui.Settle();
        IReadOnlyList<Problem> problems = window.BottomPanel.Problems.Problems;
        Assert.True(problems.Count(p => p.Severity == ProblemSeverity.Error) == 1,
            $"The compiler's one error should be one problem with its stack trace, not a problem a line; Problems lists [{string.Join(" | ", problems.Select(p => p.Message))}]");
        Problem error = Assert.Single(problems, p => p.Severity == ProblemSeverity.Error);
        Assert.Equal("File doesn't contain valid XAML: 'Hello' is an unexpected token.\n   at System.Xml.XmlTextReaderImpl.Throw(Exception e)\n   at System.Xml.XmlTextReaderImpl.ParseAttributes()", error.Message);
        Problem warning = Assert.Single(problems, p => p.Severity == ProblemSeverity.Warning);
        Assert.Equal("Detected package version outside of dependency constraint.\n Hello -> A -> X (>= 2.0)\n Hello -> X (>= 1.0)", warning.Message);
        Assert.StartsWith("Build failed: 1 error, 1 warning", window.Status, StringComparison.Ordinal);
        window.Perform(AppActions.ShowProblems);
        window.BottomPanel.Problems.Shown = ProblemsTab.Build;
        Ui.Settle();
        TextBlock[] shown = [.. window.ProblemsWindow.GetVisualDescendants().OfType<TextBlock>()];
        Assert.Contains(shown, t => t.Text == "File doesn't contain valid XAML: 'Hello' is an unexpected token.");
        Assert.DoesNotContain(shown, t => t.Text?.Contains("ParseAttributes", StringComparison.Ordinal) == true);
        window.Close();
    }

    /// <summary>One program under dotnet watch, never built before: a saved edit changes what it does with no restart; Apply
    /// Hot Reload Changes in the Run window saves and says what dotnet watch made of it; a parameter added with a default,
    /// which leaves the callers calling the method the update deletes, is said as the restart dotnet watch makes, however
    /// long after the Apply the program calls it and however far the window fell behind; and the program dotnet watch
    /// started again, and dotnet watch, end with the window.</summary>
    [AvaloniaFact]
    public void With_hot_reload_edits_reach_the_running_program_a_signature_change_restarts_it_and_it_ends_with_the_window()
    {
        string started = Path.Combine(Folder, "program.pid");
        string asked = Path.Combine(Folder, "asked");
        Write("src/Hello/Program.cs", $"File.WriteAllText(@\"{started}\", Environment.ProcessId.ToString());\n"
            + $"while (true)\n{{\n    Console.WriteLine(Talk.Say());\n    if (File.Exists(@\"{asked}\"))\n    {{\n        Console.WriteLine(Talk.Later());\n    }}\n    Thread.Sleep(200);\n}}\n\n"
            + "static class Talk\n{\n    public static string Say() => \"before the edit\";\n\n    public static string Later() => \"asked later\";\n}\n");
        MainWindow window = Open();
        window.ApplySettings(window.Settings with { HotReload = true });
        bool ending = false;
        try
        {
            window.OpenFile(Path.Combine(Folder, "src", "Hello", "Program.cs"));
            Ui.Settle();
            // Never built: the output folder, the program's working folder, is left for dotnet watch's first build to make.
            TestFolders.Delete(Path.Combine(Folder, "src", "Hello", "bin"));
            window.Perform(AppActions.RunProgram);
            // dotnet watch watches once it has loaded the project, which is after the program has started.
            WaitWatched(window, () => Output(window).Contains("before the edit", StringComparison.Ordinal) && Output(window).Contains("Waiting for changes", StringComparison.Ordinal),
                "the program to run under dotnet watch", TimeSpan.FromMinutes(2));

            Passenger.App.Workspace.OpenDocument program = window.EditorArea.Active!;
            int at = program.Text.Snapshot.ToString().IndexOf("before the edit", StringComparison.Ordinal);
            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(at, "before the edit".Length, "after the edit")]);
            window.Perform(AppActions.Save);
            Ui.WaitFor(() => Output(window).Contains("after the edit", StringComparison.Ordinal), "the edit to reach the program", TimeSpan.FromMinutes(1));
            Assert.True(window.Runner.IsRunning);

            Assert.True(window.BottomPanel.Run.ShowsHotReload, "The Run window should offer Apply Hot Reload Changes under dotnet watch");
            at = program.Text.Snapshot.ToString().IndexOf("after the edit", StringComparison.Ordinal);
            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(at, "after the edit".Length, "applied again")]);
            window.Perform(AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => Output(window).Contains("applied again", StringComparison.Ordinal), "the applied edit to reach the program", TimeSpan.FromMinutes(1));
            WaitWatched(window, () => window.Status?.Contains("dotnet watch applied the changes", StringComparison.Ordinal) == true, "the status bar to say the changes applied");

            // The status bar says the restart however late: here once the Apply has ended.
            WaitWatched(window, () => window.Actions.Work.Of(AppActions.ApplyHotReloadChanges) is null, "the Apply before to end");
            int first = Processes.ReadId(started);
            at = program.Text.Snapshot.ToString().IndexOf("Later() =>", StringComparison.Ordinal);
            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(at, "Later() =>".Length, "Later(int times = 1) =>")]);
            Assert.True(window.Perform(AppActions.ApplyHotReloadChanges), $"Apply Hot Reload Changes could not run: {window.Status}");
            WaitWatched(window, () => window.Status?.Contains("dotnet watch applied the changes", StringComparison.Ordinal) == true
                && window.Actions.Work.Of(AppActions.ApplyHotReloadChanges) is null, "the Apply of a changed signature to end with the changes applied");
            Write("asked", "");
            WaitWatched(window, () => window.Status?.Contains("dotnet watch is restarting the application", StringComparison.Ordinal) == true,
                "the status bar to say dotnet watch restarts the program, which called the method as it was after the Apply had ended", TimeSpan.FromMinutes(1));
            // An edit before dotnet watch loads the project again is in what it loads, never applied.
            WaitWatched(window, () => Processes.ReadId(started) is int again && again > 0 && again != first
                && Output(window) is { } output && output.LastIndexOf("Waiting for changes", StringComparison.Ordinal) > output.LastIndexOf("Restarting application", StringComparison.Ordinal),
                "dotnet watch to start the program again and watch it", TimeSpan.FromMinutes(1));

            // The window kept from drawing until the restart, as a slow machine's falls behind.
            int second = Processes.ReadId(started);
            at = program.Text.Snapshot.ToString().IndexOf("Say() =>", StringComparison.Ordinal);
            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(at, "Say() =>".Length, "Say(int times = 1) =>")]);
            Assert.True(window.Perform(AppActions.ApplyHotReloadChanges), $"Apply Hot Reload Changes could not run: {window.Status}");
            Assert.Equal("Hot Reload: saved for dotnet watch to apply", window.Status);
            Assert.True(Waiting.Until(() => Processes.ReadId(started) is int again && again > 0 && again != second, TimeSpan.FromMinutes(1)),
                "dotnet watch did not start the program again, for the signature changed while the window was held");
            WaitWatched(window, () => window.Status?.Contains("dotnet watch is restarting the application", StringComparison.Ordinal) == true,
                "the status bar to say dotnet watch restarted the program while the window was held", TimeSpan.FromMinutes(1));

            ending = true;
            AssertEndsWithTheWindow(window, started, "the program dotnet watch started");
        }
        finally
        {
            if (!ending)
            {
                window.Perform(AppActions.Stop);
                Ui.WaitFor(() => !window.Runner.IsRunning, "the program to stop");
                window.Close();
            }
            window.ApplySettings(new Passenger.App.Settings.AppSettings());
        }
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    /// <summary>Waits as <see cref="Ui.WaitFor"/> does for what the window says of a program under dotnet watch; one that
    /// never comes says what the status bar and the Run window said by the end of the wait, not as it began.</summary>
    private static void WaitWatched(MainWindow window, Func<bool> condition, string what, TimeSpan? limit = null)
    {
        try
        {
            Ui.WaitFor(condition, what, limit);
        }
        catch (TimeoutException waited)
        {
            throw new TimeoutException($"{waited.Message}; the status bar says \"{window.Status}\" and the Run window:\n{Output(window)}", waited);
        }
    }

    /// <summary>The Run console over three programs run one after another in one window: what is typed into it is the
    /// first one's input, Ctrl+D ending it; what the second writes to standard error alone is drawn in the error
    /// colour, whatever colour codes it writes, none of which shows even split between two reads, and the console's
    /// menu clears it with its colours; and the third's three megabytes keep to the console's buffer, its oldest lines
    /// dropped.</summary>
    [AvaloniaFact]
    public void The_run_console_takes_typed_input_never_shows_a_split_colour_code_colours_standard_error_clears_from_its_menu_and_keeps_riders_buffer()
    {
        Write("src/Hello/Program.cs",
            "Console.Write(\"Name? \");\nstring? name = Console.ReadLine();\nConsole.WriteLine($\"Hello, {name}\");\n"
            + "string rest = Console.In.ReadToEnd();\nConsole.WriteLine($\"then {rest.Length} more, and the end of its input\");\n");
        MainWindow window = Open();
        OutputView run = window.BottomPanel.Run;
        try
        {
            window.Perform(AppActions.RunProgram);
            Ui.WaitFor(() => Output(window).Contains("Name? ", StringComparison.Ordinal), "the program to ask", TimeSpan.FromMinutes(2));
            Ui.Click(run.Editor, settle: false);
            window.KeyTextInput("Adx");
            Ui.Press(window, "BACK_SPACE");
            window.KeyTextInput("a");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => Output(window).Contains("Hello, Ada\n", StringComparison.Ordinal), "the program to read the line typed");
            Assert.Contains("Name? Ada\nHello, Ada\n", Output(window), StringComparison.Ordinal);
            TextSnapshot text = run.Document.Snapshot;
            Passenger.Editor.SyntaxSpan typed = Assert.Single(run.Editor.Highlighter!.GetSpans(text, 0, text.Length), s => s.Kind == Passenger.Editor.TokenKind.UserInput);
            Assert.Equal("Ada", text.GetText(typed.Start, typed.Length));

            // Ctrl+D, Rider's Send EOF on every platform, sends what was typed and ends the input, which ends the program.
            window.KeyTextInput("ok");
            Ui.Press(window, "control D");
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the program to end at the end of its input");
            Assert.Contains("ok^D\nthen 2 more, and the end of its input\n", Output(window), StringComparison.Ordinal);
            Assert.Contains("exit code 0", Output(window), StringComparison.Ordinal);

            // The first sequence straddles a read's end at 8,192 characters; the rest are cut by pauses in the writing.
            Write("src/Hello/Program.cs",
                "Console.Out.Write(new string('x', 8190) + \"\\u001b[32mgreen\\u001b[0m\\n\");\nConsole.Out.Flush();\n"
                + "Console.Out.Write(\"before \\u001b[3\");\nConsole.Out.Flush();\nThread.Sleep(500);\n"
                + "Console.Out.Write(\"1mred\\u001b[0m after\\r\");\nConsole.Out.Flush();\nThread.Sleep(300);\nConsole.Out.Write(\"\\n\");\nConsole.Out.Flush();\nThread.Sleep(300);\n"
                + "Console.Error.Write(\"\\u001b]0;a title\\u0007error \\u001b[1;3\");\nConsole.Error.Flush();\nThread.Sleep(300);\n"
                + "Console.Error.Write(\"1mbold\\u001b[0m\\n\");\nConsole.Error.Flush();\n");
            window.Perform(AppActions.RunProgram);
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the program writing colour codes to finish", TimeSpan.FromMinutes(2));
            string output = Output(window);
            Assert.DoesNotContain("Name?", output, StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b", output, StringComparison.Ordinal);
            Assert.DoesNotContain("[3", output, StringComparison.Ordinal);
            Assert.DoesNotContain("1m", output, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", output, StringComparison.Ordinal);
            Assert.Contains(new string('x', 8190) + "green\n", output, StringComparison.Ordinal);
            Assert.Contains("\nbefore red after\n", output, StringComparison.Ordinal);
            text = run.Document.Snapshot;
            Passenger.Editor.IHighlighter colours = run.Editor.Highlighter!;
            Passenger.Editor.SyntaxSpan error = Assert.Single(colours.GetSpans(text, 0, text.Length));
            Assert.Equal(Passenger.Editor.TokenKind.ErrorOutput, error.Kind);
            Assert.Equal("error bold\n", text.GetText(error.Start, error.Length));

            // Below the first line's top, which is the splitter's to grab.
            Ui.RightClick(run.Editor, run.Editor.PointOf(14) + new Avalonia.Vector(1, run.Editor.LineHeight / 2));
            ContextMenu menu = Assert.IsType<ContextMenu>(run.Menu);
            Assert.Equal(["Copy", "Select All", "Clear All"], menu.Items.OfType<MenuItem>().Select(i => (string?)i.Header));
            Assert.False(menu.Items.OfType<MenuItem>().First().IsEnabled, "Copy is offered with nothing chosen");
            Ui.Choose(menu, "Clear All");
            menu.Close();
            text = run.Document.Snapshot;
            Assert.Equal(string.Empty, text.ToString());
            Assert.Empty(colours.GetSpans(text, 0, text.Length));

            // Three megabytes, two lines of it on standard error, each between pauses since the two streams' reads may cross.
            Write("src/Hello/Program.cs",
                "for (int i = 0; i < 50_000; i++)\n{\n    Console.WriteLine($\"line {i:D6} \" + new string('x', 52));\n"
                + "    if (i is 38_000 or 49_999)\n    {\n        Thread.Sleep(300);\n"
                + "        Console.Error.WriteLine($\"error {i:D6} \" + new string('e', 51));\n        Thread.Sleep(300);\n    }\n}\n");
            window.Perform(AppActions.RunProgram);
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the program writing three megabytes to finish", TimeSpan.FromMinutes(2));
            text = run.Document.Snapshot;
            Assert.InRange(text.Length, 900 * 1024, OutputView.DefaultLimitKb * 1024);
            Assert.Matches(@"^line \d{6} x{52}$", text.GetLineText(0));
            Assert.DoesNotContain("line 000000 ", text.ToString(), StringComparison.Ordinal);
            Assert.Contains($"line 049999 {new string('x', 52)}\n", text.ToString(), StringComparison.Ordinal);
            IReadOnlyList<Passenger.Editor.SyntaxSpan> errors = colours.GetSpans(text, 0, text.Length);
            Assert.Equal([$"error 038000 {new string('e', 51)}\n", $"error 049999 {new string('e', 51)}\n"], errors.Select(e => text.GetText(e.Start, e.Length)));
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning, "the run to end");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_projects_chosen_in_the_explorer_build_rebuild_and_clean_on_their_own()
    {
        Write("Sample.slnx", "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/Hello/Hello.csproj\" />\n    <Project Path=\"src/Tools/Tools.csproj\" />\n  </Folder>\n</Solution>\n");
        Write("src/Tools/Tools.csproj", TestProjects.Library());
        Write("src/Tools/Tool.cs", "public static class Tool\n{\n    public static int Count => \"none\";\n}\n");
        string hello = Path.Combine(Folder, "src", "Hello", "bin", "Debug", TestProjects.Framework, "Hello.dll");
        string tools = Path.Combine(Folder, "src", "Tools", "bin", "Debug", TestProjects.Framework, "Tools.dll");
        Restore.Run(Path.Combine(Folder, "Sample.slnx"));
        MainWindow window = Open();
        ExplorerTree tree = window.ExplorerView.Tree;
        tree.Expand(Assert.Single(tree.Rows, r => r.Kind == NodeKind.SolutionFolder && r.Name == "src"));
        window.ExplorerView.Choose(Assert.Single(tree.Rows, r => r.Kind == NodeKind.Project && r.Name == "Hello"));

        window.Perform(AppActions.BuildSelected);
        Ui.WaitFor(() => !window.IsBuilding, "the build", TimeSpan.FromMinutes(2));
        Assert.True(window.Status == "Build succeeded", $"The build did not succeed: {Ui.Said(window)}");
        Assert.True(File.Exists(hello), "Build Selected Projects did not build the chosen project");
        Assert.False(File.Exists(tools), "Build Selected Projects built a project that was not chosen");

        window.Perform(AppActions.CleanSelected);
        Ui.WaitFor(() => !window.IsBuilding, "the clean", TimeSpan.FromMinutes(2));
        Assert.Equal("Clean succeeded", window.Status);
        Assert.False(File.Exists(hello), "Clean Selected Projects left the project's output");

        window.ExplorerView.Choose([.. tree.Rows.Where(r => r.Kind == NodeKind.Project)]);
        window.Perform(AppActions.RebuildSelected);
        Ui.WaitFor(() => !window.IsBuilding, "the rebuild", TimeSpan.FromMinutes(2));
        Assert.StartsWith("Rebuild failed: 1 error", window.Status, StringComparison.Ordinal);
        Problem problem = Assert.Single(window.BottomPanel.Problems.Problems, p => p.Code == "CS0029");
        Assert.EndsWith("Tool.cs", problem.Path, StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>A build under dotnet test's environment, which sets MSBUILDENSURESTDOUTFORTASKPROCESSES, ends though a
    /// node MSBuild started waits on to be reused (the test's own MSBuild, <see cref="OwnMSBuild"/>, has none idle).
    /// Then, in a person's IDE's environment, after a build that left its nodes for the next, Stop pressed while each
    /// project's build runs a step that goes on writing to its output folder until ended (as a code generator, a
    /// bundler or tests in the build would) ends every step, and nothing more is written.</summary>
    [AvaloniaFact]
    public void A_build_under_dotnet_tests_environment_ends_and_stopping_a_build_ends_its_steps_wherever_msbuild_ran_them_and_nothing_more_is_written_after()
    {
        WriteTwoProjects();
        using OwnMSBuild tested = new(("MSBUILDENSURESTDOUTFORTASKPROCESSES", "1"));
        // Settling waits for Roslyn to say the projects loaded, which it says once its restore's output has ended.
        MainWindow window = Open();
        Assert.True(window.CSharpService?.IsReady, window.CSharpService?.State);
        List<int> steps = [];
        try
        {
            window.Perform(AppActions.Build);
            int command = BuildCommand(window, null);
            Assert.True(Processes.Gone(command, TimeSpan.FromMinutes(2)), "The build's dotnet command never ended");
            Ui.PumpUntil(() => !window.IsBuilding,
                "the Build window to stop saying it is building once the build's dotnet command ended (a node MSBuild started holding its output open keeps it building)",
                TimeSpan.FromSeconds(10));
            Assert.True(window.Status == "Build succeeded", $"The build did not succeed: {Ui.Said(window)}");
            Assert.EndsWith("Process finished with exit code 0.\n", window.BottomPanel.Build.Document.Snapshot.ToString(), StringComparison.Ordinal);

            using OwnMSBuild person = new(("MSBUILDENSURESTDOUTFORTASKPROCESSES", null));
            window.Perform(AppActions.Build);
            Ui.WaitFor(() => !window.IsBuilding, "the first build in a person's environment", TimeSpan.FromMinutes(2));
            Assert.True(window.Status == "Build succeeded", $"The build did not succeed: {Ui.Said(window)}");

            Write("Directory.Build.targets", """
                <Project>
                  <Target Name="Write" BeforeTargets="Build" Condition="'$(DesignTimeBuild)' != 'true'">
                    <Exec Condition="'$(OS)' != 'Windows_NT'" Command="echo $$ &gt; step.pid; while true; do date &gt;&gt; '$(OutDir)written.txt'; sleep 0.2; done" />
                    <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Set-Content -Path step.pid -Value $PID; while ($true) { Add-Content -Path '$(OutDir)written.txt' -Value (Get-Date -Format o); Start-Sleep -Milliseconds 200 }&quot;" />
                  </Target>
                </Project>
                """);
            string[] projects = ["Hello", "Tools"];
            int? first = window.Builder.ProcessId;
            window.Perform(AppActions.Build);
            command = BuildCommand(window, first);
            // Both projects build at once; one that waits for the other would never start, its step never ending.
            _ = Processes.WrittenId([.. projects.Select(p => Path.Combine(Folder, "src", p, "step.pid"))], "a project's step to start");
            // Pumped, not settled: the build holds background work until it ends.
            Ui.PumpUntil(() => projects.Any(p => File.Exists(Path.Combine(Folder, "src", p, "bin", "Debug", TestProjects.Framework, "written.txt"))),
                "a project's step to write into its output folder", TimeSpan.FromSeconds(60));
            steps.AddRange(projects.Select(p => Processes.ReadId(Path.Combine(Folder, "src", p, "step.pid"))).Where(id => id > 0));
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsBuilding, "the build to stop");
            Assert.Equal("Build stopped", window.Status);

            foreach (int step in steps)
            {
                Assert.True(Processes.Gone(step), $"After Stop, the build's step {Processes.Describe(step)} went on running: MSBuild ran it in a process that Stop did not end");
            }
            Assert.True(Processes.Gone(command), "After Stop, the build's dotnet command went on running");
            string[] written = [.. projects.Select(p => Path.Combine(Folder, "src", p, "bin", "Debug", TestProjects.Framework, "written.txt")).Where(File.Exists)];
            Assert.NotEmpty(written);
            long[] lengths = [.. written.Select(f => new FileInfo(f).Length)];
            Ui.Hold(TimeSpan.FromSeconds(2), "long enough for a build still running to write to its output folder again");
            Assert.True(lengths.SequenceEqual(written.Select(f => new FileInfo(f).Length)), "After Stop, the build went on writing to its output folder");
        }
        finally
        {
            steps.ForEach(Processes.Kill);
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsBuilding, "the build to stop");
            window.Close();
        }
    }

    /// <summary>Roslyn restores a project never restored itself, and its restore leaves an MSBuild node waiting to be reused,
    /// in the language server's process group, which ends when the server is started again (after it crashed or hung). A
    /// build the IDE starts never takes that node up, so the server started again while the build runs ends nothing of
    /// the build's. On Windows the restore's node is no part of the server's tree once the restore has gone.</summary>
    [AvaloniaFact]
    public void A_build_goes_on_through_the_language_server_starting_again_while_it_runs()
    {
        Nice.SkipUnlessMSBuildKeepsNodes();
        WriteTwoProjects();
        Write("Directory.Build.targets", """
            <Project>
              <Target Name="Wait" BeforeTargets="Build" Condition="'$(DesignTimeBuild)' != 'true'">
                <Exec Condition="'$(OS)' != 'Windows_NT'" Command="echo $$ &gt; step.pid; sleep 4" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Set-Content -Path step.pid -Value $PID; Start-Sleep -Seconds 4&quot;" />
              </Target>
            </Project>
            """);
        using OwnMSBuild msbuild = new(("MSBUILDENSURESTDOUTFORTASKPROCESSES", null));
        // Settling waits for the projects to load, which Roslyn says once its restore of Tools has ended.
        MainWindow window = Open();
        int server = window.CSharpService!.Session!.Server.ProcessId;
        int[] restored = ServerNodes(server);
        Assert.True(OperatingSystem.IsWindows() || restored.Length > 0,
            $"The language server's restore left no MSBuild node in its process group, so there is none for a build to take up. MSBuild nodes now:\n{string.Join('\n', Processes.MSBuildNodes().Select(n => $"{Processes.Describe(n.Key)} {n.Value}"))}");
        try
        {
            window.Perform(AppActions.Build);
            string[] projects = ["Hello", "Tools"];
            foreach (string project in projects)
            {
                Processes.WrittenId(Path.Combine(Folder, "src", project, "step.pid"), $"{project}'s step to start");
            }
            Process.GetProcessById(server).Kill();
            Ui.WaitFor(() => !window.IsBuilding, "the build", TimeSpan.FromMinutes(2));
            Assert.True(window.Status == "Build succeeded",
                $"The build failed once the language server was started again under it ({window.Status}):\n{window.BottomPanel.Build.Document.Snapshot}");
            Ui.WaitFor(() => window.CSharpService.Session is { IsLoaded: true } session && session.Server.ProcessId != server, "the language server to be started again");
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsBuilding, "the build to stop");
            window.Close();
        }
    }

    /// <summary>A WPF app built the moment it opens, while the project host's design-time build of it (the language
    /// server's) is writing its generated code: both write obj/…/App.g.cs, which Windows lets one writer have. The
    /// build waits for the design-time build, the Build window saying so, and runs alone. Here the design-time build
    /// holds App.g.cs as MarkupCompile does while it writes it, until the test lets it go; a build's MarkupCompile
    /// begun meanwhile says so.</summary>
    [AvaloniaFact]
    public void A_build_begun_while_a_wpf_apps_design_time_build_writes_its_generated_code_waits_for_it_and_succeeds()
    {
        string app = PathNames.Canonical(TestFolders.Create("wpf-build-"));
        string release = Path.Combine(app, "release");
        try
        {
            TestFolders.Prepare(() =>
            {
                WpfApp.Create(app, "Overlap");
                File.WriteAllText(Path.Combine(app, "Directory.Build.targets"), """
                    <Project>
                      <Target Name="Hold" BeforeTargets="MarkupCompilePass1" Condition="'$(DesignTimeBuild)' == 'true' And !Exists('release')">
                        <MakeDir Directories="$(IntermediateOutputPath)" />
                        <Exec Condition="'$(OS)' == 'Windows_NT'" Timeout="120000" Command="powershell -NoProfile -Command &quot;$held = [IO.File]::Open('$(IntermediateOutputPath)App.g.cs', 'OpenOrCreate', 'Write', 'Read'); Set-Content -Path held -Value held; while (-not (Test-Path release)) { Start-Sleep -Milliseconds 100 }; $held.Dispose()&quot;" />
                        <Exec Condition="'$(OS)' != 'Windows_NT'" Timeout="120000" Command="echo held &gt; held; while [ ! -f release ]; do sleep 0.1; done" />
                      </Target>
                      <Target Name="Overlap" BeforeTargets="MarkupCompilePass1" Condition="'$(DesignTimeBuild)' != 'true' And Exists('held') And !Exists('release')">
                        <WriteLinesToFile File="overlapped" Lines="begun" />
                      </Target>
                    </Project>
                    """);
                Restore.Run(Path.Combine(app, "Overlap.csproj"));
            }, app);
            MainWindow window = new() { Width = 1200, Height = 800 };
            window.Show();
            window.OpenFolder(app);
            try
            {
                // Pumped, not settled: the language server's load waits for the design-time build.
                Ui.PumpUntil(() => File.Exists(Path.Combine(app, "held")), "the design-time build to hold App.g.cs", TimeSpan.FromMinutes(2));
                Assert.True(window.Perform(AppActions.Build));
                const string waiting = "Waiting for the design-time build of Overlap to end";
                Ui.PumpUntil(() => !window.IsBuilding || window.BottomPanel.Build.Document.Snapshot.ToString().Contains(waiting, StringComparison.Ordinal),
                    "the build to wait for the design-time build or end", TimeSpan.FromMinutes(2));
                File.WriteAllText(release, "go");
                Ui.PumpUntil(() => !window.IsBuilding, "the build", TimeSpan.FromMinutes(3));
                string built = window.BottomPanel.Build.Document.Snapshot.ToString();
                Assert.False(File.Exists(Path.Combine(app, "overlapped")), $"The build's MarkupCompile ran while the design-time build had App.g.cs open ({window.Status}):\n{built}");
                Assert.True(window.Status == "Build succeeded", $"The WPF app did not build once its design-time build had ended ({window.Status}):\n{built}");
                Assert.Contains(waiting, built, StringComparison.Ordinal);
            }
            finally
            {
                File.WriteAllText(release, "go");
                window.Close();
            }
        }
        finally
        {
            TestFolders.Delete(app);
        }
    }

    /// <summary>An Avalonia app built as it opens, while the host's design-time build of it writes its resources
    /// (GenerateAvaloniaResources runs before CoreCompile, so in a design-time build too), which Windows lets one
    /// writer have: the build waits. Here the design-time build holds the resources open until the test lets it go,
    /// then deletes the empty file it held (taken as up to date, it would be embedded: AVLN9999) for the task to write;
    /// a build begun meanwhile says so.</summary>
    [AvaloniaFact]
    public void A_build_begun_while_an_avalonia_apps_design_time_build_writes_its_resources_waits_for_it_and_succeeds()
    {
        string app = PathNames.Canonical(TestFolders.Create("avalonia-build-"));
        string release = Path.Combine(app, "release");
        try
        {
            TestFolders.Prepare(() =>
            {
                File.WriteAllText(Path.Combine(app, "Overlap.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
                    + $"  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"Avalonia\" Version=\"{AvaloniaApp.Version}\" />\n"
                    + "    <AvaloniaResource Include=\"Assets/**\" />\n  </ItemGroup>\n</Project>\n");
                Directory.CreateDirectory(Path.Combine(app, "Assets"));
                File.WriteAllText(Path.Combine(app, "Assets", "note.txt"), "a resource\n");
                File.WriteAllText(Path.Combine(app, "Shapes.cs"), "namespace Overlap;\n\npublic sealed class Circle\n{\n}\n");
                File.WriteAllText(Path.Combine(app, "Directory.Build.targets"), """
                    <Project>
                      <Target Name="Hold" BeforeTargets="GenerateAvaloniaResources" Condition="'$(DesignTimeBuild)' == 'true' And !Exists('release')">
                        <MakeDir Directories="$(IntermediateOutputPath)Avalonia" />
                        <Exec Condition="'$(OS)' == 'Windows_NT'" Timeout="120000" Command="powershell -NoProfile -Command &quot;$held = [IO.File]::Open('$(IntermediateOutputPath)Avalonia\resources', 'OpenOrCreate', 'Write', 'Read'); Set-Content -Path held -Value held; while (-not (Test-Path release)) { Start-Sleep -Milliseconds 100 }; $held.Dispose(); Remove-Item '$(IntermediateOutputPath)Avalonia\resources'&quot;" />
                        <Exec Condition="'$(OS)' != 'Windows_NT'" Timeout="120000" Command="echo held &gt; held; while [ ! -f release ]; do sleep 0.1; done" />
                      </Target>
                      <Target Name="Overlap" BeforeTargets="GenerateAvaloniaResources" Condition="'$(DesignTimeBuild)' != 'true' And Exists('held') And !Exists('release')">
                        <WriteLinesToFile File="overlapped" Lines="begun" />
                      </Target>
                    </Project>
                    """);
                Restore.Run(Path.Combine(app, "Overlap.csproj"));
            }, app);
            MainWindow window = new() { Width = 1200, Height = 800 };
            window.Show();
            window.OpenFolder(app);
            try
            {
                // Pumped, not settled: the language server's load waits for the design-time build.
                Ui.PumpUntil(() => File.Exists(Path.Combine(app, "held")), "the design-time build to hold the Avalonia resources", TimeSpan.FromMinutes(2));
                Assert.True(window.Perform(AppActions.Build));
                const string waiting = "Waiting for the design-time build of Overlap to end";
                Ui.PumpUntil(() => !window.IsBuilding || window.BottomPanel.Build.Document.Snapshot.ToString().Contains(waiting, StringComparison.Ordinal),
                    "the build to wait for the design-time build or end", TimeSpan.FromMinutes(2));
                File.WriteAllText(release, "go");
                Ui.PumpUntil(() => !window.IsBuilding, "the build", TimeSpan.FromMinutes(3));
                string built = window.BottomPanel.Build.Document.Snapshot.ToString();
                Assert.False(File.Exists(Path.Combine(app, "overlapped")), $"The build wrote the Avalonia resources while the design-time build had them open ({window.Status}):\n{built}");
                Assert.True(window.Status == "Build succeeded", $"The Avalonia app did not build once its design-time build had ended ({Ui.Said(window)}):\n{built}");
                Assert.Contains(waiting, built, StringComparison.Ordinal);
            }
            finally
            {
                File.WriteAllText(release, "go");
                window.Close();
            }
        }
        finally
        {
            TestFolders.Delete(app);
        }
    }

    /// <summary>The dotnet command of the build just begun, once it has started: a build starts it once the project host
    /// has held its design-time builds off. Pumped, not settled, as the build holds background work until it ends.</summary>
    private static int BuildCommand(MainWindow window, int? before)
    {
        Ui.PumpUntil(() => window.Builder.ProcessId is { } started && started != before, "the build's dotnet command to start");
        return window.Builder.ProcessId!.Value;
    }

    /// <summary>The MSBuild worker nodes in a process group (the language server's), by the group ps lists for each process;
    /// none on Windows, which has no ps.</summary>
    private static int[] ServerNodes(int group)
    {
        if (OperatingSystem.IsWindows())
        {
            return [];
        }
        return [.. Processes.Ps("-A", "-o", "pid=,pgid=,command=")
            .Select(line => line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries))
            .Where(words => words.Length == 3 && words[2].Contains("/nodemode:1", StringComparison.Ordinal)
                && int.TryParse(words[1], System.Globalization.CultureInfo.InvariantCulture, out int pgid) && pgid == group)
            .Select(words => int.Parse(words[0], System.Globalization.CultureInfo.InvariantCulture))];
    }

    /// <summary>What a program writes in the Run window links where it points: an address opens in the browser, a message
    /// naming a file of its project in a compiler's shape opens that file (a relative path the project's, though the run's
    /// working folder is its output folder), and the stack trace of what it threw has each frame's file and line a link.</summary>
    [AvaloniaFact]
    public void An_address_a_message_naming_a_file_and_a_stack_trace_a_program_writes_are_links_where_they_point()
    {
        Write("src/Hello/Program.cs", "Console.WriteLine(\"The orders are documented at https://example.com/orders/help?topic=load.\");\n"
            + "Console.WriteLine(\"Program.cs(2,9): warning ORD001: the orders were checked by the program\");\nOrders.Load(\"orders.json\");\n\nstatic class Orders\n{\n"
            + "    public static void Load(string file)\n    {\n        Parse(file);\n    }\n\n"
            + "    private static void Parse(string file) =>\n        throw new InvalidOperationException($\"No orders in {file}\");\n}\n");
        // No solution at the top, as in many repositories: Run runs src/Hello/Hello.csproj.
        File.Delete(Path.Combine(Folder, "Sample.slnx"));
        MainWindow window = Open();
        Assert.Equal(["Hello"], window.Configurations.Select(c => c.Name));
        string output = RunToEnd(window);
        Assert.Contains("The orders are documented", output, StringComparison.Ordinal);
        OutputView run = window.BottomPanel.Run;
        Click(window, run.Editor, output.IndexOf("example.com", StringComparison.Ordinal));
        Assert.Equal(["https://example.com/orders/help?topic=load"], window.Browsed.Select(u => u.AbsoluteUri));
        Assert.Null(window.EditorArea.Active);
        Click(window, run.Editor, output.IndexOf("Program.cs(2,9)", StringComparison.Ordinal) + 2);
        Assert.Equal(PathNames.Canonical(Path.Combine(Folder, "src", "Hello", "Program.cs")), window.EditorArea.Active?.Path);
        Assert.Equal(new TextPosition(1, 8), window.EditorArea.CodeEditor.CaretPosition);
        Assert.Single(window.Browsed);

        string frame = output.Split('\n').FirstOrDefault(l => l.Contains("at Orders.Load(", StringComparison.Ordinal)) ?? string.Empty;
        Assert.True(frame.EndsWith("Program.cs:line 9", StringComparison.Ordinal), $"The run's output has no frame of Load at its line:\n{output}");
        int start = output.IndexOf(frame, StringComparison.Ordinal);
        string place = frame[(frame.IndexOf(" in ", StringComparison.Ordinal) + 4)..];
        int link = start + frame.Length - place.Length;

        Rest(window, run.Editor, start + frame.IndexOf("Orders.Load", StringComparison.Ordinal));
        Assert.Null(run.Editor.HoveredLink);
        Rest(window, run.Editor, link + 3);
        TextLink hovered = run.Editor.HoveredLink ?? throw new Xunit.Sdk.XunitException($"The pointer rests on the frame's file but nothing is underlined:\n{frame}");
        Assert.Equal(place, run.Document.Snapshot.GetText(hovered.Range.Start, hovered.Range.Length));
        Assert.Contains(run.Editor.Highlighter!.GetSpans(run.Document.Snapshot, start, frame.Length), span => span.Kind == TokenKind.Link && span.Start == link && span.Length == place.Length);

        Click(window, run.Editor, link + 3);
        Assert.Equal(PathNames.Canonical(Path.Combine(Folder, "src", "Hello", "Program.cs")), window.EditorArea.Active?.Path);
        Assert.Equal(new TextPosition(8, 0), window.EditorArea.CodeEditor.CaretPosition);
        Assert.True(window.EditorArea.CodeEditor.IsFocused, "The frame's file opened, but the keys stayed in the Run window");
        window.Close();
    }

    /// <summary>Settings' Toolset and Build, Presentation: Build opens the Build window as it starts, unless "when
    /// build is explicitly invoked" is off, and a build's end brings it forward as "Showing build results after build
    /// completed" says, by default only when errors were received; Run's build, an implicit one, opens it only with
    /// "even when the build is invoked implicitly".</summary>
    [AvaloniaFact]
    public void Build_opens_the_build_window_as_it_starts_and_its_end_brings_it_forward_as_settings_say()
    {
        MainWindow window = Open();
        try
        {
            window.ShowToolWindow(ToolWindowIds.Problems);
            window.Perform(AppActions.Build);
            Assert.True(window.IsBuilding && window.DockHost.IsShown(ToolWindowIds.Build), "Build Solution started without opening the Build window, which Rider's opens by default");
            BuildToEnd(window, "Build succeeded");
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Build), "The Build window Build Solution opened went away as the build ended");

            window.ApplySettings(window.Settings with { OpenBuildWindowOnBuild = false });
            window.ShowToolWindow(ToolWindowIds.Problems);
            window.Perform(AppActions.Build);
            Assert.True(window.IsBuilding && window.DockHost.IsShown(ToolWindowIds.Problems), "With Rider's \"when build is explicitly invoked\" off, Build Solution still opened the Build window");
            BuildToEnd(window, "Build succeeded");
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Problems), "A build that succeeded brought the Build window forward, which Rider does only for errors by default");
            Write("src/Hello/Program.cs", "Console.WriteLine(\"Hello\");\nint count = \"three\";\n");
            window.Perform(AppActions.Build);
            BuildToEnd(window, "Build failed");
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Build), "A build that failed left the Build window behind the Problems window, where Rider brings it forward with its errors");

            window.ApplySettings(window.Settings with { ShowBuildResults = Passenger.App.Settings.BuildResultsMode.Never });
            window.ShowToolWindow(ToolWindowIds.Problems);
            window.Perform(AppActions.Build);
            BuildToEnd(window, "Build failed");
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Problems), "With build results shown never, a failed build still brought the Build window forward");

            Write("src/Hello/Program.cs", "Console.WriteLine(\"Hello\");\n");
            window.ApplySettings(window.Settings with { ShowBuildResults = Passenger.App.Settings.BuildResultsMode.Always });
            window.ShowToolWindow(ToolWindowIds.Problems);
            window.Perform(AppActions.Build);
            BuildToEnd(window, "Build succeeded");
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Build), "With build results shown always, a build that succeeded did not bring the Build window forward");

            window.ApplySettings(new Passenger.App.Settings.AppSettings());
            window.ShowToolWindow(ToolWindowIds.Problems);
            window.Perform(AppActions.RunProgram);
            Ui.PumpUntil(() => window.IsBuilding, "the run's build to start", TimeSpan.FromMinutes(1));
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Problems), "Run's build took the bottom panel, which Rider leaves as it was for a build before a run");
            Assert.StartsWith("Building Hello", window.Status, StringComparison.Ordinal);
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the program to finish", TimeSpan.FromMinutes(2));
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Run), "The program ran, but its Run window is not in front");

            window.ApplySettings(window.Settings with { OpenBuildWindowOnImplicitBuild = true });
            window.ShowToolWindow(ToolWindowIds.Problems);
            // A change for it to build, a run of what its last build left starting with none.
            Write("src/Hello/Program.cs", "Console.WriteLine(\"Hello again\");\n");
            window.Perform(AppActions.RunProgram);
            Ui.PumpUntil(() => window.IsBuilding, "the second run's build to start", TimeSpan.FromMinutes(1));
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Build), "With Rider's \"even when the build is invoked implicitly\" on, Run's build did not open the Build window");
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the second program to finish", TimeSpan.FromMinutes(2));
            Assert.True(window.DockHost.IsShown(ToolWindowIds.Run), "The program ran after a build in the Build window, but its Run window is not in front");
        }
        finally
        {
            window.ApplySettings(new Passenger.App.Settings.AppSettings());
            window.Close();
        }
    }

    /// <summary>Waits for the build under way to end, and checks what the status bar says of it.</summary>
    private static void BuildToEnd(MainWindow window, string said)
    {
        Ui.WaitFor(() => !window.IsBuilding, "the build", TimeSpan.FromMinutes(2));
        Assert.True(window.Status?.StartsWith(said, StringComparison.Ordinal) == true, $"The build did not end saying \"{said}\": {Ui.Said(window)}");
    }

    [AvaloniaFact]
    public void A_web_project_whose_profile_launches_the_browser_opens_its_launch_url_once_it_listens_and_not_before()
    {
        // Each profile's port held from now until its site is about to listen there.
        using HeldPort browse = new();
        using HeldPort quiet = new();
        Write("Sample.slnx", "<Solution>\n  <Project Path=\"src/Hello/Hello.csproj\" />\n  <Project Path=\"src/Site/Site.csproj\" />\n</Solution>\n");
        Write("src/Site/Site.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>"));
        // Started, it listens only once the test lets it, to tell opening once it listens from opening once it started.
        string listen = Path.Combine(Folder, "listen");
        Write("src/Site/Program.cs", $"Console.WriteLine(\"Warming up\");\nwhile (!File.Exists(@\"{listen}\"))\n{{\n    Thread.Sleep(20);\n}}\n"
            + "WebApplication app = WebApplication.Create(args);\napp.MapGet(\"/orders\", () => \"Orders\");\napp.Run();\n");
        Write("src/Site/Properties/launchSettings.json", "{\n  \"profiles\": {\n"
            + $"    \"Browse\": {{ \"commandName\": \"Project\", \"launchBrowser\": true, \"launchUrl\": \"orders\", \"applicationUrl\": \"http://127.0.0.1:{browse.Port}\" }},\n"
            + $"    \"Quiet\": {{ \"commandName\": \"Project\", \"applicationUrl\": \"http://127.0.0.1:{quiet.Port}\" }}\n  }}\n}}\n");
        Restore.Run(Path.Combine(Folder, "src", "Site"));
        MainWindow window = Open();
        try
        {
            // As dotnet new web writes it, the project file names no output type: the Web SDK makes it an executable.
            Assert.Equal(["Hello", "Site: Browse", "Site: Quiet"], window.Configurations.Select(c => c.Name));
            Assert.True(window.SelectConfiguration("Site: Browse"));
            Ui.Launched(window, window.RunAsync(), "the site to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Output(window).Contains("Warming up", StringComparison.Ordinal), "the site to start", TimeSpan.FromMinutes(2));
            Ui.Hold(TimeSpan.FromSeconds(2), "the site warming up, its port refusing every connection: no browser is to open meanwhile");
            Assert.True(window.Browsed.Count == 0, $"The browser opened {window.Browsed.FirstOrDefault()} before the site listened");

            browse.LetGo(listen);
            Ui.WaitFor(() => window.Browsed.Count > 0, "the browser to open once the site listens", TimeSpan.FromSeconds(60));
            Uri opened = Assert.Single(window.Browsed);
            Assert.Equal($"http://127.0.0.1:{browse.Port}/orders", opened.AbsoluteUri);
            using (System.Net.Http.HttpClient client = new())
            {
                Assert.Equal("Orders", client.GetStringAsync(opened).GetAwaiter().GetResult());
            }
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning, "the site to stop");

            File.Delete(listen);
            Assert.True(window.SelectConfiguration("Site: Quiet"));
            Ui.Launched(window, window.RunAsync(), "the quiet site to start", TimeSpan.FromMinutes(2));
            quiet.LetGo(listen);
            Ui.WaitFor(() => Output(window).Contains("Now listening on:", StringComparison.Ordinal), "the quiet site to listen", TimeSpan.FromMinutes(2));
            Ui.Hold(TimeSpan.FromSeconds(1.5), "the quiet site running a while, listening: no browser is to open meanwhile");
            Assert.Single(window.Browsed);
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning, "the site to stop");
            window.Close();
        }
    }

    /// <summary>The pointer brought to rest on a character of a console.</summary>
    private static void Rest(MainWindow window, CodeEditor editor, int offset)
    {
        window.MouseMove(At(window, editor, offset));
        Ui.Settle();
    }

    /// <summary>A click on a character of a console, as a person clicks a link there.</summary>
    private static void Click(MainWindow window, CodeEditor editor, int offset)
    {
        Point at = At(window, editor, offset);
        window.MouseMove(at);
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Ui.Settle();
    }

    /// <summary>Over a character's left half, in the window, its line scrolled into view first.</summary>
    private static Point At(MainWindow window, CodeEditor editor, int offset)
    {
        int line = editor.Document.Snapshot.GetLineFromOffset(offset);
        Point top = editor.PointOf(offset);
        if (top.Y < 0 || top.Y + editor.LineHeight > editor.Bounds.Height - 20)
        {
            editor.ScrollTo(new Vector(0, Math.Max(0, (line * editor.LineHeight) - (editor.Bounds.Height / 3))));
            Ui.Settle();
            top = editor.PointOf(offset);
        }
        return editor.TranslatePoint(top + new Vector(2, editor.LineHeight / 2), window)!.Value;
    }

    [AvaloniaFact]
    public void A_build_that_cannot_start_says_why_and_leaves_nothing_waiting()
    {
        MainWindow window = Open();
        TestFolders.Delete(Folder);
        try
        {
            // Read before Build is pressed: the watcher's notice of its root going is not always given.
            window.ReloadSolution();
            Assert.Empty(window.Configurations);
            Task<BuildOutcome> built = window.BuildAsync();
            // Settle fails if the build left background work held.
            Ui.Settle();
            Assert.True(built.IsCompleted, "The build's caller never heard how it ended");
            Assert.Equal(BuildStatus.NotStarted, built.Result.Status);
            Assert.False(window.IsBuilding);
            Assert.Contains("is gone", window.Status, StringComparison.Ordinal);
            Assert.Contains("is gone", window.BottomPanel.Build.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.CreateDirectory(Folder);
            window.Close();
        }
    }

    /// <summary>A program that runs until it is ended: the Run window's Rerun while it runs stops it and runs it again, and the
    /// one run again, still running when the window closes, ends with it.</summary>
    [AvaloniaFact]
    public void The_run_windows_rerun_while_the_program_runs_stops_it_and_runs_it_again_and_a_program_still_running_ends_with_the_window()
    {
        string runs = Path.Combine(Folder, "program.pids");
        string started = Path.Combine(Folder, "program.pid");
        Write("src/Hello/Program.cs", $"File.WriteAllText(@\"{started}\", Environment.ProcessId.ToString());\n"
            + $"File.AppendAllText(@\"{runs}\", Environment.ProcessId + \"\\n\");\nThread.Sleep(Timeout.Infinite);\n");
        MainWindow window = Open();
        bool ending = false;
        try
        {
            window.Perform(AppActions.RunProgram);
            Ui.WaitFor(() => Runs(runs).Length == 1, "the program to start", TimeSpan.FromMinutes(2));
            int first = Runs(runs)[0];

            Button rerun = window.BottomPanel.Run.GetVisualDescendants().OfType<Button>().Single(b => ToolTip.GetTip(b) is string tip && tip.StartsWith("Rerun", StringComparison.Ordinal));
            Assert.True(rerun.IsEffectivelyEnabled, "The Run window's Rerun is greyed while its program runs");
            Ui.Click(rerun, settle: false);
            Ui.WaitFor(() => Runs(runs).Length == 2, "the program to run again", TimeSpan.FromMinutes(2));
            Assert.True(Processes.Gone(first), $"Rerun left the first run going: {Processes.Describe(first)}");
            Assert.True(window.IsRunning, "The program Rerun started again is not running");

            ending = true;
            AssertEndsWithTheWindow(window, started, "the program Run started again");
        }
        finally
        {
            if (!ending)
            {
                window.Close();
            }
        }

        static int[] Runs(string file) => File.Exists(file)
            ? [.. Ui.Shared(file).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)]
            : [];
    }

    /// <summary>A program Run built, built again by a dotnet build typed in the Terminal window, still starts with no
    /// build before it, though Passenger was started from a shell whose PWD names the folder through a link: the
    /// terminal's shell is told its folder by its real path, as the IDE's builds name it, since a build through another
    /// spelling of a project's path deletes what the IDE's build wrote and it did not (IncrementalClean),
    /// runtimeconfig.json and deps.json among them.</summary>
    [AvaloniaFact]
    public void A_program_run_built_and_built_again_in_the_terminal_starts_with_no_build_before_it()
    {
        string folder = Folder;
        // Windows' shells read no PWD.
        string? linked = OperatingSystem.IsWindows() ? null : Path.Combine(TestFolders.Create("build-link-"), "Sample");
        if (linked is not null)
        {
            TestFolders.LinkFolder(linked, PathNames.Canonical(folder));
        }
        MainWindow window = Open();
        try
        {
            Ui.Launched(window, window.RunAsync(), "the run built before it", TimeSpan.FromMinutes(2));
            Assert.Contains("Hello from the run", Ended(window), StringComparison.Ordinal);

            window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
            string? pwd = Environment.GetEnvironmentVariable("PWD");
            Environment.SetEnvironmentVariable("PWD", linked ?? pwd);
            try
            {
                Ui.Run(window, AppActions.ShowTerminal);
                Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
            }
            finally
            {
                Environment.SetEnvironmentVariable("PWD", pwd);
            }
            Passenger.Terminal.TerminalSession shell = window.TerminalPanel.Active!;
            Ui.WaitFor(() => window.TerminalPanel.View.IsFocused, "the keys to go to the terminal");
            // Its nodes end with it: what it writes is the same, and nothing of it outlives the test.
            window.KeyTextInput($"\"{Dotnet.Host()}\" build -nodeReuse:false && echo built-0 || echo built-1");
            Ui.Press(window, "ENTER");
            try
            {
                Ui.WaitFor(() => Said(shell) is not null, "the terminal's build to end", TimeSpan.FromMinutes(3));
            }
            catch (TimeoutException waited)
            {
                throw new TimeoutException($"{waited.Message}. The terminal shows:\n{TerminalTests.Screen(shell)}", waited);
            }
            Assert.True(Said(shell) == "built-0", $"The build typed in the terminal failed:\n{TerminalTests.Screen(shell)}");

            RunConfigurationsDialog dialog = DialogButtonsTests.Check(window.ShowRunConfigurations());
            dialog.BuildBox.IsChecked = false;
            Ui.Settle();
            Ui.Click(dialog.OkButton);
            Assert.False(window.Configuration?.BuildBeforeLaunch, "Edit Configurations kept Hello's build before launch");
            Ui.Launched(window, window.RunAsync(), "the run with no build before it");
            string output = Ended(window);
            Assert.True(output.Contains("Hello from the run", StringComparison.Ordinal) && output.Contains("Process finished with exit code 0.", StringComparison.Ordinal),
                $"After a build in the terminal, Hello did not start as the IDE's build left it:\n{output}");
        }
        finally
        {
            window.Close();
        }

        static string? Said(Passenger.Terminal.TerminalSession shell) =>
            TerminalTests.Screen(shell).Split('\n').Select(line => line.Trim()).LastOrDefault(line => line is "built-0" or "built-1");
    }

    [AvaloniaFact]
    public void A_build_still_running_when_the_window_closes_ends_with_it()
    {
        // A step of the build that would run for ten minutes, as a slow tool or a hung test would: its process names itself.
        Write("src/Hello/Hello.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <Target Name="Linger" BeforeTargets="Build" Condition="'$(DesignTimeBuild)' != 'true'">
                <Exec Condition="'$(OS)' != 'Windows_NT'" Command="echo $$ &gt; build.pid; exec sleep 600" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Set-Content -Path build.pid -Value $PID; Start-Sleep -Seconds 600&quot;" />
              </Target>
            </Project>
            """);
        MainWindow window = Open();
        window.Perform(AppActions.Build);
        AssertEndsWithTheWindow(window, Path.Combine(Folder, "src", "Hello", "build.pid"), "the build's step");
    }

    /// <summary>Waits for a process the window started to write its id to a file, closes the window, and checks that the
    /// process, and so whatever it holds (a port, its files, a watch on the sources), is gone, and that the IDE's process
    /// may end: the window's end finishes within its limit. Whatever a failure leaves running is ended.</summary>
    private static void AssertEndsWithTheWindow(MainWindow window, string idFile, string what)
    {
        int process = 0;
        try
        {
            process = Processes.WrittenId(idFile, $"{what} to start");
            int command = window.Runner.IsRunning ? window.Runner.ProcessId ?? 0 : 0;
            window.Close();
            Assert.True(window.Ended?.Wait(MainWindow.EndLimit + TimeSpan.FromSeconds(5)), "The window closed without ending what it started within its limit, so the IDE's process would not end");
            Assert.True(Processes.Gone(process), $"After the window closed, {what} went on running, holding what it held for the next run");
            Assert.True(Processes.Gone(command), $"After the window closed, the dotnet command that started {what} went on running (dotnet watch would start it again on a save)");
        }
        finally
        {
            Processes.Kill(process);
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning && !window.IsBuilding, "the window to hear the run and the build end");
        }
    }

    private static string RunToEnd(MainWindow window)
    {
        if (!window.Runner.IsRunning)
        {
            Ui.Press(window, Ui.Shortcut(AppActions.RunProgram));
        }
        return Ended(window);
    }

    /// <summary>The Run console once the run already started has ended; never a run of its own, since a short program can end
    /// before a test looks, and a Run pressed then would run the configuration again without what the test gave it.</summary>
    private static string Ended(MainWindow window)
    {
        Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the run to end");
        return Output(window);
    }

    private MainWindow Open() => Ui.OpenFolder(Folder);

    /// <summary>The solution with a second project, a library, which builds at the same time as the first.</summary>
    private void WriteTwoProjects()
    {
        Write("Sample.slnx", "<Solution>\n  <Folder Name=\"/src/\">\n    <Project Path=\"src/Hello/Hello.csproj\" />\n    <Project Path=\"src/Tools/Tools.csproj\" />\n  </Folder>\n</Solution>\n");
        Write("src/Tools/Tools.csproj", TestProjects.Library());
        Write("src/Tools/Tool.cs", "public static class Tool\n{\n    public static int Count => 3;\n}\n");
    }

    private void Write(string relative, string text) => TestFolders.Write(Folder, relative, text);

    /// <summary>MSBuild of a test's own, for what the IDE starts while it lasts (the IDE passes its environment on): a handshake
    /// salt no other build has, so no server or node another left is used and the test's builds start their own, as on a
    /// machine with none idle; and variables set as the test needs, put back at its end. What its builds leave ends with the
    /// tests' process, as with the IDE's.</summary>
    private sealed class OwnMSBuild : IDisposable
    {
        private readonly Dictionary<string, string?> before = [];

        public OwnMSBuild(params (string Name, string? Value)[] variables)
        {
            Set("MSBUILDNODEHANDSHAKESALT", Guid.NewGuid().ToString("N"));
            // A node it leaves waits two minutes to be reused, not fifteen.
            Set("MSBUILDNODECONNECTIONTIMEOUT", "120000");
            foreach ((string name, string? value) in variables)
            {
                Set(name, value);
            }
        }

        public void Dispose()
        {
            foreach ((string name, string? value) in before)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        private void Set(string name, string? value)
        {
            before.TryAdd(name, Environment.GetEnvironmentVariable(name));
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
