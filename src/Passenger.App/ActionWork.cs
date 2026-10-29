namespace Passenger.App;

/// <summary>What the window's actions are doing that takes a while, from the moment each is asked for until its work is done
/// or handed over (a program running, a session open): a <see cref="Working"/> under the id of the action that starts it,
/// saying its stage. While one works, its action starts nothing more (<see cref="ActionRegistry"/> says what is under way),
/// a button that runs it shows it busy (<see cref="ActionButtons"/>), and the status bar says its stage beside a spinner.
/// Stop ends the starts it can. UI thread only.</summary>
public sealed class ActionWork
{
    private readonly List<Working> under = [];

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
            Changed?.Invoke(this, new WorkChange(work, Ended: true));
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

    /// <summary>The work is done or handed over.</summary>
    public void Dispose() => owner.Ended(this);
}
