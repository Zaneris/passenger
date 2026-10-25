using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Explorer;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>A file is one file however its path is spelled. A repository opened by a lower-case path where case is ignored
/// (on Windows also by its 8.3 short names, elsewhere through a link) shows its changed file coloured, diffs the open file
/// itself, commits it and binds a breakpoint in it, though Git, MSBuild and the debugger name it by the disk's own names.
/// Its window starts no C# language server, which none of that asks. Debugged, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class PathsTests : IDisposable
{
    /// <summary>Why a spelling in other letter case is not tried on Linux.</summary>
    internal const string CaseTold = "Linux's file systems tell letter case apart, so a path in other letter case is another path, which is not there";

    private readonly string root = PathNames.Canonical(TestFolders.Create("paths-"));

    public enum Spelling
    {
        LowerCase,
        ShortNames,
    }

    public void Dispose() => TestFolders.Delete(root);

    [AvaloniaTheory]
    [InlineData(Spelling.LowerCase)]
    [InlineData(Spelling.ShortNames)]
    public void A_repository_opened_by_another_spelling_of_its_path_colours_diffs_debugs_and_commits_its_changed_file(Spelling spelling)
    {
        Assert.SkipWhen(spelling == Spelling.LowerCase && PathNames.Comparison == StringComparison.Ordinal, CaseTold);
        // A name with a space, which Windows gives an 8.3 name of its own.
        string repository = Path.Combine(root, "Shop Repository");
        TestFolders.Write(repository, ".gitignore", "bin/\nobj/\n");
        TestFolders.Write(repository, "Shop.csproj",
            TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
        string program = TestFolders.Write(repository, "Program.cs", "int total = 0;\nfor (int i = 1; i <= 3; i++)\n{\n    total += i;\n}\nConsole.WriteLine(total);\n");
        TestGit.Init(repository);
        File.WriteAllText(program, "int total = 10;\nfor (int i = 1; i <= 3; i++)\n{\n    total += i;\n}\nConsole.WriteLine(total);\n");
        Restore.Run(repository);

        string spelled = Spelled(repository, spelling);
        MainWindow window = Ui.OpenFolder(spelled, languageServer: false);

        Ui.WaitFor(() => window.Vcs.Changes.Any(c => c.Name == "Program.cs" && c.Kind == ChangeKind.Modified), "Git to list Program.cs as changed");
        ExplorerNode row = Assert.Single(window.ExplorerView.Tree.Rows, r => r.Name == "Program.cs");
        Ui.WaitFor(() => row.Change == ChangeKind.Modified, $"Program.cs to be coloured as changed in the Explorer, which has it at {row.Path} where Git has it at {window.Vcs.Changes[0].Path}");

        window.OpenFile(row.Path!);
        Ui.Settle();
        Assert.Equal(program, window.EditorArea.Active?.Path);
        Task<DiffWindow?> opening = window.ShowDiffAsync(Assert.Single(window.Vcs.Changes).Path);
        Ui.WaitFor(() => opening.IsCompleted, "the diff to open");
        DiffWindow diff = opening.Result!;
        Assert.True(ReferenceEquals(window.EditorArea.Active!.Text, diff.View.Right.Document), "Show Diff opened a copy of the open file rather than the file itself");
        diff.Close();
        Ui.Settle();

        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.GoTo(new TextPosition(3, 4));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Ui.Run(window, AppActions.Debug);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused, "the program to stop at the breakpoint");
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(3, LineMarkKind.BreakpointBound)), "the breakpoint to bind");
        Ui.WaitFor(() => editor.LineMarks.Contains(new LineMark(3, LineMarkKind.ExecutionPoint)), "the stop to be shown at the breakpoint in the open file");
        Ui.Run(window, AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");

        CommitView commit = window.CommitView;
        window.Perform(AppActions.Commit);
        Ui.WaitFor(() => commit.Chosen.Any(c => c.Name == "Program.cs"), "Program.cs in the Commit window");
        commit.Message = "Start at ten";
        Task<bool> committed = commit.CommitAsync(push: false);
        Ui.WaitFor(() => committed.IsCompleted, "the commit");
        Assert.True(committed.Result, window.Status);
        Assert.Equal("Start at ten", Git(repository, "log", "-1", "--format=%s").Trim());
        Ui.WaitFor(() => window.Vcs.Changes.Count == 0, "nothing left changed");
        window.Close();
    }

    /// <summary>Another spelling of an existing folder's path: all in lower case, or with Windows' 8.3 short names (through
    /// a symbolic link elsewhere, the other way another name reaches the same folder).</summary>
    private string Spelled(string path, Spelling spelling)
    {
        if (spelling == Spelling.LowerCase)
        {
            return path.ToLowerInvariant();
        }
        if (!OperatingSystem.IsWindows())
        {
            string link = Path.Combine(root, "Linked Repository");
            Directory.CreateSymbolicLink(link, path);
            return link;
        }
        char[] buffer = new char[1024];
        uint length = GetShortPathName(path, buffer, (uint)buffer.Length);
        Assert.True(length > 0 && length < buffer.Length, $"Windows gave no short name for {path} (error {Marshal.GetLastPInvokeError()})");
        string spelled = new(buffer, 0, (int)length);
        Assert.True(spelled.Contains('~', StringComparison.Ordinal), $"The volume of {path} makes no 8.3 names (fsutil 8dot3name query), so {spelled} is the long path again");
        return spelled;
    }

    private static string Git(string folder, params string[] arguments) => TestGit.Run(folder, arguments);

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string path, [Out] char[] shortPath, uint length);
}

