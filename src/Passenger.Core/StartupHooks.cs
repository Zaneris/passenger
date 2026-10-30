namespace Passenger.Core;

/// <summary>The startup hooks Passenger gives a program it starts (the inspectors, the test frames' hook): its own first,
/// ahead of those the program's configuration or Passenger's own environment name, and taken out again by the hook as it
/// starts, so nothing the program starts in turn loads it. Framework-free and linked into the hooks.</summary>
public static class StartupHooks
{
    /// <summary>The Avalonia Passenger's hooks into an Avalonia program are compiled against; a program on another major
    /// version is not served.</summary>
    public const int AvaloniaMajor = 12;

    /// <summary>A program's startup hooks with <paramref name="hook"/> first, those its own environment names (else
    /// Passenger's) after it.</summary>
    public static string First(IReadOnlyDictionary<string, string> environment, string hook)
    {
        string? others = environment.TryGetValue(RuntimeVariables.StartupHooks, out string? given) ? given : Environment.GetEnvironmentVariable(RuntimeVariables.StartupHooks);
        return others is { Length: > 0 } ? $"{hook}{Path.PathSeparator}{others}" : hook;
    }

    /// <summary>The path of an assembly the program will load, by its file's name ("Avalonia.Base.dll"), from those the runtime
    /// was given for it (its trusted platform assemblies); null when it has none of that name.</summary>
    public static string? TrustedAssembly(string file) =>
        (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator)
            .FirstOrDefault(p => string.Equals(Path.GetFileName(p), file, StringComparison.OrdinalIgnoreCase));

    /// <summary>A hook, by its assembly's path, out of this process's startup hooks, so the programs it starts do not load
    /// it; the other hooks stay.</summary>
    public static void TakeOut(string hook)
    {
        if (Environment.GetEnvironmentVariable(RuntimeVariables.StartupHooks) is { Length: > 0 } hooks)
        {
            // Compared as the system compares names: Windows' ignore letter case.
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string[] others = [.. hooks.Split(Path.PathSeparator).Where(h => h.Length > 0 && !string.Equals(Path.GetFullPath(h), hook, comparison))];
            OwnEnvironment.Set(RuntimeVariables.StartupHooks, others.Length == 0 ? null : string.Join(Path.PathSeparator, others));
        }
    }
}
