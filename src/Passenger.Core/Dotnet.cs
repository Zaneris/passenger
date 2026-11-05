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

    /// <summary>Lets go of the SDKs named, once one is installed or removed.</summary>
    public static void ForgetSdks() => Sdks.Clear();

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
    public static string GlobalJsonOver(string folder)
    {
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            FileInfo file = new(Path.Combine(at, "global.json"));
            if (file.Exists)
            {
                return $"{file.FullName}\n{file.LastWriteTimeUtc.Ticks}\n{file.Length}";
            }
        }
        return string.Empty;
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
    /// installers put it.</summary>
    public static string Host() => HostSearch is { } standIn ? standIn() : Find()?.Host ?? throw new FileNotFoundException(NotFound);

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
        string name = HostName;
        if (Chosen is { } own && File.Exists(Path.Combine(own, name)))
        {
            return new DotnetFound(Path.Combine(own, name), DotnetPlace.Installed);
        }
        ShellEnvironment.Ready();
        string fromRuntime = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", name));
        if (PlacesStandIn is null && File.Exists(fromRuntime))
        {
            return new DotnetFound(fromRuntime, DotnetPlace.Runtime);
        }
        if (Environment.GetEnvironmentVariable(RuntimeVariables.Root) is { Length: > 0 } root && File.Exists(Path.Combine(root, name)))
        {
            return new DotnetFound(Path.Combine(root, name), DotnetPlace.Root);
        }
        if (Programs.OnPath(name) is { } onPath)
        {
            return new DotnetFound(onPath, DotnetPlace.Path);
        }
        return (PlacesStandIn ?? Installed(name)).FirstOrDefault(File.Exists) is { } installed ? new DotnetFound(installed, DotnetPlace.Installed) : null;
    }

    /// <summary>The installation <see cref="Use"/> chose, which <see cref="Find"/> finds before any other for as long as its
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
        string[] path = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
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
    /// host's real path, as a link to it (a Linux distribution's /usr/bin/dotnet) lies outside the installation.</summary>
    public static string Root() => Path.GetDirectoryName(PathNames.Canonical(Host()))!;

    /// <summary>The folder dotnet keeps a person's global tools and templates under, as the dotnet Passenger starts finds
    /// it: DOTNET_CLI_HOME, else the home folder (USERPROFILE on Windows, HOME elsewhere), read once the login shell's
    /// environment is in; null when neither is set.</summary>
    public static string? CliHome()
    {
        ShellEnvironment.Ready();
        return Set("DOTNET_CLI_HOME") ?? Set(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");

        static string? Set(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
    }

    /// <summary>Where Microsoft's installers, the install script, the package managers and Passenger
    /// (<see cref="DotnetInstall.Folder"/>) put the .NET command line.</summary>
    private static IEnumerable<string> Installed(string name)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

/// <summary>How <see cref="Dotnet.Find"/> found a dotnet: the one running this app, DOTNET_ROOT's, the PATH's, or one
/// neither names, where an installer puts it or <see cref="Dotnet.Use"/> chose it.</summary>
public enum DotnetPlace
{
    Runtime,
    Root,
    Path,
    Installed,
}

/// <summary>A <c>dotnet</c> found, and how.</summary>
public sealed record DotnetFound(string Host, DotnetPlace Place)
{
    /// <summary>The installation's folder, its sdk and shared folders beside the host's real path.</summary>
    public string Root => Path.GetDirectoryName(PathNames.Canonical(Host))!;
}
