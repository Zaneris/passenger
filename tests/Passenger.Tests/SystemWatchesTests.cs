using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The system's watches a window holds (its folder's, Git's elsewhere, the language server's) go with the window or
/// the folder they watch: however many times windows open and close and folders change in them, the process holds as many
/// as before. On macOS and Windows its folders share the system's watchers, each reading a folder and all below it (macOS
/// allows a process 512 of its streams and every program together 1,024; each of Windows' takes a handle and a buffer): hundreds
/// of folders apart, as a language server's packages are, take a few. Linux's inotify watches each folder alone, one each.</summary>
public sealed class SystemWatchesTests : IDisposable
{
    private readonly string first = PathNames.Canonical(TestFolders.Create("watches-first-"));
    private readonly string second = PathNames.Canonical(TestFolders.Create("watches-second-"));
    private readonly string apart = PathNames.Canonical(TestFolders.Create("watches-apart-"));
    private readonly string many = PathNames.Canonical(TestFolders.Create("watches-many-"));

    public void Dispose()
    {
        TestFolders.Delete(first);
        TestFolders.Delete(second);
        TestFolders.Delete(apart);
        TestFolders.Delete(many);
    }

    /// <summary>Windows opened on a repository and on a worktree of it (whose Git folders lie outside it), the folder changed
    /// in each, its watcher failed and started again, and one on a C# project with its language server, each closed: the
    /// system's watches of every tree and file, and macOS's streams, are back where they were, none left on their folders.</summary>
    [AvaloniaFact]
    public void Windows_opened_and_closed_and_folders_changed_many_times_leave_the_system_watches_as_they_were()
    {
        TestFolders.Write(first, "One.cs", "class One { }\n");
        TestGit.Init(first);
        string tree = Path.Combine(second, "tree");
        TestGit.Run(first, "worktree", "add", "-q", "-b", "feature", tree);
        TestFolders.Write(apart, "Sample.csproj", TestProjects.Console());
        TestFolders.Write(apart, "Program.cs", "class Program\n{\n    static void Main()\n    {\n    }\n}\n");
        Ui.Await(Restore.RunAsync(apart), "the C# project restored", Restore.Limit);
        // A window first, so what the process makes once for all its windows is made before the count.
        Open(first).Close();
        Ui.Settle();
        (int Trees, int Files, int Streams) before = Held();

        for (int round = 0; round < 12; round++)
        {
            MainWindow window = Open(round % 2 == 0 ? first : tree);
            Ui.WaitFor(() => window.FolderWatcher?.Ready.IsCompleted == true, "the folder's watcher to watch the folder");
            window.OpenFolder(round % 2 == 0 ? tree : first);
            Ui.Settle();
            Ui.WaitFor(() => window.FolderWatcher?.Ready.IsCompleted == true && window.Vcs.GitWatchers.Count == (PathNames.Same(window.FolderWatcher.Top, tree) ? 2 : 0),
                () => $"the folder's watcher and Git's elsewhere to watch, where Git's watch {string.Join(", ", window.Vcs.GitWatchers.Select(w => w.Top))}");
            if (round % 3 == 0)
            {
                window.FolderWatcher!.Fail(window.FolderWatcher.Top, new IOException("The watch failed"));
                Ui.Settle();
            }
            window.Close();
            Ui.Settle();
        }
        MainWindow csharp = Open(apart, languageServer: true);
        Ui.WaitFor(() => csharp.CSharpService?.Watching?.Watching.Count > 0,
            "the language server's registrations outside the folder to be watched for it");
        csharp.Close();
        Ui.Settle();

        Ui.WaitFor(() => Held() == before, () => $"the system's watches of trees, of files and the shared watchers to be back at {before}, where they are {Held()}: "
            + $"shared watchers over {string.Join(", ", WatchedTree.SharedWatchers)}");
        Assert.DoesNotContain(WatchedTree.SharedWatchers, f => PathNames.Within(f, first) || PathNames.Within(f, second) || PathNames.Within(f, apart));
    }

