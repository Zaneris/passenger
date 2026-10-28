using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.Testing;
using Passenger.App.Debugging;
using Passenger.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A file-based program (<c>dotnet run app.cs</c>) of two files, app.cs including helper.cs and referencing a
/// package, opened as a folder or as its C# file alone: a project of its own, named by app.cs, whose included file is its
/// too, so the language server answers across both with no error; Run builds and runs it with its app.run.json profile's
/// arguments, Debug stops in the included file, and a program written beside it is one too. Debugged through the window,
/// so run alone.</summary>
[Collection(Alone.Name)]
public sealed class FileBasedProgramsTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("program-"));

    public FileBasedProgramsTests()
    {
        string feed = Directory.CreateDirectory(Path.Combine(folder, "feed")).FullName;
        string package = Path.Combine(feed, "Passenger.ProgramTest.1.0.0.nupkg");
        File.WriteAllBytes(package, StandInFeed.Package("Passenger.ProgramTest", "1.0.0"));
        using (ZipArchive zip = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            zip.CreateEntryFromFile(typeof(PathNames).Assembly.Location, "lib/net11.0/Passenger.Core.dll");
        }
        TestFolders.Write(folder, "nuget.config", """
            <configuration>
              <packageSources><clear /><add key="local" value="feed" /></packageSources>
              <config><add key="globalPackagesFolder" value="packages" /></config>
            </configuration>
            """);
        // What the program builds lies in its folder, which the test removes, rather than in the SDK's cache of the person's.
        TestFolders.Write(folder, "Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <UseArtifactsOutput>true</UseArtifactsOutput>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(folder, "app.cs", """
            #:package Passenger.ProgramTest@1.0.0
            #:property PublishAot=false
            #:include helper.cs
            int unused = 0;
            Console.WriteLine(Helper.Greet(string.Join(" ", args)));
            Console.WriteLine(typeof(Passenger.Core.PathNames).Name);

            """);
        TestFolders.Write(folder, "helper.cs", """
            static class Helper
            {
                public static string Greet(string name)
                {
                    int unused = 0;
                    return $"Hello, {name}";
                }
            }

            """);
        TestFolders.Write(folder, "app.run.json", """
            {
              "profiles": {
                "greet": { "commandName": "Project", "commandLineArgs": "Ada Lovelace" }
              }
            }
            """);
        // Native AOT, on for a program by default, would restore its compiler's packages, which the stand-in feed lacks.
        Restore.Run(App);
    }

    public void Dispose() => TestFolders.Delete(folder);

    private string App => Path.Combine(folder, "app.cs");

    private string Helper => Path.Combine(folder, "helper.cs");

    [AvaloniaFact]
    public void A_program_opened_as_a_folder_is_a_project_that_answers_across_its_files_runs_with_its_profile_and_stops_in_its_included_file()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Assert.Null(window.OpenFile(App));
            Holds(window);

            TestFolders.Write(folder, "tool.cs", "#!/usr/bin/env dotnet\n#:property PublishAot=false\nConsole.WriteLine(\"tool\");\n");
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "tool"), "tool.cs, written beside app.cs, to be a program of its own", TimeSpan.FromMinutes(1));
            Assert.Equal(2, window.Projects.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_program_opened_as_its_file_opens_with_its_folder_as_the_same_project()
    {
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        try
        {
            Assert.Null(window.Open(App));
            Ui.WaitFor(() => window.Folder is { } open && PathNames.Same(open, folder) && window.EditorArea.Active?.Path is { } shown && PathNames.Same(shown, App),
                "app.cs open in its folder's window");
            Holds(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A program beside a solution is the folder's, as in a folder with no solution: a project row of the Solution
    /// view after the solution's own, with its run configuration, its file served in its project, and Run and Debug
    /// building and launching it; Build builds the solution and the program.</summary>
    [AvaloniaFact]
    public void A_program_beside_a_solution_is_listed_after_its_projects_and_runs_and_debugs()
    {
        TestFolders.Write(folder, "Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(folder, "Lib/Shapes.cs", "namespace Lib;\n\npublic static class Shapes\n{\n}\n");
        TestFolders.Write(folder, "Tools.slnx", "<Solution>\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Assert.Equal("Tools", window.Solution?.Name);
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "app: greet"), "app.cs beside the solution to be a program with its app.run.json profile's configuration",
                TimeSpan.FromMinutes(2));
            Assert.Equal(["Lib.csproj", "app.cs"], window.Projects.Select(p => Path.GetFileName(p.Path)).Order(StringComparer.Ordinal));
            Ui.WaitFor(() => window.ExplorerView.Tree.Root?.Children is [{ Name: "Lib" }, { Name: "app", IsProgram: true }],
                () => $"the Solution view to list Lib, then the program app; it lists {string.Join(", ", window.ExplorerView.Tree.Root?.Children?.Select(c => c.Name) ?? [])}");
            Assert.Equal("2 projects", window.ExplorerView.Tree.Root!.Detail);
            Ui.WaitFor(() => window.Projects.Single(p => PathNames.Same(p.Path, App)).IsEvaluated, "the program evaluated", TimeSpan.FromMinutes(2));
            ExplorerNode program = window.ExplorerView.Tree.Root.Children![1];
            window.ExplorerView.Tree.Expand(program);
            Assert.Equal(["Dependencies", "app.cs", "helper.cs"], program.Children!.Select(c => c.Name));
            Assert.Equal([Path.Combine(folder, "Tools.slnx"), App], window.WorkspaceModel!.BuildTargets(window.Projects));
            Assert.Null(window.OpenFile(App));
            WaitForWarning(window, App, "app.cs");

            // A program written beside them is found and loaded by the same language server, which is not started again.
            CSharpService? served = window.CSharpService;
            TestFolders.Write(folder, "tools/tool.cs", "#:property PublishAot=false\nint unused = 0;\nConsole.WriteLine(\"tool\");\n");
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "tool"), "tools/tool.cs, written beside the solution, to be a program of its own", TimeSpan.FromMinutes(1));
            Assert.Same(served, window.CSharpService);
            Assert.Null(window.OpenFile(Path.Combine(folder, "tools", "tool.cs")));
            WaitForWarning(window, Path.Combine(folder, "tools", "tool.cs"), "tool.cs");
            RunsAndStops(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A second Run of a program nothing of which changed builds nothing, the Build window saying it is up to date;
    /// a change to a file it includes builds it first, and the run shows the change.</summary>
    [AvaloniaFact]
    public void A_second_run_of_an_unchanged_program_builds_nothing_and_a_change_to_its_included_file_builds_it()
    {
        List<string> builds = [];
        EventHandler<ChildStart> started = (_, child) =>
        {
            if (child.Command.Contains(" build ", StringComparison.Ordinal) && child.Command.Contains("app.cs", StringComparison.Ordinal))
            {
                lock (builds)
                {
                    builds.Add(child.Command);
                }
            }
        };
        ChildProcesses.Started += started;
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "app: greet"), "app.cs's configuration", TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("app: greet"));
            Ran(window, builds, built: true, "Hello, Ada Lovelace", "its first run");
            Ran(window, builds, built: false, "Hello, Ada Lovelace", "a run with nothing changed");

            File.WriteAllText(Helper, File.ReadAllText(Helper).Replace("$\"Hello, {name}\"", "$\"Goodbye, {name}\"", StringComparison.Ordinal));
            Ran(window, builds, built: true, "Goodbye, Ada Lovelace", "a run after the file it includes changed");
            Ran(window, builds, built: false, "Goodbye, Ada Lovelace", "a run with nothing changed since");
        }
        finally
        {
            ChildProcesses.Started -= started;
            window.Close();
        }
    }

    /// <summary>Runs the program chosen, holding it to a dotnet build of it or none, and to printing what it should.</summary>
    private static void Ran(MainWindow window, List<string> builds, bool built, string printed, string which)
    {
        int before;
        lock (builds)
        {
            before = builds.Count;
        }
        Ui.Launched(window, window.RunAsync(), $"app.cs's run ({which})", TimeSpan.FromMinutes(2));
        Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), $"the program to finish ({which})", TimeSpan.FromMinutes(2));
        Assert.Contains(printed, Output(window), StringComparison.Ordinal);
        int ran;
        lock (builds)
        {
            ran = builds.Count - before;
        }
        window.BottomPanel.Build.Flush();
        string console = window.BottomPanel.Build.Document.Snapshot.ToString();
        bool skipped = console.Contains(BuildRunController.UpToDateSaid("app"), StringComparison.Ordinal);
        Assert.True(built ? ran == 1 && !skipped : ran == 0 && skipped,
            $"{which} {(built ? "was not built first" : "was built though nothing it reads changed")}: {ran} dotnet builds of app.cs started, and the Build window says:\n{console}");
    }

    /// <summary>Write to Launch Profile writes a program's generated configuration's changed arguments into its profile in
    /// app.run.json, and Save as Launch Profile saves one made here as a new profile there, which generates it from then on
    /// and Run runs with its arguments.</summary>
    [AvaloniaFact]
    public void Write_and_save_as_launch_profile_take_a_programs_app_run_json()
    {
        string settings = Path.Combine(folder, "app.run.json");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "app: greet"), "app.cs's configuration", TimeSpan.FromMinutes(2));
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Assert.True(dialog.Choose("app: greet"));
            dialog.ArgumentsBox.Text = "Grace Hopper";
            Ui.Settle();
            Assert.True(dialog.WriteButton.IsEffectivelyEnabled, $"Write to Launch Profile cannot be used on a program's profile: {ToolTip.GetTip(dialog.WriteButton)}");
            Ui.Click(dialog.WriteButton);
            Assert.True(dialog.Problem.Length == 0, $"Write to Launch Profile was refused: {dialog.Problem}");
            Assert.Contains("\"commandLineArgs\": \"Grace Hopper\"", File.ReadAllText(settings), StringComparison.Ordinal);
            Assert.Equal("Written to its launch profile in app.run.json: Program arguments.", dialog.Changes);

            Ui.Click(dialog.AddButton);
            Ui.Click(Assert.IsType<ContextMenu>(dialog.AddMenu).Items.OfType<MenuItem>().First());
            dialog.NameBox.Text = "app: Staging";
            dialog.ArgumentsBox.Text = "Alan Turing";
            Ui.Settle();
            Assert.True(dialog.SaveAsProfileButton.IsEffectivelyEnabled, $"Save as Launch Profile cannot be used on a program's configuration: {ToolTip.GetTip(dialog.SaveAsProfileButton)}");
            Ui.Click(dialog.SaveAsProfileButton);
            Assert.True(dialog.Problem.Length == 0, $"Save as Launch Profile was refused: {dialog.Problem}");
            Assert.StartsWith("Saved as the launch profile Staging in app's app.run.json: it is now app: Staging, generated from that profile", dialog.Changes, StringComparison.Ordinal);
            Assert.Equal(["greet", "Staging"], LaunchProfile.Read(settings).Select(p => p.Name));
            Ui.Click(dialog.OkButton);

            Assert.True(window.SelectConfiguration("app: Staging"));
            Ui.Launched(window, window.RunAsync(), "app.cs's run of its saved profile", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the program to finish", TimeSpan.FromMinutes(2));
            Assert.Contains("Hello, Alan Turing\n", Output(window), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A program holding xUnit tests (<c>#:package xunit.v3</c>) is a test project of its own: the Unit Tests window
    /// shows its tests from its code, and Run All builds it and runs them, each passing or failing as it does.</summary>
    [AvaloniaFact]
    public void A_programs_xunit_tests_show_in_the_tests_window_and_run_from_it()
    {
        // The run's own NuGet configuration, whose package cache holds xunit.v3 as the tests' own project restores it.
        string tests = PathNames.Canonical(TestFolders.Create("program-tests-"));
        try
        {
            TestFolders.Write(tests, "Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <UseArtifactsOutput>true</UseArtifactsOutput>\n  </PropertyGroup>\n</Project>\n");
            string sums = TestFolders.Write(tests, "sums.cs", """
                #:package xunit.v3@3.2.2
                #:property PublishAot=false
                using Xunit;

                public class SumTests
                {
                    [Fact]
                    public void Two_and_two_make_four() => Assert.Equal(4, 2 + 2);

                    [Fact]
                    public void Two_and_two_make_five() => Assert.Equal(5, 2 + 2);
                }

                """);
            Restore.Run(sums);
            MainWindow window = Ui.OpenFolder(tests, languageServer: false);
            try
            {
                Ui.WaitFor(() => window.Projects is [{ IsTestProject: true }], "sums.cs to be a program holding tests", TimeSpan.FromMinutes(2));
                window.Perform(AppActions.ShowTests);
                Ui.WaitFor(() => Method(window, "Two_and_two_make_four") is not null && Method(window, "Two_and_two_make_five") is not null,
                    () => $"the Unit Tests window to show the program's tests from its code; it shows {string.Join(", ", window.Tests.Tree.Rows.Select(r => r.Name))}");

                Assert.True(window.Perform(AppActions.RunAllTests));
                Ui.WaitFor(() => !window.Tests.IsBusy && Method(window, "Two_and_two_make_four")?.State is TestState.Passed or TestState.Failed,
                    () => $"the run of the program's tests ({window.BottomPanel.Tests.Status})", TimeSpan.FromMinutes(3));
                Assert.True(Method(window, "Two_and_two_make_four")!.State == TestState.Passed,
                    $"The program's passing test did not pass: {Method(window, "Two_and_two_make_four")!.Details}\n{window.BottomPanel.Tests.Status}");
                Assert.Equal(TestState.Failed, Method(window, "Two_and_two_make_five")!.State);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            TestFolders.Delete(tests);
        }
    }

    private static TestNode? Method(MainWindow window, string name) =>
        window.Tests.Tree.Roots.SelectMany(r => r.Methods).FirstOrDefault(m => m.Name == name);

    /// <summary>Files a program includes from another folder and by a wildcard, each with directives of its own, are the
    /// program's code rather than programs of their own: the folder's one project is the program, compiling them, and its
    /// run prints what each gives.</summary>
    [AvaloniaFact]
    public void Files_a_program_includes_from_another_folder_or_by_a_wildcard_are_never_programs_themselves()
    {
        TestFolders.Write(folder, "app.cs", """
            #:package Passenger.ProgramTest@1.0.0
            #:property PublishAot=false
            #:include helper.cs
            #:include shared/util.cs
            #:include lib/*.cs
            Console.WriteLine(Helper.Greet(Util.Name + " " + Extra.Name));

            """);
        TestFolders.Write(folder, "shared/util.cs", "#:property Nullable=enable\nstatic class Util\n{\n    public const string Name = \"util\";\n}\n");
        TestFolders.Write(folder, "lib/extra.cs", "#:property ImplicitUsings=enable\nstatic class Extra\n{\n    public const string Name = \"extra\";\n}\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects is [{ IsEvaluated: true }], () => $"app.cs to be the folder's one program, evaluated; the projects are {string.Join(", ", window.Projects.Select(p => p.Path))}",
                TimeSpan.FromMinutes(2));
            ProjectModel program = Assert.Single(window.Projects);
            Assert.True(PathNames.Same(program.Path, App), $"The folder's one project is {program.Path}, not the program app.cs");
            Assert.True(program.Holds(Path.Combine(folder, "shared", "util.cs")) && program.Holds(Path.Combine(folder, "lib", "extra.cs")),
                $"app.cs does not compile the files it includes: {string.Join(", ", program.Evaluated!.Items.Where(i => i.Type == "Compile").Select(i => i.Path))}");
            Assert.True(window.SelectConfiguration("app: greet"));
            Ui.Launched(window, window.RunAsync(), "app.cs to build and run", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the program to finish", TimeSpan.FromMinutes(2));
            Assert.Contains("Hello, util extra\n", Output(window), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A program that references another (<c>#:ref lib.cs</c>, a library of its own) has that program for a project
    /// too, which the language server answers across into, and Run builds and runs the two.</summary>
    [AvaloniaFact]
    public void A_program_referencing_another_has_it_for_a_project_answered_across_and_run_with_it()
    {
        TestFolders.Write(folder, "lib.cs", """
            #:property OutputType=Library
            #:property PublishAot=false
            public static class Lib
            {
                public static string Shout(string text) => text.ToUpperInvariant();
            }

            """);
        TestFolders.Write(folder, "app.cs", """
            #:property PublishAot=false
            #:ref lib.cs
            int unused = 0;
            Console.WriteLine(Lib.Shout("referenced"));

            """);
        Restore.Run(App);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "app: greet") && window.Projects.All(p => p.IsEvaluated), "the two programs evaluated",
                TimeSpan.FromMinutes(2));
            Assert.Equal(["app.cs", "lib.cs"], window.Projects.Select(p => Path.GetFileName(p.Path)).Order(StringComparer.Ordinal));
            Assert.Contains(Path.Combine(folder, "lib.cs"), window.Projects.Single(p => PathNames.Same(p.Path, App)).ProjectReferences);
            Assert.Null(window.OpenFile(App));
            WaitForWarning(window, App, "app.cs");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "Lib.Sh");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == "lib.cs", "Go to Definition of Lib.Shout to open lib.cs");

            Assert.True(window.SelectConfiguration("app: greet"));
            Ui.Launched(window, window.RunAsync(), "app.cs to build and run", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the program to finish", TimeSpan.FromMinutes(2));
            Assert.Contains("REFERENCED\n", Output(window), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What a person gets of the program in a window open on its folder with app.cs shown.</summary>
    private void Holds(MainWindow window)
    {
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "app: greet"), "app.cs to be a program with its app.run.json profile's configuration",
            TimeSpan.FromMinutes(2));
        ProjectModel program = Assert.Single(window.Projects);
        Assert.True(PathNames.Same(program.Path, App), $"The folder's one project is {program.Path}, not the program app.cs");
        Assert.Equal("Exe", program.OutputType);
        Assert.Contains(program.Packages, p => p.Name == "Passenger.ProgramTest" && p.Version == "1.0.0");

        // An unused variable is warned of only in the program's project: a file outside it would be told no more than its syntax.
        WaitForWarning(window, App, "app.cs");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "Helper.Gr");
        Ui.Run(window, AppActions.GoToDeclaration);
        Ui.WaitFor(() => window.EditorArea.Active?.Path is { } shown && PathNames.Same(shown, Helper), "Go to Definition of Helper.Greet to open helper.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Contains("public static string Greet", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line), StringComparison.Ordinal);
        WaitForWarning(window, Helper, "helper.cs");
        RunsAndStops(window);
    }

    /// <summary>Run prints the program's output with its profile's arguments, and Debug stops at a breakpoint in its included file.</summary>
    private void RunsAndStops(MainWindow window)
    {
        Assert.True(window.SelectConfiguration("app: greet"));
        Ui.Launched(window, window.RunAsync(), "app.cs to build and run", TimeSpan.FromMinutes(2));
        Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the program to finish", TimeSpan.FromMinutes(2));
        Assert.Contains("Hello, Ada Lovelace\nPathNames\n", Output(window), StringComparison.Ordinal);

        int line = Array.FindIndex(File.ReadAllLines(Helper), l => l.Contains("return $\"Hello", StringComparison.Ordinal));
        window.Debugger.Breakpoints.Toggle(Helper, line);
        Ui.Launched(window, window.DebugAsync(), "app.cs to build and start under the debugger", TimeSpan.FromMinutes(2));
        try
        {
            Ui.WaitFor(() => window.Debugger.Session?.State is DebugState.Paused or DebugState.Ended && (window.Debugger.Stop is not null || window.Debugger.Session.State == DebugState.Ended),
                "the program to stop at the breakpoint in helper.cs", TimeSpan.FromMinutes(1));
            Assert.True(window.Debugger.Stop?.Top is { Source.Path: { } at } && PathNames.Same(at, Helper) && window.Debugger.Stop.Top.Line == line + 1,
                $"The program did not stop at line {line + 1} of helper.cs but at {window.Debugger.Stop?.Top?.Source?.Path}:{window.Debugger.Stop?.Top?.Line} ({window.Status})");
        }
        finally
        {
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        }
    }

    /// <summary>Waits for the server's warning on the unused variable in an open file, then holds the file to no error.</summary>
    private static void WaitForWarning(MainWindow window, string path, string name)
    {
        IEnumerable<Passenger.Lsp.Diagnostic> Diagnostics() =>
            window.EditorArea.Documents.FirstOrDefault(d => PathNames.Same(d.Path, path)) is { } open && window.CSharpService?.For(open) is { } served ? served.Diagnostics : [];
        Ui.WaitFor(() => Diagnostics().Any(d => d.Code?.ToString() == "CS0219"), $"the server's warning on {name}'s unused variable, which it gives only in the program's project",
            TimeSpan.FromMinutes(2));
        Assert.Empty(Diagnostics().Where(d => d.Severity == Passenger.Lsp.DiagnosticSeverity.Error).Select(d => $"{d.Code}: {d.Message}"));
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();
}
