using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Vcs;
using Passenger.App.Welcome;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Recent solutions and the Welcome screen, in the run's own state folder: the window a person uses keeps each
/// folder it opens, and a start that names nothing reopens the last unless Settings says not to. The Welcome screen
/// lists them newest first, narrowed as a search is typed, opened by Up, Down and Enter, removed for good by Delete; one
/// gone from disk is marked and refused with why. Clone Repository clones and opens, says why it could not, and stops
/// when canceled; File, Recent Solutions opens one with its recent files. File, Open takes the solution or project chosen
/// as the workspace, of a folder's several, kept and reopened as itself, and refuses anything else with why.</summary>
public sealed class WelcomeTests : IClassFixture<WelcomeTests.Workspaces>, IDisposable
{
    private readonly Workspaces workspaces;
    private readonly string settings = TestFolders.Create("settings-");
    private readonly string alpha = TestFolders.Create("alpha-");
    private readonly string beta = TestFolders.Create("beta-");

    public WelcomeTests(Workspaces workspaces)
    {
        this.workspaces = workspaces;
        workspaces.Reset();
    }

    public void Dispose()
    {
        TestFolders.Delete(settings);
        TestFolders.Delete(alpha);
        TestFolders.Delete(beta);
    }

    [AvaloniaFact]
    public void A_start_that_names_nothing_reopens_the_folder_opened_last_as_it_was_left()
    {
        using IDisposable theme = TestsTheme();
        File.WriteAllText(Path.Combine(alpha, "One.cs"), "class One { }\n");
        File.WriteAllText(Path.Combine(beta, "Two.cs"), "class Two { }\n");
        MainWindow first = Started(LaunchOptions.Empty);
        Assert.True(first.Folder is null && Shown(first), "A first start with nothing to reopen did not show the Welcome screen");
        // Each open reads the folder's documents off the window's thread before it begins: settled, it has.
        first.OpenFolder(alpha);
        Ui.Settle();
        first.OpenFile("One.cs");
        first.OpenFolder(beta);
        Ui.Settle();
        first.OpenFile("Two.cs");
        Ui.Settle();
        first.Close();

        MainWindow second = Started(LaunchOptions.Empty);
        Assert.True(second.Folder == PathNames.Canonical(beta), $"The start reopened {second.Folder ?? "nothing"} rather than {beta}, opened last");
        Assert.Equal("Two.cs", second.EditorArea.Active?.Name);
        Assert.False(Shown(second), "The Welcome screen shows over the folder reopened");

        second.Close();
        MainWindow third = Started(LaunchOptions.Empty with { Folder = alpha });
        Assert.Equal(PathNames.Canonical(alpha), third.Folder);
        third.Close();
    }

    [AvaloniaFact]
    public void With_reopening_off_the_Welcome_screen_lists_the_recent_ones_newest_first_narrowed_by_a_search_and_opens_one_by_its_keys()
    {
        using IDisposable theme = TestsTheme();
        MainWindow first = Started(LaunchOptions.Empty);
        first.OpenFolder(alpha);
        Ui.Settle();
        first.OpenFolder(beta);
        Ui.Settle();
        first.ApplySettings(first.Settings with { ReopenProjectsOnStartup = false });
        first.Close();

        MainWindow window = Started(LaunchOptions.Empty);
        Assert.True(window.Folder is null, $"Reopening is off, yet the start opened {window.Folder}");
        Assert.True(Shown(window), "Nothing is open, and the Welcome screen is not shown");
        Assert.Equal([Name(beta), Name(alpha)], window.Welcome.Rows.Select(r => r.Name));
        Assert.Equal([RecentRow.Home(PathNames.Canonical(beta)), RecentRow.Home(PathNames.Canonical(alpha))], window.Welcome.Rows.Select(r => r.Shown));

        Type(window, "alpha");
        Assert.Equal([Name(alpha)], window.Welcome.Rows.Select(r => r.Name));
        Ui.Press(window, "ESCAPE");
        Assert.Equal(2, window.Welcome.Rows.Count);
        Ui.Press(window, "UP");
        Assert.Equal(Name(beta), window.Welcome.Selected?.Name);
        Ui.Press(window, "DOWN");
        Ui.Press(window, "ENTER");
        Assert.True(window.Folder == PathNames.Canonical(alpha), $"Down and Enter on {Name(alpha)} opened {window.Folder ?? "nothing"}");
        window.Close();
    }

