using System.Diagnostics;

namespace Passenger.Core;

/// <summary>The login shell's environment, read once at startup: an app opened from the Finder gets launchd's bare PATH
/// and no DOTNET_ROOT, so dotnet, git and what they run would not be the ones a terminal finds. What the shell sets goes
/// into this process's environment, which every program it starts inherits.</summary>
public static class ShellEnvironment
{
    /// <summary>The longest a slow shell profile holds up the programs started, counted from the read's start, so it is
    /// waited out once; past it, the environment stays as given.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    /// <summary>The variable naming the file the shell writes its environment to.</summary>
    [IdentifierHome]
    private const string Written = "PASSENGER_SHELL_ENVIRONMENT";

    /// <summary>What a shell sets for itself, never for the programs an IDE starts.</summary>
    private static readonly HashSet<string> Own = ["_", "SHLVL", "PWD", "OLDPWD", "TERM", "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "TERM_SESSION_ID", "PS1", "PS2", Written];

    private static Task? loading;

    private static long began;

    /// <summary>Starts reading it, unless the IDE was started from a terminal (whose environment is the shell's already)
    /// or on Windows (where a program gets the user's whole environment).</summary>
    public static void Load()
    {
        if (OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("TERM") is not null || loading is not null)
        {
            return;
        }
        began = Stopwatch.GetTimestamp();
        loading = Task.Run(ReadAsync);
    }

    /// <summary>Waits for the shell's environment, if it is being read, before a program is looked for or started: until the
    /// limit from the read's start at most, so a profile that never finishes costs one wait, once.</summary>
    public static void Ready()
    {
        if (loading is { IsCompleted: false } reading)
        {
            TimeSpan left = Limit - Stopwatch.GetElapsedTime(began);
            reading.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }
    }

    private static async Task ReadAsync()
    {
        string shell = Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } named && File.Exists(named) ? named
            : OperatingSystem.IsMacOS() ? "/bin/zsh" : "/bin/sh";
        string? file = null;
        try
        {
            file = Path.GetTempFileName();
            // Login and interactive, for an rc file's PATH; answered in a file, so no job the profile leaves holds a pipe.
            ProcessStartInfo start = ChildProcesses.Command("/bin/sh",
                ["-c", "exec \"$@\" >/dev/null 2>&1", "sh", shell, "-l", "-i", "-c", "/usr/bin/env -0 > \"$" + Written + "\""]);
            start.Environment[Written] = file;
            ChildResult read = await ChildProcesses.RunAsync(start, limit: Limit);
            if (!read.Succeeded)
            {
                return;
            }
            foreach (string pair in (await File.ReadAllTextAsync(file)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0 && !Own.Contains(pair[..equals]))
                {
                    Environment.SetEnvironmentVariable(pair[..equals], pair[(equals + 1)..]);
                }
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException
            or TimeoutException)
        {
            // No shell to ask, or one whose profile never finished: the environment stays as the IDE was given it.
        }
        finally
        {
            if (file is not null)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The system's temporary folder is cleared in time.
                }
            }
        }
    }
}
