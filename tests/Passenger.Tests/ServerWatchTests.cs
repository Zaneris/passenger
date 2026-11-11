using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.Tests;

/// <summary>What the C# language server registered to hear of in a folder only Passenger's watchers of its own watch for
/// it, a project's obj: a burst there, a build's or a script's, more at once than macOS's watcher names, still reaches the
/// server, every file of it, as the folder's watcher's bursts do, and so does what a project below another's writes in its
/// own obj. The class's windows share one project, restored once, but that last test's, which makes its own.</summary>
public sealed class ServerWatchTests : IClassFixture<ServerWatchTests.Project>
{
    private const int Files = 1000;

    /// <summary>How many sources a folder moved in or out holds.</summary>
    private const int Moved = 50;

    private readonly Project project;

    public ServerWatchTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>A build that writes a thousand sources into obj side by side, as a generator emitting its files does, and
    /// rewrites the project's restore assets among them: the server is told of every source and of the assets.</summary>
    [AvaloniaFact]
    public void A_builds_burst_into_obj_reaches_the_server_every_file_and_the_assets_it_rewrote()
    {
        MainWindow window = project.Open("Program.cs");
        WatchedFiles watched = Watched(window);
        string assets = project.PathOf(Path.Combine("obj", "project.assets.json"));
        DateTime assetsBefore = File.GetLastWriteTimeUtc(assets);
        Told told = new(watched, name => name.StartsWith("Burst", StringComparison.Ordinal) || name == "project.assets.json", gone: false);
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        window.Perform(AppActions.Build);
        Ui.WaitFor(() => !window.IsBuilding, "the build to end", TimeSpan.FromMinutes(3));
        double built = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds;
        Assert.True(window.Status?.Contains("succeeded", StringComparison.Ordinal) == true, $"The build did not succeed: {window.Status}");
        Assert.True(File.GetLastWriteTimeUtc(assets) > assetsBefore, "The build did not rewrite the project's restore assets");
        Assert.Equal(Files, Directory.GetFiles(project.PathOf(Path.Combine("obj", "burst")), "Burst*.cs").Length);
        // Pumped, not settled, so the time is when it happened, not when the work after it ended.
        bool heard = Ui.Pumped(() => told.Count == Files + 1, TimeSpan.FromSeconds(90));
        double after = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds;
        told.Dispose();
        Assert.True(heard, $"Of the {Files} sources a build wrote into obj and its restore assets, the language server was told of {told.Count}"
            + $"{(told.Names.Contains("project.assets.json") ? "" : ", not the assets")}: {WatchLog(window)}");
        TestContext.Current.TestOutputHelper?.WriteLine($"A build writing {Files} sources into obj and its assets ended {built:0.00} s after it began; "
            + $"the language server was told of every one {after:0.00} s after it began, in {told.Notifications} notifications; {Recovered(window)}; {Records(window)}");
        window.Close();
    }

    /// <summary>A thousand sources in obj deleted side by side, as a clean or a script deletes them: the server, which hears
    /// of obj only from Passenger's own watcher of it, is told of every one as gone.</summary>
    [AvaloniaFact]
    public void A_thousand_files_deleted_side_by_side_in_obj_each_reach_the_server_as_gone()
    {
        MainWindow window = project.Open("Program.cs");
        WatchedFiles watched = Watched(window);
        string folder = Directory.CreateDirectory(project.PathOf(Path.Combine("obj", "gone"))).FullName;
        string Gone(int i) => Path.Combine(folder, $"Gone{i}.cs");
        Told made = new(watched, name => name.StartsWith("Gone", StringComparison.Ordinal), gone: false);
        Parallel.For(0, Files, i => File.WriteAllText(Gone(i), $"static class Gone{i}\n{{\n}}\n"));
        bool heardMade = Ui.Pumped(() => made.Count == Files, TimeSpan.FromSeconds(90));
        made.Dispose();
        Assert.True(heardMade, $"Of the {Files} sources written into obj side by side, the language server was told of {made.Count}: {WatchLog(window)}");

        Told gone = new(watched, name => name.StartsWith("Gone", StringComparison.Ordinal), gone: true);
        long deleted = System.Diagnostics.Stopwatch.GetTimestamp();
        Parallel.For(0, Files, i => File.Delete(Gone(i)));
        // Pumped, not settled, so the time is when it happened, not when the work after it ended.
        bool heard = Ui.Pumped(() => gone.Count == Files, TimeSpan.FromSeconds(90));
        double after = System.Diagnostics.Stopwatch.GetElapsedTime(deleted).TotalSeconds;
        gone.Dispose();
        Assert.True(heard, $"Of the {Files} sources deleted side by side in obj, the language server was told {gone.Count} went: {WatchLog(window)}");
        TestContext.Current.TestOutputHelper?.WriteLine($"{Files} sources deleted side by side in obj: the language server was told of every one {after:0.00} s after, "
            + $"in {gone.Notifications} notifications; {Recovered(window)}; {Records(window)}");
        window.Close();
    }

