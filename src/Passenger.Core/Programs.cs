namespace Passenger.Core;

/// <summary>Programs found as a terminal finds them, on the login shell's PATH (<see cref="ShellEnvironment"/>), in one
/// place: git here, the .NET command line in <see cref="Dotnet"/>.</summary>
public static class Programs
{
    /// <summary>The developer folder xcode-select names, once it holds a git: kept, as the stub's choice of it stays put.</summary>
    private static string? developerFolder;

    /// <summary>The extensions of what Windows starts, directly or through cmd.exe, where PATHEXT lists none of them.</summary>
    private const string StartedExtensions = ".COM;.EXE;.BAT;.CMD";

    /// <summary>The first file of that name on the PATH, or null.</summary>
    public static string? OnPath(string name)
    {
        ShellEnvironment.Ready();
        return PathFolders().Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }

    /// <summary>The program a command of that name runs, found on the PATH as a terminal finds it (<see cref="CommandIn"/>),
    /// or null.</summary>
    public static string? Command(string name)
    {
        ShellEnvironment.Ready();
        return CommandIn(PathFolders(), name);
    }

    /// <summary>The program a command of that name runs from these folders, the first folder's first: on Windows a name
    /// with none of the extensions PATHEXT lists of what Windows starts (.exe, .cmd and the like) is tried with each, in
    /// its order, and never alone; elsewhere the name alone. Null where none is there.</summary>
    public static string? CommandIn(IEnumerable<string> folders, string name) =>
        First(folders, OperatingSystem.IsWindows() ? WindowsNames(name, Environment.GetEnvironmentVariable("PATHEXT")) : [name], File.Exists);

    /// <summary>The first of the names in the folders that <paramref name="exists"/> says is there, each folder's names in
    /// their order before the next folder's.</summary>
    internal static string? First(IEnumerable<string> folders, IReadOnlyList<string> names, Func<string, bool> exists) =>
        folders.SelectMany(folder => names.Select(n => Path.Combine(folder, n))).FirstOrDefault(exists);

    /// <summary>The file names a command runs from on Windows where PATHEXT is <paramref name="pathExtensions"/>: its own
    /// where it ends in an extension of what Windows starts, else it with each of those PATHEXT lists, in its order.</summary>
    internal static IReadOnlyList<string> WindowsNames(string name, string? pathExtensions)
    {
        string[] started = [.. (pathExtensions ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.ToLowerInvariant()).Where(e => e is ".com" or ".exe" or ".bat" or ".cmd").Distinct()];
        if (started.Length == 0)
        {
            started = [.. StartedExtensions.ToLowerInvariant().Split(';')];
        }
        return started.Contains(Path.GetExtension(name).ToLowerInvariant()) ? [name] : [.. started.Select(e => name + e)];
    }

    /// <summary>Every file on the PATH whose name matches <paramref name="pattern"/> (its * and ? as a folder's listing
    /// takes them), the first folder's first, each once; a folder that cannot be listed is passed over.</summary>
    public static IReadOnlyList<string> AllOnPath(string pattern)
    {
        ShellEnvironment.Ready();
        List<string> found = [];
        foreach (string folder in PathFolders())
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, pattern))
                {
                    if (!found.Contains(file, StringComparer.Ordinal))
                    {
                        found.Add(file);
                    }
                }
            }
            catch (Exception unlisted) when (unlisted is IOException or UnauthorizedAccessException)
            {
                // A folder this user may not list offers no program to find.
            }
        }
        return found;
    }

    /// <summary>The PATH's folders as this process has it, in its order, empty ones left out.</summary>
    public static string[] PathFolders() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A PATH's value with <paramref name="folder"/> first and nowhere else, its other entries kept in their order,
    /// an empty one too, which a POSIX shell reads as the folder it works in; the folder alone for an empty PATH.</summary>
    public static string FirstOnPath(string path, string folder) =>
        path.Length == 0 ? folder : string.Join(Path.PathSeparator, [folder, .. path.Split(Path.PathSeparator).Where(at => !PathNames.Same(at, folder))]);

    /// <summary>Node.js on the PATH; null when there is none.</summary>
    public static string? Node() => Command("node");

    /// <summary>Git on the PATH, but not Apple's /usr/bin/git when the developer folder xcode-select names holds no git
    /// behind it: that stub only asks to install the command line tools, on every status. Null when there is none.</summary>
    public static async Task<string?> GitAsync()
    {
        string? git = Command("git");
        if (OperatingSystem.IsMacOS() && git == "/usr/bin/git" && !File.Exists("/Library/Developer/CommandLineTools/usr/bin/git")
            && await DeveloperFolderAsync() is null)
        {
            return null;
        }
        return git;
    }

    /// <summary>The folder Apple's stubs run the developer tools from (an Xcode of any name, the command line tools), asked
    /// of xcode-select until one holds a git; null while none does.</summary>
    private static async Task<string?> DeveloperFolderAsync()
    {
        if (developerFolder is { } kept && File.Exists(Path.Combine(kept, "usr", "bin", "git")))
        {
            return kept;
        }
        try
        {
            ChildAnswer asked = await ChildProcesses.AnswerAsync(ChildProcesses.Command("/usr/bin/xcode-select", ["-p"]),
                line => line.Trim().Length > 0, limit: TimeSpan.FromSeconds(10));
            if (asked.Answer?.Trim() is { Length: > 0 } folder && File.Exists(Path.Combine(folder, "usr", "bin", "git")))
            {
                return developerFolder = folder;
            }
        }
        catch (Exception failed) when (ChildFailures.Of(failed))
        {
            // No xcode-select to ask: the stub has nothing to run either.
        }
        return null;
    }
}
