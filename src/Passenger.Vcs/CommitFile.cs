using System.Diagnostics;
using System.Text;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>A file a commit makes for git, deleted once git is done with it whatever came of the commit: its message
/// (<c>commit -F</c>), or the data Passenger's gpg has gpg sign (<see cref="Signed"/>), written once under a name nothing
/// had, only the person's to read on macOS and Linux from its making, so a program that opens it a moment after (a
/// scanner, an indexer) stands in no write's way; an index of its own in the git folder, which git writes; or an ignore
/// file at its place in a tree of its own, which git reads as a work tree's (<see cref="Ignoring"/>). A deletion
/// Windows refuses while another program holds the file is made again apart from the commit, which neither waits for it
/// nor fails for it.</summary>
internal sealed class CommitFile : IDisposable
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    /// <summary>What follows an operation's socket in the name of the data its gpg signs.</summary>
    private const string SignedName = "-gpg-";

    /// <summary>The file and what goes with it.</summary>
    private readonly string[] files;

    /// <summary>The tree of its own it was written in, which goes with it; null for none.</summary>
    private string? tree;

    /// <summary>Whether a deletion refused is left to the end of the operation that made it (<see cref="DeleteSigned"/>),
    /// as this process ends with its answer.</summary>
    private readonly bool leftToOperation;

    private CommitFile(bool leftToOperation, params string[] files)
    {
        this.leftToOperation = leftToOperation;
        this.files = files;
    }

    /// <summary>Where git reads or writes it.</summary>
    public string Path => files[0];

    /// <summary>The tree of its own an ignore file was written in (<see cref="Ignoring"/>).</summary>
    public string Tree => tree ?? throw new InvalidOperationException($"{Path} was written in no tree of its own");

    /// <summary>An ignore file's lines at its path from a tree's top (<paramref name="relative"/>), written whole in a tree of
    /// its own in the temporary folder, for git to read as a work tree's; one whose write fails is deleted, and the failure
    /// thrown.</summary>
    public static CommitFile Ignoring(string relative, string lines)
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"passenger-ignoring-{Guid.NewGuid():N}");
        string path = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        CommitFile file;
        try
        {
            file = Written(path, Encoding.UTF8.GetBytes(lines), leftToOperation: false);
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(root, recursive: true);
            throw;
        }
        file.tree = root;
        return file;
    }

    /// <summary>A commit's message, written whole in a file of its own in the temporary folder; one whose write fails is
    /// deleted, and the failure thrown.</summary>
    public static CommitFile Message(string message) =>
        Written(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"passenger-message-{Guid.NewGuid():N}"), Encoding.UTF8.GetBytes(message), leftToOperation: false);

    /// <summary>The data Passenger's gpg has gpg sign, written whole beside the operation's <paramref name="socket"/>, whose
    /// end deletes it where this process could not (<see cref="DeleteSigned"/>); one whose write fails is deleted, and the
    /// failure thrown.</summary>
    public static CommitFile Signed(string socket, ReadOnlySpan<byte> data) => Written($"{socket}{SignedName}{Guid.NewGuid():N}", data, leftToOperation: true);

    /// <summary>The data the gpgs of the operation with <paramref name="socket"/> left beside it, deleted as a commit's
    /// file is, once the operation's git has ended.</summary>
    public static void DeleteSigned(string socket)
    {
        if (System.IO.Path.GetDirectoryName(socket) is not { Length: > 0 } folder)
        {
            return;
        }
        try
        {
            foreach (string left in Directory.EnumerateFiles(folder, System.IO.Path.GetFileName(socket) + SignedName + "*"))
            {
                Delete(left);
            }
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            PassengerLog.Write($"What a commit's gpg left beside {socket} could not be looked for: {unread.Message}");
        }
    }

    /// <summary>An index of a commit's own in <paramref name="gitDir"/>, its name beginning with <paramref name="kind"/>,
    /// for git to write; the lock file git writes it through goes with it.</summary>
    public static CommitFile Index(string gitDir, string kind)
    {
        string path = System.IO.Path.Combine(gitDir, $"passenger-{kind}-{Guid.NewGuid():N}");
        return new CommitFile(false, path, path + ".lock");
    }

    public void Dispose()
    {
        foreach (string file in files)
        {
            if (leftToOperation)
            {
                Refused(file);
            }
            else
            {
                Delete(file);
            }
        }
        if (tree is not null)
        {
            try
            {
                Directory.Delete(tree, recursive: true);
            }
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
            {
                PassengerLog.Write($"An ignore file's tree was left at {tree}: {refused.Message}");
            }
        }
    }

    private static CommitFile Written(string path, ReadOnlySpan<byte> bytes, bool leftToOperation)
    {
        CommitFile file = new(leftToOperation, path);
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        try
        {
            using FileStream stream = new(file.Path, options);
            stream.Write(bytes);
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return file;
    }

    private static void Delete(string file)
    {
        if (Refused(file) is not { } refused || !File.Exists(file))
        {
            return;
        }
        if (OperatingSystem.IsWindows())
        {
            // Not held: the program holding the file may keep it a while, and nothing waits on its going.
            UnawaitedWork.Observe(BackgroundWork.Unheld(() => DeleteOnceLetGoAsync(file)));
            return;
        }
        PassengerLog.Write($"A commit's file was left at {file}: {refused.Message}");
    }

    private static async Task DeleteOnceLetGoAsync(string file)
    {
        long began = Stopwatch.GetTimestamp();
        while (Refused(file) is { } refused)
        {
            if (Stopwatch.GetElapsedTime(began) > Limit)
            {
                PassengerLog.Write($"A commit's file was left at {file}, another program holding it past {Durations.Said(Limit)}: {refused.Message}");
                return;
            }
            await Task.Delay(Pause).ConfigureAwait(false);
        }
    }

    private static Exception? Refused(string file)
    {
        try
        {
            File.Delete(file);
            return null;
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            return refused;
        }
    }
}
