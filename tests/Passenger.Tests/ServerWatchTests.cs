using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.Tests;

/// <summary>What the C# language server registered to hear of in a folder only Passenger's watchers of its own watch for
/// it, a project's obj: a burst there, a build's or a script's, more at once than macOS's watcher names, still reaches the
/// server, every file of it, as the folder's watcher's bursts do. The class's windows share one project, restored once.</summary>
public sealed class ServerWatchTests : IClassFixture<ServerWatchTests.Project>
{
    private const int Files = 1000;

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
        string? log = Directory.EnumerateFiles(server.LogFolder, $"server-*-{server.ProcessId}.log").FirstOrDefault();
        IEnumerable<string> lines = (log is null ? [] : Ui.Shared(log).Split('\n').Where(l => l.Contains("watched for the server", StringComparison.Ordinal) || l.Contains("Told the server", StringComparison.Ordinal)))
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
