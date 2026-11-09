using System.Diagnostics;
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

    /// <summary>No editor of the person's serves the project, and none was to be started: its scripts are Passenger's own
    /// reading's until something it has no answer for asks for Godot's.</summary>
    public sealed class Unstarted() : Exception("no Godot editor of the person's serves the project, and a headless one starts only once asked for");

    /// <summary>The project's GDScript server, connected and initialized: the person's editor open on it where one is, else,
    /// where <paramref name="headless"/>, a headless one started on the Godot Settings names (<paramref name="setting"/>), in
    /// the window's user folders for Godot (<paramref name="environment"/>, <see cref="GodotUserFolders"/>) and its log under
    /// <paramref name="logFolder"/>; else none (<see cref="Unstarted"/>).</summary>
    public static async Task<LanguageSession> StartAsync(string? project, string? setting, IReadOnlyDictionary<string, string> environment, string logFolder,
        bool headless, CancellationToken cancel)
    {
        if (project is null)
        {
            throw new Unavailable($"it is in no Godot project (no {GodotProject.FileName} above it), whose editor answers for GDScript", "no Godot project");
        }
        GodotFound found = GodotLaunch.Found(setting) is { Exists: true } godot ? godot
            : throw new Unavailable("no Godot was found to answer for it: install Godot, then choose its executable in "
                + $"{SettingsDialog.PlaceOfPage(SettingsDialog.GodotPage)}, set {GodotExecutables.Variable} to it, or put it on the PATH", "no Godot");
        Directory.CreateDirectory(logFolder);
        if (GodotServerWords.EditorPort(found.Executable, found.Version ?? await GodotExecutables.VersionAsync(found, cancel), name => GodotUserFolders.Variable(environment, name),
                File.Exists, File.ReadAllText) is { } port
            && await OwnEditorAsync(project, port, logFolder, cancel) is { } own)
        {
            IdeErrors.Write($"GDScript: the Godot editor open on {project} answers on port {port}");
            return own;
        }
        if (!headless)
        {
            throw new Unstarted();
        }
        string? version = await GodotExecutables.VersionAsync(found, cancel);
        Before before = await BackgroundWork.Run(() => new Before(project, MadeFiles.Uids | MadeFiles.Imports));
        // No plugin or tool script of the project's runs there to write into it later; a GDExtension would not load to be known.
        bool recovering = GodotExecutables.HasRecoveryMode(version) && !before.Extensions;
        IdeErrors.Write(recovering ? $"GDScript: Godot's headless editor on {project} runs in its recovery mode, so no editor plugin or tool script of the project runs in it"
            : before.Extensions ? $"GDScript: Godot's headless editor on {project} runs its plugins and tool scripts, as the project's GDExtensions load only outside recovery mode"
            : $"GDScript: Godot {version ?? "of no version said"} has no recovery mode, so Godot's headless editor on {project} runs the project's plugins and tool scripts");
        Task<LanguageSession> starting = ListenedAsync();
        // Apart, as the editor's first seconds are its own: it saves its layout a moment after it loads it, its server already listening.
        IdeErrors.Observe(BackgroundWork.Unheld(() => KeepAsync(before, starting)));
        return await starting;

        // Started again on another port where another program held the one chosen before Godot listened there.
        async Task<LanguageSession> ListenedAsync()
        {
            for (int tries = 1; ; tries++)
            {
                int lsp = GodotPorts.Choose();
                ProcessStartInfo editor = ChildProcesses.Command(found.Executable,
                    [.. GodotLaunch.Chosen(setting)?.Arguments ?? [], GodotExecutables.Headless, .. recovering ? (string[])[GodotExecutables.RecoveryMode] : [], "--editor",
                        "--path", project, "--lsp-port", lsp.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    project);
                foreach ((string name, string value) in environment)
                {
                    editor.Environment[name] = value;
                }
                try
                {
                    return await LanguageSession.StartAsync(Start(editor, project, logFolder, lsp), cancel);
                }
                catch (LanguageServerFailed taken) when (taken.PortTaken && tries < GodotPorts.Tries)
                {
                    IdeErrors.Write($"GDScript: Godot's headless editor on {project} could not serve: {taken.Reason}, so it is started again on another port");
                }
            }
        }
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
            Taken = GodotPorts.HeldBy,
            // It parses a document as each text comes and pushes what it found, none too.
            Pushes = new PushPace(EachText: true, TimeSpan.Zero),
        };

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
            await BackgroundWork.Run(async () =>
            {
                await before.HeardAsync();
                before.PutBack([], final: true);
            });
        }
    }

    /// <summary>Godot's headless import of a project where what runs next without its editor needs one (<see cref="NeedsImport"/>),
    /// as a game, GUT's and GoDotTest's tests load only what the editor imported: started with the arguments Settings gives each
    /// start of Godot (<paramref name="leading"/>), through <see cref="PuttingBackAsync"/>, its files written over put back and its
    /// .uid files taken away, the .import files it makes left for what runs next. Null where it imported or none was needed,
    /// else why Godot could not.</summary>
    internal static async Task<string?> ImportIfNeededAsync(string godot, IReadOnlyList<string> leading, string project, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancel)
    {
        if (!await BackgroundWork.Run(() => NeedsImport(project), cancel))
        {
            return null;
        }
        ProcessStartInfo start = ChildProcesses.Command(godot, [.. leading, GodotExecutables.Headless, "--path", project, "--import"], project);
        foreach ((string name, string value) in environment)
        {
            start.Environment[name] = value;
        }
        ChildResult imported = await PuttingBackAsync(project, () => ChildProcesses.RunAsync(start, cancel: cancel), MadeFiles.Uids);
        return imported.Succeeded ? null
            : $"Godot could not import {PathNames.Shown(project, null)} as its editor does: it ended with {imported.Ended}";
    }

    /// <summary>Whether Godot has yet to import a project as a run without its editor reads it, of the files Godot's editor scans
    /// (<see cref="Scanned"/>): an asset with no .import beside it, which a game cannot load, or any asset of a project never
    /// imported here (no cache of its global classes); or global classes the cache no longer lists as its GDScript files
    /// declare them (<see cref="GodotClassCache.Stale"/>), those newer than the cache read, or every one where there is none.
    /// A worker's.</summary>
    private static bool NeedsImport(string folder)
    {
        // By the walk's own spelling of the paths it finds, so each is the project's res:// path.
        string project = PathNames.Canonical(folder);
        string cache = Path.Combine(project, GodotClassCache.RelativePath);
        string? listed = Text(cache);
        DateTime? cached = listed is null ? null : File.GetLastWriteTimeUtc(cache);
        List<string> scripts = [];
        foreach ((string _, IReadOnlyList<string> files) in PathNames.FilesUnder(project, Scanned))
        {
            foreach (string file in files.Where(f => !Path.GetFileName(f).StartsWith('.')))
            {
                string extension = Path.GetExtension(file);
                if (Imported.Contains(extension) && (cached is null || !File.Exists(file + ".import")))
                {
                    return true;
                }
                if (extension.Equals(".gd", StringComparison.OrdinalIgnoreCase) && (cached is null || File.GetLastWriteTimeUtc(file) > cached)
                    && GodotResPaths.Of(project, file) is { } script)
                {
                    scripts.Add(script);
                }
            }
        }
        return GodotClassCache.Stale(listed, scripts, path => GodotResPaths.FileOf(project, path) is { } file ? Text(file) : null,
            path => GodotResPaths.FileOf(project, path) is { } file && File.Exists(file));

        static string? Text(string file)
        {
            try
            {
                return File.Exists(file) ? File.ReadAllText(file) : null;
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Whether Godot's editor scans a folder of a project: none hidden (its cache, Git's), none a .gdignore leaves out,
    /// and none holding a project of its own.</summary>
    private static bool Scanned(string folder) =>
        !Path.GetFileName(folder).StartsWith('.') && !File.Exists(Path.Combine(folder, ".gdignore")) && !File.Exists(Path.Combine(folder, GodotProject.FileName));

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
            // When a build's reload was seen; and what of the files the editor would make stood at the last look before it.
            long? reloading = null;
            IReadOnlyList<string> standing = before.Standing();
            while (!server.HasExited)
            {
                await Task.Delay(reloading is null ? Reloads : TimeSpan.FromMilliseconds(200));
                if (before.Built is var now && now != built)
                {
                    built = now;
                    if (reloading is null)
                    {
                        reloading = Stopwatch.GetTimestamp();
                        before.KeepStanding(standing);
                    }
                }
                if (reloading is not { } since)
                {
                    standing = before.Standing();
                    continue;
                }
                if (!before.TakeAwayMade(told))
                {
                    return;
                }
                // A build's reload is watched until it has been quiet a while, what it made decided.
                if (Stopwatch.GetElapsedTime(Math.Max(since, before.LastFound)) >= Starting && !before.Deciding)
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

    /// <summary>The process of the person's own Godot editor on a project (canonical), which makes its own files there and
    /// writes its own settings, or null: a Godot whose command line opens its editor on that folder whole
    /// (<see cref="GodotExecutables.OpensEditorOn"/>), not Passenger's own (whose server it is, given its port).</summary>
    private static int? PersonsEditorOf(string project)
    {
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id != Environment.ProcessId && DotnetProcesses.Describe(process.Id) is { } running && running.CommandWords is var words
                    && !GodotExecutables.EngineArguments(words).Contains("--lsp-port") && GodotExecutables.OpensEditorOn(words, project))
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
    /// own Godot editor running, Passenger's Git writing the work tree, other files coming with it, as one operation, or
    /// for project.godot, which the folder's watcher hears, no word of Godot's own safe save of it.</summary>
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

        /// <summary>project.godot as it stood when Godot's own safe save of it was last heard; null for none heard.</summary>
        private (ulong? Identity, DateTime Written)? safeSaved;

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

        /// <summary>The newest write time of a game's build's assemblies where Godot's .NET tools read them (under
        /// <c>.godot/mono/temp/bin</c>), a copy's kept from its source, which changes as a build lands and a headless editor
        /// reloads within a second; the earliest time where none is there.</summary>
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
        /// Godot's writes (its cache, what it makes beside files, its safe save's <c>name-XXXXXX</c> of a file there, noted for
        /// project.godot), a file gone (a temporary one renamed away), Git's folder, a folder, Passenger's saves. A batch whose
        /// changes the watcher lost leaves the kept files' changes waiting the other's, never what Godot made.</summary>
        public void Heard(Workspace.FolderChanges changes)
        {
            long now = Stopwatch.GetTimestamp();
            List<string> theirs = [];
            string? another = null;
            string settings = Path.Combine(Project, GodotProject.FileName);
            lock (gate)
            {
                // A project gone, deleted whole, is let go of by the next look, which says so.
                if (disposed || !File.Exists(settings))
                {
                    return;
                }
                foreach (string path in changes.Paths)
                {
                    if (!PathNames.IsUnder(path, Project))
                    {
                        continue;
                    }
                    if (GodotSafeSave.Saves(path, settings))
                    {
                        safeSaved = Stamp(settings);
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

            // Whether a path is Godot's safe save's file: named after a file there or in the batch.
            static bool SafeSaved(string path, IReadOnlySet<string> batch) => GodotSafeSave.FileOf(path) is { } file && (batch.Contains(file) || File.Exists(file));

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
            Lazy<int?> persons = PersonsEditor();
            RemoveMade(told, gitWrote, final, now, persons);
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
                        if (ByAnother(name, path, persons))
                        {
                            Theirs(name, there ? $"GDScript: {path} was written by another program than Passenger's headless editor, so it is kept as written"
                                : $"GDScript: {path} was deleted by another program than Passenger's headless editor, so it is left deleted");
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

        /// <summary>The files a headless editor would make that are there now, another program's should a build come next.</summary>
        public IReadOnlyList<string> Standing()
        {
            lock (gate)
            {
                return [.. unmade.Where(File.Exists)];
            }
        }

        /// <summary>The files that stood (<see cref="Standing"/>) at the last look before a game's build the editor reloads:
        /// another program's, kept from then on.</summary>
        public void KeepStanding(IReadOnlyList<string> standing)
        {
            lock (gate)
            {
                foreach (string path in standing.Where(unmade.Contains))
                {
                    unmade.Remove(path);
                    pending.Remove(path);
                    IdeErrors.Write($"GDScript: {path} was there before the game's build Godot's headless editor reloads, so it is another program's, kept");
                }
            }
        }

        /// <summary>What a headless editor made that was not there taken away as it reloads a game's build, as
        /// <see cref="PutBack"/> does, the files it writes over left as they are, and one written before the build's newest
        /// assembly kept, as the editor makes its own only once it has reloaded that build. False once the project has gone,
        /// as its start work's watch has said.</summary>
        public bool TakeAwayMade(HashSet<string> told)
        {
            if (!File.Exists(Path.Combine(Project, GodotProject.FileName)))
            {
                return false;
            }
            long now = Stopwatch.GetTimestamp();
            bool gitWrote = Passenger.Vcs.Git.WroteWorkTreeSince(looked);
            looked = now;
            RemoveMade(told, gitWrote, final: false, now, PersonsEditor(), Built);
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

        /// <summary>Whether a file changed since it was looked at was another program's change: project.godot not as Godot's
        /// safe save was heard to leave it, as replacing a file says nothing of who did; one written in place, which that save
        /// never makes; or any, made, written over or deleted, while the person's own Godot editor runs on the project
        /// (<paramref name="persons"/>).</summary>
        private bool ByAnother(string name, string path, Lazy<int?> persons) =>
            name == GodotProject.FileName && Stamp(path) != safeSaved
            || FileIdentity.Told && identities.GetValueOrDefault(name) is { } was && FileIdentity.Of(path) == was || persons.Value is not null;

        /// <summary>What tells a file as it stands from itself as it stood, without reading it.</summary>
        private static (ulong? Identity, DateTime Written) Stamp(string path) => (FileIdentity.Of(path), File.GetLastWriteTimeUtc(path));

        /// <summary>Waits as a change found does (<see cref="Settle"/>, at most) for the folder's watcher to say Godot saved
        /// project.godot, where it changed with no such word yet: a run's last save is heard a moment after the run ends.</summary>
        public async Task HeardAsync()
        {
            long began = Stopwatch.GetTimestamp();
            while (Unsaid() && Stopwatch.GetElapsedTime(began) < Settle)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }
        }

        /// <summary>Whether project.godot stands changed with no word of a safe save that left it so, and is not another's yet.</summary>
        private bool Unsaid()
        {
            string name = GodotProject.FileName;
            lock (gate)
            {
                (ulong? Identity, DateTime Written) now = Stamp(Path.Combine(Project, name));
                return files.TryGetValue(name, out (byte[]? Bytes, DateTime Written) was) && !saved.Contains(name) && !others.Contains(name)
                    && now != (identities.GetValueOrDefault(name), was.Written) && now != safeSaved;
            }
        }

        /// <summary>The person's own Godot editor on the project as one look finds it, the processes scanned only once that
        /// look has a change to decide, and only once.</summary>
        private Lazy<int?> PersonsEditor() => new(() => PersonsEditorOf(Project), LazyThreadSafetyMode.None);

        /// <summary>Takes away the files a headless editor made that were not there (a script's .uid, an asset's .import), each
        /// once it has waited as a change does, but those that came while Passenger's Git wrote the work tree, those written
        /// before <paramref name="built"/> (a game's build the editor reloads), and all where the person's own Godot editor
        /// runs on the project (<paramref name="persons"/>), which makes its own, theirs from then on.</summary>
        private void RemoveMade(HashSet<string> told, bool gitWrote, bool final, long now, Lazy<int?> persons, DateTime? built = null)
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
                if (built is { } assemblies && made.Where(path => WrittenBefore(path, assemblies)).ToList() is { Count: > 0 } older)
                {
                    unmade.ExceptWith(older);
                    older.ForEach(path => pending.Remove(path));
                    made = [.. made.Except(older, StringComparer.Ordinal)];
                    IdeErrors.Write($"GDScript: {Plural.Listed([.. older.Select(p => Path.GetRelativePath(Project, p))])} written before the game's build Godot's headless editor "
                        + "reloads, so another program's, kept");
                }
            }
            if (made.Count == 0)
            {
                return;
            }
            if (persons.Value is { } editor)
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

        /// <summary>Whether a file was last written before a moment, as one of the person's written before a build is; false
        /// where it cannot be read.</summary>
        private static bool WrittenBefore(string path, DateTime moment)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path) < moment;
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
            {
                return false;
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
