using System.IO.Compression;
using Avalonia.Headless.XUnit;
using Passenger.App;
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
