using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>GDScript's language server, which lives in Godot's editor: the person's own editor where it is open on the
/// project (its language server listening on the port its Editor Settings give, <see cref="GodotServerWords.DefaultPort"/>
/// by default), else a headless
/// editor Passenger starts for the project through ChildProcesses on a free loopback port, ended with the folder. A
/// headless editor writes into the project as it starts: project.godot, saved by an editor plugin with what that editor
/// holds, its layout, script states and .NET tools' messaging file, and a .uid or .import beside a file that has none; each
/// is put back as it was, or taken away, as it writes them through its start work, and a .uid it makes as it reloads a
/// game's build later, another program's writes meanwhile kept, so the person's project and their own editor and tools are
/// left as they were. From Godot 4.5 it runs in recovery mode, where no plugin or tool script of the project runs to write later.</summary>
public static class GodotScripts
{
    /// <summary>What a headless editor writes into the project as it starts that is the person's: the project's settings (a
    /// plugin's save writes the .NET editor's assembly name, a newer Godot's features) and their editor's own files.</summary>
    private static readonly string[] Kept = [GodotProject.FileName, Path.Combine(".godot", "editor", "editor_layout.cfg"),
        Path.Combine(".godot", "editor", "script_editor_cache.cfg"), Path.Combine(".godot", "mono", "metadata", "ide_messaging_meta.txt")];

    /// <summary>The ports the IDE messaging servers of Passenger's own headless editors were heard on, as each wrote its meta
    /// file over the person's, which is put back.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> HeadlessPorts = new();

    /// <summary>Whether a port is the IDE messaging server's of one of Passenger's own headless editors, never asked as the
    /// person's editor (<see cref="GodotIdeLink"/>).</summary>
    public static bool Headless(int port) => HeadlessPorts.ContainsKey(port);

    /// <summary>The Godot project a file is in: the nearest folder above it holding a project.godot; null for none.</summary>
    public static string? ProjectOf(string path)
    {
        for (string? folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (File.Exists(Path.Combine(folder, GodotProject.FileName)))
            {
                return folder;
            }
        }
        return null;
    }

    /// <summary>Why GDScript has no server: no project.godot above the file, or no Godot to run its editor.</summary>
    public sealed class Unavailable(string problem, string label) : Exception(problem)
    {
        public string Label { get; } = label;
    }

