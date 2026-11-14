using System.Composition;
using Microsoft.CodeAnalysis.LanguageServer;
using Microsoft.CodeAnalysis.LanguageServer.Handler;

namespace Passenger.Roslyn.Extension;

/// <summary>The later parts of completion lists asked in parts (<see cref="CompletionParts"/>), each worked out beside its
/// list's first part and kept, once that is answered, until the client asks for it by its id. A list asked again or let
/// go of never asks for its later part, so only the newest few are kept: an older one's work is stopped and it is let go
/// of. One begun is stopped too if its first part's asking is given up before it is kept.</summary>
internal sealed class LaterCompletions : ILspService
{
    /// <summary>How many later parts are kept at most.</summary>
    private const int Most = 4;

    private readonly Lock gate = new();
    private readonly List<Pending> kept = [];
    private long made;

    /// <summary>A later part begun as its first part is asked for, stopped with <paramref name="asking"/> until it is kept.</summary>
    public Pending Begin(CancellationToken asking) => new(this, Interlocked.Increment(ref made), asking);

    /// <summary>The later part kept by <paramref name="id"/>, no longer kept; null for one let go of.</summary>
    public Pending? Take(long id)
    {
        lock (gate)
        {
            int at = kept.FindIndex(p => p.Id == id);
            if (at < 0)
            {
                return null;
            }
            Pending taken = kept[at];
            kept.RemoveAt(at);
            return taken;
        }
    }

    private void Keep(Pending pending)
    {
        lock (gate)
        {
            kept.Add(pending);
            while (kept.Count > Most)
            {
                kept[0].Stop();
                kept.RemoveAt(0);
            }
        }
    }

    /// <summary>A later part being worked out: disposed by the handler that began it, which leaves it to be asked for once
    /// kept, else stops it.</summary>
    internal sealed class Pending : IDisposable
    {
        private readonly LaterCompletions owner;
        private readonly CancellationTokenSource stop = new();
        private readonly CancellationTokenRegistration whileAsked;
        private bool keeping;

        public Pending(LaterCompletions owner, long id, CancellationToken asking)
        {
            this.owner = owner;
            Id = id;
            whileAsked = asking.Register(stop.Cancel);
        }

        public long Id { get; }

        /// <summary>The part, null once stopped or where none was found.</summary>
        public Task<PrioritizedCompletionList?> Answer { get; private set; } = Task.FromResult<PrioritizedCompletionList?>(null);

        /// <summary>Works the part out on the thread pool, beside what the caller goes on with.</summary>
        public void Work(Func<CancellationToken, Task<PrioritizedCompletionList?>> work) => Answer = Task.Run(async () =>
        {
            try
            {
                return await work(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return null;
            }
        });

        /// <summary>Kept for the client to ask for: the id it asks by.</summary>
        public long Keep()
        {
            keeping = true;
            owner.Keep(this);
            return Id;
        }

        public void Stop() => stop.Cancel();

        public void Dispose()
        {
            whileAsked.Dispose();
            if (!keeping)
            {
                Stop();
            }
        }
    }
}

[ExportCSharpVisualBasicLspServiceFactory(typeof(LaterCompletions), WellKnownLspServerKinds.Any)]
[Shared]
internal sealed class LaterCompletionsFactory : ILspServiceFactory
{
    public ILspService CreateILspService(LspServices lspServices, WellKnownLspServerKinds serverKind) => new LaterCompletions();
}