    /// <summary>A source written in obj, which the server is told came, then deleted unheard, as a burst on a busy machine
    /// drops the deletion's event: obj's watcher took the source into its record as it handed it over, so the listing
    /// after the burst finds it gone, and the server is told it went.</summary>
    [AvaloniaFact]
    public void A_source_told_come_then_deleted_unheard_in_obj_reaches_the_server_as_gone()
    {
        MainWindow window = project.Open("Program.cs");
        WatchedFiles watched = Watched(window);
        string obj = PathNames.Canonical(project.PathOf("obj"));
        WatchedTree tree = window.CSharpService!.Watching!.Of(obj)!;
        Ui.WaitFor(() => tree.Ready.IsCompleted, "obj's watcher to have recorded what is in it");
        List<FileSystemWatcher> watchers = [.. tree.Watches.Values];
        string source = Path.Combine(obj, "Unheard.cs");
        using Told made = new(watched, name => name == "Unheard.cs", gone: false);
        using Told gone = new(watched, name => name == "Unheard.cs", gone: true);
        File.WriteAllText(source, "static class Unheard\n{\n}\n");
        Assert.True(Ui.Pumped(() => made.Count == 1, TimeSpan.FromSeconds(30)), $"The language server was not told of the source written in obj: {WatchLog(window)}");
        bool heardGone;
        try
        {
            watchers.ForEach(w => w.EnableRaisingEvents = false);
            File.Delete(source);
            FolderWatchTests.RaiseError(tree.Watches[tree.Top], new InternalBufferOverflowException("Too many changes at once; directory listing is required."));
            heardGone = Ui.Pumped(() => gone.Count == 1, TimeSpan.FromSeconds(30));
        }
        finally
        {
            watchers.ForEach(w => w.EnableRaisingEvents = true);
        }
        Assert.True(heardGone, $"The language server was never told the source deleted unheard in obj went, so it keeps its document and its problems: {WatchLog(window)}");
        window.Close();
    }

    /// <summary>A folder of fifty sources moved into the project from elsewhere, which a folder's watcher hears as the folder
    /// alone: Go to File lists every source and the server is told of each as made; one of them deleted after, unheard as a
    /// burst drops it, is found gone by the listing after the burst, and the server told it went.</summary>
    [AvaloniaFact]
    public void A_folder_of_fifty_sources_moved_in_reaches_go_to_file_and_the_server_as_fifty_made_and_one_deleted_unheard_as_gone()
    {
        MainWindow window = project.Open("Program.cs");
        WatchedFiles watched = Watched(window);
        string elsewhere = TestFolders.Create("elsewhere-");
        TestFolders.WriteAll(elsewhere, Enumerable.Range(0, Moved).Select(i => (Path.Combine("Arriving", $"Arriving{i}.cs"), $"static class Arriving{i}\n{{\n}}\n")));
        using Told made = new(watched, name => name.StartsWith("Arriving", StringComparison.Ordinal), gone: false);
        Directory.Move(Path.Combine(elsewhere, "Arriving"), project.PathOf("Arriving"));
        bool heardMade = Ui.Pumped(() => made.Count == Moved, TimeSpan.FromSeconds(60));
        Assert.True(heardMade, $"Of the {Moved} sources in a folder moved into the project, the language server was told of {made.Count} as made: {WatchLog(window)}");
        Ui.PumpUntil(() => Enumerable.Range(0, Moved).All(i => window.FileIndex.Files.Contains(Path.Combine("Arriving", $"Arriving{i}.cs"))), "every source moved in in Go to File");

        FolderWatcher watcher = window.FolderWatcher!;
        List<FileSystemWatcher> watchers = [.. watcher.Watches.Values];
        using Told gone = new(watched, name => name == "Arriving7.cs", gone: true);
        bool heardGone;
        try
        {
            watchers.ForEach(w => w.EnableRaisingEvents = false);
            File.Delete(project.PathOf(Path.Combine("Arriving", "Arriving7.cs")));
            FolderWatchTests.RaiseError(watcher.Watches[watcher.Top], new InternalBufferOverflowException("Too many changes at once; directory listing is required."));
            heardGone = Ui.Pumped(() => gone.Count == 1, TimeSpan.FromSeconds(30));
        }
        finally
        {
            watchers.ForEach(w => w.EnableRaisingEvents = true);
        }
        Assert.True(heardGone, $"The language server was never told a source of the folder moved in, deleted unheard, went: {WatchLog(window)}");
        Ui.PumpUntil(() => !window.FileIndex.Files.Contains(Path.Combine("Arriving", "Arriving7.cs")), "the source deleted unheard gone from Go to File");
        window.Close();
    }

