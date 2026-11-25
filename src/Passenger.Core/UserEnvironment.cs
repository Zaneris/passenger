using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Passenger.Core;

/// <summary>The person's own environment on Windows as the registry keeps it (HKEY_CURRENT_USER\Environment), which a
/// program Explorer starts next takes: a variable read as written, its %names% unexpanded, and written keeping the kind
/// it had (one that may hold %names% where it is new), read back, then told to every window as Windows' own settings tell
/// it. Its Path changes a folder at a time (<see cref="With"/>, <see cref="Without"/>), every other entry kept as written,
/// where it was. A program running already, a terminal among them, keeps the environment it started with; the Path a
/// program started now would take, the machine's with the user's, is <see cref="PathNow"/>.</summary>
public static partial class UserEnvironment
{
    /// <summary>The variable naming the folders a command is looked for in.</summary>
    public const string PathVariable = "Path";

    /// <summary>A test's stand-in for the Path a program started now would take, read in place of the machine's and the
    /// user's: a Path as Windows writes one, ";" naming no folder.</summary>
    [IdentifierHome]
    public const string PathNowVariable = "PASSENGER_WINDOWS_PATH";

    /// <summary>The key under HKEY_LOCAL_MACHINE holding the machine's variables.</summary>
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    /// <summary>A test's stand-in for the variables, by name; null for the registry's.</summary>
    public static Dictionary<string, string>? StandIn { get; set; }

    /// <summary>The key under HKEY_CURRENT_USER holding the variables: a test's own in place of Environment.</summary>
    public static string Key { get; set; } = "Environment";

    /// <summary>A variable as written, its %names% unexpanded; null where it is not set.</summary>
    public static string? Get(string name)
    {
        if (StandIn is { } standIn)
        {
            return standIn.GetValueOrDefault(name);
        }
        return OperatingSystem.IsWindows() ? Registered(name) : null;
    }

    /// <summary>Sets a variable, or removes it for null.</summary>
    public static void Set(string name, string? value)
    {
        if (StandIn is { } standIn)
        {
            if (value is null)
            {
                standIn.Remove(name);
            }
            else
            {
                standIn[name] = value;
            }
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            Register(name, value);
        }
    }

    /// <summary>Puts a folder on the Path, at its end or, <paramref name="first"/>, at its start: false where it was there
    /// already (first, where that is asked).</summary>
    public static bool Put(string folder, bool first)
    {
        if (With(Get(PathVariable), folder, first) is not { } path)
        {
            return false;
        }
        Set(PathVariable, path);
        return true;
    }

    /// <summary>Takes a folder off the Path, wherever it is named: false where it was not; the variable removed where
    /// nothing else was left in it.</summary>
    public static bool Take(string folder)
    {
        if (Without(Get(PathVariable), folder) is not { } path)
        {
            return false;
        }
        Set(PathVariable, path.Length == 0 ? null : path);
        return true;
    }

