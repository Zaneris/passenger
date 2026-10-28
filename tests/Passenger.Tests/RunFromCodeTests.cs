using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A program is run from where it is written: the run icon beside a project's Main, a file-based program's first
/// line and a launch profile runs its program, as Run of that configuration does, and so does its row in the Explorer.</summary>
public sealed class RunFromCodeTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public RunFromCodeTests(RestoredFolders folders)
    {
        folder = folders.Take("runhere-", made =>
        {
            // What the program builds lies in the folder, which the test removes, rather than in the SDK's cache of the person's.
            TestFolders.Write(made, "Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <UseArtifactsOutput>true</UseArtifactsOutput>\n  </PropertyGroup>\n</Project>\n");
            TestFolders.Write(made, "Sample.slnx", "<Solution>\n  <Project Path=\"Hello/Hello.csproj\" />\n</Solution>\n");
            TestFolders.Write(made, "Hello/Hello.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
            TestFolders.Write(made, "Hello/Program.cs", """
                namespace Hello;

                public static class Program
                {
                    public static void Main(string[] args)
                    {
                        Console.WriteLine($"Hello from Main {string.Join(" ", args)}");
                    }
                }

                """);
            TestFolders.Write(made, "Hello/Properties/launchSettings.json", """
                {
                  "profiles": {
                    "Quiet": { "commandName": "Project" },
                    "Loud": { "commandName": "Project", "commandLineArgs": "LOUD" }
                  }
                }

                """);
            // Native AOT, on for a program by default, would restore its compiler's packages.
            TestFolders.Write(made, "tool.cs", "#:property PublishAot=false\nConsole.WriteLine(\"Hello from tool\");\n");
            return Path.Combine(made, "Sample.slnx");
        });
        Restore.Run(Path.Combine(folder, "tool.cs"));
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_run_icon_beside_Main_a_programs_first_line_and_a_launch_profile_runs_each_program()
    {
        MainWindow window = Ui.OpenFolder(Path.Combine(folder, "Sample.slnx"), languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Hello: Loud") && window.Configurations.Any(c => c.Name == "tool"),
                () => $"the configurations of Hello's profiles and of tool.cs; there are [{string.Join(", ", window.Configurations.Select(c => c.Name))}]", TimeSpan.FromMinutes(2));

            MenuItem[] items = GutterMenu(window, Path.Combine(folder, "Hello", "Program.cs"), "    public static void Main(string[] args)");
            Assert.Equal(["Run 'Hello: Quiet'", "Debug 'Hello: Quiet'", "Profile 'Hello: Quiet'", "Run 'Hello: Loud'", "Debug 'Hello: Loud'", "Profile 'Hello: Loud'"],
                items.Select(i => (string?)i.Header));
            Ui.Choose(window.RunMarkMenu, "Run 'Hello: Quiet'");
            Ran(window, "Hello from Main \n", "Main's icon");
            Assert.Equal("Hello: Quiet", window.Configuration?.Name);

            GutterMenu(window, Path.Combine(folder, "Hello", "Properties", "launchSettings.json"), "    \"Loud\": { \"commandName\": \"Project\", \"commandLineArgs\": \"LOUD\" }");
            Ui.Choose(window.RunMarkMenu, "Run 'Hello: Loud'");
            Ran(window, "Hello from Main LOUD\n", "the Loud profile's icon");

            items = GutterMenu(window, Path.Combine(folder, "tool.cs"), "#:property PublishAot=false");
            Assert.Equal(["Run 'tool'", "Debug 'tool'", "Profile 'tool'"], items.Select(i => (string?)i.Header));
            Ui.Choose(window.RunMarkMenu, "Run 'tool'");
            Ran(window, "Hello from tool\n", "tool.cs's icon");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A program's row in the Explorer runs it from its menu, a project's and a file-based program's alike.</summary>
    [AvaloniaFact]
    public void A_programs_Explorer_row_runs_it()
    {
        MainWindow window = Ui.OpenFolder(Path.Combine(folder, "Sample.slnx"), languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Hello: Loud") && window.Configurations.Any(c => c.Name == "tool"),
                () => $"the configurations of Hello's profiles and of tool.cs; there are [{string.Join(", ", window.Configurations.Select(c => c.Name))}]", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.ExplorerView.Tree.Root?.Children?.Any(c => c.Name == "tool") == true, "tool.cs's row in the Explorer");

            Ui.ChooseFromRowMenu(window, Row(window, "Hello"), "Run 'Hello: Quiet'");
            Ran(window, "Hello from Main \n", "Hello's row");

            Ui.ChooseFromRowMenu(window, Row(window, "tool"), "Run 'tool'");
            Ran(window, "Hello from tool\n", "tool's row");
            Assert.Equal("tool", window.Configuration?.Name);
        }
        finally
        {
            window.Close();
        }

        static ExplorerNode Row(MainWindow window, string name) => window.ExplorerView.Tree.Root!.Children!.Single(c => c.Name == name);
    }

    /// <summary>Waits for the program a run icon started to write what it says and end.</summary>
    private static void Ran(MainWindow window, string said, string from) =>
        Ui.WaitFor(() => Output(window).Contains(said, StringComparison.Ordinal) && Output(window).Contains("Process finished", StringComparison.Ordinal),
            () => $"the program {from} ran to write '{said.Trim()}' and end; the Run window has '{Output(window)}' and the status bar says {Ui.Said(window)}",
            TimeSpan.FromMinutes(2));

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    /// <summary>The menu of the run icon beside a file's line, the file opened and the icon clicked as a person does.</summary>
    private static MenuItem[] GutterMenu(MainWindow window, string path, string line)
    {
        Assert.Null(window.OpenFile(path));
        Ui.Settle();
        OpenDocument document = window.EditorArea.Active!;
        int at = Array.IndexOf(document.Text.Snapshot.ToString().Split('\n'), line);
        Assert.True(at >= 0, $"{Path.GetFileName(path)} has no line '{line}'");
        Ui.WaitFor(() => document.RunMarks.Any(m => m.Line == at),
            () => $"the run icon beside {line.Trim()} in {Path.GetFileName(path)}; it has icons on lines [{string.Join(", ", document.RunMarks.Select(m => m.Line))}]");
        CodeEditor shown = window.EditorArea.CodeEditor;
        double x = Enumerable.Range(0, 400).First(x => shown.IsOnGutterColumn(shown.Gutter!.Marks, new Point(x, 0))) + 3;
        Ui.Click(shown, new Point(x, shown.PointOf(document.Text.Snapshot.GetLineStart(at)).Y + (shown.LineHeight / 2)));
        return [.. Assert.IsType<MenuFlyout>(window.RunMarkMenu).Items.OfType<MenuItem>()];
    }
}