    [AvaloniaFact]
    public void One_removed_from_the_Welcome_screen_is_gone_for_good_and_one_deleted_on_disk_is_marked_and_says_so_when_opened()
    {
        using IDisposable theme = TestsTheme();
        string gamma = TestFolders.Create("gamma-");
        MainWindow first = Started(LaunchOptions.Empty);
        first.OpenFolder(alpha);
        Ui.Settle();
        first.OpenFolder(beta);
        Ui.Settle();
        first.ApplySettings(first.Settings with { ReopenProjectsOnStartup = false });
        first.Close();

        // Another window opened on the same state before the removal, which it writes over after it.
        MainWindow window = Started(LaunchOptions.Empty);
        MainWindow other = Started(LaunchOptions.Empty);
        RecentRow removed = window.Welcome.Rows.Single(r => r.Name == Name(alpha));
        Ui.Click(window.Welcome.RowControl(removed)!, new Point(12, 12));
        Ui.Press(window, "DELETE");
        Assert.Equal([Name(beta)], window.Welcome.Rows.Select(r => r.Name));
        window.Close();
        other.OpenFolder(gamma);
        Ui.Settle();
        other.Close();

        MainWindow next = Started(LaunchOptions.Empty);
        Assert.True(next.Welcome.Rows.Select(r => r.Name).SequenceEqual([Name(gamma), Name(beta)]),
            $"{Name(alpha)} was removed, yet the next start lists [{string.Join(", ", next.Welcome.Rows.Select(r => r.Name))}]");
        next.Close();

        string gone = PathNames.Canonical(beta);
        TestFolders.Delete(beta);
        MainWindow last = Started(LaunchOptions.Empty);
        RecentRow missing = last.Welcome.Rows.Single(r => r.Path == gone);
        Assert.True(missing.Missing, $"{gone} is gone from disk, and the Welcome screen does not mark it");
        Assert.False(last.Welcome.Rows.Single(r => r.Name == Name(gamma)).Missing);
        Ui.Press(last, "DOWN");
        Ui.Press(last, "ENTER");
        Assert.Null(last.Folder);
        Assert.Contains($"{RecentRow.Home(gone)} no longer exists", last.Welcome.Notice);
        Assert.Contains("no longer exists", last.Status);
        last.Close();
    }

    [AvaloniaFact]
    public void File_Recent_Solutions_lists_the_others_and_opens_the_one_chosen_with_its_recent_files()
    {
        using IDisposable theme = TestsTheme();
        File.WriteAllText(Path.Combine(alpha, "One.cs"), "class One { }\n");
        File.WriteAllText(Path.Combine(alpha, "Three.cs"), "class Three { }\n");
        MainWindow window = Started(LaunchOptions.Empty);
        window.OpenFolder(alpha);
        Ui.Settle();
        window.OpenFile("One.cs");
        window.OpenFile("Three.cs");
        window.OpenFolder(beta);
        Ui.Settle();

        NativeMenu recent = Ui.Submenu(Ui.Submenu(window.MenuBar, "File"), MainMenu.RecentSolutions);
        NativeMenuItem[] items = [.. recent.Items.OfType<NativeMenuItem>()];
        Assert.Equal([$"{Name(alpha)} – {RecentRow.Home(PathNames.Canonical(alpha))}"], items.Select(i => i.Header));
        items[0].Command!.Execute(null);
        Ui.Settle();
        InThisWindow(window);
        Assert.Equal(PathNames.Canonical(alpha), window.Folder);

        Passenger.App.Navigation.SymbolPopup files = window.ShowRecentFiles();
        Ui.WaitFor(() => files.Rows.Count > 0, "the recent files");
        Assert.Equal("Three.cs", window.EditorArea.Active?.Name);
        Assert.Equal(["One.cs"], files.Rows.Select(r => r.Name));
        window.Close();
    }