    /// <summary>A folder of fifty sources the server was told of moved out of the project, which a folder's watcher hears as
    /// the folder alone: Go to File lets every source go, and the server is told of each as gone.</summary>
    [AvaloniaFact]
    public void A_folder_of_fifty_sources_moved_out_leaves_go_to_file_and_reaches_the_server_as_fifty_deletions()
    {
        MainWindow window = project.Open("Program.cs");
        WatchedFiles watched = Watched(window);
        using (Told made = new(watched, name => name.StartsWith("Leaving", StringComparison.Ordinal), gone: false))
        {
            TestFolders.WriteAll(project.Folder, Enumerable.Range(0, Moved).Select(i => (Path.Combine("Leaving", $"Leaving{i}.cs"), $"static class Leaving{i}\n{{\n}}\n")));
            Assert.True(Ui.Pumped(() => made.Count == Moved, TimeSpan.FromSeconds(60)), $"Of the {Moved} sources written, the language server was told of {made.Count}: {WatchLog(window)}");
        }
        Ui.PumpUntil(() => Enumerable.Range(0, Moved).All(i => window.FileIndex.Files.Contains(Path.Combine("Leaving", $"Leaving{i}.cs"))), "every source written in Go to File");

        using Told gone = new(watched, name => name.StartsWith("Leaving", StringComparison.Ordinal), gone: true);
        Directory.Move(project.PathOf("Leaving"), Path.Combine(TestFolders.Create("elsewhere-"), "Leaving"));
        bool heardGone = Ui.Pumped(() => gone.Count == Moved, TimeSpan.FromSeconds(60));
        Assert.True(heardGone, $"Of the {Moved} sources in a folder moved out of the project, the language server was told {gone.Count} went: {WatchLog(window)}");
        Ui.PumpUntil(() => !window.FileIndex.Files.Any(f => f.StartsWith("Leaving", StringComparison.Ordinal)), "every source moved out gone from Go to File");
        window.Close();
    }

    /// <summary>A folder outside the project watched for the server, its watcher started again with its first walk held, as a
    /// large folder's (the references' packs) went on for minutes on CI's cold Windows disk: a source written in the project
    /// and one written into obj, which a watcher of Passenger's own hears, still reach the server meanwhile, never waiting
    /// behind that walk, and the window goes quiet without it.</summary>
    [AvaloniaFact]
    public void While_an_outside_folders_watcher_makes_its_first_walk_sources_written_in_the_project_and_in_obj_reach_the_server_and_the_window_goes_quiet()
    {
        MainWindow window = project.Open("Program.cs");
        WatchedFiles watched = Watched(window);
        ServerWatches watches = window.CSharpService!.Watching!;
        string inside = PathNames.Canonical(project.Folder);
        (string Folder, bool Recursive)[] outside = [.. watches.Watching.Where(w => !PathNames.Within(w.Folder, inside)).OrderBy(w => w.Recursive)];
        Assert.True(outside.Length > 0, $"No folder outside the project is watched for the language server: {string.Join(", ", watches.Watching)}");
        string walked = outside[0].Folder;
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task holding = watches.HoldNextWalk(folder => PathNames.Same(folder, walked), released.Task);
        using Told told = new(watched, name => name is "Meanwhile.cs" or "Beside.cs", gone: false);
        bool heard;
        bool quiet;
        string busy;
        try
        {
            watches.LetGo([walked]).Dispose();
            Assert.True(Ui.Pumped(() => holding.IsCompleted, TimeSpan.FromSeconds(30)), $"The watcher of {walked}, started again, never came to its first walk: {WatchLog(window)}");
            File.WriteAllText(project.PathOf("Meanwhile.cs"), "static class Meanwhile\n{\n}\n");
            File.WriteAllText(project.PathOf(Path.Combine("obj", "Beside.cs")), "static class Beside\n{\n}\n");
            // Pumped, not settled, so a walk that held the IDE's quiet fails here on what it held back, not on the settle.
            heard = Ui.Pumped(() => told.Count == 2, TimeSpan.FromSeconds(30));
            quiet = Ui.Quiet(TimeSpan.FromSeconds(30));
            busy = quiet ? string.Empty : Ui.Busy();
        }
        finally
        {
            released.TrySetResult();
        }
        Assert.True(heard, $"Of Meanwhile.cs written in the project and Beside.cs written into obj, the language server was told only of [{string.Join(", ", told.Names)}] "
            + $"while the watcher of {walked} made its first walk, so what a rename or a build writes waits until a walk of a folder outside the project ends: {WatchLog(window)}");
        Assert.True(quiet, $"The window never went quiet while the watcher of {walked} made its first walk, so every settle, screenshot and test waits out a walk "
            + $"of a folder outside the project, minutes long on a cold disk. Still busy:\n{busy}");
        window.Close();
    }

