using System.Diagnostics.CodeAnalysis;

namespace Passenger.Core;

/// <summary>The requests asked of a peer and not yet answered, each numbered and waiting for the answer that names it:
/// completed by that answer, failed by the peer's end (as is every request asked after it), or let go of by an asker that
/// waits no longer. A message that waits for no answer takes its number from the same count. Each request keeps what it
/// was asked with (<typeparamref name="TAsked"/>), for what its answer and its failure say. Thread-safe.</summary>
public sealed class PendingRequests<TAsked, TAnswer>
{
    private readonly Lock gate = new();
    private readonly Dictionary<int, Waiting> waiting = [];
    private int last;

    /// <summary>What the peer's end fails a request with, once it has ended.</summary>
    private Func<TAsked, Exception>? ended;

    /// <summary>A request numbered and waiting for its answer; once the peer has ended, its end's failure thrown instead.</summary>
    public (int Id, Task<TAnswer> Answer) Add(TAsked asked)
    {
        TaskCompletionSource<TAnswer> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            if (ended is not null)
            {
                throw ended(asked);
            }
            int id = ++last;
            waiting[id] = new Waiting(asked, answer);
            return (id, answer.Task);
        }
    }

    /// <summary>The next number, for a message that waits for no answer.</summary>
    public int NextId()
    {
        lock (gate)
        {
            return ++last;
        }
    }

    /// <summary>What a request still waiting was asked with, leaving it waiting, for an answer given in parts.</summary>
    public bool TryGet(int id, [MaybeNullWhen(false)] out TAsked asked)
    {
        lock (gate)
        {
            bool found = waiting.TryGetValue(id, out Waiting? request);
            asked = found ? request!.Asked : default;
            return found;
        }
    }

    /// <summary>Completes a request with its answer; false when none waits by that number.</summary>
    public bool Answer(int id, TAnswer answer) => Take(id)?.Answer.TrySetResult(answer) ?? false;

    /// <summary>Fails a request with the peer's error, as <paramref name="failure"/> words it for what was asked.</summary>
    public bool Fail(int id, Func<TAsked, Exception> failure) => Take(id) is { } request && request.Answer.TrySetException(failure(request.Asked));

    /// <summary>Lets go of a request no one waits for any longer, cancelling it: an answer that still comes finds no one,
    /// and a failure that came first is observed here rather than left for the finalizer.</summary>
    public void Forget(int id, Task<TAnswer> answer, CancellationToken cancelled = default)
    {
        Take(id)?.Answer.TrySetCanceled(cancelled);
        UnawaitedWork.Dismiss(answer);
    }

    /// <summary>The peer has ended: every request waiting fails, as does each asked from now on (with the first end's
    /// failure, should it end twice).</summary>
    public void End(Func<TAsked, Exception> failure)
    {
        List<Waiting> failed;
        lock (gate)
        {
            ended ??= failure;
            failed = [.. waiting.Values];
            waiting.Clear();
        }
        foreach (Waiting request in failed)
        {
            request.Answer.TrySetException(failure(request.Asked));
        }
    }

    private Waiting? Take(int id)
    {
        lock (gate)
        {
            return waiting.Remove(id, out Waiting? request) ? request : null;
        }
    }

    private sealed record Waiting(TAsked Asked, TaskCompletionSource<TAnswer> Answer);
}