    [AvaloniaFact]
    public void Open_takes_the_solution_chosen_of_a_folders_several_and_Recent_Solutions_keeps_and_reopens_that_one()
    {
        using IDisposable theme = TestsTheme();
        string folder = Solutions();
        string a = PathNames.Canonical(Path.Combine(folder, "A.sln"));
        string b = PathNames.Canonical(Path.Combine(folder, "B.slnx"));
        MainWindow window = Started(LaunchOptions.Empty, workspaces.Server);

        // Named for its folder, A.sln is what the folder opens as on its own: only the choice makes B the workspace.
        Assert.Null(window.Open(folder));
        Ui.Settle();
        OpenedAs(window, a, "A", ["AppA"]);
        Assert.Null(window.Open(b));
        Ui.Settle();
        OpenedAs(window, b, "B", ["AppB"]);

        window.Open(beta);
        Ui.Settle();
        InThisWindow(window);
        NativeMenuItem[] items = [.. Ui.Submenu(Ui.Submenu(window.MenuBar, "File"), MainMenu.RecentSolutions).Items.OfType<NativeMenuItem>()];
        Assert.Equal([$"B – {RecentRow.Home(b)}", $"A – {RecentRow.Home(a)}"], items.Select(i => i.Header));
        items[0].Command!.Execute(null);
        Ui.Settle();
        InThisWindow(window);
        OpenedAs(window, b, "B", ["AppB"]);
        window.Close();

        MainWindow next = Started(LaunchOptions.Empty, workspaces.Server);
        OpenedAs(next, b, "B", ["AppB"]);
        next.Close();
    }

    [AvaloniaFact]
    public void Open_takes_a_project_beside_a_folders_solutions_as_the_workspace_alone_and_Recent_Solutions_reopens_it()
    {
        using IDisposable theme = TestsTheme();
        string tool = PathNames.Canonical(Path.Combine(Solutions(), "Tool.csproj"));
        MainWindow window = Started(LaunchOptions.Empty, workspaces.Server);

        Assert.Null(window.Open(tool));
        Ui.Settle();
        OpenedAs(window, tool, "Tool", ["Tool"]);
        ExplorerTree tree = window.ExplorerView.Tree;
        Assert.True(tree.IsSolutionView && tree.Rows[0].Kind == NodeKind.Project && tree.Rows.Any(r => r.Depth == 1 && r.Name == "Tool.cs"),
            $"The Solution view was to show Tool, its files under it; it shows [{string.Join(", ", tree.Rows.Select(r => $"{r.Kind} {r.Name}"))}]");
        Assert.Equal(("Tool", tool), (window.RecentSolutions[0].Name, window.RecentSolutions[0].Path));
        window.Close();

        MainWindow next = Started(LaunchOptions.Empty, workspaces.Server);
        OpenedAs(next, tool, "Tool", ["Tool"]);
        next.Close();
    }

    [AvaloniaFact]
    public void Open_opens_any_other_file_in_an_editor_a_binary_one_as_its_bytes_read_only_and_refuses_a_path_that_is_not_there()
    {
        using IDisposable theme = TestsTheme();
        string notes = Path.Combine(alpha, "notes.txt");
        File.WriteAllText(notes, "No solution\n");
        string blob = Path.Combine(alpha, "blob.bin");
        byte[] bytes = [(byte)'P', 0, (byte)'K', (byte)'\r', (byte)'\n', 0x7F, 0x90, 0xE9];
        File.WriteAllBytes(blob, bytes);
        MainWindow window = Started(LaunchOptions.Empty);

        string? why = window.Open(notes);
        Assert.True(why is null && window.EditorArea.Active?.Path == PathNames.Canonical(notes) && window.Folder is null,
            $"notes.txt was to open in an editor with no folder; Open said '{why}', shows {window.EditorArea.Active?.Path ?? "nothing"} and opened {window.Folder ?? "no folder"}");
        Assert.Equal("No solution\n", window.EditorArea.Active!.Text.Snapshot.ToString());

        why = window.Open(blob);
        Assert.True(why is null && window.EditorArea.Active?.Path == PathNames.Canonical(blob),
            $"blob.bin, a binary file, was to open as its bytes' text; Open said '{why}'");
        Assert.Equal("P\u2400K\u240D\n\u2421\u00B7\u00E9", window.EditorArea.Active!.Text.Snapshot.ToString());
        Assert.True(window.EditorArea.Active.IsReadOnly, "A binary file's tab should be read-only, since its text is no longer its bytes");
        window.Perform(AppActions.SaveAll);
        Ui.Settle();
        Assert.True(bytes.AsSpan().SequenceEqual(File.ReadAllBytes(blob)), "A binary file shown as text should never be written over");

        why = window.Open(Path.Combine(alpha, "Gone.sln"));
        Assert.True(why?.EndsWith("Gone.sln was not opened: it does not exist", StringComparison.Ordinal) == true && window.Folder is null,
            $"Gone.sln, which is not there, was to be refused with the reason; Open said '{why}' and opened {window.Folder ?? "nothing"}");
        Assert.True(window.Status == why && window.Welcome.Notice == why, $"The status bar says '{window.Status}' and the Welcome screen '{window.Welcome.Notice}', not why");
        window.Close();
    }

