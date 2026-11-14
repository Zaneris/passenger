using System.Diagnostics;
using System.Text;
using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>A file a commit makes for git, deleted once git is done with it whatever came of the commit: its message
/// (<c>commit -F</c>), written once in the temporary folder under a name nothing had, only the person's to read on macOS
/// and Linux from its making, so a program that opens it a moment after (a scanner, an indexer) stands in no write's way;
/// or an index of its own in the git folder, which git writes. A deletion Windows refuses while another program holds
/// the file is made again apart from the commit, which neither waits for it nor fails for it.</summary>
internal sealed class CommitFile : IDisposable
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    /// <summary>The file and what goes with it.</summary>
    private readonly string[] files;

    private CommitFile(params string[] files) => this.files = files;

    /// <summary>Where git reads or writes it.</summary>
    public string Path => files[0];

    /// <summary>A commit's message, written whole in a file of its own; one whose write fails is deleted, and the failure
    /// thrown.</summary>
    public static CommitFile Message(string message)
    {
        CommitFile file = new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"passenger-message-{Guid.NewGuid():N}"));
        FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        try
        {
            using FileStream stream = new(file.Path, options);
            stream.Write(Encoding.UTF8.GetBytes(message));
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return file;
    }

    /// <summary>An index of a commit's own in <paramref name="gitDir"/>, its name beginning with <paramref name="kind"/>,
    /// for git to write; the lock file git writes it through goes with it.</summary>
    public static CommitFile Index(string gitDir, string kind)
    {
        string path = System.IO.Path.Combine(gitDir, $"passenger-{kind}-{Guid.NewGuid():N}");
        return new CommitFile(path, path + ".lock");
    }

    public void Dispose()
    {
        foreach (string file in files)
        {
            Delete(file);
        }
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
                PassengerLog.Write($"A commit's file was left at {file}, another program holding it past {Limit.TotalMinutes:0} minutes: {refused.Message}");
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
