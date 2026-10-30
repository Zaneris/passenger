using Passenger.Core;
using StreamJsonRpc;

namespace Passenger.Lsp;

/// <summary>A JSON-RPC conversation's cancellations: a request of ours cancelled is told to the peer by work whose
/// failure is seen as it fails, a connection gone meanwhile leaving nothing to cancel and so said to no one; a request
/// of the peer's is cancelled by the strategy the conversation was made with. Set before the conversation listens.</summary>
internal sealed class ToldCancellation : ICancellationStrategy
{
    private readonly JsonRpc rpc;
    private readonly ICancellationStrategy? theirs;

    private ToldCancellation(JsonRpc rpc)
    {
        this.rpc = rpc;
        theirs = rpc.CancellationStrategy;
    }

    /// <summary>Has <paramref name="rpc"/> cancel its requests this way, before it starts listening.</summary>
    public static void Use(JsonRpc rpc) => rpc.CancellationStrategy = new ToldCancellation(rpc);

    /// <summary>Tells the peer, off the canceller's thread; unheld, as nothing in the window waits on the word, and a
    /// peer that stopped reading would hold every wait until it is ended.</summary>
    public void CancelOutboundRequest(RequestId requestId) => UnawaitedWork.Observe(BackgroundWork.Unheld(() => TellAsync(requestId)));

    private async Task TellAsync(RequestId requestId)
    {
        if (rpc.IsDisposed)
        {
            return;
        }
        try
        {
            await rpc.NotifyWithParameterObjectAsync(LspMethods.CancelRequest, new Dictionary<string, object?> { ["id"] = requestId }, null).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ConnectionLostException or ObjectDisposedException or IOException)
        {
            // The connection went as the request was cancelled: there is no request left to cancel.
        }
    }

    public void OutboundRequestEnded(RequestId requestId) => theirs?.OutboundRequestEnded(requestId);

    public void IncomingRequestStarted(RequestId requestId, CancellationTokenSource cancellationTokenSource) =>
        theirs?.IncomingRequestStarted(requestId, cancellationTokenSource);

    public void IncomingRequestEnded(RequestId requestId) => theirs?.IncomingRequestEnded(requestId);
}
