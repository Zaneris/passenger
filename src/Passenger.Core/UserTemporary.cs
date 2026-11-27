using System.Runtime.InteropServices;
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
        string hash = FileNames.Hash(key, 10);
        return Path.Combine(Folder(), kind is null ? $"{Prefix}{hash}" : $"{Prefix}{hash}-{kind}");
    }

    /// <summary>What begins the name of every state's own folder (<see cref="Of"/>).</summary>
    private const string Prefix = "passenger-";

    /// <summary>Removes the folders of states let go of for good (a screenshot's, a test run's): each one's own and those of
    /// each kind of it (<see cref="Of"/>), found in one look through <see cref="Folder"/>. One that cannot be removed stays.</summary>
    public static void LetGoOf(IEnumerable<string> states)
    {
        HashSet<string> names = new(states.Select(state => Path.GetFileName(Of(state))), StringComparer.Ordinal);
        if (names.Count == 0 || !Directory.Exists(Folder()))
        {
            return;
        }
        foreach (string folder in Directory.EnumerateDirectories(Folder(), Prefix + "*"))
        {
            string name = Path.GetFileName(folder);
            int kind = name.IndexOf('-', Prefix.Length);
            if (names.Contains(kind < 0 ? name : name[..kind]))
            {
                Remove(folder);
            }
        }
    }

    private static void Remove(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PassengerLog.Write($"A temporary folder Passenger no longer needs stays, {folder}: {error.Message}");
        }
    }

    /// <summary><paramref name="folder"/> made the user's alone (mode 0700), or an <see cref="IOException"/> saying why not:
    /// a link, or one another user made first (on Linux's shared /tmp), whose mode only its owner may set. On Windows the
    /// user's temporary folder, under their profile, is theirs alone already.</summary>
    public static string Own(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
            return folder;
        }
        const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        try
        {
            Directory.CreateDirectory(folder, Owner);
            if (new DirectoryInfo(folder).LinkTarget is not null)
            {
                throw new IOException($"{folder} is a link, not a folder of the user's own");
            }
            File.SetUnixFileMode(folder, Owner);
        }
        catch (UnauthorizedAccessException another)
        {
            throw new IOException($"{folder} is another user's, or in one, not a folder of the user's own", another);
        }
        return folder;
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
