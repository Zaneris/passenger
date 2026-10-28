using Passenger.App;
using Passenger.App.CSharp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A small project a test class's windows share, restored once, and the warm language server they share on it:
/// each window takes up the server the last one left running (<see cref="WarmServer"/>) rather than start its own. Each
/// test starts on the project as written (<see cref="Reset"/>, from its class's constructor): what a test changed is put
/// back and what it added removed; a server that saw a file come or go, or a project file change, even one undone since,
/// or that a failed test left (<see cref="OpenWindows"/>), is ended, so the next window starts one that loads the files as
/// written, while one whose documents a test only rewrote is kept once it holds them as written again; a class whose tests
/// write files in turn for several windows keeps it through them (<see cref="SharedProject(string, bool)"/>). Its
/// constructor writes the project; xunit then makes it ready (<see cref="InitializeAsync"/>).</summary>
public abstract class SharedProject : IAsyncLifetime
{
    /// <summary>The project's own files as written, by path, outside what restores and builds write (bin and obj).</summary>
    private readonly Dictionary<string, byte[]> written = new(StringComparer.Ordinal);
    private readonly HashSet<string> folders = new(StringComparer.Ordinal);

    /// <summary>When each of the project's own files and folders was last written as the shared server knows them: a file
    /// written and put back, or a file added and removed (its folder's time), shows here as no content does.</summary>
    private Dictionary<string, DateTime> stamps = [];

    /// <summary>A test wrote a file of its own to the project since the last reset.</summary>
    private bool changed;

    /// <summary>Whether the files a test writes keep the shared server, which the next window has load those it lacks
    /// (<see cref="WarmServer.HeardAsync"/>), as a window open on the folder does as files come.</summary>
    private readonly bool loadsAgain;

    /// <summary>A project in a test folder of <paramref name="name"/>; with <paramref name="loadsAgain"/>, files a test
    /// writes keep the shared server for its next window, which has it load them, where otherwise each such file ends it.
    /// After the test they go with the server, as without: a server letting go of files it analysed solution-wide, by the
    /// thousand, takes far longer than a new one's start.</summary>
    protected SharedProject(string name, bool loadsAgain = false)
    {
        Folder = TestFolders.Create(name);
        this.loadsAgain = loadsAgain;
    }

    public string Folder { get; }

    /// <summary>The server the class's windows share.</summary>
    public WarmServer Server { get; } = new();

    public string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>The project made ready once its constructor has written it: restored, and its files taken as the ones
    /// every test starts on (<see cref="PrepareAsync"/>). A fixture that makes more with programs (dotnet new, git), or
    /// is restored otherwise, says so here.</summary>
    public virtual ValueTask InitializeAsync() => new(PrepareAsync());

