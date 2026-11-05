using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Passenger.Core;

/// <summary>The one folder Passenger makes its Unix sockets in, a state folder's. It is in the user's own temporary
/// folder (macOS's as the system names it, whatever TMPDIR is; Linux's XDG_RUNTIME_DIR, else /tmp; Windows' temporary
/// folder), named by a short hash of the user and the state folder, so a socket's path stays within the 104 bytes macOS
/// allows (108 elsewhere) whatever TMPDIR or PASSENGER_SETTINGS is, and each state has its own. Each socket in it is
/// named for its process first (<c>PID-…</c>), so one a process that died left can be taken away; only a window's ends
/// in .sock.</summary>
public static partial class SocketFolders
{
    /// <summary>The variable naming the user's folder of this login's sockets and other run-time files on Linux.</summary>
    public const string RuntimeVariable = "XDG_RUNTIME_DIR";

    /// <summary>A state folder's socket folder.</summary>
    public static string For(string state)
    {
        string key = $"{Environment.UserName}\n{Path.TrimEndingDirectorySeparator(PathNames.Canonical(state))}";
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..10];
        return Path.Combine(UserTemp(), $"passenger-{hash}");
    }

    /// <summary>A state folder's socket folder made the user's alone (mode 0700), or an <see cref="IOException"/> saying why
    /// not: a link, or one another user made first (on Linux's shared /tmp), whose mode only its owner may set, is never
    /// listened or connected in.</summary>
    public static string Own(string state)
    {
        string folder = For(state);
        if (OperatingSystem.IsWindows())
        {
            // The user's temporary folder, under their profile, is theirs alone already.
            Directory.CreateDirectory(folder);
            return folder;
        }
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(folder, Owner);
        if (new DirectoryInfo(folder).LinkTarget is not null)
        {
            throw new IOException($"{folder} is a link, not a folder of the user's own");
        }
        File.SetUnixFileMode(folder, Owner);
        return folder;
    }

    /// <summary>A state folder's socket folder when it is there and the user's own, else null.</summary>
    public static string? Found(string state)
    {
        if (!Directory.Exists(For(state)))
        {
            return null;
        }
        try
        {
            return Own(state);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The user's own temporary folder: macOS's per-user one as the system names it (an agent's environment may
    /// point TMPDIR elsewhere, where the person's windows would not be found), Linux's XDG_RUNTIME_DIR, else /tmp.</summary>
    private static string UserTemp()
    {
        if (OperatingSystem.IsMacOS() && DarwinUserTemp() is { } own)
        {
            return own;
        }
        if (OperatingSystem.IsLinux())
        {
            return Environment.GetEnvironmentVariable(RuntimeVariable) is { Length: > 0 } runtime && Path.IsPathFullyQualified(runtime) && Directory.Exists(runtime)
                ? runtime
                : "/tmp";
        }
        return Path.GetTempPath();
    }

    private static unsafe string? DarwinUserTemp()
    {
        const int DarwinUserTempDir = 65537;
        byte* buffer = stackalloc byte[1024];
        nuint length = Confstr(DarwinUserTempDir, buffer, 1024);
        return length is > 1 and <= 1024 ? Encoding.UTF8.GetString(buffer, (int)length - 1) : null;
    }

    [LibraryImport("libc", EntryPoint = "confstr")]
    private static unsafe partial nuint Confstr(int name, byte* buffer, nuint length);
}
