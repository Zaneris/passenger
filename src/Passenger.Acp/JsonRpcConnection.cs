using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Passenger.Acp;

/// <summary>JSON-RPC 2.0 as ACP speaks it: one message per line, over a stream from the agent and a stream to it. Both
/// sides ask and answer, so the agent's questions come to <see cref="RequestHandler"/> and its notifications to
/// <see cref="NotificationReceived"/>. It starts no process: the host hands it the streams of one it started.</summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> pending = new();
    private readonly ConcurrentDictionary<Task, byte> answering = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int nextId;
    private int started;

    public JsonRpcConnection(Stream fromAgent, Stream toAgent)
    {
        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        reader = new StreamReader(fromAgent, utf8);
        writer = new StreamWriter(toAgent, utf8) { NewLine = "\n" };
    }

    /// <summary>Every line sent ("->") or received ("<-"), raw, for a log of the conversation.</summary>
    public event Action<string, string>? MessageLogged;

    /// <summary>A notification from the agent, by method and parameters. Raised on the reading thread, one at a time in
    /// the order they came, because a later update often depends on an earlier one.</summary>
    public event Action<string, JsonNode?>? NotificationReceived;

    /// <summary>Answers a question from the agent: returns the result, or throws <see cref="AcpError"/> to refuse it.
    /// Questions are answered side by side, so one waiting on the person never holds up the updates behind it.</summary>
    public Func<string, JsonNode?, Task<JsonNode?>>? RequestHandler { get; set; }

    /// <summary>Completes when the agent's stream ends.</summary>
    public Task Completion => ended.Task;

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0)
        {
            _ = Task.Run(ReadLoopAsync);
        }
    }

    public async Task<JsonNode> RequestAsync(string method, JsonObject parameters, CancellationToken cancel = default)
    {
        int id = Interlocked.Increment(ref nextId);
        TaskCompletionSource<JsonNode> reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = reply;
        try
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            return await reply.Task.WaitAsync(cancel);
        }
        finally
        {
            pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonObject parameters)
    {
        return SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
    }

    private async Task SendAsync(JsonObject message)
    {
        string line = message.ToJsonString();
        await writeLock.WaitAsync();
        try
        {
            MessageLogged?.Invoke("->", line);
            await writer.WriteLineAsync(line);
            await writer.FlushAsync();
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await reader.ReadLineAsync() is string line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                MessageLogged?.Invoke("<-", line);
                JsonNode? message;
                try
                {
                    message = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    // A line that is not JSON is the agent's bug, and one bad line should not end the conversation.
                    continue;
                }

                if (message is not JsonObject)
                {
                    continue;
                }

                bool hasId = message["id"] is not null;
                string? method = message["method"]?.GetValue<string>();
                if (method is not null && hasId)
                {
                    Task answer = AnswerAsync(message["id"]!.DeepClone(), method, message["params"]);
                    answering[answer] = 0;
                    _ = answer.ContinueWith(done => answering.TryRemove(done, out _), TaskScheduler.Default);
                }
                else if (method is not null)
                {
                    NotificationReceived?.Invoke(method, message["params"]);
                }
                else if (hasId)
                {
                    Complete(message);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The agent went away; the requests still waiting are failed below.
        }
        finally
        {
            // Without this, a request waiting when the agent dies would wait forever.
            foreach (int id in pending.Keys)
            {
                if (pending.TryRemove(id, out TaskCompletionSource<JsonNode>? reply))
                {
                    reply.TrySetException(new AcpError(AcpError.ConnectionClosed, "The agent closed the connection."));
                }
            }

            ended.TrySetResult();
        }
    }

    private void Complete(JsonNode message)
    {
        if (message["id"] is not JsonValue idValue || !idValue.TryGetValue(out int id) || !pending.TryRemove(id, out TaskCompletionSource<JsonNode>? reply))
        {
            return;
        }

        if (message["error"] is JsonNode error)
        {
            int code = error["code"] is JsonValue codeValue && codeValue.TryGetValue(out int number) ? number : AcpError.InternalError;
            reply.TrySetException(new AcpError(code, error["message"]?.GetValue<string>() ?? error.ToJsonString()));
        }
        else
        {
            reply.TrySetResult(message["result"] ?? new JsonObject());
        }
    }

    private async Task AnswerAsync(JsonNode id, string method, JsonNode? parameters)
    {
        // The agent blocks until it hears back, so every question gets an answer, even a failure.
        JsonObject answer = new() { ["jsonrpc"] = "2.0", ["id"] = id };
        try
        {
            if (RequestHandler is null)
            {
                throw new AcpError(AcpError.MethodNotFound, $"{method} is not supported");
            }

            answer["result"] = await RequestHandler(method, parameters) ?? JsonValue.Create((string?)null);
        }
        catch (AcpError refused)
        {
            answer["error"] = new JsonObject { ["code"] = refused.Code, ["message"] = refused.Message };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            answer["error"] = new JsonObject { ["code"] = AcpError.InternalError, ["message"] = error.Message };
        }

        try
        {
            await SendAsync(answer);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // Nobody is left to hear it.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(answering.Keys).WaitAsync(TimeSpan.FromSeconds(1)).ContinueWith(_ => { }, TaskScheduler.Default);
        writer.Dispose();
        reader.Dispose();
        writeLock.Dispose();
    }
}

/// <summary>A JSON-RPC error: one the agent answered with, or one we answer the agent with.</summary>
public sealed class AcpError(int code, string message) : Exception(message)
{
    public const int MethodNotFound = -32601;
    public const int InternalError = -32603;
    public const int Refused = -32000;
    public const int ConnectionClosed = -32099;

    public int Code { get; } = code;
}
