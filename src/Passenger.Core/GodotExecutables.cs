using System.Text.RegularExpressions;

namespace Passenger.Core;

/// <summary>A Godot executable found, and where: the person's choice (<see cref="From"/> naming it), GODOT or GODOT4, the
/// PATH, or an app bundle in Applications. Godot's .NET build carries GodotSharp beside its executable (in a bundle's Resources),
/// with the packages a project restores (<see cref="Packages"/>); a build without it cannot run C#, and Godot 3's runs it
/// on Mono.</summary>
public sealed record GodotFound(string Executable, string From)
{
    /// <summary>Whether the executable is there.</summary>
    public bool Exists { get; init; }

    /// <summary>Its GodotSharp folder, or null for a build without .NET.</summary>
    public string? Sharp { get; init; }

    /// <summary>Its version ("4.7.2"), as its packages, its bundle or its file's name say it; null where none does.</summary>
    public string? Version { get; init; }

    /// <summary>The folder of the Godot packages its build carries (Godot.NET.Sdk, GodotSharp), which its editor gives a
    /// restore as a feed; null for a build without .NET.</summary>
    public string? Packages => Sharp is null ? null : Path.Combine(Sharp, "Tools", "nupkgs");

    /// <summary>Why it cannot run a Godot 4 project's C#, in words for the person, or null when it can.</summary>
    public string? WhyNot => !Exists ? $"{From} names {Executable}, which is not there"
        : Sharp is null ? $"{Executable} is a build of Godot without .NET, which cannot run C#: Godot's .NET build can"
        : Major < 4 ? $"{Executable} is Godot {Version}, whose C# runs on Mono: Passenger runs C# on Godot 4's .NET build"
        : null;

    /// <summary>Why it cannot run a Godot 4 project of GDScript alone, which any build of Godot 4 runs, .NET or not: it is not
    /// there, or it is an older Godot, whose projects and debugger are not Godot 4's; null when it can.</summary>
    public string? WhyNotScripts => !Exists ? $"{From} names {Executable}, which is not there"
        : Major < 4 ? $"{Executable} is Godot {Version}: Passenger runs Godot 4's projects"
        : null;

    /// <summary>Its major version, or null where none is known.</summary>
    public int? Major => Version?.Split('.')[0] is { } major && int.TryParse(major, System.Globalization.CultureInfo.InvariantCulture, out int number) ? number : null;
}

/// <summary>Godot found the way the .NET SDK is: the person's choice first, then GODOT, else GODOT4, then the PATH as the
/// login shell gives it (<see cref="ShellEnvironment"/>), then on macOS the app bundles in /Applications and ~/Applications. Among
/// those the PATH and the bundles offer, the first Godot 4 .NET build is taken, else the first found, which says why it
/// cannot run C#.</summary>
public static partial class GodotExecutables
{
    /// <summary>The variable naming Godot's executable, as Godot's own tools take it.</summary>
    public const string Variable = "GODOT";

    /// <summary>The variable Godot's C# documentation names Godot 4's executable by, read where <see cref="Variable"/> names
    /// none.</summary>
    public const string Variable4 = "GODOT4";

    /// <summary>What the PATH is called where it holds Godot.</summary>
    public const string PathWords = "the PATH";

    /// <summary>The flag that has Godot run with its dummy display and audio drivers: no window, nothing drawn or heard.</summary>
    public const string Headless = "--headless";

    /// <summary>The flag that has Godot on macOS draw a game through its embedded display, the editor's Game view's, as a
    /// background process with no window and no Dock icon of its own: what it draws reaches no screen.</summary>
    public const string Embedded = "--embedded";

    /// <summary>Whether a Godot of a version draws a game in another program's window given by <c>--wid</c>: from Godot 4.4.</summary>
    public static bool Embeds(string? version) => version is not null && Numbers(version) >= new Version(4, 4);

    /// <summary>The flag that has Godot's editor run in its recovery mode: no editor plugin, tool script or GDExtension of the
    /// project runs in it.</summary>
    public const string RecoveryMode = "--recovery-mode";