    [AvaloniaFact]
    public void Clone_Repository_clones_into_the_folder_chosen_and_opens_the_clone()
    {
        using IDisposable theme = TestsTheme();
        string source = TestFolders.Create("source-");
        File.WriteAllText(Path.Combine(source, "Readme.md"), "# Shared\n");
        TestGit.Init(source);
        string remotes = TestFolders.Create("remotes-");
        string bare = Path.Combine(remotes, "shared.git");
        TestGit.Run(remotes, "clone", "--bare", "-q", source, bare);
        string clones = TestFolders.Create("clones-");
        MainWindow window = Started(LaunchOptions.Empty);

        Assert.True(window.Perform(AppActions.CloneRepository));
        Ui.Settle();
        CloneDialog dialog = Assert.Single(window.OwnedWindows.OfType<CloneDialog>());
        dialog.Folder = clones;
        Type(dialog, bare);
        Assert.Equal(Path.Combine(clones, "shared"), dialog.Destination);
        Ui.Press(dialog, "ENTER");
        Ui.Settle();

        string cloned = PathNames.Canonical(Path.Combine(clones, "shared"));
        Assert.True(dialog.Problem is null, $"The clone failed: {dialog.Problem}");
        Assert.False(dialog.IsVisible, "The dialog stayed open over the clone made");
        Assert.True(window.Folder == cloned, $"The clone was made, but the window opened {window.Folder ?? "nothing"} rather than it");
        Assert.True(File.Exists(Path.Combine(cloned, "Readme.md")), "The clone does not hold the repository's file");
        Ui.WaitFor(() => window.Vcs.Repository is not null, "the clone's repository");
        Assert.Equal(cloned, window.RecentSolutions[0].Path);
        window.Close();
    }

    /// <summary>Clone Repository from a remote that asks for a login asks the person over the dialog, and clones once they
    /// sign in.</summary>
    [AvaloniaFact]
    public void Clone_Repository_asks_for_the_login_a_remote_asks_for_and_clones_once_signed_in()
    {
        using IDisposable theme = TestsTheme();
        string source = TestFolders.Create("source-");
        File.WriteAllText(Path.Combine(source, "Readme.md"), "# Shared\n");
        TestGit.Init(source);
        string served = TestFolders.Create("served-");
        using StandInGitRemote remote = new(served);
        TestGit.Run(served, "clone", "--bare", "-q", source, remote.Repository);
        string clones = TestFolders.Create("clones-");
        MainWindow window = Started(LaunchOptions.Empty);
        CloneDialog dialog = window.ShowCloneRepository();
        Ui.Settle();
        dialog.Folder = clones;
        Type(dialog, remote.Url);
        Ui.Press(dialog, "ENTER");

        Ui.PumpUntil(() => dialog.Question is { IsVisible: true }, $"the clone's question for the login {remote.Url} asks for");
        GitQuestionDialog asked = DialogButtonsTests.Check(dialog.Question!);
        Assert.Equal($"Sign in to {remote.Place}", asked.Title);
        asked.KeyTextInput(StandInGitRemote.User);
        Ui.Press(asked, "TAB");
        asked.KeyTextInput(StandInGitRemote.Password);
        Ui.Press(asked, "ENTER");
        Ui.Settle();
        Assert.True(dialog.Problem is null, $"The clone failed once signed in: {dialog.Problem}");
        Assert.True(File.Exists(Path.Combine(clones, "repo", "Readme.md")), "The clone made once signed in does not hold the repository's file");
        window.Close();
    }

