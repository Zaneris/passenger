using System.Net.Sockets;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Passenger.App.Mcp;

/// <summary>An MCP connection whose other end may go at any moment, an agent or a window: an answer or a notification
/// its stream can no longer carry is dropped as the connection's ordinary end, since the SDK sends some without
/// awaiting them. A request still fails to the sender awaiting it, and a message that cannot be written for another
/// reason than a broken stream still throws. A message is written whole once begun: its sender's cancellation stops it
/// only while it waits its turn, since a line cut before its end would take the next message with it.</summary>
public sealed class PeerTransport(ITransport inner) : ITransport
{
    /// <summary>One message written at a time, so a sender's cancellation never reaches a write already begun.</summary>
    private readonly SemaphoreSlim sending = new(1, 1);

    public string? SessionId => inner.SessionId;

    public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;

    public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await inner.SendMessageAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException failed) when (message is not JsonRpcRequest && failed.InnerException is IOException or SocketException or ObjectDisposedException)
        {
            // The other end went: nobody is left to tell.
        }
        finally
        {
            sending.Release();
        }
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    /// <summary>A client's connection, each session it makes a <see cref="PeerTransport"/>.</summary>
    public static IClientTransport Of(IClientTransport transport) => new Client(transport);

    private sealed class Client(IClientTransport inner) : IClientTransport
    {
        public string Name => inner.Name;

        public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default) =>
            new PeerTransport(await inner.ConnectAsync(cancellationToken).ConfigureAwait(false));
    }
}
