namespace Passenger.Core;

/// <summary>Whether a path read out of text a program or a file wrote (a stack frame, a compiler's message, a link, a
/// PDB's document) may be asked of the disk, told from its text alone: a path on this machine's own volumes may, and a
/// share's, a device's or one under an automount root never, since merely asking about it reaches another machine (Windows
/// signing in to the share's host as the person), opens a device or mounts what it names. Every place that turns such text
/// into a file asks here before it asks the disk, so showing a line reaches nothing; a share's path inside a folder the IDE
/// already works in (<see cref="Reach"/>, one the person opened on a share) is that folder's.</summary>
public static class FoundPaths
{
    /// <summary>The roots under which naming a path mounts it: macOS's and autofs's -hosts map (/net/host/… mounts that
    /// host's exports) and, on macOS, its network servers' map.</summary>
    private static readonly string[] Automounted = OperatingSystem.IsMacOS() ? ["/net", "/Network"] : ["/net"];

    private static readonly Lock Reaching = new();

    /// <summary>The folders the IDE works in, made whole, replaced whole under <see cref="Reaching"/> and read without it.</summary>
    private static string[] reached = [];

    /// <summary>Whether a path may be asked of the disk: one on this machine's own volumes, as its text says, or a share's
    /// or an automounted one inside a folder the IDE works in.</summary>
    public static bool Askable(string path) => Askable(path, []);

    /// <summary>Whether a path may be asked of the disk, as <see cref="Askable(string)"/> and inside one of
    /// <paramref name="folders"/> too, which the IDE reaches already; never a device's.</summary>
    public static bool Askable(string path, IEnumerable<string> folders)
    {
        if (WhyNotAsked(path) is not { } why)
        {
            return true;
        }
        if (why is not (FoundPathRefusal.Share or FoundPathRefusal.Automounted))
        {
            return false;
        }
        // Its dots resolved, so none climbs out of the folder to another host.
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        foreach (string folder in Volatile.Read(ref reached).Concat(folders))
        {
            if (PathNames.IsUnder(full, folder))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Holds a folder the IDE works in (a window's open folder) until disposed: a share's or an automounted path
    /// inside it is its own and asked as any.</summary>
    public static IDisposable Reach(string folder)
    {
        string full = Path.GetFullPath(folder);
        lock (Reaching)
        {
            reached = [.. reached, full];
        }
        return new Held(full);
    }

    private sealed class Held(string folder) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            lock (Reaching)
            {
                int at = Array.IndexOf(reached, folder);
                reached = at < 0 ? reached : [.. reached[..at], .. reached[(at + 1)..]];
            }
        }
    }

    /// <summary>The path on this machine a file address names (no host, or localhost), as the disk may be asked of it; null
    /// for any other address, another host's file, or a share's, a device's or an automounted path however it is escaped
    /// (file:///%5C%5Chost%5Cshare).</summary>
    public static string? OfFileAddress(Uri address)
    {
        if (!address.IsFile || (address.Host.Length > 0 && !address.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        string path = Uri.UnescapeDataString(address.AbsolutePath);
        if (OperatingSystem.IsWindows())
        {
            // A drive's path comes as /C:/x.
            path = (path is ['/', ..] ? path[1..] : path).Replace('/', '\\');
        }
        return Path.IsPathFullyQualified(path) && Askable(path) ? path : null;
    }

    /// <summary>Why a path is never asked of the disk, as written and once made whole; null for one that may be.</summary>
    public static FoundPathRefusal? WhyNotAsked(string path)
    {
        if (path.Length == 0 || path.Contains('\0'))
        {
            return FoundPathRefusal.Malformed;
        }
        if (Form(path) is { } written)
        {
            return written;
        }
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return FoundPathRefusal.Malformed;
        }
        // Windows makes a legacy device's name (COM1, NUL) its device's path, \\.\COM1.
        return Form(full);
    }

    /// <summary>What a path's text names, before or after it is made whole: \\?\ and \\.\ open the device namespace (a
    /// pipe, a port, a volume, \\?\UNC\ a share), as NT's own \??\ does, which Windows passes on as written; any other two
    /// slashes begin a share's path on Windows, and elsewhere a root the system defines, read as a Windows share's.</summary>
    private static FoundPathRefusal? Form(ReadOnlySpan<char> path)
    {
        if (OperatingSystem.IsWindows() && path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return FoundPathRefusal.Device;
        }
        if (path is [var first, var second, ..] && IsSlash(first) && IsSlash(second))
        {
            return path is [_, _, '?' or '.', var third, ..] && IsSlash(third) ? FoundPathRefusal.Device : FoundPathRefusal.Share;
        }
        if (!OperatingSystem.IsWindows())
        {
            foreach (string root in Automounted)
            {
                // In any letter case where the root's volume ignores it.
                if (path.StartsWith(root, PathNames.Comparison) && (path.Length == root.Length || path[root.Length] == '/'))
                {
                    return FoundPathRefusal.Automounted;
                }
            }
        }
        return null;
    }

    private static bool IsSlash(char c) => c is '/' or '\\';
}

/// <summary>Why a path found in text is never asked of the disk.</summary>
public enum FoundPathRefusal
{
    /// <summary>A share's path (\\host\share, //host/share): asking reaches its host over the network.</summary>
    Share,

    /// <summary>A device's (\\?\, \\.\, \??\, a legacy device's name): asking opens it.</summary>
    Device,

    /// <summary>Under an automount root (/net): asking mounts what it names, from another host.</summary>
    Automounted,

    /// <summary>No path the system reads: empty, holding a NUL, or one it refuses to make whole.</summary>
    Malformed,
}