    [AvaloniaFact]
    public void Tab_from_the_clone_URL_goes_to_the_directory_to_clone_into_and_then_to_Browse()
    {
        using IDisposable theme = TestsTheme();
        MainWindow window = Started(LaunchOptions.Empty);
        CloneDialog dialog = window.ShowCloneRepository();
        Ui.Settle();
        Ui.Press(dialog, "Tab");
        Assert.True(dialog.FocusManager?.GetFocusedElement() is TextBox { Text: { } typed } && typed == dialog.Folder,
            $"Tab from the URL went to {dialog.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"} rather than the directory to clone into");
        Ui.Press(dialog, "Tab");
        Assert.True(dialog.FocusManager?.GetFocusedElement() is Button { Content: "Browse…" }, "Tab from the directory did not go on to Browse…");
        dialog.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Clone_Repository_says_why_a_URL_could_not_be_cloned_and_leaves_nothing_behind()
    {
        using IDisposable theme = TestsTheme();
        string clones = TestFolders.Create("clones-");
        MainWindow window = Started(LaunchOptions.Empty);
        CloneDialog dialog = window.ShowCloneRepository();
        Ui.Settle();
        dialog.Folder = clones;

        Ui.Press(dialog, "ENTER");
        Assert.Equal("A repository URL is needed", dialog.Problem);

        string missing = Path.Combine(clones, "missing.git");
        Type(dialog, missing);
        Ui.Press(dialog, "ENTER");
        Ui.Settle();
        Assert.True(dialog.Problem?.Contains("does not exist", StringComparison.Ordinal) == true,
            $"Cloning {missing}, which is not there, was to say it does not exist; it says: {dialog.Problem ?? "nothing"}");
        Assert.True(dialog.IsVisible, "The dialog closed on a clone that failed, taking the URL to correct with it");
        Assert.Null(window.Folder);
        Assert.Empty(Directory.EnumerateFileSystemEntries(clones));
        dialog.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Cancel_stops_a_clone_under_way_and_removes_what_it_made()
    {
        using IDisposable theme = TestsTheme();
        string clones = TestFolders.Create("clones-");
        // A server that takes git's request and never answers it, as one far away and stuck would.
        using TcpListener server = new(IPAddress.Loopback, 0);
        server.Start();
        List<TcpClient> asked = [];
        Task<TcpClient> accepted = server.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
        int? git = null;
        EventHandler<ChildStart> started = (_, child) => git = child.Command.Contains(" clone --progress ", StringComparison.Ordinal) ? child.ProcessId : git;
        ChildProcesses.Started += started;
        MainWindow window = Started(LaunchOptions.Empty);
        try
        {
            CloneDialog dialog = window.ShowCloneRepository();
            Ui.Settle();
            dialog.Folder = clones;
            Type(dialog, $"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/stuck.git");
            // Pressed without waiting for the clone, which waits for the server.
            dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            dialog.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.None, null);
            asked.Add(Ui.AwaitPumped(accepted, "git to ask the server for the repository"));
            Assert.True(dialog.IsCloning, "git asked the server, but the dialog does not say it is cloning");
            Assert.True(Directory.Exists(Path.Combine(clones, "stuck")), "git asked the server before making the clone's folder");
            int[] running = [git!.Value, .. Processes.Descendants(git.Value)];

            // The dialog Escape closes takes no key's release.
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Ui.Settle();
            Assert.False(dialog.IsVisible, "Cancel left the dialog open");
            Assert.Null(window.Folder);
            Assert.All(running, id => Assert.True(Processes.Gone(id), $"Cancel left git running: {Processes.Describe(id)}"));
            Assert.False(Directory.Exists(Path.Combine(clones, "stuck")), "Cancel left the folder the clone had begun");
        }
        finally
        {
            ChildProcesses.Started -= started;
            foreach (TcpClient client in asked)
            {
                client.Dispose();
            }
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Close_Solution_saves_its_files_ends_what_it_runs_and_shows_the_Welcome_screen_with_it_first_among_the_recent_ones()
    {
        using IDisposable theme = TestsTheme();
        // A step of the build that would run for ten minutes, writing its process's id.
        string app = Path.Combine(alpha, "App");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "App.csproj"), $"""
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
        string program = Path.Combine(app, "Program.cs");
        File.WriteAllText(program, "Console.WriteLine(\"Hello\");\n");
        string solution = PathNames.Canonical(Path.Combine(alpha, "App.slnx"));
        File.WriteAllText(solution, "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        MainWindow window = Started(LaunchOptions.Empty with { Workspace = solution });
        int step = 0;
        try
        {
            window.OpenFile(program);
            Ui.Settle();
            int server = window.CSharpService?.Session?.Server.ProcessId ?? throw new InvalidOperationException("The language server did not start");

            // A build under way, and an edit made since it saved the files.
            window.Perform(AppActions.Build);
            step = Processes.WrittenId(Path.Combine(app, "build.pid"), "the build's step to start");
            window.KeyTextInput("// edited ");
            Assert.True(window.EditorArea.Active!.IsModified, "The edit typed did not reach the file");

            NativeMenuItem close = Ui.Submenu(window.MenuBar, "File").Items.OfType<NativeMenuItem>().Single(i => i.Header == "Close Solution");
            Assert.True(close.IsEnabled, "File > Close Solution cannot run with a solution open");
            close.Command!.Execute(null);
            Ui.Settle();

            Assert.True(window.Folder is null && Shown(window), $"Close Solution left {window.Folder ?? "no folder"} open, the Welcome screen {(Shown(window) ? "shown" : "not shown")}");
            Assert.Equal("Welcome to Passenger", window.Title);
            Assert.Equal(solution, window.Welcome.Rows.FirstOrDefault()?.Path);
            Assert.Empty(window.EditorArea.Documents);
            Assert.Empty(window.ExplorerView.Tree.Rows);
            Assert.Equal("// edited Console.WriteLine(\"Hello\");\n", File.ReadAllText(program));
            Assert.False(window.IsBuilding, "The build still runs once its solution is closed");
            Assert.True(Processes.Gone(step), $"The build's step went on running once its solution was closed: {Processes.Describe(step)}");
            Assert.Null(window.CSharpService);
            Assert.True(Processes.Gone(server), "The language server went on running once its solution was closed");
            Assert.False(MenuItem(window, "File", "Close Solution").IsEnabled, "Close Solution can still run with nothing open");

            File.Delete(Path.Combine(app, "build.pid"));
            Assert.Null(window.OpenRecent(solution));
            Ui.Settle();
            Assert.Equal(PathNames.Canonical(alpha), window.Folder);
            Assert.Equal("Program.cs", window.EditorArea.Active?.Name);
            Assert.False(Shown(window), "The Welcome screen shows over the solution opened again");
        }
        finally
        {
            Processes.Kill(step);
            window.Close();
        }
    }

    private static NativeMenuItem MenuItem(MainWindow window, string menu, string title) =>
        Ui.Submenu(window.MenuBar, menu).Items.OfType<NativeMenuItem>().Single(i => i.Header == title);

    /// <summary>The window a person starts, as the app makes it: its layout and settings kept in the test's state folder,
    /// then the command line taken; on a warm server when given one, which it takes up only for what it opens.</summary>
    private MainWindow Started(LaunchOptions options, Passenger.App.CSharp.WarmServer? warm = null)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, WarmServer = warm };
        window.Show();
        window.KeepLayout();
        window.KeepSettings();
        window.Start(options);
        Ui.Settle();
        return window;
    }

    private static string Name(string folder) => Path.GetFileName(folder);

    /// <summary>The Open Solution question, asked as another folder or solution opens over the one open, answered This
    /// Window by its button.</summary>
    private static void InThisWindow(MainWindow window)
    {
        OpenProjectDialog asked = window.Asking ?? throw new InvalidOperationException("Opening another folder over one open did not ask where");
        Ui.Click(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(asked).OfType<Button>().First(b => (string?)b.Content == OpenProjectDialog.ThisWindow));
    }

    /// <summary>The class's folder named A holding A.sln (AppA), B.slnx (AppB) and, beside them, the project Tool.csproj.</summary>
    private string Solutions() => PathNames.Canonical(workspaces.PathOf("A"));

    /// <summary>A folder of several workspaces, the class's own, restored once for the tests that open them, which only
    /// read it; and the warm server their windows share. All.slnx, beside A, is only what restores its three projects.</summary>
    public sealed class Workspaces : SharedProject
    {
        public Workspaces()
            : base("solutions-")
        {
            string folder = PathOf("A");
            string project = TestProjects.Library();
            Directory.CreateDirectory(Path.Combine(folder, "AppA"));
            Directory.CreateDirectory(Path.Combine(folder, "AppB"));
            File.WriteAllText(Path.Combine(folder, "AppA", "AppA.csproj"), project);
            File.WriteAllText(Path.Combine(folder, "AppB", "AppB.csproj"), project);
            File.WriteAllText(Path.Combine(folder, "Tool.csproj"), project);
            File.WriteAllText(Path.Combine(folder, "Tool.cs"), "class Tool { }\n");
            File.WriteAllText(Path.Combine(folder, "A.sln"),
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"AppA\", \"AppA\\AppA.csproj\", \"{0F7C6F3B-9D31-4B0B-8B9C-3E2B1D3F0A11}\"\nEndProject\n");
            File.WriteAllText(Path.Combine(folder, "B.slnx"), "<Solution>\n  <Project Path=\"AppB/AppB.csproj\" />\n</Solution>\n");
            File.WriteAllText(PathOf("All.slnx"), "<Solution>\n  <Project Path=\"A/AppA/AppA.csproj\" />\n  <Project Path=\"A/AppB/AppB.csproj\" />\n  <Project Path=\"A/Tool.csproj\" />\n</Solution>\n");
            // A build makes each project's bin, which a reset would take for a file the test added: made now, the folders stay as reset.
            foreach (string made in (string[])[folder, Path.Combine(folder, "AppA"), Path.Combine(folder, "AppB")])
            {
                Directory.CreateDirectory(Path.Combine(made, "bin"));
            }
        }
    }

    /// <summary>That the window has a solution or project as its workspace, as a person sees it and the language server
    /// loads it: its folder open, its name at the Explorer's top and in the title, its projects, and those the server
    /// opens (the solution, or the project alone).</summary>
    private static void OpenedAs(MainWindow window, string path, string name, string[] projects)
    {
        Lsp.RoslynSession? server = window.CSharpService?.Session;
        string has = $"the folder {window.Folder}, {window.ExplorerView.Tree.Rows.FirstOrDefault()?.Name} at the Explorer's top, the title " +
            $"'{window.Title}', the projects [{string.Join(", ", window.WorkspaceModel?.Projects.Select(p => p.Name) ?? [])}], and the language " +
            $"server opened {server?.Solution ?? string.Join(", ", server?.Projects ?? [])}";
        bool solution = path.EndsWith(".sln", StringComparison.Ordinal) || path.EndsWith(".slnx", StringComparison.Ordinal);
        Assert.True(window.Folder == Path.GetDirectoryName(path) && window.ExplorerView.Tree.Rows.FirstOrDefault()?.Name == name && window.Title == name
            && window.WorkspaceModel?.Projects.Select(p => p.Name).SequenceEqual(projects) == true
            && (solution ? server?.Solution == path : server is { Solution: null } && server.Projects.SequenceEqual([path])),
            $"{Path.GetFileName(path)} was to be the workspace; the window has {has}");
    }

    /// <summary>Whether the window shows its Welcome screen: in it, and not hidden.</summary>
    private static bool Shown(MainWindow window) => TopLevel.GetTopLevel(window.Welcome) == window && window.Welcome.IsEffectivelyVisible;

    /// <summary>Text typed where the keys are.</summary>
    private static void Type(TopLevel window, string text)
    {
        window.KeyTextInput(text);
        Ui.Settle();
    }

    /// <summary>Puts the tests' theme back once a window that keeps its settings has put the person's in force.</summary>
    private static IDisposable TestsTheme() => new ThemeBack(Avalonia.Application.Current!.RequestedThemeVariant);

    private sealed class ThemeBack(Avalonia.Styling.ThemeVariant? theme) : IDisposable
    {
        public void Dispose() => Avalonia.Application.Current!.RequestedThemeVariant = theme;
    }
}
