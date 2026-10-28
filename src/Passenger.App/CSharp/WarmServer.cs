using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>A language server a window leaves running as it closes (its projects loaded), for the next window that opens
/// the same solution or projects to take up, loaded and warm, with that window's options, rather than start and load its
/// own: what a test class gives its windows on one project (<see cref="MainWindow.WarmServer"/>). The problems its projects
/// loaded with go with it, for the window that takes it up to hear as one that loaded them would; files that came meanwhile
/// (<see cref="HeardAsync"/>) are loaded by that window first, and what changes on disk meanwhile it hears of from
/// watchers of the holder's own over what it registered, as a window's server does from the window's, the builds a project
/// it then loads again asks for waiting for that window to give them. One no window takes is ended by the next one kept,
/// by <see cref="Drop"/> or with the holder. The IDE's windows have none.</summary>
public sealed class WarmServer : IAsyncDisposable
{
    private readonly Lock gate = new();
    private RoslynSession? kept;
    private IReadOnlyList<ProjectLoadProblem> keptProblems = [];

    /// <summary>What the server kept registered to hear of, watched for it.</summary>
    private ServerWatches? watching;

    /// <summary>Files of its projects came since it was kept, which it has not loaded.</summary>
    private bool unloaded;

    /// <summary>What the server kept asks of its projects' builds while no window has it (a project whose file it heard
    /// change, loaded again), held for the window that takes it up.</summary>
    private HeldQuestions? asking;

    /// <summary>The server kept, with the problems its projects loaded with, if it loaded what a window opens and is still
    /// running, and whether the window must have it load its projects again (files came since); it is the window's from
    /// then on, its options made the window's and its projects' builds, those it asked for meanwhile too, given by the
    /// window's <paramref name="feed"/>. One that does not match stays until another is kept or it is dropped.</summary>
    internal (RoslynSession Session, IReadOnlyList<ProjectLoadProblem> Problems, bool LoadAgain, ServerWatches? Watches)? Take(string folder, string? solution,
        IReadOnlyList<string> projects, IProjectFeed? feed)
    {
        HeldQuestions? held;
        (RoslynSession, IReadOnlyList<ProjectLoadProblem>, bool, ServerWatches?) taken;
        lock (gate)
        {
            // One that loads its projects itself cannot be had to load them again: it is no use once files came.
            if (kept is not { IsLoaded: true } session || session.Server.HasExited || session.Folder != folder || session.Solution != solution
                || (solution is null && !session.Projects.SequenceEqual(projects, StringComparer.Ordinal)) || (unloaded && !session.Fed))
            {
                return null;
            }
            kept = null;
            ServerWatches? handed = watching;
            watching = null;
            held = asking;
            asking = null;
            if (session.Fed)
            {
                session.Server.Feed = feed;
            }
            taken = (session, keptProblems, unloaded, handed);
        }
        held?.Answer(feed);
        return taken;
    }

    /// <summary>How many questions of its projects' builds the server kept has asked that wait for a window to take it up.</summary>
    public int Unanswered
    {
        get
        {
            lock (gate)
            {
                return asking?.Count ?? 0;
            }
        }
    }

    /// <summary>Keeps a window's server, and the problems its projects loaded with, for the next window, ending the one
    /// kept before, which no window took; what it registered goes on being watched by the window's watches given, alone.</summary>
    internal Task Keep(RoslynSession session, IReadOnlyList<ProjectLoadProblem> problems, ServerWatches? watches)
    {
        RoslynSession? before;
        ServerWatches alone;
        HeldQuestions? left;
        lock (gate)
        {
            before = kept;
            kept = session;
            keptProblems = problems;
            unloaded = false;
            left = asking;
            // Its questions wait for the window that takes it up: the closing window's builds go with its folder.
            asking = session.Fed ? new HeldQuestions() : null;
            if (asking is not null)
            {
                session.Server.Feed = asking;
            }
            if (watching != watches)
            {
                StopWatching();
            }
            alone = watching = watches ?? new ServerWatches(session.Server, folder: null);
        }
        left?.Answer(null);
        alone.Follow(null);
        // Not held: the window's end waits for it with no UI thread to come back to (MainWindow.Ended).
        return before is null || before == session ? Task.CompletedTask : BackgroundWork.Unheld(() => before.DisposeAsync().AsTask());
    }

    /// <summary>Ends the server kept, so the next window starts its own.</summary>
    public Task Drop()
    {
        RoslynSession? ending;
        HeldQuestions? left;
        lock (gate)
        {
            ending = kept;
            kept = null;
            left = asking;
            asking = null;
            StopWatching();
        }
        left?.Answer(null);
        // Not held: the window's end waits for it with no UI thread to come back to (MainWindow.Ended).
        return ending is null ? Task.CompletedTask : BackgroundWork.Unheld(() => ending.DisposeAsync().AsTask());
    }

