namespace Passenger.Workspace;

/// <summary>Where the platforms differ in watching a folder, and only here: macOS and Windows watch a whole tree
/// through one handle, where Linux's inotify takes a watch for every folder from a small pool the user's programs
/// share, so there each kept folder is watched on its own.</summary>
public sealed class FolderWatching
{
    /// <summary>What inotify_add_watch fails with once the user's watches are used up, ENOSPC, which .NET's watcher raises
    /// as an IOException carrying the errno.</summary>
    private const int NoSpace = 28;

    private const string LimitPath = "/proc/sys/fs/inotify/max_user_watches";

    /// <summary>The limit suggested, unless it is that already.</summary>
    private const long Suggested = 524_288;

    private FolderWatching(bool eachFolder) => EachFolder = eachFolder;

    /// <summary>One recursive watcher over the whole tree: macOS's and Windows'.</summary>
    public static FolderWatching WholeTree { get; } = new(eachFolder: false);

    /// <summary>A watcher for each kept folder, and running out of watches said with how to raise the limit:
    /// Linux's.</summary>
    public static FolderWatching Inotify { get; } = new(eachFolder: true);

    /// <summary>The running system's.</summary>
    public static FolderWatching Platform { get; } = OperatingSystem.IsLinux() ? Inotify : WholeTree;

    /// <summary>Whether each kept folder is watched on its own rather than the whole tree at once.</summary>
    public bool EachFolder { get; }

    /// <summary>What the person is told when a watcher's error says the user's watches ran out, with how to raise the
    /// limit; null for any other error.</summary>
    public string? OutOfWatches(Exception error)
    {
        if (!EachFolder || error is not IOException { HResult: NoSpace })
        {
            return null;
        }
        long? limit = long.TryParse(Read(LimitPath), out long read) ? read : null;
        long raised = limit >= Suggested ? limit.Value * 2 : Suggested;
        string now = limit is null ? "" : $" is {limit}";
        return "Changes made outside Passenger in some folders will go unseen: the inotify watches this user's programs share "
            + $"ran out (fs.inotify.max_user_watches{now}). To raise the limit, run echo fs.inotify.max_user_watches={raised} "
            + "| sudo tee /etc/sysctl.d/90-inotify.conf && sudo sysctl --system, then open the folder again.";
    }

    private static string? Read(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