    /// <summary>A project in a folder below another project's, whose obj the server's watch of the folder above reaches and
    /// only a watcher of Passenger's own hears: a global using added to its project file, which its design-time build
    /// writes into a source in its obj, reaches the server, which then finds the type it names.</summary>
    [AvaloniaFact]
    public void A_global_using_a_project_below_another_projects_folder_writes_into_its_obj_reaches_the_server()
    {
        string folder = PathNames.Canonical(TestFolders.Create("nested-"));
        string shop = TestFolders.Write(folder, "Shop.csproj", TestProjects.Library().With("  <ItemGroup>\n    <Compile Remove=\"Lib/**\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Shop.cs", "class Shop\n{\n}\n");
        string implicitUsings = "<ImplicitUsings>enable</ImplicitUsings>";
        string library = TestFolders.Write(folder, Path.Combine("Lib", "Lib.csproj"), TestProjects.Library(implicitUsings));
        string code = TestFolders.Write(folder, Path.Combine("Lib", "Code.cs"), "class Code\n{\n    public object Text() => new StringBuilder();\n}\n");
        Restore.Run(shop);
        Restore.Run(library);
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile(code);
        Ui.WaitFor(() => Codes(window, code).Contains("CS0246"), () => $"the language server's error on StringBuilder in Lib's Code.cs: {WatchLog(window)}", TimeSpan.FromMinutes(2));

        File.WriteAllText(library, TestProjects.Library(implicitUsings).With("  <ItemGroup>\n    <Using Include=\"System.Text\" />\n  </ItemGroup>\n"));
        Ui.WaitFor(() => !Codes(window, code).Contains("CS0246"),
            () => $"the language server to take the global using of System.Text its design-time build wrote into Lib's obj, where Code.cs still has "
                + $"[{string.Join(", ", Codes(window, code))}], so the type it names stays unknown: {WatchLog(window)}", TimeSpan.FromMinutes(2));
        window.Close();
    }

    /// <summary>The codes of the server's diagnostics in an open file.</summary>
    private static List<string?> Codes(MainWindow window, string path) =>
        window.EditorArea.Documents.FirstOrDefault(d => PathNames.Same(d.Path, path)) is { } open && window.CSharpService?.For(open) is { } served
            ? [.. served.Diagnostics.Select(d => d.Code?.ToString())]
            : [];

    /// <summary>The server's watched files, once obj is watched for it by a watcher of Passenger's own whose first walk is done.</summary>
    private WatchedFiles Watched(MainWindow window)
    {
        string obj = PathNames.Canonical(project.PathOf("obj"));
        Ui.WaitFor(() => window.CSharpService?.Watching?.Watching.Contains((obj, true)) == true,
            () => $"obj to be watched for the language server, which watches {string.Join(", ", window.CSharpService?.Watching?.Watching ?? [])}");
        return window.CSharpService!.Session!.Server.WatchedFiles;
    }

    /// <summary>What the IDE's log says the watchers found by listing folders again after a burst of changes, last.</summary>
    private static string Recovered(MainWindow window) =>
        string.Join(" | ", Ui.IdeLog().Split('\n').Where(l => l.Contains("listed", StringComparison.Ordinal) && l.Contains("again after a burst", StringComparison.Ordinal)).TakeLast(4));

    /// <summary>What the IDE's log says each of the server's watchers of its own records, its folders and entries.</summary>
    private static string Records(MainWindow window) =>
        string.Join(" | ", Ui.IdeLog().Split('\n').Where(l => l.Contains($"(process {window.CSharpService!.Session!.Server.ProcessId}): the watcher of", StringComparison.Ordinal) && l.Contains(" records ", StringComparison.Ordinal)));

    /// <summary>What the server's log says of its files watched and told, and the IDE's of the folders watched for it, last.</summary>
    private static string WatchLog(MainWindow window)
    {
        LanguageServer server = window.CSharpService!.Session!.Server;
        IEnumerable<string> lines = Ui.Shared(server.LogPath).Split('\n').Where(l => l.Contains("watched for the server", StringComparison.Ordinal) || l.Contains("Told the server", StringComparison.Ordinal))
            .Concat(Ui.Shared(IdeLog.PathOf(IdeLog.Ide)).Split('\n').Where(l => l.Contains($"(process {server.ProcessId})", StringComparison.Ordinal)));
        return string.Join(" | ", lines.TakeLast(12));
    }

    /// <summary>The names of the files a server is told of from now on that a test looks for, made or changed, or gone.</summary>
    private sealed class Told : IDisposable
    {
        private readonly WatchedFiles watched;
        private readonly Func<string, bool> looked;
        private readonly bool gone;
        private readonly HashSet<string> names = new(StringComparer.Ordinal);
        private int notifications;

        public Told(WatchedFiles watched, Func<string, bool> looked, bool gone)
        {
            this.watched = watched;
            this.looked = looked;
            this.gone = gone;
            watched.Sent += OnSent;
        }

        public int Count
        {
            get
            {
                lock (names)
                {
                    return names.Count;
                }
            }
        }

        public IReadOnlySet<string> Names
        {
            get
            {
                lock (names)
                {
                    return new HashSet<string>(names, StringComparer.Ordinal);
                }
            }
        }

        public int Notifications => Volatile.Read(ref notifications);

        public void Dispose() => watched.Sent -= OnSent;

        private void OnSent(object? sender, IReadOnlyList<FileEvent> changes)
        {
            Interlocked.Increment(ref notifications);
            lock (names)
            {
                names.UnionWith(changes.Where(c => (c.Change == FileChangeType.Deleted) == gone).Select(c => Path.GetFileName(c.Path)).Where(looked));
            }
        }
    }

    /// <summary>A console project whose build, after it compiles, writes a thousand sources into obj side by side and its
    /// restore assets again among them (stamped anew, as a copy keeps its source's time), as a generator emitting its
    /// files and a restore do; restored once for every test.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("server-watch-")
        {
            TestFolders.Write(Folder, "Sample.csproj", TestProjects.Console());
            TestFolders.Write(Folder, "Program.cs", "class Program\n{\n    static void Main()\n    {\n    }\n}\n");
            string burst = string.Join(";", Enumerable.Range(0, Files).Select(i => $"Burst{i}"));
            TestFolders.Write(Folder, "Directory.Build.targets", $$"""
                <Project>
                  <Target Name="BurstIntoObj" AfterTargets="Build" Condition="'$(DesignTimeBuild)' != 'true'">
                    <ItemGroup>
                      <Burst Include="{{burst}}" />
                    </ItemGroup>
                    <WriteLinesToFile File="$(BaseIntermediateOutputPath)burst-source/%(Burst.Identity).txt" Lines="static class %(Burst.Identity) { }" Overwrite="true" />
                    <Copy SourceFiles="$(ProjectAssetsFile)" DestinationFiles="$(BaseIntermediateOutputPath)assets.kept" />
                    <Touch Files="$(BaseIntermediateOutputPath)assets.kept" />
                    <Copy SourceFiles="@(Burst->'$(BaseIntermediateOutputPath)burst-source/%(Identity).txt');$(BaseIntermediateOutputPath)assets.kept"
                          DestinationFiles="@(Burst->'$(BaseIntermediateOutputPath)burst/%(Identity).cs');$(ProjectAssetsFile)" />
                  </Target>
                </Project>
                """);
            // The server's first load makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
        }
    }
}
