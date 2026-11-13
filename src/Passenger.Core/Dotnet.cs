using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Passenger.Core;

/// <summary>Where the .NET command line is, found with the login shell's PATH (<see cref="ShellEnvironment"/>).</summary>
public static partial class Dotnet
{
    /// <summary>The SDK each folder's dotnet names, by the folder and the global.json over it as it stood when asked.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> Sdks = new(PathNames.Comparer);

    /// <summary>What names a folder's SDK: <c>dotnet --version</c> run there, or a test's stand-in.</summary>
    public static Func<string, ProcessStartInfo> SdkQuestion { get; set; } = folder => ChildProcesses.DotnetQuestion(["--version"], folder);

    /// <summary>The SDK a folder's dotnet runs (its global.json's, else the newest), as <c>dotnet --version</c> names it
    /// there: asked once for the folder and the global.json over it as it stands, on the thread pool whoever asks first, and
    /// again after <see cref="ForgetSdks"/>; an <see cref="InvalidOperationException"/> saying why when it names none, which
    /// is asked again next time.</summary>
    public static async Task<string> SdkAsync(string folder, CancellationToken cancel = default)
    {
        string key = $"{folder}\n{GlobalJsonOver(folder)}";
        // Not held, the ask being every caller's: on the first one's thread, one busy or never running again would hold the rest.
        Lazy<Task<string>> once = Sdks.GetOrAdd(key, _ => new Lazy<Task<string>>(() => BackgroundWork.Unheld(() => AskSdkAsync(folder))));
        try
        {
            return await once.Value.WaitAsync(cancel).ConfigureAwait(false);
        }
        catch (Exception) when (once.Value.IsFaulted)
        {
            Sdks.TryRemove(KeyValuePair.Create(key, once));
            throw;
        }
    }

    /// <summary>Lets go of the SDKs named and the dotnet each folder takes, once one is installed or removed.</summary>
    public static void ForgetSdks()
    {
        Sdks.Clear();
        Taken.Clear();
    }

    /// <summary>The version dotnet names, taken as its line comes: whatever dotnet does after naming it is never waited for.</summary>
    private static async Task<string> AskSdkAsync(string folder)
    {
        ChildAnswer version;
        try
        {
            version = await ChildProcesses.AnswerAsync(SdkQuestion(folder), line => SdkVersion().IsMatch(line.Trim()), limit: TimeSpan.FromMinutes(1))
                .ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception unstarted)
        {
            // Not started (its folder deleted since, say): it names no SDK.
            throw new InvalidOperationException($"dotnet --version could not start: {unstarted.Message}", unstarted);
        }
        return version.Answer?.Trim() ?? throw new InvalidOperationException($"dotnet --version failed: {version.Said}");
    }