    /// <summary>Restores the project, once its files are written, and takes them as the ones every test starts on; with no
    /// restore (<paramref name="restore"/>) for a class whose windows start no C# language server and whose tests read
    /// nothing a restore writes (the Solution view's projects and Find in Files' scopes are the MSBuild host's evaluation);
    /// restored by the folder's real path (<paramref name="byRealPath"/>) when a project names the others by theirs (an
    /// Aspire AppHost's references), so the solution and it never have one project restored twice at once.</summary>
    protected Task PrepareAsync(bool restore = true, bool byRealPath = false) =>
        TestFolders.PrepareAsync(async () =>
        {
            if (restore)
            {
                await Restore.RunAsync(byRealPath ? PathNames.Canonical(Folder) : Folder);
            }
            folders.UnionWith(Directory.EnumerateDirectories(Folder, "*", SearchOption.AllDirectories).Where(Own));
            foreach (string file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories).Where(Own))
            {
                written[file] = File.ReadAllBytes(file);
            }
            stamps = Stamps();
        }, Folder);

    /// <summary>A window on the project, which never saves what its test typed, with a file open (when one is named) once C#
    /// is ready: on the server the class's last window left, or one of its own when there is none to take up; keeping its
    /// settings in a state folder of the test's own when one is given. A test that asks nothing of C# (a web language's
    /// server, the brackets' pairs, which are the file's own parse) opens it with no C# language server
    /// (<paramref name="languageServer"/>), once the folder's opening work has landed, the warm server left for the next.</summary>
    public MainWindow Open(string? file, Action<MainWindow>? before = null, string? state = null, bool languageServer = true)
    {
        MainWindow window = state is null
            ? new() { Width = 1200, Height = 800, Autosaves = false, WarmServer = Server, StartsLanguageServer = languageServer }
            : new() { Width = 1200, Height = 800, Autosaves = false, WarmServer = Server, StateFolder = state, StartsLanguageServer = languageServer };
        window.Show();
        if (state is not null)
        {
            window.KeepSettings();
        }
        window.OpenFolder(Folder);
        if (file is not null)
        {
            window.OpenFile(file);
        }
        before?.Invoke(window);
        Ui.Settle();
        if (languageServer)
        {
            Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the project ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        }
        return window;
    }

    /// <summary>Writes a file a test needs that the project does not have: the next window starts a server of its own, which
    /// loads it as a server started on the folder would, rather than one that would only hear of it through its file
    /// watcher; and before the next test the file goes, with that server. From the UI thread; a test with no window awaits
    /// <see cref="WriteAsync"/>.</summary>
    public string Write(string name, string text)
    {
        if (loadsAgain)
        {
            string path = Written(name, text);
            Heard(new Dictionary<string, string>(StringComparer.Ordinal) { [path] = text });
            return path;
        }
        Drop();
        return Written(name, text);
    }

    /// <summary>Writes several files a test needs that the project does not have, as <see cref="Write(string, string)"/>
    /// writes one, the warm server ended once for them all, the files written several at once (as
    /// <see cref="TestFolders.WriteAll"/> writes them). From the UI thread.</summary>
    public void Write(IEnumerable<(string Name, string Text)> files)
    {
        if (loadsAgain)
        {
            System.Collections.Concurrent.ConcurrentDictionary<string, string> texts = new(StringComparer.Ordinal);
            Parallel.ForEach(files, file => texts[Written(file.Name, file.Text)] = file.Text);
            Heard(texts);
            return;
        }
        Drop();
        Parallel.ForEach(files, file => Written(file.Name, file.Text));
    }

    /// <inheritdoc cref="Write(string, string)"/>
    public async Task<string> WriteAsync(string name, string text)
    {
        if (loadsAgain)
        {
            string path = Written(name, text);
            if (!await Server.HeardAsync(new Dictionary<string, string>(StringComparer.Ordinal) { [path] = text }, Hearing.Scaled()))
            {
                await Server.Drop();
            }
            return path;
        }
        await Server.Drop();
        return Written(name, text);
    }

    private string Written(string name, string text)
    {
        changed = true;
        string path = PathOf(name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Has the kept server hear of files written (<see cref="WarmServer.HeardAsync"/>), ended if it does not
    /// within <see cref="Hearing"/>; on the UI thread, the window's loop running as it waits.</summary>
    private void Heard(IReadOnlyDictionary<string, string> texts)
    {
        if (!Ui.AwaitPumped(Waiting.Apart(() => Server.HeardAsync(texts, Hearing.Scaled())), "the kept language server to hear of the files written", Hearing + TestFolders.Spare))
        {
            Drop();
        }
    }

    /// <summary>Rewrites one of the project's own files for a test: the warm server is kept once it holds the file as
    /// written, read through its own watcher as it reads a file changed on disk (<see cref="WarmServer.HoldsAsync"/>), and
    /// ended if it does not within <see cref="Rereading"/>; <see cref="Reset"/> puts the file back for the next test. A file
    /// the project does not have goes through <see cref="Write(string, string)"/>.</summary>
    public string Rewrite(string name, string text)
    {
        string path = Path.GetFullPath(PathOf(name));
        if (!written.ContainsKey(path))
        {
            throw new ArgumentException($"{name} is no file of the project as written: Write adds it", nameof(name));
        }
        File.WriteAllText(path, text);
        if (!Holds(new Dictionary<string, string>(StringComparer.Ordinal) { [path] = text }))
        {
            Drop();
        }
        return path;
    }

    /// <summary>The project as written for the test about to run (<see cref="Restored"/>): the shared server is ended if a
    /// file came or went, or one it holds no document of changed (a project file); one whose documents a test only rewrote
    /// is kept once it holds them as written again, read through its own watcher (<see cref="WarmServer.HoldsAsync"/>), and
    /// ended if it does not within <see cref="Rereading"/>. From the UI thread (a window test's constructor), the window's
    /// loop running as it waits for the server; elsewhere a test awaits <see cref="ResetAsync"/>.</summary>
    public void Reset()
    {
        if (Restored() is not { } rewritten || (rewritten.Count > 0 && !Holds(rewritten)))
        {
            Drop();
        }
        stamps = Stamps();
        changed = false;
    }

    /// <inheritdoc cref="Reset"/>
    public async Task ResetAsync()
    {
        if (Restored() is not { } rewritten || (rewritten.Count > 0 && !await Server.HoldsAsync(rewritten, Rereading.Scaled())))
        {
            await Server.Drop();
        }
        stamps = Stamps();
        changed = false;
    }

    /// <summary>The project written back as it was: what a test before it changed on disk written back with the time it was
    /// written at (a moment after a build since, which the next build then compiles again), what it added removed. What of
    /// it the kept server must read again, by path; null when a file came or went, or one it holds no document of changed.</summary>
    private Dictionary<string, string>? Restored()
    {
        bool structure = changed;
        HashSet<string> rewritten = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories).Where(Own).ToList())
        {
            if (!written.ContainsKey(file))
            {
                File.Delete(file);
                structure = true;
            }
        }
        foreach (string folder in Directory.EnumerateDirectories(Folder, "*", SearchOption.AllDirectories).Where(Own).OrderByDescending(f => f.Length).ToList())
        {
            if (!folders.Contains(folder))
            {
                Directory.Delete(folder, recursive: true);
                structure = true;
            }
        }
        foreach ((string file, byte[] content) in written)
        {
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, content);
                structure = true;
            }
            else if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(content))
            {
                File.WriteAllBytes(file, content);
                rewritten.Add(file);
            }
        }
        List<string> retimed = [];
        foreach ((string path, DateTime stamp) in Stamps())
        {
            if (!stamps.TryGetValue(path, out DateTime before) || before != stamp)
            {
                // A file written and put back by its test; or a folder's time.
                if (written.ContainsKey(path))
                {
                    rewritten.Add(path);
                }
                else
                {
                    retimed.Add(path);
                }
            }
        }
        // Its old time, so no edit takes it as just written, but after any build since, which then compiles it again.
        DateTime built = LastBuilt();
        foreach (string file in written.Keys.Where(file => File.GetLastWriteTimeUtc(file) != stamps[file]))
        {
            File.SetLastWriteTimeUtc(file, stamps[file] > built ? stamps[file] : Min(built + TimeSpan.FromSeconds(1), DateTime.UtcNow));
        }
        // A folder's time is a file that came or went there, but for a save's temporary file renamed over one rewritten.
        structure |= retimed.Any(folder => !rewritten.Any(file => PathNames.Same(Path.GetDirectoryName(file)!, folder)));
        return structure ? null : rewritten.ToDictionary(file => file, File.ReadAllText, StringComparer.Ordinal);
    }

    /// <summary>How long a kept server has to read again the files a test rewrote, through its own watcher.</summary>
    private static readonly TimeSpan Rereading = TimeSpan.FromSeconds(10);

    /// <summary>How long a kept server has to hear of the files a test writes, which it reads in half a second or not for
    /// many: past it the next window starts a server of its own, sooner than a longer wait would let it.</summary>
    private static readonly TimeSpan Hearing = TimeSpan.FromSeconds(3);

    /// <summary>Ends the shared server and removes the project; a fixture that holds more (a stand-in feed) ends it too.</summary>
    public virtual async ValueTask DisposeAsync()
    {
        await Server.Drop();
        TestFolders.Delete(Folder);
    }

    /// <summary>Whether the kept server holds the files as the texts given, waited for on the UI thread with the window's
    /// loop running, never for its work to land.</summary>
    private bool Holds(IReadOnlyDictionary<string, string> texts) =>
        Ui.AwaitPumped(Waiting.Apart(() => Server.HoldsAsync(texts, Rereading.Scaled())), "the kept language server to read the files again", Rereading + TestFolders.Spare);

    /// <summary>Ends the shared server, waited for on the UI thread with the window's loop running, within the time a
    /// window's end gives what it started.</summary>
    private void Drop() => Ui.AwaitPumped(Waiting.Apart(Server.Drop), "the shared language server to end", MainWindow.EndLimit + TestFolders.Spare);

    /// <summary>When a build last compiled one of the project's assemblies (a .dll under bin or obj, which neither a restore
    /// nor a design-time build writes), or never.</summary>
    private DateTime LastBuilt() => Directory.EnumerateFiles(Folder, "*.dll", SearchOption.AllDirectories)
        .Where(file => !Own(file)).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();

    private static DateTime Min(DateTime one, DateTime other) => one < other ? one : other;

    private Dictionary<string, DateTime> Stamps()
    {
        Dictionary<string, DateTime> now = new(StringComparer.Ordinal) { [Folder] = Directory.GetLastWriteTimeUtc(Folder) };
        foreach (string folder in folders)
        {
            now[folder] = Directory.GetLastWriteTimeUtc(folder);
        }
        foreach (string file in written.Keys)
        {
            now[file] = File.GetLastWriteTimeUtc(file);
        }
        return now;
    }

    /// <summary>Whether a path is the project's own, not under what a restore or a build writes, nor a repository's .git,
    /// which git writes as a window only reads it (status refreshing the index): a class whose tests change the repository
    /// puts it back itself.</summary>
    private bool Own(string path) =>
        !Path.GetRelativePath(Folder, path).Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj" or ".git");
}
