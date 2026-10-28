using Nerdbank.Streams;
using StreamJsonRpc;

namespace Passenger.Lsp;

/// <summary>A JSON-RPC conversation over a stream of header-delimited messages, as Microsoft.Testing.Platform's test hosts
/// speak it: requests and notifications whose parameters are one object, and the peer's notifications handled one at a
/// time in the order they came, each read as its handler's type straight from the message it came in. StreamJsonRpc's
/// types stay in here: a request the peer refuses, or that the conversation's end leaves unanswered, fails with a
/// <see cref="JsonRpcFailed"/>.</summary>
public sealed class JsonRpcPeer : IDisposable
{
    private readonly JsonRpc rpc;

    public JsonRpcPeer(Stream stream)
    {
        // Each message in one piece, parsed where it lies rather than copied whole into an array rented for it.
        rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream.UsePipeWriter(), new ContiguousReader(stream), new SystemTextJsonFormatter()))
        {
            SynchronizationContext = new Microsoft.VisualStudio.Threading.NonConcurrentSynchronizationContext(sticky: false),
        };
    }

    /// <summary>Handles the peer's notification of <paramref name="method"/>, its parameters read as one
    /// <typeparamref name="T"/>; given before <see cref="Start"/>.</summary>
    public void On<T>(string method, Action<T?> handle) =>
        rpc.AddLocalRpcMethod(typeof(Handler<T>).GetMethod(nameof(Handler<T>.Handle))!, new Handler<T>(handle),
            new JsonRpcMethodAttribute(method) { UseSingleObjectParameterDeserialization = true });

    /// <summary>Starts reading what the peer sends.</summary>
    public void Start()
    {
        rpc.StartListening();
        // A peer ending on Windows resets its connection, faulting the conversation's end, which the requests waiting hear of.
        _ = rpc.Completion.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Asks the peer and waits for its answer, read as <typeparamref name="T"/>. Canceled, it stops waiting at
    /// once, and with <paramref name="tellCanceled"/> the peer is told the request was canceled.</summary>
    public async Task<T?> AskAsync<T>(string method, object parameters, bool tellCanceled, CancellationToken cancel)
    {
        using CancellationTokenSource asking = new();
        using CancellationTokenRegistration stop = tellCanceled ? cancel.Register(asking.Cancel) : default;
        Task<T?> answer = rpc.InvokeWithParameterObjectAsync<T?>(method, parameters, asking.Token);
        _ = answer.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            return await answer.WaitAsync(cancel);
        }
        catch (RemoteInvocationException refused)
        {
            throw new JsonRpcFailed(refused.Message, refused: true, refused);
        }
        catch (Exception error) when (error is RemoteRpcException or ObjectDisposedException or IOException)
        {
            throw new JsonRpcFailed($"the conversation ended before {method} was answered", refused: false, error);
        }
    }

    /// <summary>Tells the peer something it does not answer.</summary>
    public async Task TellAsync(string method, object parameters)
    {
        try
        {
            await rpc.NotifyWithParameterObjectAsync(method, parameters);
        }
        catch (Exception error) when (error is RemoteRpcException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            throw new JsonRpcFailed($"the conversation ended before {method} was sent", refused: false, error);
        }
    }

    public void Dispose() => rpc.Dispose();

    /// <summary>A notification's handler as StreamJsonRpc calls one: a method of one parameter.</summary>
    private sealed class Handler<T>(Action<T?> handle)
    {
        public void Handle(T? parameters) => handle(parameters);
    }
}

/// <summary>A request a JSON-RPC peer refused (its words as the message) or never answered, the conversation having ended.</summary>
public sealed class JsonRpcFailed(string message, bool refused, Exception inner) : Exception(message, inner)
{
    /// <summary>Whether the peer answered with an error, rather than not at all.</summary>
    public bool Refused { get; } = refused;
}