    /// <summary>The project's GDScript server, connected and initialized: the person's editor open on it where one is, else
    /// a headless one started on the Godot Settings names (<paramref name="setting"/>), in the window's user folders for Godot
    /// (<paramref name="environment"/>, <see cref="GodotUserFolders"/>) and its log under <paramref name="logFolder"/>.</summary>
    public static async Task<LanguageSession> StartAsync(string? project, string? setting, IReadOnlyDictionary<string, string> environment, string logFolder,
        CancellationToken cancel)
    {
        if (project is null)
        {
            throw new Unavailable($"it is in no Godot project (no {GodotProject.FileName} above it), whose editor answers for GDScript", "no Godot project");
        }
        GodotFound found = GodotLaunch.Found(setting) is { Exists: true } godot ? godot
            : throw new Unavailable("no Godot was found to answer for it: install Godot, then choose its executable in "
                + $"{SettingsDialog.PlaceOfPage(SettingsDialog.GodotPage)}, set {GodotExecutables.Variable} to it, or put it on the PATH", "no Godot");
        Directory.CreateDirectory(logFolder);
        if (EditorPort(environment) is { } port && await OwnEditorAsync(project, port, logFolder, cancel) is { } own)
        {
            IdeErrors.Write($"GDScript: the Godot editor open on {project} answers on port {port}");
            return own;
        }
        string? version = await GodotExecutables.VersionAsync(found, cancel);
        int lsp = FreePort();
        Before before = await BackgroundWork.Run(() => new Before(project, MadeFiles.Uids | MadeFiles.Imports));
        // No plugin or tool script of the project's runs there to write into it later; a GDExtension would not load to be known.
        bool recovering = GodotExecutables.HasRecoveryMode(version) && !before.Extensions;
        IdeErrors.Write(recovering ? $"GDScript: Godot's headless editor on {project} runs in its recovery mode, so no editor plugin or tool script of the project runs in it"
            : before.Extensions ? $"GDScript: Godot's headless editor on {project} runs its plugins and tool scripts, as the project's GDExtensions load only outside recovery mode"
            : $"GDScript: Godot {version ?? "of no version said"} has no recovery mode, so Godot's headless editor on {project} runs the project's plugins and tool scripts");
        ProcessStartInfo editor = ChildProcesses.Command(found.Executable,
            [.. GodotLaunch.Chosen(setting)?.Arguments ?? [], GodotExecutables.Headless, .. recovering ? (string[])[GodotExecutables.RecoveryMode] : [], "--editor", "--path", project,
                "--lsp-port", lsp.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            project);
        foreach ((string name, string value) in environment)
        {
            editor.Environment[name] = value;
        }
        Task<LanguageSession> starting = LanguageSession.StartAsync(Start(editor, project, logFolder, lsp), cancel);
        // Apart, as the editor's first seconds are its own: it saves its layout a moment after it loads it, its server already listening.
        IdeErrors.Observe(BackgroundWork.Unheld(() => KeepAsync(before, starting)));
        return await starting;
    }

    /// <summary>The person's editor's server, where one listens on the port and works on this project; null where none
    /// listens, or it is open on another project (it says so as it is initialized), which it is then let go of.</summary>
    private static async Task<LanguageSession?> OwnEditorAsync(string project, int port, string logFolder, CancellationToken cancel)
    {
        LanguageSession session;
        try
        {
            session = await LanguageSession.StartAsync(Start(null, project, logFolder, port), cancel);
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
        if (session.Server.OtherFolder is { } other)
        {
            IdeErrors.Write($"GDScript: the Godot editor on port {port} is open on {other}, not {project}, so Passenger starts its own");
            await session.DisposeAsync();
            return null;
        }
        return session;
    }

    private static ServerStart Start(ProcessStartInfo? editor, string project, string logFolder, int port) =>
        new(editor, project, logFolder, ClientCapabilities.Web)
        {
            Port = port,
            TellsRootPath = true,
            Listening = Waits.Of(TimeSpan.FromSeconds(90)),
            // It parses a document as each text comes and pushes what it found, none too.
            Pushes = new PushPace(EachText: true, TimeSpan.Zero),
        };

    /// <summary>A loopback port free now, for a headless editor to listen on.</summary>
    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>The port the person's Godot editor serves GDScript on, by its newest Editor Settings
    /// (<see cref="GodotServerWords.PortIn"/>); null where Godot's editor has kept no settings in the user's folders, so
    /// has never run there.</summary>
    private static int? EditorPort(IReadOnlyDictionary<string, string> environment)
    {
        string folder = SettingsFolder(environment);
        string? newest;
        try
        {
            newest = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "editor_settings-4*.tres").OrderBy(f => f, StringComparer.Ordinal).LastOrDefault() : null;
            return newest is null ? null : GodotServerWords.PortIn(File.ReadAllText(newest));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Where Godot keeps its editor settings, as it finds its user folders: APPDATA's Godot on Windows,
    /// Application Support's on macOS, XDG's config (or ~/.config) godot on Linux, the variables given first.</summary>
    private static string SettingsFolder(IReadOnlyDictionary<string, string> environment)
    {
        string Variable(string name) => GodotUserFolders.Variable(environment, name) ?? string.Empty;
        string home = Variable("HOME") is { Length: > 0 } set ? set : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsWindows() ? Path.Combine(Variable("APPDATA"), "Godot")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "Godot")
            : Path.Combine(Variable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(home, ".config"), "godot");
    }

    /// <summary>What a headless editor makes beside the project's files where they have none, which a watch of it takes away.</summary>
    [Flags]
    internal enum MadeFiles
    {
        None = 0,

        /// <summary>A script's or shader's <c>.uid</c>: a project from Godot 4.3 or older has none.</summary>
        Uids = 1,

        /// <summary>An asset's <c>.import</c>, beside an asset never imported.</summary>
        Imports = 2,
    }

    /// <summary>A headless editor's run on a project (an import, an export), what it writes over of the person's put back
    /// once it has ended, however it ended, and what it made of <paramref name="removes"/> taken away; meanwhile no other
    /// watch of a headless editor on the project takes an asset's .import away, which the run makes for what follows it.</summary>
    internal static async Task<T> PuttingBackAsync<T>(string project, Func<Task<T>> run, MadeFiles removes = MadeFiles.None)
    {
        using Before before = await BackgroundWork.Run(() => new Before(project, removes));
        foreach (Before other in Watching(project).Where(b => b != before))
        {
            other.KeepImports();
        }
        try
        {
            return await run();
        }
        finally
        {
            await BackgroundWork.Run(() => before.PutBack([], final: true));
        }
    }

    /// <summary>Godot's headless import of a project, through <see cref="PuttingBackAsync"/>: its files written over put
    /// back and what it made of <paramref name="removes"/> taken away, the .import files it makes left for what runs next.</summary>
    internal static Task<ChildResult> ImportAsync(string godot, IReadOnlyList<string> leading, string project, IReadOnlyDictionary<string, string> environment,
        MadeFiles removes, CancellationToken cancel)
    {
        ProcessStartInfo start = ChildProcesses.Command(godot, [.. leading, GodotExecutables.Headless, "--path", project, "--import"], project);
        foreach ((string name, string value) in environment)
        {
            start.Environment[name] = value;
        }
        return PuttingBackAsync(project, () => ChildProcesses.RunAsync(start, cancel: cancel), removes);
    }

    /// <summary>Whether a project has assets Godot has not imported (one with no .import beside it), which a game run without
    /// its editor cannot load. A worker's.</summary>
    internal static bool Unimported(string project)
    {
        try
        {
            return Directory.EnumerateFiles(project, "*", Listing).Any(file => Imported.Contains(Path.GetExtension(file)) && !File.Exists(file + ".import"));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The project's files as Godot's editor scans them: dot folders (its cache, Git's) passed over.</summary>
    private static readonly EnumerationOptions Listing = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };

    /// <summary>The watches of headless editors now, each until it ends.</summary>
    private static readonly List<Before> Watched = [];
    private static readonly Lock WatchedGate = new();

    private static Before[] Watching(string project)
    {
        lock (WatchedGate)
        {
            string canonical = PathNames.Canonical(project);
            return [.. Watched.Where(b => PathNames.Same(b.Project, canonical))];
        }
    }

    /// <summary>What a folder's watcher saw, for the watches of headless editors on projects in it (<see cref="Before.Heard"/>).</summary>
    public static void FolderChanged(Workspace.FolderChanges changes)
    {
        Before[] watching;
        lock (WatchedGate)
        {
            watching = [.. Watched];
        }
        foreach (Before before in watching)
        {
            before.Heard(changes);
        }
    }

    /// <summary>How long a headless editor is watched after its start work: its server answering and its first scan of the
    /// project written (<see cref="Before.Scanned"/>).</summary>
    private static readonly TimeSpan Starting = TimeSpan.FromSeconds(10);

    /// <summary>The longest a headless editor's first scan is waited for once its server answers, and its start work watched
    /// once it has scanned.</summary>
    private static readonly TimeSpan Scanning = TimeSpan.FromMinutes(5);

    /// <summary>How often a headless editor past its start work is looked at for a game's build it reloads.</summary>
    private static readonly TimeSpan Reloads = TimeSpan.FromSeconds(1);

    /// <summary>The files a headless editor writes over put back, and those it makes taken away, through its start work until
    /// quiet <see cref="Starting"/>, another program's writes kept (<see cref="Before.PutBack"/>). Past then nothing it writes
    /// over is put back, so no later write of another's is taken for the editor's (in recovery mode no plugin or tool script
    /// runs in it); but until it ends, the .uid files it makes as .NET's tools in it reload a game's build (<see cref="Before.Built"/>)
    /// are taken away as in its start work. It ends early once the project has gone (a test's, deleted under it), said once.</summary>
    private static async Task KeepAsync(Before before, Task<LanguageSession> starting)
    {
        using (before)
        {
            long? answered = null;
            long? settled = null;
            HashSet<string> told = [];
            // Quiet for a while after its start work, what it last made decided, but an editor that writes on and on is not waited out for good.
            while (settled is not { } at
                || (Stopwatch.GetElapsedTime(Math.Max(at, before.LastFound)) < Starting || before.Deciding) && Stopwatch.GetElapsedTime(at) < Scanning)
            {
                if (!before.PutBack(told))
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                answered ??= starting.IsCompleted ? Stopwatch.GetTimestamp() : null;
                settled ??= answered is { } said && (!starting.IsCompletedSuccessfully || Stopwatch.GetElapsedTime(said) >= Scanning || before.Scanned)
                    ? Stopwatch.GetTimestamp() : null;
            }
            if (!before.PutBack(told))
            {
                return;
            }
            IdeErrors.Write($"GDScript: Godot's headless editor on {before.Project} has done its start work, so what it writes over there is no longer put back");
            if (!starting.IsCompletedSuccessfully)
            {
                return;
            }
            LanguageServer server = starting.Result.Server;
            DateTime built = before.Built;
            // The build being reloaded (when it wrote, the earliest of a run of them) and when it was seen.
            (DateTime Built, long Seen)? reloading = null;
            while (!server.HasExited)
            {
                await Task.Delay(reloading is null ? Reloads : TimeSpan.FromMilliseconds(200));
                if (before.Built is var now && now != built)
                {
                    reloading ??= (now, Stopwatch.GetTimestamp());
                    built = now;
                }
                if (reloading is not { } build)
                {
                    continue;
                }
                if (!before.TakeAwayMade(told, build.Built))
                {
                    return;
                }
                // A build's reload is watched until it has been quiet a while, what it made decided.
                if (Stopwatch.GetElapsedTime(Math.Max(build.Seen, before.LastFound)) >= Starting && !before.Deciding)
                {
                    reloading = null;
                }
            }
        }
    }

    /// <summary>The files beside which a headless editor makes one of its own where there is none: a script's or shader's
    /// <c>.uid</c>, an asset's <c>.import</c>.</summary>
    private static readonly HashSet<string> Uided = new(StringComparer.OrdinalIgnoreCase) { ".gd", ".cs", ".gdshader", ".gdshaderinc" };

    private static readonly HashSet<string> Imported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".svg", ".bmp", ".tga", ".exr", ".hdr", ".wav", ".ogg", ".mp3", ".glb", ".gltf", ".fbx", ".obj", ".dae",
        ".ttf", ".otf", ".woff", ".woff2", ".fnt", ".csv",
    };

    /// <summary>What Godot's editor writes beside the project's files as it scans and imports them, and a safe save's file
    /// written before it takes the saved one's place.</summary>
    private static readonly HashSet<string> GodotWrites = new(StringComparer.OrdinalIgnoreCase) { ".uid", ".import", ".translation", ".tmp" };

    /// <summary>Whether the person's own Godot editor runs on a project, which makes its own files there and writes its own
    /// settings: a Godot started on its folder as an editor, not Passenger's (whose server it is, given its port).</summary>
    private static bool PersonsEditorOn(string project) => PersonsEditorOf(project) is not null;

    /// <summary>The process of the person's own Godot editor on a project (<see cref="PersonsEditorOn"/>), or null.</summary>
    private static int? PersonsEditorOf(string project)
    {
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && DotnetProcesses.Describe(process.Id) is { } running && running.CommandLine.Contains(project, StringComparison.Ordinal)
                    && !running.CommandLine.Contains("--lsp-port", StringComparison.Ordinal)
                    && running.CommandLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(w => w is "--editor" or "-e"))
                {
                    return process.Id;
                }
            }
        }
        return null;
    }

