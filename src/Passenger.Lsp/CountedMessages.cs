using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Passenger.Lsp;

/// <summary>The connection's header-delimited messages, each of the server's words that its colours moved noted with how
/// many messages had crossed to the server whole as its last byte was read off the connection, which its parsing follows
/// later: the client handles what the server sends one message at a time, in order, so a word handled after a request
/// went out may have arrived before it, and a request waits for its writing and then for the copy to the server's input
/// after its caller sends it (<see cref="SentRequest"/>). It records what crossed lately, by name alone, and the server's
/// requests Passenger has not answered yet, so a wait past its patience says where it stands (#1233).</summary>
internal sealed class CountedMessages : HeaderDelimitedMessageHandler
{
    /// <summary>How many of the last messages each way are kept for a wait's words.</summary>
    private const int Kept = 16;

    private readonly TracedStream input;
    private readonly TracedStream output;
    private readonly ConditionalWeakTable<object, SentRequest> followed = new();
    private long written;
    private long lastWordAfter = -1;

    private readonly Lock recording = new();
    private readonly Crossed[] recent = new Crossed[Kept];
    private int recorded;
    private long lastHeard;
    private readonly Dictionary<RequestId, (string Method, long Since)> asked = [];

    public CountedMessages(TracedStream input, TracedStream output, IJsonRpcMessageFormatter formatter)
        : base(input, output, formatter)
    {
        this.input = input;
        this.output = output;
    }

    /// <summary>The server's requests Passenger has not answered yet, each with when it came, the oldest first.</summary>
    public IReadOnlyList<(string Method, long Since)> Unanswered
    {
        get
        {
            lock (recording)
            {
                return [.. asked.Values.OrderBy(a => a.Since)];
            }
        }
    }

    /// <summary>How many messages Passenger has written that have not yet gone whole into the server's input: a server that
    /// stops reading fills its pipe, and they wait to go in.</summary>
    public long Unsent => Math.Max(0, Interlocked.Read(ref written) - input.Crossed);

    /// <summary>When the server's last message came (a <see cref="Stopwatch"/> timestamp); 0 before any.</summary>
    public long LastHeard
    {
        get
        {
            lock (recording)
            {
                return lastHeard;
            }
        }
    }

    /// <summary>The last messages each way, the oldest first, a line each: when, which way ("&lt;--" from the server), and
    /// what (a request's method and id, an answer's id, a notification's method).</summary>
    public IReadOnlyList<string> Recent()
    {
        Crossed[] kept;
        int count;
        lock (recording)
        {
            kept = [.. recent];
            count = recorded;
        }
        long now = Stopwatch.GetTimestamp();
        DateTime clock = DateTime.Now;
        List<string> lines = [];
        for (int i = Math.Max(0, count - Kept); i < count; i++)
        {
            Crossed crossed = kept[i % Kept];
            string when = (clock - Stopwatch.GetElapsedTime(crossed.At, now)).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            string what = crossed.Kind switch
            {
                CrossedKind.Told => crossed.Method ?? "a notification",
                CrossedKind.Asked => $"{crossed.Method} #{crossed.Id}",
                CrossedKind.Failed => $"an error answering #{crossed.Id}",
                _ => $"the answer to #{crossed.Id}",
            };
            lines.Add($"{when} {(crossed.In ? "<--" : "-->")} {what}");
        }
        return lines;
    }

    /// <summary>Notes a message that crossed, by name alone so none of its body is kept: among the last, and a request of
    /// the server's until Passenger answers it.</summary>
    private void Record(JsonRpcMessage message, bool incoming)
    {
        long now = Stopwatch.GetTimestamp();
        Crossed crossed = message switch
        {
            JsonRpcRequest { RequestId.IsEmpty: true } told => new(now, incoming, CrossedKind.Told, told.Method, default),
            JsonRpcRequest request => new(now, incoming, CrossedKind.Asked, request.Method, request.RequestId),
            JsonRpcError error => new(now, incoming, CrossedKind.Failed, null, error.RequestId),
            JsonRpcResult result => new(now, incoming, CrossedKind.Answered, null, result.RequestId),
            _ => new(now, incoming, CrossedKind.Told, message.GetType().Name, default),
        };
        lock (recording)
        {
            recent[recorded % Kept] = crossed;
            recorded++;
            if (incoming)
            {
                lastHeard = now;
                if (crossed.Kind == CrossedKind.Asked)
                {
                    asked[crossed.Id] = (crossed.Method ?? "a request", now);
                }
            }
            else if (crossed.Kind is CrossedKind.Answered or CrossedKind.Failed && !crossed.Id.IsEmpty)
            {
                asked.Remove(crossed.Id);
            }
        }
    }

    private enum CrossedKind : byte { Told, Asked, Answered, Failed }

    /// <summary>A message that crossed: when, which way, what kind, and its method or the id it answers.</summary>
    private readonly record struct Crossed(long At, bool In, CrossedKind Kind, string? Method, RequestId Id);

    /// <summary>The messages that had crossed to the server whole when its last word that its colours moved was read; -1
    /// before one is.</summary>
    public long LastWordAfter => Interlocked.Read(ref lastWordAfter);

    /// <summary>Has the request of these parameters (by reference) tell <paramref name="request"/> its place among the
    /// messages as it is written.</summary>
    public void Follow(object parameters, SentRequest request) => followed.AddOrUpdate(parameters, request);

    // Pooled: every message the server sends passes here, most finishing later than they began.
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    protected override async ValueTask<JsonRpcMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        JsonRpcMessage? message = await base.ReadCoreAsync(cancellationToken).ConfigureAwait(false);
        if (message is not null)
        {
            Record(message, incoming: true);
            long crossed = output.CrossedAcrossBeforeNext();
            if (message is JsonRpcRequest { Method: LspMethods.SemanticTokensRefresh })
            {
                Interlocked.Exchange(ref lastWordAfter, crossed);
            }
        }
        return message;
    }

    // One message a frame, in order, under the handler's own lock: the frames cross the server's input in this order.
    protected override void Write(JsonRpcMessage content, CancellationToken cancellationToken)
    {
        base.Write(content, cancellationToken);
        Record(content, incoming: false);
        long frame = Interlocked.Increment(ref written);
        if (content is JsonRpcRequest { Arguments: { } parameters } && followed.TryGetValue(parameters, out SentRequest? request))
        {
            request.Written(this, frame);
        }
    }
}

/// <summary>A colour request on its way to the server, placed among the server's words that its colours moved: one the
/// server could read only after it had sent every word read so far answers with what they told of.</summary>
public sealed class SentRequest
{
    private CountedMessages? messages;
    private long frame;

    /// <summary>Whether every word read so far was read before the request had crossed to the server whole, as one still
    /// waiting its turn to be written is.</summary>
    public bool AfterEveryWord
    {
        get
        {
            long at = Volatile.Read(ref frame);
            return at == 0 || messages!.LastWordAfter < at;
        }
    }

    internal void Written(CountedMessages by, long at)
    {
        messages = by;
        Volatile.Write(ref frame, at);
    }
}