    /// <summary>The Path Windows gives a program started now, as written (its %names% unexpanded): the machine's, then the
    /// user's, as Windows joins them; a test's stand-in (<see cref="PathNowVariable"/>) in their place. Null off Windows.</summary>
    public static string? PathNow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        if (Environment.GetEnvironmentVariable(PathNowVariable) is { } standIn)
        {
            return standIn;
        }
        return string.Join(';', ((string?[])[MachinePath(), Get(PathVariable)]).Where(path => !string.IsNullOrEmpty(path)));
    }

    /// <summary>The folders a Path names that no entry of <paramref name="had"/> is, as Windows reads an entry
    /// (<see cref="Names"/>), its %names% expanded, each once and in the Path's order; an entry that is no whole path from a
    /// drive or a share (a relative one, a %name% left unexpanded) is passed over, as no folder to look for a program in.</summary>
    public static IReadOnlyList<string> Beyond(string? path, IReadOnlyList<string> had)
    {
        List<string> beyond = [];
        foreach (string entry in Entries(path))
        {
            string folder = Environment.ExpandEnvironmentVariables(entry).Trim().Trim('"');
            if (Whole(folder) && !had.Any(at => Names(at, folder)) && !beyond.Any(at => Names(at, folder)))
            {
                beyond.Add(folder);
            }
        }
        return beyond;
    }

    /// <summary>Whether a path is whole as Windows reads one, either slash: from a drive's root (C:\) or a share
    /// (\\server\share).</summary>
    private static bool Whole(string path) =>
        path is [var drive, ':', '\\' or '/', ..] && char.IsAsciiLetter(drive)
        || path is ['\\' or '/', '\\' or '/', var server, ..] && server is not ('\\' or '/' or '?' or '.');

    /// <summary>Whether the Path names a folder.</summary>
    public static bool Holds(string folder) => Entries(Get(PathVariable)).Any(entry => Names(entry, folder));

    /// <summary>A Path with a folder on it: appended (before a closing separator, which stays the Path's last), or put first
    /// (and taken from where else it was), the other entries as written and in their order, so <see cref="Without"/> gives
    /// back the Path as it was; null where the Path has it there already.</summary>
    public static string? With(string? path, string folder, bool first)
    {
        string[] entries = Entries(path);
        if (first ? entries is [var at, ..] && Names(at, folder) : entries.Any(entry => Names(entry, folder)))
        {
            return null;
        }
        if (first)
        {
            string rest = Without(path, folder) ?? path ?? string.Empty;
            return rest.Length == 0 ? folder : $"{folder};{rest}";
        }
        return string.IsNullOrEmpty(path) ? folder : path.EndsWith(';') ? $"{path}{folder};" : $"{path};{folder}";
    }

    /// <summary>A Path without a folder, every other entry as written and where it was, an empty one too; null where the
    /// Path does not name it.</summary>
    public static string? Without(string? path, string folder)
    {
        string[] entries = Entries(path);
        return entries.Any(entry => Names(entry, folder)) ? string.Join(';', entries.Where(entry => !Names(entry, folder))) : null;
    }

    private static string[] Entries(string? path) => string.IsNullOrEmpty(path) ? [] : path.Split(';');

    /// <summary>Whether an entry names a folder, as Windows reads a Path's: its %names% expanded, in or out of quotes,
    /// either slash, a closing one or not, in any letter case.</summary>
    public static bool Names(string entry, string folder) =>
        entry.Trim().Length > 0 && string.Equals(Plain(Environment.ExpandEnvironmentVariables(entry)), Plain(folder), StringComparison.OrdinalIgnoreCase);

    private static string Plain(string path) => path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');

    [SupportedOSPlatform("windows")]
    private static string? Registered(string name)
    {
        using RegistryKey? environment = Registry.CurrentUser.OpenSubKey(Key);
        return environment?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    [SupportedOSPlatform("windows")]
    private static string? MachinePath()
    {
        using RegistryKey? environment = Registry.LocalMachine.OpenSubKey(MachineKey);
        return environment?.GetValue(PathVariable, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>Writes a variable keeping its kind, reads it back, then tells every window, as Windows' own settings do, so
    /// a program Explorer starts next takes it. A variable that is no text is left as it is.</summary>
    [SupportedOSPlatform("windows")]
    private static void Register(string name, string? value)
    {
        using (RegistryKey environment = Registry.CurrentUser.CreateSubKey(Key))
        {
            if (value is null)
            {
                environment.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                RegistryValueKind kind = environment.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null
                    ? RegistryValueKind.ExpandString
                    : environment.GetValueKind(name);
                if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                {
                    throw new IOException($"Your user variable {name} holds no text (it is {kind}), so it is left as it is");
                }
                environment.SetValue(name, value, kind);
            }
            if (environment.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string != value)
            {
                throw new IOException($"Your user variable {name} did not read back as written");
            }
        }
        // A window that does not answer within 5 s is passed over, as Windows' own settings pass it over.
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "Environment", 0x0002, 5000, out _);
    }

    [LibraryImport("user32", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