    /// <summary>Lets go of the watching for the server kept, under the gate.</summary>
    private void StopWatching()
    {
        watching?.Dispose();
        watching = null;
    }

    /// <summary>Whether the server kept holds these files written as the texts given or has yet to load them, waited for
    /// until the limit: those it has documents of read again as the watcher tells it of a file changed on disk,
    /// and those it has none of loaded by the window that takes it up (<see cref="Take"/>); false when none is kept, or it
    /// still held another text at the limit.</summary>
    public async Task<bool> HeardAsync(IReadOnlyDictionary<string, string> texts, TimeSpan limit)
    {
        RoslynSession? session;
        lock (gate)
        {
            session = kept;
        }
        if (session is null || session.Server.HasExited)
        {
            return false;
        }
        // By the canonical paths the server's documents have.
        Dictionary<string, string> written = texts.ToDictionary(text => PathNames.Canonical(text.Key), text => text.Value, PathNames.Comparer);
        try
        {
            IReadOnlyList<Roslyn.Extension.TextHeld> held = await session.HeldTextsAsync([.. written.Keys]).WaitAsync(limit).ConfigureAwait(false);
            if (held.Any(text => text.Hash is null))
            {
                FilesCame(session);
            }
            Dictionary<string, string> read = held.Where(text => text.Hash is not null).ToDictionary(text => text.Path, text => written[text.Path], PathNames.Comparer);
            return read.Count == 0 || await HoldsAsync(read, limit).ConfigureAwait(false);
        }
        catch (Exception failed) when (failed is LanguageServerFailed or TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Files of its projects came while the server was kept (a test's own written): the window that takes it up
    /// has it load its projects first, as a window does that sees files come.</summary>
    private void FilesCame(RoslynSession session)
    {
        lock (gate)
        {
            unloaded |= kept == session;
        }
    }

    /// <summary>Whether the server kept holds these files as the texts given, waited for until the limit; false when
    /// none is kept, one of them is no document of its solution, or it still held another text at the limit.</summary>
    public async Task<bool> HoldsAsync(IReadOnlyDictionary<string, string> texts, TimeSpan limit)
    {
        RoslynSession? session;
        lock (gate)
        {
            session = kept;
        }
        if (session is null || session.Server.HasExited)
        {
            return false;
        }
        // By the canonical paths the server's documents have.
        Dictionary<string, string> wanted = texts.ToDictionary(text => PathNames.Canonical(text.Key), text => Roslyn.Extension.TextHash.Of(text.Value), PathNames.Comparer);
        DateTime until = DateTime.UtcNow + limit;
        while (true)
        {
            try
            {
                IReadOnlyList<Roslyn.Extension.TextHeld> held = await session.HeldTextsAsync([.. wanted.Keys]).WaitAsync(limit).ConfigureAwait(false);
                if (held.All(text => text.Hash is { } hash && wanted.TryGetValue(text.Path, out string? want) && hash == want))
                {
                    return true;
                }
                if (held.Any(text => text.Hash is null))
                {
                    return false;
                }
            }
            catch (Exception failed) when (failed is LanguageServerFailed or TimeoutException)
            {
                return false;
            }
            if (DateTime.UtcNow >= until)
            {
                return false;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    public ValueTask DisposeAsync() => new(Drop());

    /// <summary>The questions a kept server asks of its projects' builds while no window has it, answered by the builds of
    /// the window that takes it up, or, with none (the server dropped), as having none to give.</summary>
    private sealed class HeldQuestions : IProjectFeed
    {
        private readonly TaskCompletionSource<IProjectFeed?> taker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int waiting;

        /// <summary>How many questions wait for a window.</summary>
        public int Count => Volatile.Read(ref waiting);

        public void Answer(IProjectFeed? feed) => taker.TrySetResult(feed);

        public async Task<FedProjects?> ProjectsAsync(CancellationToken cancel) =>
            await TakerAsync(cancel).ConfigureAwait(false) is { } feed ? await feed.ProjectsAsync(cancel).ConfigureAwait(false) : null;

        public async Task<FedProject> InfoAsync(string path, CancellationToken cancel) =>
            await TakerAsync(cancel).ConfigureAwait(false) is { } feed
                ? await feed.InfoAsync(path, cancel).ConfigureAwait(false)
                : new FedProject(new Roslyn.Extension.ProjectInfoResult(null, "no window took the language server up"), []);

        private async Task<IProjectFeed?> TakerAsync(CancellationToken cancel)
        {
            Interlocked.Increment(ref waiting);
            try
            {
                return await taker.Task.WaitAsync(cancel).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref waiting);
            }
        }
    }
}