    /// <summary>The files of <see cref="Kept"/> as they were before a headless editor ran (null for one not there), with
    /// their times, and the files it would make that were not there. A change is the editor's to put back only where
    /// nothing says it is another's: a save of Passenger's (the person's own from then on), a write in place, the person's
    /// own Godot editor running, Passenger's Git writing the work tree, or other files coming with it, as one operation.</summary>
    private sealed class Before : IDisposable
    {
        /// <summary>How long a change found waits before it is put back, for the folder's watcher to say what came with it.</summary>
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);

        /// <summary>How long after Passenger saves a file its change is taken for the save's.</summary>
        private static readonly TimeSpan OwnSave = TimeSpan.FromSeconds(5);

        private readonly Dictionary<string, (byte[]? Bytes, DateTime Written)> files = [];

        /// <summary>Each file's identity as it was, and as Passenger last put it back (<see cref="FileIdentity"/>).</summary>
        private readonly Dictionary<string, ulong?> identities = new(StringComparer.Ordinal);

        /// <summary>The files a headless editor would make that were not there, by path.</summary>
        private readonly HashSet<string> unmade = new(StringComparer.Ordinal);

        /// <summary>The kept files another's write changed, theirs from then on.</summary>
        private readonly HashSet<string> others = new(StringComparer.Ordinal);

