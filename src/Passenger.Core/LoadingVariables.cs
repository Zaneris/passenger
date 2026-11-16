namespace Passenger.Core;

/// <summary>The environment variables that load code into a program as it starts: a library its loader preloads or finds
/// (LD_PRELOAD, LD_AUDIT, LD_LIBRARY_PATH, every DYLD_), .NET's startup hooks, additional deps and shared store, a profiler
/// (CORECLR_PROFILER*, DOTNET_PROFILER* and their notification profilers), and a diagnostic port the runtime connects to.
/// A launch another program asks Passenger for (Aspire's orchestrator) takes one only where every file it names is the
/// person's own, in a folder no other user writes (<see cref="PersonsFolders"/>, the run's own folders): so an
/// instrumentation the person installed, OpenTelemetry's in the home folder, Program Files or a project's build, is taken,
/// and a file in a folder another user can write is not.</summary>
public static class LoadingVariables
{
    private const string Preload = "LD_PRELOAD";
    private const string Audit = "LD_AUDIT";
    private const string LibraryPath = "LD_LIBRARY_PATH";
    private const string MacLoader = "DYLD_";
    private const string AdditionalDeps = "DOTNET_ADDITIONAL_DEPS";
    private const string SharedStore = "DOTNET_SHARED_STORE";
    private const string ClrProfiler = "CORECLR_PROFILER";
    private const string ClrProfilerPath = "CORECLR_PROFILER_PATH";
    private const string ClrEnableProfiling = "CORECLR_ENABLE_PROFILING";
    private const string ClrNotificationProfilers = "CORECLR_NOTIFICATION_PROFILERS";
    private const string ClrEnableNotificationProfilers = "CORECLR_ENABLE_NOTIFICATION_PROFILERS";
    private const string NotificationProfilers = "DOTNET_NOTIFICATION_PROFILERS";
    private const string EnableNotificationProfilers = "DOTNET_ENABLE_NOTIFICATION_PROFILERS";
    private const string LegacyDiagnosticPorts = "COMPlus_DiagnosticPorts";

    /// <summary>How a variable's value names what it loads.</summary>
    private enum Naming
    {
        /// <summary>It loads nothing itself: a switch, or a profiler's class id, which loads only from a path or the system's registration.</summary>
        Nothing,
        /// <summary>Libraries, by colons or spaces.</summary>
        Preloaded,
        /// <summary>Libraries or folders, by colons.</summary>
        Colons,
        /// <summary>Startup hooks by the system's path separator, a bare assembly name one of the program's own.</summary>
        Hooks,
        /// <summary>Files or folders by the system's path separator.</summary>
        Separated,
        /// <summary>One file.</summary>
        One,
        /// <summary>Profilers by semicolons, each <c>path={class id}</c>.</summary>
        Notification,
        /// <summary>Diagnostic ports by semicolons, each its address then its settings after commas.</summary>
        Ports,
    }

    /// <summary>The folders no other user writes: the home folder (but a home that is a disk's root) and the system's own; and
    /// the one Passenger runs from, whose code runs already.</summary>
    public static IReadOnlyList<string> PersonsFolders()
    {
        List<string> folders = [AppContext.BaseDirectory];
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        if (Path.IsPathFullyQualified(home) && Path.GetPathRoot(home) is { } root && !PathNames.Same(Path.TrimEndingDirectorySeparator(home), Path.TrimEndingDirectorySeparator(root)))
        {
            folders.Add(home);
        }
        if (OperatingSystem.IsWindows())
        {
            folders.AddRange(new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows }
                .Select(f => Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify)).Where(f => f.Length > 0));
        }
        else
        {
            folders.AddRange(OperatingSystem.IsMacOS() ? ["/usr", "/System"] : ["/usr", "/lib", "/lib32", "/lib64"]);
        }
        return folders;
    }

    /// <summary>Why the first of these variables that loads code from outside <paramref name="own"/> is refused, or null where
    /// none does. Reads the disk (each path's links resolved), so off the UI thread.</summary>
    public static string? WhyNot(IEnumerable<(string Name, string Value)> variables, IReadOnlyList<string> own)
    {
        string[] folders = [.. own.Where(Path.IsPathFullyQualified).Select(f => Path.TrimEndingDirectorySeparator(PathNames.Canonical(f)))];
        foreach ((string name, string value) in variables)
        {
            foreach (string named in Named(NamingOf(name), value))
            {
                if (OperatingSystem.IsWindows() && NamingOf(name) == Naming.Ports)
                {
                    return $"{name} has its runtime connect to {named}, a named pipe another user could have made";
                }
                if (!Path.IsPathFullyQualified(named))
                {
                    return $"{name} names {named}, which is no full path to a file of the person's own";
                }
                string path = PathNames.Canonical(named);
                if (!folders.Any(f => PathNames.Within(path, f)))
                {
                    return $"{name} loads {path}, outside the folders only the person and the system write, where another user could have put it";
                }
            }
        }
        return null;
    }

    /// <summary>How a variable names what it loads; a loader's variable only on the system whose loader reads it.</summary>
    private static Naming NamingOf(string name) =>
        Is(name, Preload) && Elf ? Naming.Preloaded
        : (Is(name, Audit) || Is(name, LibraryPath)) && Elf || name.StartsWith(MacLoader, StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsMacOS() ? Naming.Colons
        : Is(name, RuntimeVariables.StartupHooks) ? Naming.Hooks
        : Is(name, AdditionalDeps) || Is(name, SharedStore) ? Naming.Separated
        : Is(name, RuntimeVariables.DiagnosticPorts) || Is(name, LegacyDiagnosticPorts) ? Naming.Ports
        : Is(name, ClrEnableProfiling) || Is(name, RuntimeVariables.EnableProfiling) || Is(name, ClrEnableNotificationProfilers) || Is(name, EnableNotificationProfilers)
            || Is(name, ClrProfiler) || Is(name, RuntimeVariables.Profiler) ? Naming.Nothing
        : Starts(name, ClrNotificationProfilers) || Starts(name, NotificationProfilers) ? Naming.Notification
        : Starts(name, ClrProfilerPath) || Starts(name, RuntimeVariables.ProfilerPath) ? Naming.One
        : Starts(name, ClrProfiler) || Starts(name, RuntimeVariables.Profiler) ? Naming.One
        : Naming.Nothing;

    private static bool Elf => !OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS();

    private static bool Is(string name, string variable) => name.Equals(variable, StringComparison.OrdinalIgnoreCase);

    private static bool Starts(string name, string variable) => name.StartsWith(variable, StringComparison.OrdinalIgnoreCase);

    /// <summary>What a value names for the loader or the runtime to load, each once, empty parts passed over.</summary>
    private static IEnumerable<string> Named(Naming naming, string value) => naming switch
    {
        Naming.Preloaded => value.Split([':', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries),
        Naming.Colons => value.Split(':', StringSplitOptions.RemoveEmptyEntries),
        Naming.Hooks => value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.IndexOfAny(['/', '\\']) >= 0 || Path.IsPathRooted(p)),
        Naming.Separated => value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        Naming.One => value.Length > 0 ? [value] : [],
        Naming.Notification => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.LastIndexOf('=') is int at and > 0 ? p[..at] : p),
        Naming.Ports => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.Split(',')[0]).Where(p => p.Length > 0),
        _ => [],
    };
}
