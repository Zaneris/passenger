namespace Passenger.Core;

/// <summary>The one folder Passenger makes its Unix sockets in, a state folder's. It is in the user's own temporary
/// folder (macOS's as the system names it, whatever TMPDIR is; Linux's XDG_RUNTIME_DIR, else /tmp; Windows' temporary
/// folder), named by a short hash of the user and the state folder, so a socket's path stays within the 104 bytes macOS
/// allows (108 elsewhere) whatever TMPDIR or PASSENGER_SETTINGS is, and each state has its own. Each socket in it, and
/// each file kept beside one, is named for its process first (<c>PID-…</c>), so one a process that died left can be
/// taken away; only a window's ends in .sock.</summary>
public static partial class SocketFolders
{
    /// <summary>The variable naming the user's folder of this login's sockets and other run-time files on Linux.</summary>
    public const string RuntimeVariable = "XDG_RUNTIME_DIR";

    /// <summary>A state folder's socket folder.</summary>
    public static string For(string state) => UserTemporary.Of(state);

    /// <summary>A state folder's socket folder made the user's alone (mode 0700), or an <see cref="IOException"/> saying why
    /// not: a link, or one another user made first (on Linux's shared /tmp), whose mode only its owner may set, is never
    /// listened or connected in.</summary>
    public static string Own(string state) => UserTemporary.Own(For(state));

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
}