    /// <summary>Whether a Godot of a version has <see cref="RecoveryMode"/>: from Godot 4.5.</summary>
    public static bool HasRecoveryMode(string? version) => version is not null && Numbers(version) >= new Version(4, 5);

    /// <summary>Whether a Godot of a version draws a game here with no window on the person's screen: on macOS through its
    /// embedded display, from Godot 4.5; on Windows any Godot 4, in a window on a desktop of its own (<see cref="OwnDesktop"/>);
    /// on Linux where Xvfb is installed, in a window on an X server of Passenger's own that shows nowhere.</summary>
    public static bool DrawsWithoutWindow(string? version) => version is not null && (OperatingSystem.IsMacOS() ? Numbers(version) >= new Version(4, 5)
        : OperatingSystem.IsWindows() ? Numbers(version) >= new Version(4, 0)
        : OperatingSystem.IsLinux() && Numbers(version) >= new Version(4, 0) && Programs.OnPath(VirtualDisplay) is not null);

    /// <summary>The X server that draws to memory alone, which Linux's games no one sees draw on.</summary>
    public const string VirtualDisplay = "Xvfb";

    /// <summary>The arguments that have a game no one sees draw at a size, no sound heard: macOS's embedded display
    /// (<see cref="EmbeddedArguments"/>); elsewhere a window of its own where no one sees it, on Linux an X11 one.</summary>
    public static IReadOnlyList<string> UnseenArguments(int width, int height) => OperatingSystem.IsMacOS() ? EmbeddedArguments(width, height)
        : [.. OperatingSystem.IsLinux() ? (string[])["--display-driver", "x11"] : [], "--resolution",
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{width}x{height}"), "--audio-driver", "Dummy"];

    /// <summary>The arguments that have a game Passenger is the debugger of draw through the embedded display at a size, no
    /// sound heard: the display takes the window and process of the editor it would sit in, which Passenger's are.</summary>
    public static IReadOnlyList<string> EmbeddedArguments(int width, int height) => [.. EmbeddedIn(width, height), "--audio-driver", "Dummy"];

    /// <summary>The arguments that have a game draw through macOS's embedded display at a size in pixels, the display taking the
    /// window and process of the editor it sits in, which Passenger's are: what the Game window shows, heard as it plays.</summary>
    public static IReadOnlyList<string> EmbeddedIn(int width, int height) =>
        [Embedded, "--wid", "1", "--editor-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--resolution", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{width}x{height}")];

    /// <summary>Whether a Godot of a version draws a game through macOS's embedded display, which the Game window shows: from 4.5.</summary>
    public static bool DrawsEmbedded(string? version) => version is not null && Numbers(version) >= new Version(4, 5);

    /// <summary>Whether a Godot of a version takes the pointer on macOS's embedded display in points, which it scales to its
    /// window's pixels itself: from 4.7, before that in those pixels.</summary>
    public static bool TakesPointerInPoints(string? version) => version is not null && Numbers(version) >= new Version(4, 7);

    /// <summary>Whether Godot given these arguments runs headless: <see cref="Headless"/>, or the headless display driver.</summary>
    public static bool RunsHeadless(IReadOnlyList<string> arguments)
    {
        string[] engine = [.. EngineArguments(arguments)];
        return engine.Contains(Headless) || engine.Zip(engine.Skip(1)).Any(pair => pair is ("--display-driver", "headless"));
    }

    /// <summary>The variable by which SDL, Godot's gamepad library, turns its HID driver on or off. A headless Godot, which nobody
    /// plays, has it off: on macOS the driver's search for gamepads runs the main thread's run loop, and as Godot ends that
    /// hands whatever waits on the loop to servers Godot has already deleted. Its other gamepad drivers stay.</summary>
    public const string HidGamepads = "SDL_JOYSTICK_HIDAPI";

    /// <summary>What <see cref="HidGamepads"/> says of a driver turned off.</summary>
    public const string HidGamepadsOff = "0";

    /// <summary>The environment of a Godot given these arguments, for a start that passes it on itself: as it stands, with
    /// <see cref="HidGamepads"/> off where they run it headless and it does not say.</summary>
    public static IReadOnlyDictionary<string, string> EnvironmentOf(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment) =>
        !RunsHeadless(arguments) || environment.ContainsKey(HidGamepads) ? environment
        : new Dictionary<string, string>(environment, StringComparer.Ordinal) { [HidGamepads] = HidGamepadsOff };

    /// <summary>Whether Godot given these arguments opens its editor on the project rather than its game.</summary>
    public static bool OpensEditor(IReadOnlyList<string> arguments) => EngineArguments(arguments).Any(IsEditorFlag);

    /// <summary>Godot's arguments without its flags that open the editor, for a launch that opens it itself, the game's own kept.</summary>
    public static IReadOnlyList<string> WithoutEditorFlags(IReadOnlyList<string> arguments) =>
        [.. EngineArguments(arguments).Where(a => !IsEditorFlag(a)), .. arguments.SkipWhile(a => !IsSeparator(a))];

    /// <summary>Whether an argument is the flag that opens Godot's editor (<c>--editor</c>, <c>-e</c>).</summary>
    public static bool IsEditorFlag(string argument) => argument is "--editor" or "-e";

    /// <summary>Whether an argument is Godot's separator: every argument after it is the game's own, whatever it looks like, read
    /// through <c>OS.get_cmdline_user_args()</c>.</summary>
    public static bool IsSeparator(string argument) => argument is "--" or "++";

    /// <summary>The arguments Godot reads itself: those before its separator (<see cref="IsSeparator"/>).</summary>
    public static IEnumerable<string> EngineArguments(IEnumerable<string> arguments) => arguments.TakeWhile(a => !IsSeparator(a));

    /// <summary>Whether a Godot command line (its words, the program first) opens Godot's editor on a project folder, given
    /// canonical: an editor flag with a <c>--path</c> naming that folder whole, or that folder's project.godot named, which
    /// opens the editor by itself. A relative path, read from a working folder unknown here, names none.</summary>
    public static bool OpensEditorOn(IReadOnlyList<string> words, string project)
    {
        string folder = Path.TrimEndingDirectorySeparator(project);
        string[] engine = [.. EngineArguments(words)];
        bool editor = engine.Any(IsEditorFlag);
        for (int i = 0; i < engine.Length; i++)
        {
            string? named = null;
            if (engine[i] == "--path" && i + 1 < engine.Length)
            {
                named = editor ? engine[i + 1] : null;
                i++;
            }
            else if (Path.GetFileName(engine[i]) == "project.godot")
            {
                named = Path.GetDirectoryName(engine[i]);
            }
            if (named is not null && Path.IsPathFullyQualified(named) && PathNames.Same(Path.TrimEndingDirectorySeparator(PathNames.Canonical(named)), folder))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Godot's arguments without each <c>--path</c> naming the project's <paramref name="folder"/> (from the working
    /// folder <paramref name="working"/>), for a launch that names it itself; null where one names another folder. A game's own
    /// arguments, after Godot's separator, are kept as they are.</summary>
    public static IReadOnlyList<string>? WithoutPath(IReadOnlyList<string> arguments, string working, string folder)
    {
        List<string> kept = [];
        int engine = EngineArguments(arguments).Count();
        for (int i = 0; i < engine; i++)
        {
            if (arguments[i] != "--path" || i + 1 == engine)
            {
                kept.Add(arguments[i]);
                continue;
            }
            string named = Path.TrimEndingDirectorySeparator(PathNames.Canonical(Path.Combine(working, arguments[++i])));
            if (!PathNames.Same(named, Path.TrimEndingDirectorySeparator(folder)))
            {
                return null;
            }
        }
        kept.AddRange(arguments.Skip(engine));
        return kept;
    }

    /// <summary>What the Applications folders are called where they hold Godot.</summary>
    public const string ApplicationsWords = "Applications";

    /// <summary>The names Godot is installed under on a PATH (a package's, a link made by hand), before the official
    /// downloads' (<see cref="Downloaded"/>).</summary>
    private static readonly string[] Names = ["godot", "godot4", "godot-mono", "godot4-mono", "godot-dotnet", "godot4-dotnet"];

    /// <summary>Godot as this computer has it: <paramref name="chosen"/> (a path, with the words for where it is chosen) when
    /// given, else as GODOT or GODOT4, the PATH and Applications offer it; null when nothing does.</summary>
    public static GodotFound? Find((string Path, string From)? chosen)
    {
        ShellEnvironment.Ready();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Find(chosen, Named(Environment.GetEnvironmentVariable(Variable), Environment.GetEnvironmentVariable(Variable4)),
            Programs.PathFolders(),
            OperatingSystem.IsMacOS() ? ["/Applications", Path.Combine(home, "Applications")] : [], OperatingSystem.IsWindows());
    }

    /// <summary>The Godot the variables name, with the variable naming it: GODOT's value, else GODOT4's; null where both are
    /// unset or empty.</summary>
    public static (string Path, string From)? Named(string? godot, string? godot4) =>
        godot is { Length: > 0 } ? (godot, Variable) : godot4 is { Length: > 0 } ? (godot4, Variable4) : null;

    /// <summary><see cref="Find((string, string)?)"/> over the variable's Godot (<see cref="Named"/>) and the folders given,
    /// as a system of that kind names its programs.</summary>
    public static GodotFound? Find((string Path, string From)? chosen, (string Path, string From)? named, IReadOnlyList<string> pathFolders, IReadOnlyList<string> applications, bool windows)
    {
        if (chosen is ({ Length: > 0 } path, { } from))
        {
            return At(path, from, windows);
        }
        if (named is ({ Length: > 0 } variable, { } by))
        {
            return At(variable, by, windows);
        }
        GodotFound? first = null;
        foreach (GodotFound found in OnPath(pathFolders, windows).Concat(Bundles(applications)))
        {
            if (found.WhyNot is null)
            {
                return found;
            }
            first ??= found;
        }
        return first;
    }

    /// <summary>Godot at a path: an app bundle is its executable inside, a link the file it links to, and on Windows the
    /// console wrapper the Godot it starts as a child (which a debugger or profiler of the wrapper would never reach).</summary>
    public static GodotFound At(string path, string from, bool windows)
    {
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        if (Directory.Exists(full) && full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return InBundle(full, from);
        }
        if (!File.Exists(full))
        {
            return new GodotFound(full, from);
        }
        string file = PathNames.Canonical(full);
        if (windows && Unwrapped(file) is { } started)
        {
            file = started;
        }
        // A link into a bundle (a package manager's) has the bundle's Resources beside its MacOS folder.
        if (BundleOf(file) is { } bundle)
        {
            return InBundle(bundle, from) with { Executable = file };
        }
        string folder = Path.GetDirectoryName(file)!;
        string sharp = Path.Combine(folder, "GodotSharp");
        return Read(new GodotFound(file, from) { Exists = true }, sharp, Path.GetFileName(file));
    }

    /// <summary>Whether a program a launch profile names is Godot: GODOT or GODOT4 (<see cref="VariableNamed"/>), or a path
    /// whose file is Godot as it is installed (a name a PATH holds it under, an official download's executable or console
    /// wrapper, a Godot app bundle or the program inside one).</summary>
    public static bool NamesGodot(string program, bool windows)
    {
        string named = program.Trim().Trim('"').Trim();
        if (VariableNamed(named, windows) is not null)
        {
            return true;
        }
        char[] separators = windows ? ['/', '\\'] : ['/'];
        string[] parts = named.TrimEnd(separators).Split(separators);
        string name = parts[^1];
        if (name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) || parts is [.., { } bundle, "Contents", "MacOS", _] && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return (name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? name : parts[^4]).StartsWith("Godot", StringComparison.OrdinalIgnoreCase);
        }
        string bare = windows && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^".exe".Length] : name;
        return Names.Contains(bare, StringComparer.OrdinalIgnoreCase) || DownloadVersion().IsMatch(name);
    }

    /// <summary>The variable naming Godot that a launch profile's program is, as cmd, a shell or MSBuild names it (%GODOT%,
    /// $GODOT, ${GODOT}, $(GODOT), and GODOT4 so); null for any other program.</summary>
    public static string? VariableNamed(string program, bool windows)
    {
        string named = program.Trim().Trim('"').Trim();
        return ((string[])[Variable, Variable4]).FirstOrDefault(v => named == $"%{v}%" || named == $"${v}" || named == $"${{{v}}}" || named == $"$({v})"
            || (windows && named.Equals($"%{v}%", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The Godot a Windows console wrapper starts (Godot_v4.7_console.exe starts Godot_v4.7.exe beside it), or
    /// null for any other file.</summary>
    private static string? Unwrapped(string file)
    {
        string name = Path.GetFileName(file);
        foreach (string wrapper in (string[])[".console.exe", "_console.exe", " console.exe"])
        {
            if (name.EndsWith(wrapper, StringComparison.OrdinalIgnoreCase)
                && Path.Combine(Path.GetDirectoryName(file)!, name[..^wrapper.Length] + ".exe") is var started && File.Exists(started))
            {
                return started;
            }
        }
        return null;
    }

    /// <summary>What runs a Godot in a terminal: on Windows the console wrapper beside its .exe, where there is one, since
    /// Godot's own .exe writes to no console it is started in; the executable itself anywhere else.</summary>
    public static string InTerminal(string executable)
    {
        if (!OperatingSystem.IsWindows() || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return executable;
        }
        string bare = executable[..^".exe".Length];
        foreach (string wrapper in (string[])[".console.exe", "_console.exe", " console.exe"])
        {
            if (File.Exists(bare + wrapper))
            {
                return bare + wrapper;
            }
        }
        return executable;
    }

    /// <summary>The app bundle an executable is the program of (X.app/Contents/MacOS/Godot), or null.</summary>
    private static string? BundleOf(string file) =>
        Path.GetDirectoryName(file) is { } macOS && Path.GetFileName(macOS) == "MacOS" && Path.GetDirectoryName(macOS) is { } contents
        && Path.GetFileName(contents) == "Contents" && Path.GetDirectoryName(contents) is { } bundle && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? bundle : null;

    /// <summary>The Godot of an app bundle: Contents/MacOS/Godot, or the one program there, with its Resources' GodotSharp.</summary>
    private static GodotFound InBundle(string bundle, string from)
    {
        string programs = Path.Combine(bundle, "Contents", "MacOS");
        string executable = Path.Combine(programs, "Godot");
        if (!File.Exists(executable) && Directory.Exists(programs) && Directory.GetFiles(programs) is [{ } only])
        {
            executable = only;
        }
        if (!File.Exists(executable))
        {
            return new GodotFound(executable, from);
        }
        GodotFound found = Read(new GodotFound(executable, from) { Exists = true }, Path.Combine(bundle, "Contents", "Resources", "GodotSharp"), Path.GetFileName(bundle));
        return found.Version is null && BundleVersion(Path.Combine(bundle, "Contents", "Info.plist")) is { } version ? found with { Version = version } : found;
    }

    /// <summary>A found Godot with what its GodotSharp folder says: a .NET build where it is Godot 4's or Godot 3's (whose
    /// Mono runtime lies in it), the version its packages carry, else the one its name gives.</summary>
    private static GodotFound Read(GodotFound found, string sharp, string name)
    {
        bool dotnet = Directory.Exists(Path.Combine(sharp, "Api"));
        string? version = dotnet ? PackagedVersion(Path.Combine(sharp, "Tools", "nupkgs")) : null;
        if (version is null && Directory.Exists(Path.Combine(sharp, "Mono")))
        {
            version = "3";
        }
        return found with { Sharp = dotnet ? sharp : null, Version = version ?? NamedVersion(name) };
    }

    /// <summary>The version of GodotSharp's package in a .NET build's packages (GodotSharp.4.7.2.nupkg), or null.</summary>
    private static string? PackagedVersion(string packages)
    {
        if (!Directory.Exists(packages))
        {
            return null;
        }
        try
        {
            foreach (string package in Directory.EnumerateFiles(packages, "GodotSharp.*.nupkg"))
            {
                if (PackageVersion().Match(Path.GetFileName(package)) is { Success: true } match)
                {
                    return match.Groups[1].Value;
                }
            }
        }
        catch (Exception unlisted) when (unlisted is IOException or UnauthorizedAccessException)
        {
            // A folder this user may not list names no version.
        }
        return null;
    }

    /// <summary>The version an official download's name gives (Godot_v4.7.2-stable_mono_linux.x86_64), or null.</summary>
    private static string? NamedVersion(string name) => DownloadVersion().Match(name) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>A bundle's CFBundleShortVersionString, read from its Info.plist as XML; null where it holds none.</summary>
    private static string? BundleVersion(string plist)
    {
        try
        {
            return File.Exists(plist) && PlistVersion().Match(File.ReadAllText(plist)) is { Success: true } match ? match.Groups[1].Value : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Each Godot on the PATH, a folder at a time: the names it is installed under, then the official downloads'.</summary>
    private static IEnumerable<GodotFound> OnPath(IReadOnlyList<string> folders, bool windows)
    {
        HashSet<string> seen = new(PathNames.Comparer);
        foreach (string folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }
            List<string> files = [.. Names.Select(n => Path.Combine(folder, windows ? n + ".exe" : n)).Where(File.Exists)];
            try
            {
                files.AddRange(Directory.EnumerateFiles(folder, "Godot_v*").Where(f => Downloaded(Path.GetFileName(f), windows)).Order(StringComparer.Ordinal));
            }
            catch (Exception unlisted) when (unlisted is IOException or UnauthorizedAccessException)
            {
                // A folder this user may not list offers no Godot.
            }
            foreach (string file in files)
            {
                GodotFound found = At(file, PathWords, windows);
                if (seen.Add(found.Executable))
                {
                    yield return found;
                }
            }
        }
    }

    /// <summary>Whether a file's name is an official download's executable: Godot_v4.7.2-stable_mono_linux.x86_64, and on
    /// Windows its .exe (the console wrapper beside it taken for the Godot it starts).</summary>
    private static bool Downloaded(string name, bool windows) => DownloadVersion().IsMatch(name)
        && (windows ? name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) : !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>The Godot app bundles in the folders given (Godot.app, Godot_mono.app, Godot_v4.7.2-stable_mono.app), the
    /// newest .NET build first.</summary>
    private static IEnumerable<GodotFound> Bundles(IReadOnlyList<string> folders)
    {
        List<GodotFound> found = [];
        foreach (string folder in folders.Where(Directory.Exists))
        {
            try
            {
                found.AddRange(Directory.EnumerateDirectories(folder, "*.app")
                    .Where(b => Path.GetFileName(b).StartsWith("Godot", StringComparison.OrdinalIgnoreCase))
                    .Select(b => InBundle(b, ApplicationsWords)).Where(g => g.Exists));
            }
            catch (Exception unlisted) when (unlisted is IOException or UnauthorizedAccessException)
            {
                // A folder this user may not list offers no Godot.
            }
        }
        return found.OrderBy(g => g.WhyNot is null ? 0 : 1).ThenByDescending(g => Comparable(g.Version)).ThenBy(g => g.Executable, StringComparer.Ordinal);
    }

    /// <summary>A version as one to compare ("4.7.2-beta1" as 4.7.2), none the oldest.</summary>
    private static Version Comparable(string? version) =>
        System.Version.TryParse(version?.Split('-')[0], out Version? parsed) ? parsed : new Version(0, 0);

    /// <summary>The answers each Godot gave to <c>--version</c>, by its executable and when it was last written.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, DateTime), Lazy<Task<string?>>> Answered = new();

    /// <summary>The version a Godot's executable answers <c>--version</c> with, asked headless once per executable as it
    /// is written, else the one its files gave (<see cref="GodotFound.Version"/>).</summary>
    public static async Task<string?> VersionAsync(GodotFound found, CancellationToken cancel = default) =>
        (await AnswerAsync(found, cancel).ConfigureAwait(false) is { } line ? ReadVersion(line)?.Version : null) ?? found.Version;

    /// <summary>A Godot's whole answer to <c>--version</c> ("4.7.2.stable.mono.official.ed1daf0bf"), asked as
    /// <see cref="VersionAsync"/> asks it; null where it is not there or gave none.</summary>
    public static async Task<string?> AnswerAsync(GodotFound found, CancellationToken cancel = default)
    {
        if (!found.Exists)
        {
            return null;
        }
        (string, DateTime) key = (found.Executable, File.GetLastWriteTimeUtc(found.Executable));
        // Not held, every asker's: on the first one's thread, a thread that never runs again would hold the rest.
        Lazy<Task<string?>> once = Answered.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => BackgroundWork.Unheld(() => AskVersionAsync(found.Executable))));
        return await once.Value.WaitAsync(cancel).ConfigureAwait(false);
    }

    private static async Task<string?> AskVersionAsync(string executable)
    {
        try
        {
            ChildAnswer answer = await ChildProcesses.AnswerAsync(ChildProcesses.Command(executable, [Headless, "--version"]),
                line => ReadVersion(line) is not null, limit: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return answer.Answer?.Trim();
        }
        catch (Exception unasked) when (unasked is OperationCanceledException || ChildFailures.Of(unasked))
        {
            return null;
        }
    }

    /// <summary>What a Godot's answer to <c>--version</c> says ("4.7.2.stable.mono.official.ed1daf0bf"): its version as
    /// numbers ("4.7.2"; "4.3" for a first release), and whether it is a .NET build ("mono" among its words); null for a line
    /// that is no such answer.</summary>
    public static (string Version, bool Dotnet)? ReadVersion(string answer) =>
        VersionAnswer().Match(answer.Trim()) is { Success: true } match
            ? (match.Groups["version"].Value, match.Groups["words"].Value.Split('.').Contains("mono", StringComparer.Ordinal))
            : null;

    /// <summary>Why a project built with a Godot.NET.Sdk of one version cannot run on a Godot of another, in words naming
    /// both: an SDK of another major version, or newer than the Godot (whose GodotSharp it then builds against has APIs the
    /// running engine lacks); null when it can, or where either version cannot be read.</summary>
    public static string? Mismatch(string sdk, string godot)
    {
        if (Numbers(sdk) is not { } built || Numbers(godot) is not { } running)
        {
            return null;
        }
        return built.Major != running.Major
            ? $"it builds with Godot.NET.Sdk {sdk}, for Godot {built.Major}, and the Godot that runs it is {godot}"
            : built > running
                ? $"it builds with Godot.NET.Sdk {sdk}, newer than the Godot {godot} that runs it: run it on Godot {sdk} or newer, or build it with Godot.NET.Sdk {godot}"
                : null;
    }

    /// <summary>A version's numbers ("4.7" as 4.7.0), its words after a dash left out; null for none.</summary>
    private static Version? Numbers(string version)
    {
        string[] parts = version.Split('-')[0].Split('.');
        return parts.Length is >= 2 and <= 4 && parts.All(p => int.TryParse(p, System.Globalization.CultureInfo.InvariantCulture, out _))
            ? new Version(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                parts.Length > 2 ? int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0)
            : null;
    }

    [GeneratedRegex(@"^(?<version>\d+\.\d+(?:\.\d+)?)\.(?<words>[A-Za-z][\w.]*)$")]
    private static partial Regex VersionAnswer();

    [GeneratedRegex(@"^GodotSharp\.(\d+\.\d+(?:\.\d+)*(?:-[0-9A-Za-z.]+)?)\.nupkg$", RegexOptions.IgnoreCase)]
    private static partial Regex PackageVersion();

    [GeneratedRegex(@"^Godot_v(\d+\.\d+(?:\.\d+)*)", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadVersion();

    [GeneratedRegex(@"<key>CFBundleShortVersionString</key>\s*<string>([^<]+)</string>")]
    private static partial Regex PlistVersion();
}
