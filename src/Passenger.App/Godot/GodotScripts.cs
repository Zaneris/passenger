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
/// holds, and its layout, script states and .NET tools' messaging file; each is put back as it was whenever it writes
/// them in its first seconds, so the person's project and their own editor and tools are left as they were.</summary>
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
        Before before = new(project);
        int lsp = FreePort();
        ProcessStartInfo editor = ChildProcesses.Command(found.Executable,
            [.. GodotLaunch.Chosen(setting)?.Arguments ?? [], GodotExecutables.Headless, "--editor", "--path", project, "--lsp-port", lsp.ToString(System.Globalization.CultureInfo.InvariantCulture)],
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

    /// <summary>A headless editor's run on a project (an import, an export), what it writes over of the person's put back
    /// once it has ended, however it ended.</summary>
    internal static async Task<T> PuttingBackAsync<T>(string project, Func<Task<T>> run)
    {
        using Before before = await BackgroundWork.Run(() => new Before(project));
        try
        {
            return await run();
        }
        finally
        {
            await BackgroundWork.Run(() => before.PutBack([]));
        }
    }

    /// <summary>How long after a headless editor answers the files it writes over as it starts are watched.</summary>
    private static readonly TimeSpan Starting = TimeSpan.FromSeconds(10);

    /// <summary>The files a headless editor writes over in its first seconds put back each time it does, while its server
    /// starts and for a while after it answers, however its start ended, until the project goes (a test's, deleted under
    /// it: on Windows its folder stays while the editor works in it), which ends it, said once.</summary>
    private static async Task KeepAsync(Before before, Task<LanguageSession> starting)
    {
        using (before)
        {
            long? answered = null;
            HashSet<string> told = [];
            while (answered is not { } at || Stopwatch.GetElapsedTime(at) < Starting)
            {
                if (!before.PutBack(told))
                {
                    return;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                answered ??= starting.IsCompleted ? Stopwatch.GetTimestamp() : null;
            }
            before.PutBack(told);
        }
    }

    /// <summary>The files of <see cref="Kept"/> as they were before a headless editor ran (null for one not there), with
    /// their times; one a save of Passenger's writes meanwhile is the person's own from then on, so is no longer put back.</summary>
    private sealed class Before : IDisposable
    {
        private readonly string project;
        private readonly Dictionary<string, (byte[]? Bytes, DateTime Written)> files = [];

        /// <summary>The names of those files Passenger has saved since, heard on any saving thread.</summary>
        private readonly HashSet<string> saved = new(StringComparer.Ordinal);

        /// <summary>Held over each file's put back and each save heard, so a save that begins as a file is put back lands after it.</summary>
        private readonly Lock gate = new();

        public Before(string project)
        {
            this.project = project;
            FileText.Writing += OnWriting;
            foreach (string name in Kept)
            {
                try
                {
                    string path = Path.Combine(project, name);
                    files[name] = File.Exists(path) ? (File.ReadAllBytes(path), File.GetLastWriteTimeUtc(path)) : (null, default);
                }
                catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
                {
                    // Left as the editor leaves it.
                }
            }
        }

        private void OnWriting(string path)
        {
            foreach (string name in Kept)
            {
                if (PathNames.Same(path, Path.Combine(project, name)))
                {
                    lock (gate)
                    {
                        saved.Add(name);
                    }
                }
            }
        }

        /// <summary>The files a headless editor wrote over put back as they were, with their times, one not there before
        /// deleted; false once the project has gone (its project.godot), when there is nothing to put back. A file that could
        /// not be put back is said once.</summary>
        public bool PutBack(HashSet<string> told)
        {
            if (!File.Exists(Path.Combine(project, GodotProject.FileName)))
            {
                IdeErrors.Write($"GDScript: {project} is gone, so what Godot's headless editor wrote there is no longer put back");
                return false;
            }
            foreach ((string name, (byte[]? bytes, DateTime written)) in files)
            {
                string path = Path.Combine(project, name);
                try
                {
                    lock (gate)
                    {
                        if (saved.Contains(name))
                        {
                            continue;
                        }
                        if (bytes is null)
                        {
                            if (File.Exists(path))
                            {
                                Heard(name, path);
                                File.Delete(path);
                            }
                        }
                        else if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                        {
                            Heard(name, path);
                            AtomicFile.Write(path, bytes);
                            File.SetLastWriteTimeUtc(path, written);
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

        /// <summary>The port a headless editor's meta file names, kept as the file is put back.</summary>
        private static void Heard(string name, string path)
        {
            if (name == Kept[^1] && File.Exists(path) && GodotIdeMessaging.MetaOf(File.ReadAllText(path)) is (int port, _))
            {
                HeadlessPorts[port] = true;
            }
        }

        public void Dispose() => FileText.Writing -= OnWriting;
    }
}
