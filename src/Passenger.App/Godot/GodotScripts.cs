using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>GDScript's language server, which lives in Godot's editor: the person's own editor where it is open on the
/// project (its language server listening on the port its Editor Settings give, <see cref="GodotServerWords.DefaultPort"/>
/// by default), else a headless
/// editor Passenger starts for the project through ChildProcesses on a free loopback port, ended with the folder. A
/// headless editor writes its layout and its .NET tools' messaging file into the project's .godot folder as it starts:
/// both are put back as they were whenever it writes them in its first seconds, so the person's own editor and tools
/// find their own.</summary>
public static class GodotScripts
{
    /// <summary>What a headless editor writes into the project as it starts that is the person's editor's own.</summary>
    private static readonly string[] Kept = [Path.Combine(".godot", "editor", "editor_layout.cfg"), Path.Combine(".godot", "mono", "metadata", "ide_messaging_meta.txt")];

    /// <summary>The variable naming a folder under which a Godot editor Passenger starts for GDScript keeps its user folders
    /// (its settings, caches and logs) in place of the person's: a test's.</summary>
    [IdentifierHome]
    public const string HomeVariable = "PASSENGER_GODOT_HOME";

    /// <summary>The variables that keep a Godot's user folders under <paramref name="home"/>: HOME (and XDG's) on macOS and
    /// Linux, APPDATA and LOCALAPPDATA on Windows.</summary>
    public static Dictionary<string, string> UserFolders(string home)
    {
        string full = Directory.CreateDirectory(Path.GetFullPath(home)).FullName;
        return OperatingSystem.IsWindows()
            ? new() { ["APPDATA"] = Path.Combine(full, "Roaming"), ["LOCALAPPDATA"] = Path.Combine(full, "Local") }
            : new()
            {
                ["HOME"] = full,
                ["XDG_CONFIG_HOME"] = Path.Combine(full, ".config"),
                ["XDG_DATA_HOME"] = Path.Combine(full, ".local", "share"),
                ["XDG_CACHE_HOME"] = Path.Combine(full, ".cache"),
            };
    }

    /// <summary>The user folders <see cref="HomeVariable"/> names, or null where it names none.</summary>
    public static IReadOnlyDictionary<string, string>? UserFoldersOfVariable() =>
        Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home ? UserFolders(home) : null;

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
    /// a headless one started on the Godot Settings names (<paramref name="setting"/>), with the variables
    /// <paramref name="environment"/> gives (a test's user folders) and its log under <paramref name="logFolder"/>.</summary>
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
        Dictionary<string, byte[]?> before = Read(project);
        int lsp = FreePort();
        ProcessStartInfo editor = ChildProcesses.Command(found.Executable,
            [.. GodotLaunch.Chosen(setting)?.Arguments ?? [], "--headless", "--editor", "--path", project, "--lsp-port", lsp.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            project);
        foreach ((string name, string value) in environment)
        {
            editor.Environment[name] = value;
        }
        LanguageSession started = await LanguageSession.StartAsync(Start(editor, project, logFolder, lsp), cancel);
        // Apart, as the editor's first seconds are its own: it saves its layout a moment after it loads it, its server already listening.
        IdeErrors.Observe(BackgroundWork.Unheld(() => KeepAsync(project, before)));
        return started;
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
        string Variable(string name) => environment.TryGetValue(name, out string? given) ? given : Environment.GetEnvironmentVariable(name) ?? string.Empty;
        string home = Variable("HOME") is { Length: > 0 } set ? set : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsWindows() ? Path.Combine(Variable("APPDATA"), "Godot")
            : OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support", "Godot")
            : Path.Combine(Variable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(home, ".config"), "godot");
    }

    /// <summary>The files of <see cref="Kept"/> as they are, null for one not there.</summary>
    private static Dictionary<string, byte[]?> Read(string project)
    {
        Dictionary<string, byte[]?> read = [];
        foreach (string name in Kept)
        {
            try
            {
                string path = Path.Combine(project, name);
                read[name] = File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
            {
                // Left as the editor leaves it.
            }
        }
        return read;
    }

    /// <summary>How long after a headless editor answers the files it writes over as it starts are watched.</summary>
    private static readonly TimeSpan Starting = TimeSpan.FromSeconds(10);

    /// <summary>The files a headless editor writes over in its first seconds put back each time it does, until the project
    /// goes (a test's, deleted under it: on Windows its folder stays while the editor works in it), which ends it, said once.</summary>
    private static async Task KeepAsync(string project, Dictionary<string, byte[]?> before)
    {
        long began = Stopwatch.GetTimestamp();
        HashSet<string> told = [];
        while (Stopwatch.GetElapsedTime(began) < Starting)
        {
            if (!PutBack(project, before, told))
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        PutBack(project, before, told);
    }

    /// <summary>The files a headless editor wrote over put back as they were, one not there before deleted; false once the
    /// project has gone (its project.godot), when there is nothing to put back. A file that could not be put back is said once.</summary>
    private static bool PutBack(string project, Dictionary<string, byte[]?> before, HashSet<string> told)
    {
        if (!File.Exists(Path.Combine(project, GodotProject.FileName)))
        {
            IdeErrors.Write($"GDScript: {project} is gone, so what Godot's headless editor wrote there is no longer put back");
            return false;
        }
        foreach ((string name, byte[]? bytes) in before)
        {
            string path = Path.Combine(project, name);
            try
            {
                if (bytes is null)
                {
                    File.Delete(path);
                }
                else if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                {
                    AtomicFile.Write(path, bytes);
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
}
