namespace Passenger.App;

/// <summary>What the window's actions are doing that takes a while, from the moment each is asked for until its work is done
/// or handed over (a program running, a session open): a <see cref="Working"/> under the id of the action that starts it,
/// saying its stage. While one works, its action starts nothing more (<see cref="ActionRegistry"/> says what is under way),
/// a button that runs it shows it busy (<see cref="ActionButtons"/>), and the status bar says its stage beside a spinner.
/// Stop ends the starts it can. UI thread only.</summary>
public sealed class ActionWork
{
    private static readonly AsyncLocal<Hearing?> hearing = new();

    private readonly List<Working> under = [];

    /// <summary>Hears the action about to run in this flow (an agent's ide_action), for the rest of the caller's flow: the
    /// works it begins as it runs (until <see cref="Hearing.Ran"/>) and what its operations come to.</summary>
    public static Hearing Hear() => hearing.Value = new Hearing();

    /// <summary>The action heard in this flow, or null.</summary>
    public static Hearing? Heard => hearing.Value;

    /// <summary>Tells what an operation came to, in its words, to the action heard in its flow (<see cref="Hear"/>), if any.</summary>
    public static void Tell(bool succeeded, string said) => hearing.Value?.Came(succeeded, said);

    /// <summary>Raised as a work begins, says a new stage, or ends.</summary>
    public event EventHandler<WorkChange>? Changed;

    /// <summary>The work under way that said its stage last, which the status bar shows; null with none under way.</summary>
    public Working? Latest => under.Count > 0 ? under[^1] : null;

    /// <summary>Whether Stop has a start under way to end.</summary>
    public bool CanStop => under.Any(w => w.Stoppable);

    /// <summary>Begins an action's work, saying its first stage; null when that action's work is under way already, which
    /// <see cref="Of"/> then gives.</summary>
    public Working? Begin(string id, string stage, bool stoppable = true)
    {
        UiThread.Check();
        if (Of(id) is not null)
        {
            return null;
        }
        Working work = new(this, id, stage, stoppable);
        if (hearing.Value is { } heard && heard.Began(work))
        {
            work.Heard = heard;
        }
        under.Add(work);
        Changed?.Invoke(this, new WorkChange(work, Ended: false));
        return work;
    }

    /// <summary>The work under way for an action, or null.</summary>
    public Working? Of(string id) => under.Find(w => w.Id == id);

    /// <summary>The stages of a wait within an action's change (a refactoring waiting for its language server), told one by
    /// one and null as it ends: a work under the action's id from the first stage until then, or, while that action's
    /// work is under way already, each stage given to <paramref name="otherwise"/>.</summary>
    public Action<string?> Waits(string id, Action<string> otherwise)
    {
        Working? working = null;
        return stage =>
        {
            if (stage is null)
            {
                working?.Dispose();
                working = null;
            }
            else if (working is not null)
            {
                working.Say(stage);
            }
            else if ((working = Begin(id, stage, stoppable: false)) is null)
            {
                otherwise(stage);
            }
        };
    }

    /// <summary>What a second ask for an action under way is told.</summary>
    public static string Busy(Working work) => $"Already under way: {work.Stage}";

    /// <summary>Ends every start that Stop can end: each sees its <see cref="Working.Stopping"/> and ends itself.</summary>
    public void Stop()
    {
        foreach (Working work in under.Where(w => w.Stoppable).ToList())
        {
            work.Cancel();
        }
    }

    internal void Said(Working work)
    {
        // A stage posted from a worker can land after its work has ended, which must leave it ended, not under way for good.
        if (!under.Remove(work))
        {
            return;
        }
        under.Add(work);
        Changed?.Invoke(this, new WorkChange(work, Ended: false));
    }

    internal void Ended(Working work)
    {
        if (under.Remove(work))
        {
            work.Heard?.Ended();
            Changed?.Invoke(this, new WorkChange(work, Ended: true));
        }
    }
}

/// <summary>What an action an agent runs does (<see cref="ActionWork.Hear"/>): the works it begins as it runs, each held
/// from its first moment to its hand-over, and what its operations said they came to, the last told standing. UI thread
/// only.</summary>
public sealed class Hearing
{
    private readonly List<Working> begun = [];
    private readonly TaskCompletionSource handedOver = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool running = true;

    /// <summary>What the action came to as its operation told it, and whether that failed; null while none has told.</summary>
    public (bool Failed, string Said)? Outcome { get; private set; }

    /// <summary>The action has run: a work begun from here is not its own.</summary>
    public void Ran() => running = false;

    /// <summary>Comes back once every work the action began as it ran has ended (done, or handed over: a program running,
    /// a session open), at once with none, or the wait is canceled.</summary>
    public Task DoneAsync(CancellationToken cancel) => begun.All(w => w.HasEnded) ? Task.CompletedTask : handedOver.Task.WaitAsync(cancel);

    internal void Came(bool succeeded, string said) => Outcome = (!succeeded, said);

    internal bool Began(Working work)
    {
        if (running)
        {
            begun.Add(work);
        }
        return running;
    }

    internal void Ended()
    {
        if (begun.All(w => w.HasEnded))
        {
            handedOver.TrySetResult();
        }
    }
}

/// <summary>A work that began, said a new stage, or ended.</summary>
public sealed record WorkChange(Working Work, bool Ended);

/// <summary>One action's work under way, from its first moment until it is disposed: its stage as it says it, and whether
/// Stop has asked it to end.</summary>
public sealed class Working : IDisposable
{
    private readonly ActionWork owner;

    // Never disposed: a request that took its token may register on it after the work has ended.
    private readonly CancellationTokenSource stopping = new();

    internal Working(ActionWork owner, string id, string stage, bool stoppable)
    {
        this.owner = owner;
        Id = id;
        Stage = stage;
        Stoppable = stoppable;
    }

    /// <summary>The action an agent runs that began it as it ran, heard so (<see cref="ActionWork.Hear"/>).</summary>
    internal Hearing? Heard { get; set; }

    /// <summary>Whether it is done or handed over.</summary>
    public bool HasEnded { get; private set; }

    /// <summary>The id of the action whose work this is.</summary>
    public string Id { get; }

    /// <summary>What it is doing now, as the status bar says it ("Building Hello…").</summary>
    public string Stage { get; private set; }

    /// <summary>Whether Stop ends it as it starts.</summary>
    public bool Stoppable { get; }

    /// <summary>Canceled when Stop asks the start to end.</summary>
    public CancellationToken Stopping => stopping.Token;

    public bool IsStopped => stopping.IsCancellationRequested;

    /// <summary>Says the next stage.</summary>
    public void Say(string stage)
    {
        UiThread.Check();
        Stage = stage;
        owner.Said(this);
    }

    internal void Cancel() => stopping.Cancel();

    /// <summary>Tells what the work came to, in its words, to the action an agent runs that began it, if one did.</summary>
    public void Tell(bool succeeded, string said) => Heard?.Came(succeeded, said);

    /// <summary>The work is done or handed over.</summary>
    public void Dispose()
    {
        HasEnded = true;
        owner.Ended(this);
    }
}
