using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Passenger.Core;

/// <summary>The user's own temporary folder, whatever TMPDIR says: macOS's per-user one as the system names it, on the
/// startup disk wherever the home folder is; Linux's XDG_RUNTIME_DIR, else /tmp; Windows' temporary folder. A state
/// folder's own folders in it are named by a short hash of the user and the state folder (<see cref="Of"/>), so each
/// state has its own whatever its path's length.</summary>
public static partial class UserTemporary
{
    /// <summary>The user's own temporary folder (an agent's environment may point TMPDIR elsewhere).</summary>
    public static string Folder()
    {
        if (OperatingSystem.IsMacOS() && DarwinUserTemp() is { } own)
        {
            return own;
        }
        if (OperatingSystem.IsLinux())
        {
            return Environment.GetEnvironmentVariable(SocketFolders.RuntimeVariable) is { Length: > 0 } runtime && Path.IsPathFullyQualified(runtime) && Directory.Exists(runtime)
                ? runtime
                : "/tmp";
        }
        return Path.GetTempPath();
    }

    /// <summary>A state folder's own folder in <see cref="Folder"/>, <c>passenger-HASH</c>, or <c>passenger-HASH-KIND</c>
    /// for a <paramref name="kind"/> of it; made by its caller.</summary>
    public static string Of(string state, string? kind = null)
    {
        string key = $"{Environment.UserName}\n{Path.TrimEndingDirectorySeparator(PathNames.Canonical(state))}";
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..10];
        return Path.Combine(Folder(), kind is null ? $"passenger-{hash}" : $"passenger-{hash}-{kind}");
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
