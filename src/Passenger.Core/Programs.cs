namespace Passenger.Core;

/// <summary>Programs found as a terminal finds them, on the login shell's PATH (<see cref="ShellEnvironment"/>), in one
/// place: git here, the .NET command line in <see cref="Dotnet"/>.</summary>
public static class Programs
{
    /// <summary>The developer folder xcode-select names, once it holds a git: kept, as the stub's choice of it stays put.</summary>
    private static string? developerFolder;

    /// <summary>The first of that name on the PATH, or null.</summary>
    public static string? OnPath(string name)
    {
        ShellEnvironment.Ready();
        return PathFolders().Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
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

    private static string[] PathFolders() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Node.js on the PATH; null when there is none.</summary>
    public static string? Node() => OnPath(OperatingSystem.IsWindows() ? "node.exe" : "node");

    /// <summary>Git on the PATH, but not Apple's /usr/bin/git when the developer folder xcode-select names holds no git
    /// behind it: that stub only asks to install the command line tools, on every status. Null when there is none.</summary>
    public static async Task<string?> GitAsync()
    {
        string? git = OnPath(OperatingSystem.IsWindows() ? "git.exe" : "git");
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
        catch (Exception failed) when (failed is System.ComponentModel.Win32Exception or IOException or TimeoutException)
        {
            // No xcode-select to ask: the stub has nothing to run either.
        }
        return null;
    }
}