    /// <summary>A line that is an SDK's version alone, as dotnet --version names one; the lines it lists the SDKs on when
    /// global.json names none installed have each one's folder after it.</summary>
    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.+-]+)?$")]
    private static partial Regex SdkVersion();

    /// <summary>The global.json dotnet reads in a folder (the nearest over it) with its last write and length; empty for none.</summary>
    public static string GlobalJsonOver(string folder) =>
        GlobalJsonIn(folder) is { } file ? $"{file.FullName}\n{file.LastWriteTimeUtc.Ticks}\n{file.Length}" : string.Empty;

    /// <summary>The global.json dotnet reads in a folder, the nearest over it; null for none.</summary>
    public static FileInfo? GlobalJsonIn(string folder)
    {
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            FileInfo file = new(Path.Combine(at, "global.json"));
            if (file.Exists)
            {
                return file;
            }
        }
        return null;
    }

    /// <summary>A test's stand-in for the search <see cref="Host"/> makes, a machine with no .NET's throwing what the search
    /// throws; null for the search itself.</summary>
    public static Func<string>? HostSearch { get; set; }

    /// <summary>A test's stand-in for the places the installers put dotnet, its runtime's own dotnet then passed over; null
    /// for this machine's.</summary>
    public static IReadOnlyList<string>? PlacesStandIn { get; set; }

    /// <summary>What <see cref="Host"/> throws with no .NET command line found.</summary>
    public const string NotFound = "No .NET command line found: install the .NET SDK, or set DOTNET_ROOT to where it is.";

    /// <summary>The name of the .NET command line's program on this system.</summary>
    public static string HostName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    /// <summary>The <c>dotnet</c> of the installation <see cref="Use"/> chose, else the one that runs this app
    /// (framework-dependent, its runtime three folders under the host), else one on DOTNET_ROOT or the PATH, else where the
    /// installers put it; for a folder, the one its projects take (<see cref="Find(string?)"/>).</summary>
    public static string Host(string? folder = null) => HostSearch is { } standIn ? standIn() : Find(folder)?.Host ?? throw new FileNotFoundException(NotFound);

    /// <summary>The <c>dotnet</c> <see cref="Host"/> runs and where it was found; null with none.</summary>
    public static DotnetFound? Find()
    {
        if (HostSearch is { } standIn)
        {
            try
            {
                return new DotnetFound(standIn(), DotnetPlace.Path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
        return Candidates(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).FirstOrDefault();
    }

    /// <summary>The dotnet a folder's projects take: the first, in <see cref="Find()"/>'s order, whose installation holds an SDK
    /// the global.json over the folder takes (<see cref="GlobalJsonSdk.Takes"/>), any SDK without one, as the host's resolver
    /// takes the first place holding a match; else the one <see cref="Find()"/> finds, whose dotnet then says what it lacks.
    /// One that takes it is kept for the folder and its global.json until <see cref="ForgetSdks"/>.</summary>
    public static DotnetFound? Find(string? folder)
    {
        if (folder is null || HostSearch is not null || Find() is not { } first)
        {
            return Find();
        }
        string key = $"{folder}\n{GlobalJsonOver(folder)}\n{first.Host}";
        if (Taken.TryGetValue(key, out DotnetFound? kept))
        {
            return kept;
        }
        (DotnetFound? chosen, bool takes) = Choose(Candidates(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), folder, DotnetInstallation.Read);
        if (chosen is not null && takes)
        {
            Taken[key] = chosen;
        }
        return chosen;
    }

    /// <summary>The installation a folder's projects take where it is not the one <see cref="Find()"/> finds, which what
    /// runs for the folder is given as DOTNET_ROOT, as the process is given the one it takes; null where it is that one.</summary>
    public static string? Apart(string? folder) => folder is not null && Find(folder) is { } taken && Find() is { } first && taken != first ? taken.Root : null;

    /// <summary>The dotnet each folder takes, by the folder, its global.json and the first dotnet found.</summary>
    private static readonly ConcurrentDictionary<string, DotnetFound> Taken = new(StringComparer.Ordinal);

    /// <summary>The first installation dotnet's search for the folder's SDK reaches (<see cref="Searched"/>) that holds an
    /// SDK the global.json over <paramref name="folder"/> takes, any SDK without one, each read by <paramref name="read"/>
    /// once; a process the system translates passes over one with no x64 dotnet. With none, the first of
    /// <paramref name="candidates"/>, or null for none; and whether it takes it.</summary>
    internal static (DotnetFound? Found, bool Takes) Choose(IEnumerable<DotnetFound> candidates, string folder, Func<string, DotnetInstallation> read)
    {
        GlobalJsonSdk? asked = GlobalJsonSdk.Read(GlobalJsonIn(folder)?.FullName);
        List<DotnetFound> found = [.. candidates];
        DotnetFound? first = found.FirstOrDefault();
        HashSet<string> seen = new(PathNames.Comparer);
        foreach (DotnetFound each in Searched(found, asked))
        {
            if (!seen.Add(each.Root) || (Translated && each != first && !ProgramProcessors.Of(each.Host).Contains(Architecture.X64)))
            {
                continue;
            }
            if (read(each.Root).Sdks.Any(sdk => asked?.Takes(sdk) ?? true))
            {
                return (each, true);
            }
        }
        return (first, false);
    }

    /// <summary>The dotnets whose installations dotnet looks for an SDK in, in its order: each of global.json's paths in
    /// turn, its <see cref="GlobalJsonSdk.HostPath"/> the installation of whichever dotnet runs, so every one
    /// <paramref name="found"/>, and each other folder the dotnet installed there, where there is one; without paths, every
    /// one found.</summary>
    private static IEnumerable<DotnetFound> Searched(IReadOnlyList<DotnetFound> found, GlobalJsonSdk? asked)
    {
        if (asked?.Paths is not { } paths)
        {
            return found;
        }
        return paths.SelectMany(path => path == GlobalJsonSdk.HostPath ? found
            : Path.Join(asked.Place(path), HostName) is { } host && File.Exists(host) ? [new DotnetFound(host, DotnetPlace.GlobalJson)] : []);
    }

    /// <summary>Every dotnet there is, in the order <see cref="Find()"/> takes them, the installers' places under
    /// <paramref name="home"/> for the person's own.</summary>
    internal static IEnumerable<DotnetFound> Candidates(string home)
    {
        string name = HostName;
        if (Chosen is { } own && File.Exists(Path.Combine(own, name)))
        {
            yield return new DotnetFound(Path.Combine(own, name), DotnetPlace.Installed);
        }
        ShellEnvironment.Ready();
        // Translated, this process loads MSBuild only from an x64 SDK: one installed beside the system's own comes first.
        if (Translated && PlacesStandIn is null && TranslatedPlaces(name).FirstOrDefault(File.Exists) is { } x64)
        {
            yield return new DotnetFound(x64, DotnetPlace.Translated);
        }
        string fromRuntime = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", name));
        if (PlacesStandIn is null && File.Exists(fromRuntime))
        {
            yield return new DotnetFound(fromRuntime, DotnetPlace.Runtime);
        }
        if (Environment.GetEnvironmentVariable(RuntimeVariables.Root) is { Length: > 0 } root && File.Exists(Path.Combine(root, name)))
        {
            yield return new DotnetFound(Path.Combine(root, name), DotnetPlace.Root);
        }
        if (Programs.OnPath(name) is { } onPath)
        {
            yield return new DotnetFound(onPath, DotnetPlace.Path);
        }
        foreach (string installed in (PlacesStandIn ?? Installed(name, home)).Where(File.Exists))
        {
            yield return new DotnetFound(installed, DotnetPlace.Installed);
        }
    }

    /// <summary>Whether the system translates this process: an x64 build on an Arm64 system (Rosetta on a Mac, Windows'
    /// emulation), which loads only x64 code, an SDK's MSBuild among it.</summary>
    public static bool Translated => TranslatedStandIn ?? (RuntimeInformation.ProcessArchitecture == Architecture.X64 && RuntimeInformation.OSArchitecture == Architecture.Arm64);

    /// <summary>A test's stand-in for whether the system translates this process; null for the truth.</summary>
    public static bool? TranslatedStandIn { get; set; }

    /// <summary>Where an x64 .NET lies beside an Arm64 system's own: DOTNET_ROOT_X64's, then where Microsoft's x64 installer
    /// puts it there.</summary>
    private static IEnumerable<string> TranslatedPlaces(string name)
    {
        if (Environment.GetEnvironmentVariable(RuntimeVariables.RootFor("x64")) is { Length: > 0 } root)
        {
            yield return Path.Combine(root, name);
        }
        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine("/usr/local/share/dotnet/x64", name);
        }
        else if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "x64", name);
        }
    }

    /// <summary>The installation <see cref="Use"/> chose, which <see cref="Find()"/> finds before any other for as long as its
    /// dotnet is there, and as one no PATH names: the shell's own PATH and DOTNET_ROOT did not name it.</summary>
    internal static string? Chosen
    {
        get => chosen;
        set => chosen = value;
    }

    private static volatile string? chosen;

    /// <summary>Has this process and every program it starts, the terminal's shell among them, take the .NET installation
    /// in <paramref name="root"/> (one installed, or found where neither the PATH nor DOTNET_ROOT names it): DOTNET_ROOT
    /// names it and the PATH has it first, and it comes before even the dotnet running this app.</summary>
    public static void Use(string root)
    {
        string[] path = Programs.PathFolders();
        bool taken = path.Length > 0 && PathNames.Same(path[0], root) && PathNames.Same(Environment.GetEnvironmentVariable(RuntimeVariables.Root) ?? string.Empty, root);
        Chosen = PathNames.Canonical(root);
        if (taken)
        {
            return;
        }
        OwnEnvironment.Set(RuntimeVariables.Root, root);
        OwnEnvironment.Set("PATH", string.Join(Path.PathSeparator, [root, .. path.Where(p => !PathNames.Same(p, root))]));
        ForgetSdks();
    }

    /// <summary>The folder <see cref="Host"/> is installed in, its sdk, packs and shared folders beside it: the folder of the
    /// host's real path, as a link to it (a Linux distribution's /usr/bin/dotnet) lies outside the installation. For a
    /// folder, the installation its projects take (<see cref="Find(string?)"/>).</summary>
    public static string Root(string? folder = null) => Path.GetDirectoryName(PathNames.Canonical(Host(folder)))!;

    /// <summary>The folder dotnet keeps a person's global tools and templates under, as the dotnet Passenger starts finds
    /// it: DOTNET_CLI_HOME, else the home folder (USERPROFILE on Windows, HOME elsewhere), read once the login shell's
    /// environment is in; null when neither is set.</summary>
    public static string? CliHome()
    {
        ShellEnvironment.Ready();
        return Set("DOTNET_CLI_HOME") ?? Set(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");

        static string? Set(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
    }

    /// <summary>Where Microsoft's installers, the package managers, then the install script and Passenger
    /// (<see cref="DotnetInstall.Folder"/>) put the .NET command line, the person's own under <paramref name="home"/>.</summary>
    private static IEnumerable<string> Installed(string name, string home)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", name);
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet", name);
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/usr/local/share/dotnet/dotnet";
            yield return "/opt/homebrew/opt/dotnet/libexec/dotnet";
        }
        else
        {
            yield return "/usr/share/dotnet/dotnet";
            yield return "/usr/lib/dotnet/dotnet";
            yield return "/usr/lib64/dotnet/dotnet";
        }
        yield return Path.Combine(home, ".dotnet", name);
    }
}

/// <summary>How <see cref="Dotnet.Find()"/> found a dotnet: the one running this app, DOTNET_ROOT's, the PATH's, or one
/// neither names, where an installer puts it or <see cref="Dotnet.Use"/> chose it; or, for a process the system translates,
/// the x64 one beside the system's own; or, for a folder, where its global.json's sdk paths say.</summary>
public enum DotnetPlace
{
    Runtime,
    Root,
    Path,
    Installed,
    Translated,

    /// <summary>Installed in a folder a global.json's sdk paths name.</summary>
    GlobalJson,
}

/// <summary>A <c>dotnet</c> found, and how.</summary>
public sealed record DotnetFound(string Host, DotnetPlace Place)
{
    /// <summary>The installation's folder, its sdk and shared folders beside the host's real path.</summary>
    public string Root => Path.GetDirectoryName(PathNames.Canonical(Host))!;
}
