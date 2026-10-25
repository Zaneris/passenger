using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.Host;

/// <summary>The host's side of the protocol: each request run on the thread pool as it comes, so one window's evaluation
/// never waits behind another's, and every line written whole under one lock. A request that throws answers with the
/// exception's message; the host goes on.</summary>
public sealed class Server(TextWriter protocol, HostReady ready)
{
    private readonly object writing = new();

    /// <summary>The requests that can be cancelled, while they run, by id.</summary>
    private readonly ConcurrentDictionary<int, CancellationTokenSource> cancellable = new();

    public void Run(TextReader input)
    {
        Write(new HostReply { Id = 0, Result = JsonSerializer.SerializeToElement(ready, HostJson.Options) });
        List<Task> running = [];
        while (JsonLines.Read(input, HostJson.Request, static (_, error) => Console.Error.WriteLine($"Passenger.Host: a request that does not read: {error.Message}"))
            is { } request)
        {
            running.RemoveAll(t => t.IsCompleted);
            // Before the next line is read, or a cancel right behind its request would find nothing and leave it running.
            CancellationTokenSource cancel = new();
            cancellable[request.Id] = cancel;
            running.Add(Task.Run(() => Handle(request, cancel)));
        }
    }

    private async Task Handle(HostRequest request, CancellationTokenSource cancel)
    {
        using CancellationTokenSource owned = cancel;
        try
        {
            object result = request.Method switch
            {
                HostMethods.Evaluate => Evaluate(request.Id, Read<EvaluateParams>(request), cancel.Token),
                HostMethods.Build => await Build(request.Id, Read<BuildParams>(request), cancel.Token),
                HostMethods.Cancel => Cancel(Read<CancelParams>(request)),
                HostMethods.Inspect => Inspection.Run(Read<InspectParams>(request), cancel.Token),
                HostMethods.Hold => await Hold(request.Id, cancel.Token),
                HostMethods.Properties => Properties.Run(Read<PropertiesParams>(request)),
                HostMethods.Inputs => BuildReads.Run(Read<InputsParams>(request), cancel.Token),
                _ => throw new InvalidOperationException($"the host has no method {request.Method}"),
            };
            Write(new HostReply { Id = request.Id, Result = JsonSerializer.SerializeToElement(result, result.GetType(), HostJson.Options) });
        }
        catch (Exception error)
        {
            Write(new HostReply { Id = request.Id, Error = error is OperationCanceledException ? "cancelled" : error.Message });
        }
        finally
        {
            cancellable.TryRemove(request.Id, out _);
        }
    }

    private bool Cancel(CancelParams asked)
    {
        if (!cancellable.TryGetValue(asked.Request, out CancellationTokenSource? running))
        {
            return false;
        }
        try
        {
            running.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // It ended between being found and being cancelled.
            return false;
        }
    }

    private static T Read<T>(HostRequest request) =>
        request.Params.Deserialize<T>(HostJson.Options) ?? throw new InvalidOperationException($"{request.Method} was sent no parameters");

    private EvaluateDone Evaluate(int id, EvaluateParams asked, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        int count = 0;
        Evaluation.Run(asked, evaluated =>
        {
            Interlocked.Increment(ref count);
            Write(new HostReply { Id = id, Partial = JsonSerializer.SerializeToElement(evaluated, HostJson.Options) });
        }, cancel);
        return new EvaluateDone(count, Stopwatch.GetElapsedTime(began).TotalSeconds);
    }

    private async Task<BuildDone> Build(int id, BuildParams asked, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        int count = 0;
        await DesignTimeBuilds.Run(asked, built =>
        {
            Interlocked.Increment(ref count);
            Write(new HostReply { Id = id, Partial = JsonSerializer.SerializeToElement(built, HostJson.Options) });
        }, cancel);
        return new BuildDone(count, Stopwatch.GetElapsedTime(began).TotalSeconds);
    }

    /// <summary>Held until cancelled, which answers the request as cancelled.</summary>
    private async Task<bool> Hold(int id, CancellationToken release)
    {
        await DesignTimeBuilds.HoldAsync(state => Write(new HostReply { Id = id, Partial = JsonSerializer.SerializeToElement(state, HostJson.Options) }), release);
        return true;
    }

    private void Write(HostReply reply)
    {
        string line = JsonSerializer.Serialize(reply, HostJson.Options);
        lock (writing)
        {
            protocol.WriteLine(line);
        }
    }
}