    /// <summary>Two hundred folders apart, each watched on its own as the language server's packages are, take one of the
    /// system's watchers on macOS and Windows, over the folder they share once there are many, and a watch each on Linux;
    /// each still hears what changes in it and nothing of the others, one deleted is heard gone, and with all of them let go
    /// the watchers are back where they were.</summary>
    [AvaloniaFact]
    public void Hundreds_of_folders_watched_apart_share_a_few_of_the_systems_watchers_and_each_hears_its_own_changes()
    {
        IReadOnlyList<string> before = WatchedTree.SharedWatchers;
        int trees = WatchedTree.SystemWatches;
        List<string> folders = [.. Enumerable.Range(0, 200).Select(i => Directory.CreateDirectory(Path.Combine(apart, $"package{i}", "lib", "net10.0")).FullName)];
        List<Watched> watched = [.. folders.Select(f => new Watched(f))];
        try
        {
            Ui.WaitFor(() => watched.All(w => w.Ready.IsCompleted), "every folder's watch to have walked its folder");
            Assert.True(FolderWatching.SharesWatches
                    ? WatchedTree.SharedWatchers.Count(f => PathNames.Within(f, apart)) == 1 && WatchedTree.SharedWatchers.Count <= before.Count + 1
                    : WatchedTree.SharedWatchers.Count == 0 && WatchedTree.SystemWatches - trees == folders.Count,
                $"Two hundred folders apart took {WatchedTree.SystemWatches - trees} of the system's watches through the shared watchers over "
                + $"{string.Join(", ", WatchedTree.SharedWatchers)}: each took a watcher, as macOS allows a process 512 streams and each of Windows' takes a buffer");

            string seventh = TestFolders.Write(folders[7], "Seventh.dll", "seventh");
            string last = TestFolders.Write(folders[199], "Last.dll", "last");
            Ui.WaitFor(() => watched[7].Heard.Contains(seventh) && watched[199].Heard.Contains(last),
                () => $"each folder to hear the file written in it, where the seventh heard {string.Join(", ", watched[7].Heard)} and the last {string.Join(", ", watched[199].Heard)}");
            Assert.True(watched.Where((_, i) => i is not 7 and not 199).All(w => w.Heard.IsEmpty), "A folder heard what was written in another folder of its stream");

            Directory.Delete(Path.Combine(apart, "package42"), recursive: true);
            Ui.WaitFor(() => watched[42].Heard.Contains(folders[42]), () => $"the folder deleted to be heard gone, where it heard {string.Join(", ", watched[42].Heard)}");
        }
        finally
        {
            watched.ForEach(w => w.Dispose());
        }
        Ui.WaitFor(() => WatchedTree.SharedWatchers.Count == before.Count && WatchedTree.SystemWatches == trees,
            () => $"the watchers to be back at {before.Count} and {trees} watches, where they read {string.Join(", ", WatchedTree.SharedWatchers)} and hold {WatchedTree.SystemWatches}");
    }