/// <summary>A project the solution names in other letter case than its folder is the one its files build in and take their
/// namespace from, and a sibling folder whose name begins with the open folder's is not inside it.</summary>
public sealed class ProjectPathsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(TestFolders.Create("paths-"));

    public void Dispose() => TestFolders.Delete(root);

    [AvaloniaFact]
    public void A_project_the_solution_names_in_other_letter_case_than_its_folder_builds_from_its_file_and_gives_new_classes_its_namespace()
    {
        Assert.SkipWhen(PathNames.Comparison == StringComparison.Ordinal, PathsTests.CaseTold);
        string folder = Path.Combine(root, "Store");
        // The solution has the project's folder in lower case, which builds alike where case is ignored.
        TestFolders.Write(folder, "Store.slnx", "<Solution>\n  <Project Path=\"src/shop/Shop.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "src/Shop/Shop.csproj",
            TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>"));
        string program = TestFolders.Write(folder, "src/Shop/Program.cs", "Console.WriteLine(\"Shop\");\n");
        string models = Path.Combine(folder, "src", "Shop", "Models");
        Directory.CreateDirectory(models);
        Restore.Run(Path.Combine(folder, "Store.slnx"));
        // The project's files and the build are the MSBuild host's and dotnet's: no C# language server.
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(program);
        Ui.Settle();

        Assert.True(window.ExplorerView.Tree.Rows.Any(r => r.Path == program), "The Explorer could not show the open file in its project");

        window.Perform(AppActions.BuildSelected);
        Ui.WaitFor(() => !window.IsBuilding, "the build", TimeSpan.FromMinutes(2));
        Assert.True(window.Status == "Build succeeded", $"The build did not succeed: {Ui.Said(window)}");
        Assert.True(File.Exists(Path.Combine(folder, "src", "Shop", "bin", "Debug", TestProjects.Framework, "Shop.dll")), "Build Selected Projects did not build the open file's project");

        Assert.Null(window.CreateClass(models, "Order"));
        Assert.Equal("namespace Shop.Models;\n\npublic class Order\n{\n}\n", File.ReadAllText(Path.Combine(models, "Order.cs")));
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_in_a_sibling_folder_whose_name_begins_with_the_open_folders_is_not_shown_as_inside_it()
    {
        string app = Path.Combine(root, "app");
        string inside = TestFolders.Write(app, "Inside.cs", "class Inside { }\n");
        string outside = TestFolders.Write(Path.Combine(root, "app2"), "Outside.cs", "class Outside { }\n");
        MainWindow window = Ui.OpenFolder(app);
        window.OpenFile(outside);
        window.OpenFile(inside);
        Ui.Settle();

        Passenger.App.Navigation.SymbolPopup recent = window.ShowRecentFiles();
        Ui.WaitFor(() => recent.Rows.Count > 0, "the recent files");
        Assert.Equal(Path.Combine(root, "app2"), Assert.Single(recent.Rows, r => r.Name == "Outside.cs").Where);
        Assert.Null(window.ExplorerView.Tree.Reveal(outside));
        window.Close();
    }
}
