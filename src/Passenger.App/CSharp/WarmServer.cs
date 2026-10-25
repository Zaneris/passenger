using Passenger.Core;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>A language server a window leaves running as it closes (its projects loaded), for the next window that opens
/// the same solution or projects with the same options to take up, loaded and warm, rather than start and load its own:
/// what a test class gives its windows on one project (<see cref="MainWindow.WarmServer"/>). The problems its projects
/// loaded with go with it, for the window that takes it up to hear as one that loaded them would. One no window takes is
/// ended by the next one kept, by <see cref="Drop"/> or with the holder. The IDE's windows have none.</summary>
public sealed class WarmServer : IAsyncDisposable
{
    private readonly Lock gate = new();
    private RoslynSession? kept;
    private IReadOnlyList<ProjectLoadProblem> keptProblems = [];

    /// <summary>The server kept, with the problems its projects loaded with, if it loaded what a window opens with the
    /// options it would start one with, and is still running; it is the window's from then on. One that does not match stays
    /// until another is kept or it is dropped.</summary>
    internal (RoslynSession Session, IReadOnlyList<ProjectLoadProblem> Problems)? Take(string folder, string? solution, IReadOnlyList<string> projects,
        IReadOnlyDictionary<string, object?> options)
    {
        lock (gate)
        {
            if (kept is not { IsLoaded: true } session || session.Server.HasExited || session.Folder != folder || session.Solution != solution
                || (solution is null && !session.Projects.SequenceEqual(projects, StringComparer.Ordinal)) || !Same(session.Server.Options, options))
            {
                return null;
            }
            kept = null;
            return (session, keptProblems);
        }
    }

    /// <summary>Keeps a window's server, and the problems its projects loaded with, for the next window, ending the one
    /// kept before, which no window took.</summary>
    internal Task Keep(RoslynSession session, IReadOnlyList<ProjectLoadProblem> problems)
    {
        RoslynSession? before;
        lock (gate)
        {
            before = kept;
            kept = session;
            keptProblems = problems;
        }
        // Not held: the window's end waits for it with no UI thread to come back to (MainWindow.Ended).
        return before is null || before == session ? Task.CompletedTask : BackgroundWork.Unheld(() => before.DisposeAsync().AsTask());
    }

    /// <summary>Ends the server kept, so the next window starts its own.</summary>
    public Task Drop()
    {
        RoslynSession? ending;
        lock (gate)
        {
            ending = kept;
            kept = null;
        }
        // Not held: the window's end waits for it with no UI thread to come back to (MainWindow.Ended).
        return ending is null ? Task.CompletedTask : BackgroundWork.Unheld(() => ending.DisposeAsync().AsTask());
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

    /// <summary>Whether two sets of options are the same: one set to its default (null) is not one never set, which the
    /// server takes from Passenger's own.</summary>
    private static bool Same(IReadOnlyDictionary<string, object?> set, IReadOnlyDictionary<string, object?> wanted) =>
        set.Count == wanted.Count && set.All(option => wanted.TryGetValue(option.Key, out object? value) && Equals(option.Value, value));
}
