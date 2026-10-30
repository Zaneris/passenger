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

    /// <summary>What profiles look for to know their environment is being read with no terminal, and skip what needs one
    /// (an rc's <c>exec tmux</c>, oh-my-zsh's tmux autostart, a prompt's set-up): VSCODE_RESOLVING_ENVIRONMENT and
    /// INTELLIJ_ENVIRONMENT_READER, each set to 1 for the read alone.</summary>
    private static readonly string[] Reading = ["VSCODE_RESOLVING_ENVIRONMENT", "INTELLIJ_ENVIRONMENT_READER"];

    /// <summary>What a shell sets for itself, never for the programs an IDE starts, and what the read sets for it.</summary>
    private static readonly HashSet<string> Own = ["_", "SHLVL", "PWD", "OLDPWD", "TERM", "TERM_PROGRAM", "TERM_PROGRAM_VERSION", "TERM_SESSION_ID", "PS1", "PS2", Written, .. Reading];

    /// <summary>The most of the shell's error output a failed read's log line quotes, from its end.</summary>
    private const int QuotedErrors = 2048;

    /// <summary>Set once the read has ended, however it ended; null while nothing reads it.</summary>
    private static ManualResetEventSlim? readEnded;

    private static long began;

    /// <summary>Starts reading it, unless the IDE was started from a terminal (whose environment is the shell's already)
    /// or on Windows (where a program gets the user's whole environment).</summary>
    public static void Load()
    {
        if (OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("TERM") is not null || readEnded is not null)
        {
            return;
        }
        began = Stopwatch.GetTimestamp();
        ManualResetEventSlim reading = readEnded = new();
        // Not held: what needs the environment waits on its gate.
        UnawaitedWork.Observe(BackgroundWork.Unheld(() => ReadAsync(reading)));
    }

    /// <summary>Waits for the shell's environment, if it is being read, before a program is looked for or started: until the
    /// limit from the read's start at most, so a profile that never finishes costs one wait, once.</summary>
    public static void Ready()
    {
        if (readEnded is { IsSet: false } reading)
        {
            TimeSpan left = Limit - Stopwatch.GetElapsedTime(began);
            reading.Wait(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }
    }

    /// <summary>The person's login shell on macOS and Linux, which this environment is read from and the terminal runs.</summary>
    public static string LoginShell() => LoginShell(Environment.GetEnvironmentVariable("SHELL"), File.Exists, OperatingSystem.IsMacOS());

    /// <summary>The login shell where SHELL is <paramref name="named"/> and <paramref name="exists"/> says which files are
    /// there: SHELL where it names a file, else zsh on macOS, else bash where it is installed, else sh.</summary>
    internal static string LoginShell(string? named, Func<string, bool> exists, bool macOS) =>
        named is { Length: > 0 } && exists(named) ? named
            : macOS ? "/bin/zsh"
            : exists("/bin/bash") ? "/bin/bash" : "/bin/sh";

    private static async Task ReadAsync(ManualResetEventSlim reading)
    {
        string shell = LoginShell();
        string? file = null;
        string? errors = null;
        try
        {
            file = Path.GetTempFileName();
            errors = Path.GetTempFileName();
            // Login and interactive, for an rc file's PATH; answered in a file, so no job the profile leaves holds a pipe.
            ProcessStartInfo start = ChildProcesses.Command("/bin/sh",
                ["-c", "errors=$1; shift; exec \"$@\" >/dev/null 2>\"$errors\"", "sh", errors, shell, "-l", "-i", "-c", "/usr/bin/env -0 > \"$" + Written + "\""]);
            start.Environment[Written] = file;
            foreach (string marker in Reading)
            {
                start.Environment[marker] = "1";
            }
            ChildResult read = await ChildProcesses.RunAsync(start, limit: Limit);
            if (!read.Succeeded)
            {
                Unread(shell, $"it exited with {read.ExitCode}", errors);
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
        catch (TimeoutException)
        {
            Unread(shell, $"it had not ended after {Limit.TotalSeconds:0} s", errors);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Unread(shell, error.Message, null);
        }
        finally
        {
            foreach (string? written in (string?[])[file, errors])
            {
                if (written is null)
                {
                    continue;
                }
                try
                {
                    File.Delete(written);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The system's temporary folder is cleared in time.
                }
            }
            reading.Set();
        }
    }

    /// <summary>The log's word on a read that gave nothing: the shell, why, the PATH programs are then looked for on, and
    /// the end of what the shell wrote to its errors (a file, null for none) on one line after ": ", each of its lines
    /// masked first, since a profile may print its exports (traced, or echoed).</summary>
    private static void Unread(string shell, string why, string? errors)
    {
        string said = errors is null ? string.Empty : Said(errors);
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        PassengerLog.WriteMasked(mask =>
        {
            string line = string.Join(' ', mask(said).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            return $"The login shell's environment was not read, so programs are looked for on the PATH Passenger was given ({path}): "
                + $"{shell} -l -i: {why}{(line.Length == 0 ? string.Empty : $": {line}")}";
        });
    }

    /// <summary>The end of what the shell wrote to its errors, as it wrote it, from a line's start (a line cut short could
    /// not be told a secret's); nothing when it wrote none.</summary>
    private static string Said(string errors)
    {
        try
        {
            using FileStream read = new(errors, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            bool cut = read.Length > QuotedErrors;
            read.Seek(Math.Max(0, read.Length - QuotedErrors), SeekOrigin.Begin);
            using StreamReader text = new(read);
            string said = text.ReadToEnd();
            int first = said.IndexOf('\n', StringComparison.Ordinal);
            return !cut ? said : first < 0 ? string.Empty : said[(first + 1)..];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
