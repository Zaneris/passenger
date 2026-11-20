using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>What another program wrote in the documents the server reads from disk, which it holds as they were until the
/// folder's watcher tells it of the write, a moment later (longer on a loaded machine) or never where the system dropped the
/// change: taken in before an agent's question of the code (<see cref="TakeInWrittenAsync"/>), so the answer is of the
/// files as they stand.</summary>
public sealed partial class CSharpService
{
    /// <summary>The session the last look was of, and when it began on the wall clock, as a file's write is: the next looks at
    /// what was written since.</summary>
    private (RoslynSession Session, DateTime Began)? writtenLook;

    /// <summary>How often a file told the server is asked of again until it holds it as the disk has it.</summary>
    private static readonly TimeSpan ReadingEvery = TimeSpan.FromMilliseconds(50);

    /// <summary>Takes in what was written on disk that the server may hold as it was: each document it reads from disk
    /// written since the last look (or its start), and each file heard changed, that it holds otherwise than the disk, those
    /// no watcher heard told it as found, all waited for until it holds them as the disk has them, up to <paramref
    /// name="within"/> from its start (<paramref name="waiting"/> told the first meanwhile). Null once it does, or where it
    /// cannot say (not loaded, no Passenger extension); else the first it does not.</summary>
    public async Task<string?> TakeInWrittenAsync(TimeSpan within, Action<string?>? waiting, CancellationToken cancel)
    {
        UiThread.Check();
        if (session is not { IsLoaded: true, Server.ExtensionProblem: null } asked)
        {
            return null;
        }
        Deadline until = Deadline.In(within);
        DateTime began = DateTime.UtcNow;
        DateTime since = (writtenLook is { } last && last.Session == asked ? last.Began : asked.Server.WatchedFiles.Since) - ServerWatches.Rounding;
        HashSet<string> open = new([.. documents.Where(d => d.Value.Lsp is not null).Select(d => d.Key.Path)], PathNames.Comparer);
        WrittenLately lately;
        IReadOnlyList<Roslyn.Extension.TextHeld> texts;
        try
        {
            lately = await asked.WrittenSinceAsync(since, cancel);
            List<string> asking = [.. lately.Written.Where(w => !w.Gone).Select(w => w.Path).Concat(lately.Changed.Select(c => c.Path))
                .Distinct(PathNames.Comparer).Where(p => !open.Contains(p))];
            texts = asking.Count == 0 ? [] : await asked.HeldTextsAsync(asking, cancel);
        }
        catch (LanguageServerFailed)
        {
            // A server gone, or one refusing, is said as it is; its answer is as of what it holds.
            return null;
        }
        writtenLook = (asked, began);
        List<string> unread = await Unread(texts, cancel);
        HashSet<string> heard = new(asked.Server.WatchedFiles.Changing().Paths, PathNames.Comparer);
        List<FileEvent> untold = [.. unread.Where(p => !heard.Contains(p)).Select(p => new FileEvent(PathNames.Canonical(p), FileChangeType.Changed))];
        if (untold.Count > 0)
        {
            // Matched against every watcher, so off the window's thread.
            await BackgroundWork.Run(() => asked.Server.WatchedFiles.TellFound(untold), cancel);
        }
        bool waited = unread.Count > 0;
        if (waited)
        {
            waiting?.Invoke(unread[0]);
        }
        try
        {
            while (unread.Count > 0 && !until.Passed && session == asked)
            {
                await Task.Delay(ReadingEvery, cancel);
                unread = await Unread(await asked.HeldTextsAsync(unread, cancel), cancel);
            }
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
        finally
        {
            if (waited)
            {
                waiting?.Invoke(null);
            }
        }
        // Those it holds open are the editor's, not the disk's: left as they were.
        lately.Settle([.. unread, .. open], unread);
        return unread.Count > 0 && session == asked ? unread[0] : null;
    }

    /// <summary>The files of these the server holds otherwise than the disk has them, each read off the window's thread, as
    /// they may be of any size.</summary>
    private static Task<List<string>> Unread(IReadOnlyList<Roslyn.Extension.TextHeld> texts, CancellationToken cancel) =>
        BackgroundWork.Run(() => texts.Where(t => t.Hash is { } hash && HeldTexts.OnDisk(t.Path, hash) == false).Select(t => t.Path).ToList(), cancel);
}