    /// <summary>A window over a solution of several projects and the packages of three test frameworks, its language
    /// server's registrations outside the folder's watcher each a folder watched apart (each project's obj and bin, each
    /// package's lib folder): on macOS and Windows they are read through a few of the system's watchers, a source written
    /// into obj still reaching the server, and with the window closed every watch is back where it was.</summary>
    [AvaloniaFact]
    public void A_window_over_a_solution_of_many_projects_and_packages_reads_their_folders_through_a_few_watchers()
    {
        string[] libraries = ["Shop", "Till", "Stock", "Cart"];
        foreach (string library in libraries)
        {
            TestFolders.Write(many, $"{library}/{library}.csproj", TestProjects.Library());
            TestFolders.Write(many, $"{library}/{library}Code.cs", $"namespace {library};\n\npublic static class {library}Code\n{{\n}}\n");
        }
        TestFolders.Write(many, "App/App.csproj", TestProjects.Console().With(
            "  <ItemGroup>\n" + string.Concat(libraries.Select(l => $"    <ProjectReference Include=\"../{l}/{l}.csproj\" />\n")) + "  </ItemGroup>\n"));
        TestFolders.Write(many, "App/Program.cs", "class Program\n{\n    static void Main()\n    {\n    }\n}\n");
        TestFolders.Write(many, "App.Xunit/App.Xunit.csproj", TestProjects.Library().With(
            $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n"
            + $"    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n"
            + $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n"));
        TestFolders.Write(many, "App.NUnit/App.NUnit.csproj", TestProjects.Library().With(
            $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n"
            + $"    <PackageReference Include=\"NUnit\" Version=\"{TestPackages.NUnit}\" />\n"
            + $"    <PackageReference Include=\"NUnit3TestAdapter\" Version=\"{TestPackages.NUnitAdapter}\" />\n  </ItemGroup>\n"));
        TestFolders.Write(many, "App.MSTest/App.MSTest.csproj", TestProjects.Library().With(
            $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n"
            + $"    <PackageReference Include=\"MSTest.TestAdapter\" Version=\"{TestPackages.MSTestOnVSTest}\" />\n"
            + $"    <PackageReference Include=\"MSTest.TestFramework\" Version=\"{TestPackages.MSTestOnVSTest}\" />\n  </ItemGroup>\n"));
        string[] projects = [.. libraries.Select(l => $"{l}/{l}.csproj"), "App/App.csproj", "App.Xunit/App.Xunit.csproj", "App.NUnit/App.NUnit.csproj", "App.MSTest/App.MSTest.csproj"];
        string solution = TestFolders.Write(many, "Many.slnx", "<Solution>\n" + string.Concat(projects.Select(p => $"  <Project Path=\"{p}\" />\n")) + "</Solution>\n");
        Restore.Run(solution);
        (int Trees, int Files, int Shared) before = Held();
        int notified = InotifyWatches();

        MainWindow window = Open(many, languageServer: true);
        ServerWatches watches = null!;
        Ui.WaitFor(() => window.CSharpService?.Watching is { } watching && (watches = watching).Watching.Count(w => !PathNames.Within(w.Folder, many)) >= 8,
            () => $"the language server's packages to be watched for it, where it watches {string.Join(", ", window.CSharpService?.Watching?.Watching ?? [])}");
        Ui.Settle();
        int folders = watches.Watching.Count;
        int shared = WatchedTree.SharedWatchers.Count - before.Shared;
        string counts = $"{folders} folders watched apart for the language server, {WatchedTree.SystemWatches - before.Trees} system watches of the window's trees, "
            + $"{shared} shared watchers over {string.Join(", ", WatchedTree.SharedWatchers)}"
            + (OperatingSystem.IsLinux() ? $", {InotifyWatches() - notified} inotify watches" : string.Empty);
        Assert.True(FolderWatching.SharesWatches ? shared <= 16 && shared < folders : shared == 0 && WatchedTree.SystemWatches - before.Trees >= folders,
            $"The window's folders were not read through as few of the system's watchers as they share: {counts}");

        WatchedFiles told = window.CSharpService!.Session!.Server.WatchedFiles;
        string beside = Path.Combine(many, "App", "obj", "Beside.cs");
        bool heard = false;
        void OnSent(object? sender, IReadOnlyList<FileEvent> changes) => heard |= changes.Any(c => PathNames.Same(c.Path, beside));
        told.Sent += OnSent;
        try
        {
            File.WriteAllText(beside, "static class Beside\n{\n}\n");
            Ui.WaitFor(() => Volatile.Read(ref heard), () => $"the source written into obj to reach the language server: {counts}");
        }
        finally
        {
            told.Sent -= OnSent;
        }
        window.Close();
        Ui.Settle();
        Ui.WaitFor(() => Held() == before, () => $"the system's watches of trees, of files and the shared watchers to be back at {before}, where they are {Held()}");
    }

    /// <summary>The system's word that it dropped changes, come as the watcher's record is new, has the changes taken for
    /// lost and the folder walked again, once: the burst's look once it is quiet lists what it touched against the record
    /// that walk made, rather than taking the changes for lost again because the record is newer than the burst.</summary>
    [AvaloniaFact]
    public void A_drop_taken_for_lost_is_not_taken_for_lost_again_as_its_burst_is_looked_at_once_quiet()
    {
        string folder = Directory.CreateDirectory(Path.Combine(apart, "dropped")).FullName;
        TestFolders.Write(folder, "One.cs", "class One { }\n");
        using Watched watched = new(folder);
        Ui.WaitFor(() => watched.Ready.IsCompleted, "the folder's watch to have walked its folder");
        Assert.True(watched.RecordAge < WatchedTree.DropReach, $"The watcher's record was {watched.RecordAge} old, older than a drop reaches back, so the drop was never taken for lost");
        watched.Fail(folder, new InternalBufferOverflowException("Too many changes at once; directory listing is required."));
        Ui.WaitFor(() => watched.Losses > 0, "the drop, said as the record was new, to be taken for lost");
        Ui.Hold(WatchedTree.DropReach, "the burst's look once it has been quiet a moment");
        Ui.Settle();
        Assert.True(watched.Losses == 1, $"A drop taken for lost was taken for lost {watched.Losses} times, each walking the whole folder again, as each look "
            + "at its burst found the record the last walk made newer than the burst");
    }

    /// <summary>The system's watches of trees and of files held, and the shared watchers.</summary>
    private static (int Trees, int Files, int Shared) Held() => (WatchedTree.SystemWatches, WatchedFile.SystemWatches, WatchedTree.SharedWatchers.Count);

    /// <summary>How many folders this process's inotify instances watch, on Linux; none elsewhere.</summary>
    private static int InotifyWatches()
    {
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }
        int count = 0;
        foreach (string handle in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(handle).LinkTarget == "anon_inode:inotify")
                {
                    count += File.ReadLines(Path.Combine("/proc/self/fdinfo", Path.GetFileName(handle))).Count(l => l.StartsWith("inotify wd:", StringComparison.Ordinal));
                }
            }
            catch (IOException)
            {
                // A handle closed as it was read.
            }
        }
        return count;
    }

    private static MainWindow Open(string folder, bool languageServer = false)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = languageServer };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>A folder watched on its own as the language server's packages are, recording what it hears: the paths of
    /// each batch, its folder among them as changes there are lost (gone, it is read again).</summary>
    private sealed class Watched : WatchedTree
    {
        private readonly HashSet<string> taken = new(PathNames.Comparer);

        public Watched(string folder)
            : base(folder, FolderWatching.Platform, eachFolder: true) => Begin();

        public System.Collections.Concurrent.ConcurrentBag<string> Heard { get; } = [];

        /// <summary>How many batches came with their changes taken for lost.</summary>
        public int Losses => Volatile.Read(ref losses);

        private int losses;

        protected override bool Kept(string path) => PathNames.Same(path, Top);

        protected override Notice Noticed(string path, bool created) => new(Takes: true, Counts: true, PathNames.Canonical(path));

        protected override void Take(string path, bool created, Notice notice) => taken.Add(notice.Canonical!);

        protected override FolderChanges? Gathered(bool lost, bool overflowed)
        {
            losses += lost ? 1 : 0;
            FolderChanges changes = new(new HashSet<string>(taken, PathNames.Comparer), Structure: false, Projects: false, Git: false) { Lost = lost };
            taken.Clear();
            return changes.Paths.Count > 0 || lost ? changes : null;
        }

        protected override void Hand(FolderChanges changes)
        {
            foreach (string path in changes.Paths)
            {
                Heard.Add(path);
            }
        }
    }
}