        /// <summary>The names of those files Passenger has saved since, heard on any saving thread.</summary>
        private readonly HashSet<string> saved = new(StringComparer.Ordinal);

        /// <summary>Every file of the project Passenger saved, with when (a <see cref="Stopwatch"/> timestamp).</summary>
        private readonly Dictionary<string, long> savedAt = new(StringComparer.Ordinal);

        /// <summary>The changes found and not yet put back (a kept file's name, a made file's path), with when each was first found.</summary>
        private readonly Dictionary<string, long> pending = new(StringComparer.Ordinal);

        /// <summary>Held over each file's put back and each save or batch heard, so a save that begins as a file is put back lands after it.</summary>
        private readonly Lock gate = new();

        private readonly DateTime began = DateTime.UtcNow;

        private readonly MadeFiles removes;

        /// <summary>When the files were last looked at, or the watch began.</summary>
        private long looked = Stopwatch.GetTimestamp();

        /// <param name="removes">What the editor makes that is taken away: the language server's editor's .uid and .import;
        /// an import's or export's run leaves its .import for the run after it.</param>
        public Before(string project, MadeFiles removes = MadeFiles.None)
        {
            Project = project = PathNames.Canonical(project);
            this.removes = removes;
            FileText.Writing += OnWriting;
            foreach (string name in Kept)
            {
                try
                {
                    string path = Path.Combine(project, name);
                    files[name] = File.Exists(path) ? (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)) : (null, default);
                    identities[name] = FileIdentity.Of(path);
                }
                catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
                {
                    // Left as the editor leaves it.
                }
            }
            try
            {
                foreach (string file in removes != MadeFiles.None ? Directory.EnumerateFiles(project, "*", Listing) : [])
                {
                    string extension = Path.GetExtension(file);
                    Extensions |= extension.Equals(".gdextension", StringComparison.OrdinalIgnoreCase);
                    string? own = Uided.Contains(extension) && removes.HasFlag(MadeFiles.Uids) ? file + ".uid"
                        : Imported.Contains(extension) && removes.HasFlag(MadeFiles.Imports) ? file + ".import" : null;
                    if (own is not null && !File.Exists(own))
                    {
                        unmade.Add(own);
                    }
                }
                Extensions |= new FileInfo(Path.Combine(project, IgnoredFolders.GodotCache, "extension_list.cfg")) is { Exists: true, Length: > 0 };
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed has nothing found in it to take back.
            }
            lock (WatchedGate)
            {
                Watched.Add(this);
            }
        }

        public string Project { get; }

        /// <summary>When a game's build last wrote its assemblies where Godot's .NET tools read them (under
        /// <c>.godot/mono/temp/bin</c>), which a headless editor reloads within a second; the earliest time where none is there.</summary>
        public DateTime Built
        {
            get
            {
                string bin = Path.Combine(Project, IgnoredFolders.GodotCache, "mono", "temp", "bin");
                try
                {
                    return Directory.Exists(bin) ? Directory.EnumerateFiles(bin, "*.dll", SearchOption.AllDirectories).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max() : default;
                }
                catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
                {
                    return default;
                }
            }
        }

        /// <summary>Whether the project loads a GDExtension (a .gdextension file, or the editor's list of them), which Godot's
        /// recovery mode would leave out of what its language server knows. Found only by a watch that takes made files away.</summary>
        public bool Extensions { get; }

        /// <summary>Whether a change found waits to be put back.</summary>
        public bool Deciding
        {
            get
            {
                lock (gate)
                {
                    return pending.Count > 0;
                }
            }
        }

        /// <summary>Whether the editor has written its first scan of the project since the watch began (its filesystem cache).</summary>
        public bool Scanned
        {
            get
            {
                string editor = Path.Combine(Project, IgnoredFolders.GodotCache, "editor");
                try
                {
                    return Directory.Exists(editor) && Directory.EnumerateFiles(editor, "filesystem_cache*").Any(f => File.GetLastWriteTimeUtc(f) >= began);
                }
                catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        private void OnWriting(string path)
        {
            if (!PathNames.IsUnder(path, Project))
            {
                return;
            }
            string canonical = PathNames.Canonical(path);
            lock (gate)
            {
                savedAt[canonical] = Stopwatch.GetTimestamp();
                // A script Passenger makes has no .uid yet, which the editor would make.
                if (removes.HasFlag(MadeFiles.Uids) && Uided.Contains(Path.GetExtension(canonical)) && !File.Exists(canonical + ".uid"))
                {
                    unmade.Add(canonical + ".uid");
                }
                foreach (string name in Kept)
                {
                    if (PathNames.Same(canonical, Path.Combine(Project, name)))
                    {
                        saved.Add(name);
                    }
                }
            }
        }

        /// <summary>A batch the folder's watcher handed over: a kept file's change or a made file that came with another existing
        /// file of the project's, as a checkout brings them, is that operation's, never put back or taken away. Not counted:
        /// Godot's writes (its cache, what it makes beside files, its safe save's <c>name-XXXXXX</c> of a file there), a file gone (a temporary
        /// one renamed away), Git's folder, a folder, Passenger's saves. A batch whose changes the watcher lost leaves the kept
        /// files' changes waiting the other's, never what Godot made.</summary>
        public void Heard(Workspace.FolderChanges changes)
        {
            long now = Stopwatch.GetTimestamp();
            List<string> theirs = [];
            string? another = null;
            lock (gate)
            {
                // A project gone, deleted whole, is let go of by the next look, which says so.
                if (disposed || !File.Exists(Path.Combine(Project, GodotProject.FileName)))
                {
                    return;
                }
                foreach (string path in changes.Paths)
                {
                    if (!PathNames.IsUnder(path, Project))
                    {
                        continue;
                    }
                    string relative = Path.GetRelativePath(Project, path);
                    if (Kept.FirstOrDefault(k => PathNames.Same(relative, k)) is { } name)
                    {
                        theirs.Add(name);
                    }
                    else if (unmade.Contains(path))
                    {
                        theirs.Add(path);
                    }
                    else if (another is null && !Within(relative, IgnoredFolders.GodotCache) && !Within(relative, ".git") && !GodotWrites.Contains(Path.GetExtension(path))
                        && !SafeSaved(path, changes.Paths) && !(savedAt.TryGetValue(path, out long at) && Stopwatch.GetElapsedTime(at, now) < OwnSave)
                        && File.Exists(path))
                    {
                        another = relative;
                    }
                }
                if (another is null)
                {
                    if (changes.Lost || changes.Overflowed)
                    {
                        foreach (string name in pending.Keys.Where(files.ContainsKey).ToList())
                        {
                            Theirs(name, $"GDScript: {Path.Combine(Project, name)} changed as the folder's watcher lost what changed with it, so it is kept as written");
                        }
                    }
                    return;
                }
                foreach (string mine in theirs.Distinct(StringComparer.Ordinal))
                {
                    pending.Remove(mine);
                    if (unmade.Remove(mine) || files.ContainsKey(mine) && others.Add(mine))
                    {
                        IdeErrors.Write($"GDScript: {Path.Combine(Project, mine)} came with {another}, as one operation, so it is kept as it came");
                    }
                }
            }

            // Whether a path is Godot's safe save's file: a file's own, there or in the batch, a dash and mkstemp's six letters or digits.
            static bool SafeSaved(string path, IReadOnlySet<string> batch) =>
                Path.GetFileName(path).Length > 7 && path[^7] == '-' && path[^6..].All(char.IsAsciiLetterOrDigit) && (batch.Contains(path[..^7]) || File.Exists(path[..^7]));

            // Whether a path of the project's is a folder or lies in it.
            static bool Within(string relative, string folder) =>
                relative == folder || relative.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        /// <summary>No .import the editor makes is taken away from now on: another run on the project makes them for what follows it.</summary>
        public void KeepImports()
        {
            lock (gate)
            {
                unmade.RemoveWhere(path => path.EndsWith(".import", StringComparison.OrdinalIgnoreCase));
            }
        }

        /// <summary>The files a headless editor wrote over put back as they were, with their times, one not there before
        /// deleted, and what it made taken away, each once its change has waited <see cref="Settle"/> (at once where
        /// <paramref name="final"/>) and nothing says it is another's; false once the project has gone (its project.godot),
        /// when there is nothing to put back. A file that could not be put back is said once.</summary>
        public bool PutBack(HashSet<string> told, bool final = false)
        {
            if (!File.Exists(Path.Combine(Project, GodotProject.FileName)))
            {
                IdeErrors.Write($"GDScript: {Project} is gone, so what Godot's headless editor wrote there is no longer put back");
                return false;
            }
            long now = Stopwatch.GetTimestamp();
            bool gitWrote = Passenger.Vcs.Git.WroteWorkTreeSince(looked);
            looked = now;
            RemoveMade(told, gitWrote, final, now);
            foreach ((string name, (byte[]? bytes, DateTime written)) in files)
            {
                string path = Path.Combine(Project, name);
                try
                {
                    lock (gate)
                    {
                        bool there = File.Exists(path);
                        if (saved.Contains(name) || others.Contains(name) || (bytes is null ? !there : there && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)))
                        {
                            pending.Remove(name);
                            continue;
                        }
                        if (gitWrote)
                        {
                            Theirs(name, $"GDScript: {path} changed while Passenger's Git wrote the work tree, so it is kept as written");
                            continue;
                        }
                        if (!final && Waits(name, now))
                        {
                            continue;
                        }
                        pending.Remove(name);
                        if (bytes is not null && there && ByAnother(name, path))
                        {
                            Theirs(name, $"GDScript: {path} was written by another program than Passenger's headless editor, so it is kept as written");
                            continue;
                        }
                        HeardPort(name, path);
                        if (bytes is null)
                        {
                            File.Delete(path);
                        }
                        else
                        {
                            AtomicFile.Write(path, bytes);
                            File.SetLastWriteTimeUtc(path, written);
                            identities[name] = FileIdentity.Of(path);
                            IdeErrors.Write($"GDScript: {path}, which Godot's headless editor wrote over, is put back as it was");
                        }
                    }
                }
                catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
                {
                    if (told.Add(path))
                    {
                        IdeErrors.Write($"GDScript: {path}, which Godot's headless editor wrote over, could not be put back: {unwritten.Message}");
                    }
                }
            }
            return true;
        }

        /// <summary>What a headless editor made that was not there taken away as it reloads a game's build written at
        /// <paramref name="built"/>, as <see cref="PutBack"/> does, the files it writes over left as they are: one there before
        /// that build is another program's, kept. False once the project has gone, as its start work's watch has said.</summary>
        public bool TakeAwayMade(HashSet<string> told, DateTime built)
        {
            if (!File.Exists(Path.Combine(Project, GodotProject.FileName)))
            {
                return false;
            }
            lock (gate)
            {
                foreach (string path in unmade.Where(p => File.Exists(p) && File.GetLastWriteTimeUtc(p) < built).ToList())
                {
                    unmade.Remove(path);
                    pending.Remove(path);
                    IdeErrors.Write($"GDScript: {path} was written before the game's build Godot's headless editor reloads, so it is another program's, kept");
                }
            }
            long now = Stopwatch.GetTimestamp();
            bool gitWrote = Passenger.Vcs.Git.WroteWorkTreeSince(looked);
            looked = now;
            RemoveMade(told, gitWrote, final: false, now);
            return true;
        }

        /// <summary>A kept file's change another's from now on, said.</summary>
        private void Theirs(string name, string said)
        {
            pending.Remove(name);
            others.Add(name);
            IdeErrors.Write(said);
        }

        /// <summary>When a change was last first found, a <see cref="Stopwatch"/> timestamp (0 for none yet).</summary>
        public long LastFound { get; private set; }

        /// <summary>Whether a change found now waits still: first found now, or less than <see cref="Settle"/> before.</summary>
        private bool Waits(string key, long now)
        {
            if (!pending.TryGetValue(key, out long first))
            {
                pending[key] = now;
                LastFound = now;
                return true;
            }
            return Stopwatch.GetElapsedTime(first, now) < Settle;
        }

        /// <summary>Whether a file changed since it was looked at was another program's write: one written in place, which
        /// Godot's safe save, replacing it, never makes, or one the person's own Godot editor running on the project may have.</summary>
        private bool ByAnother(string name, string path) =>
            FileIdentity.Told && identities.GetValueOrDefault(name) is { } was && FileIdentity.Of(path) == was || PersonsEditorOn(Project);

        /// <summary>Takes away the files a headless editor made that were not there (a script's .uid, an asset's .import), each
        /// once it has waited as a change does, but those that came while Passenger's Git wrote the work tree, and all where the
        /// person's own Godot editor runs on the project, which makes its own, theirs from then on.</summary>
        private void RemoveMade(HashSet<string> told, bool gitWrote, bool final, long now)
        {
            List<string> made;
            lock (gate)
            {
                made = [.. unmade.Where(File.Exists)];
                if (made.Count == 0)
                {
                    return;
                }
                if (gitWrote)
                {
                    unmade.ExceptWith(made);
                    made.ForEach(path => pending.Remove(path));
                    IdeErrors.Write($"GDScript: {Plural.Listed([.. made.Select(p => Path.GetRelativePath(Project, p))])} came while Passenger's Git wrote the work tree, so they are kept");
                    return;
                }
                made = [.. made.Where(path => final || !Waits(path, now))];
            }
            if (made.Count == 0)
            {
                return;
            }
            if (PersonsEditorOf(Project) is { } editor)
            {
                lock (gate)
                {
                    unmade.Clear();
                    pending.Clear();
                }
                IdeErrors.Write($"GDScript: the person's own Godot editor (process {editor}) runs on {Project}, so {Plural.Listed([.. made.Select(p => Path.GetRelativePath(Project, p))])} "
                    + "and what else is made there are its, kept");
                return;
            }
            foreach (string path in made)
            {
                try
                {
                    lock (gate)
                    {
                        // Heard meanwhile as another operation's.
                        if (!unmade.Contains(path))
                        {
                            continue;
                        }
                        // Still unmade: the editor may make it again, as it reloads a game's build.
                        File.Delete(path);
                        pending.Remove(path);
                    }
                    IdeErrors.Write($"GDScript: {path}, which Godot's headless editor made, is taken away: the project's own Godot makes its own as it opens it");
                }
                catch (Exception undeleted) when (undeleted is IOException or UnauthorizedAccessException)
                {
                    if (told.Add(path))
                    {
                        IdeErrors.Write($"GDScript: {path}, which Godot's headless editor made, could not be taken away: {undeleted.Message}");
                    }
                }
            }
        }

        /// <summary>The port a headless editor's meta file names, kept as the file is put back.</summary>
        private static void HeardPort(string name, string path)
        {
            if (name == Kept[^1] && File.Exists(path) && GodotIdeMessaging.MetaOf(File.ReadAllText(path)) is (int port, _))
            {
                HeadlessPorts[port] = true;
            }
        }

        private bool disposed;

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
            }
            FileText.Writing -= OnWriting;
            lock (WatchedGate)
            {
                Watched.Remove(this);
            }
        }
    }
}
