using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The system's watches a window holds (its folder's, Git's elsewhere, the language server's) go with the window or
/// the folder they watch: however many times windows open and close and folders change in them, the process holds as many
/// as before. On macOS its folders share its FSEvents streams, of which the system allows a process 512 and every program
/// together 1,024: hundreds of folders apart, as a language server's packages are, take a few.</summary>
public sealed class SystemWatchesTests : IDisposable
{
    private readonly string first = PathNames.Canonical(TestFolders.Create("watches-first-"));
    private readonly string second = PathNames.Canonical(TestFolders.Create("watches-second-"));
    private readonly string apart = PathNames.Canonical(TestFolders.Create("watches-apart-"));

    public void Dispose()
    {
        TestFolders.Delete(first);
        TestFolders.Delete(second);
        TestFolders.Delete(apart);
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

        Ui.WaitFor(() => Held() == before, () => $"the system's watches of trees, of files and macOS's streams to be back at {before}, where they are {Held()}: "
            + $"streams over {string.Join(", ", WatchedTree.MacStreams)}");
        Assert.DoesNotContain(WatchedTree.MacStreams, f => PathNames.Within(f, first) || PathNames.Within(f, second) || PathNames.Within(f, apart));
    }

    /// <summary>Two hundred folders apart, each watched on its own as the language server's packages are, take a few of
    /// macOS's streams, one over the folder they share once there are many; each still hears what changes in it and nothing
    /// of the others, one deleted is heard gone, and with all of them let go the streams are back where they were.</summary>
    [AvaloniaFact]
    public void Hundreds_of_folders_watched_apart_share_a_few_macOS_streams_and_each_hears_its_own_changes()
    {
        Assert.SkipUnless(FolderWatching.ReadsMacEvents, "Only macOS's streams are few for a process and shared among its folders");
        IReadOnlyList<string> before = WatchedTree.MacStreams;
        List<string> folders = [.. Enumerable.Range(0, 200).Select(i => Directory.CreateDirectory(Path.Combine(apart, $"package{i}", "lib", "net10.0")).FullName)];
        List<Watched> watched = [.. folders.Select(f => new Watched(f))];
        try
        {
            Ui.WaitFor(() => watched.All(w => w.Ready.IsCompleted), "every folder's watch to have walked its folder");
            Assert.True(WatchedTree.MacStreams.Count(f => PathNames.Within(f, apart)) == 1 && WatchedTree.MacStreams.Count <= before.Count + 1,
                $"Two hundred folders apart took a stream each, as the system allows a process 512: the process reads through {string.Join(", ", WatchedTree.MacStreams)}");

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
        Ui.WaitFor(() => WatchedTree.MacStreams.Count == before.Count, () => $"the streams to be back at {before.Count}, where they read {string.Join(", ", WatchedTree.MacStreams)}");
    }

    /// <summary>The system's watches of trees and of files held, and macOS's streams.</summary>
    private static (int Trees, int Files, int Streams) Held() => (WatchedTree.SystemWatches, WatchedFile.SystemWatches, WatchedTree.MacStreams.Count);

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

        protected override bool Kept(string path) => PathNames.Same(path, Top);

        protected override Notice Noticed(string path, bool created) => new(Takes: true, Counts: true, PathNames.Canonical(path));

        protected override void Take(string path, bool created, Notice notice) => taken.Add(notice.Canonical!);

        protected override FolderChanges? Gathered(bool lost, bool overflowed)
        {
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
