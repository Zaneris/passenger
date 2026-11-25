using System.Diagnostics;
using System.Text;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>A file a commit makes for git, deleted once git is done with it whatever came of the commit: its message
/// (<c>commit -F</c>), an interactive rebase's planned steps (<see cref="Todo"/>), or the data Passenger's gpg has gpg sign
/// (<see cref="Signed"/>), written once under a name nothing
/// had, only the person's to read on macOS and Linux from its making, so a program that opens it a moment after (a
/// scanner, an indexer) stands in no write's way; an index of its own in the git folder, which git writes; an ignore
/// file at its place in a tree of its own, which git reads as a work tree's (<see cref="Ignoring"/>); or a replay's
/// objects and index in a folder of its own (<see cref="Replay"/>). A deletion Windows refuses while another program
/// holds the file is made again apart from the commit, which neither waits for it nor fails for it.</summary>
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

    /// <summary>The folder of its own it was written in (<see cref="Ignoring"/>, <see cref="Replay"/>).</summary>
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

    /// <summary>An interactive rebase's planned steps, which Passenger as git's sequence editor writes over git's
    /// (<see cref="RebaseTodo"/>), written whole in a file of its own in the temporary folder; one whose write fails is
    /// deleted, and the failure thrown.</summary>
    public static CommitFile Todo(string steps) =>
        Written(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"passenger-todo-{Guid.NewGuid():N}"), Encoding.UTF8.GetBytes(steps), leftToOperation: false);

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

    /// <summary>A folder of a replay's own in the temporary folder, only the person's to read on macOS and Linux, holding an
    /// object folder (<see cref="Objects"/>) and an index (<see cref="Path"/>) git writes in place of the repository's, so
    /// nothing the replay makes is left in the repository; deleted with all in it.</summary>
    public static CommitFile Replay()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"passenger-replay-{Guid.NewGuid():N}");
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(root);
        }
        else
        {
            Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        CommitFile file = new(false, System.IO.Path.Combine(root, "index"), System.IO.Path.Combine(root, "index.lock")) { tree = root };
        try
        {
            Directory.CreateDirectory(file.Objects);
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return file;
    }

    /// <summary>The object folder of a replay's own (<see cref="Replay"/>).</summary>
    public string Objects => System.IO.Path.Combine(Tree, "objects");

    /// <summary>An index of a commit's own in <paramref name="gitDir"/>, its name beginning with <paramref name="kind"/>,
    /// for git to write; the lock file git writes it through goes with it.</summary>
    public static CommitFile Index(string gitDir, string kind)
    {
        string path = System.IO.Path.Combine(gitDir, $"passenger-{kind}-{Guid.NewGuid():N}");
        return new CommitFile(false, path, path + ".lock");
    }

    /// <summary>A copy of an index (<paramref name="bytes"/>) for a trial of its own in <paramref name="gitDir"/>, as
    /// <see cref="Index(string, string)"/>, written whole; one whose write fails is deleted, and the failure thrown.</summary>
    public static CommitFile Index(string gitDir, string kind, ReadOnlySpan<byte> bytes)
    {
        string path = System.IO.Path.Combine(gitDir, $"passenger-{kind}-{Guid.NewGuid():N}");
        return Written(path, bytes, leftToOperation: false, path + ".lock");
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
                PassengerLog.Write($"A commit file's folder was left at {tree}: {refused.Message}");
            }
        }
    }

    private static CommitFile Written(string path, ReadOnlySpan<byte> bytes, bool leftToOperation, params string[] with)
    {
        CommitFile file = new(leftToOperation, [path, .. with]);
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
