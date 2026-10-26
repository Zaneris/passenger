using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Threading.Channels;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Passenger.App.Aspire.Protocol;

namespace Passenger.Tests;

/// <summary>A stand-in for an AppHost's resource service on loopback (HTTP/2 gRPC frames), which a test drives: the resources,
/// consoles and prompts it pushes, the answers and uploads the window sends. It admits only the key the IDE gave the stand-in
/// AppHost, which that program writes to <see cref="KeyFile"/> as it starts.</summary>
internal sealed class AspireInteractionHost : IAsyncDisposable
{
    private const string Service = "/aspire.v1.DashboardService/";
    private readonly WebApplication server;
    private readonly Channel<WatchInteractionsResponseUpdate> updates = Channel.CreateUnbounded<WatchInteractionsResponseUpdate>();
    private readonly ConcurrentQueue<WatchInteractionsRequestUpdate> answers = new();
    private readonly ConcurrentQueue<UploadFileChunk[]> uploads = new();
    private readonly ConcurrentDictionary<string, Resource> resources = new(StringComparer.Ordinal);
    private readonly Channel<WatchResourcesUpdate> resourceUpdates = Channel.CreateUnbounded<WatchResourcesUpdate>();
    private readonly ConcurrentQueue<ConsoleSubscription> consoles = new();
    private readonly ConcurrentQueue<ResourceCommandRequest> commands = new();
    private readonly ConcurrentDictionary<HttpContext, byte> open = new();
    private WireBarrier? nextAnswer;

    private AspireInteractionHost(WebApplication server, string keyFile)
    {
        this.server = server;
        KeyFile = keyFile;
        server.MapPost(Service + "GetApplicationInformation", Guard(async context =>
        {
            await ReadFrameAsync(context.Request.Body, context.RequestAborted);
            await WriteFrameAsync(context, new ApplicationInformationResponse { ApplicationName = "Passenger wire fixture" });
            context.Response.AppendTrailer("grpc-status", "0");
        }));
        server.MapPost(Service + "WatchInteractions", Guard(WatchAsync));
        server.MapPost(Service + "WatchResources", Guard(async context =>
        {
            await ReadFrameAsync(context.Request.Body, context.RequestAborted);
            InitialResourceData initial = new();
            initial.Resources.AddRange(resources.Values.Select(resource => resource.Clone()));
            await WriteFrameAsync(context, new WatchResourcesUpdate { InitialData = initial });
            await foreach (WatchResourcesUpdate update in resourceUpdates.Reader.ReadAllAsync(context.RequestAborted))
            {
                await WriteFrameAsync(context, update);
            }
        }));
        server.MapPost(Service + "WatchResourceConsoleLogs", Guard(WatchConsoleAsync));
        server.MapPost(Service + "UploadFile", Guard(UploadAsync));
        server.MapPost(Service + "ExecuteResourceCommand", Guard(async context =>
        {
            byte[] frame = await ReadFrameAsync(context.Request.Body, context.RequestAborted)
                ?? throw new InvalidDataException("Missing resource command request");
            ResourceCommandRequest request = ResourceCommandRequest.Parser.ParseFrom(frame);
            commands.Enqueue(request);
            OnCommand?.Invoke(request);
            await WriteFrameAsync(context, new ResourceCommandResponse { Kind = ResourceCommandResponseKind.Succeeded });
            context.Response.AppendTrailer("grpc-status", "0");
        }));
    }

    /// <summary>What a test has a command do before it is answered (on the stand-in's own thread), as an AppHost's command acts.</summary>
    public Action<ResourceCommandRequest>? OnCommand { get; set; }

    /// <summary>Where the stand-in AppHost writes the resource service key the IDE gave it.</summary>
    public string KeyFile { get; }
    public Uri Endpoint { get; private set; } = null!;
    public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public WireBarrier FirstUpload { get; } = new();
    public ConcurrentQueue<Exception> Errors { get; } = new();
    public WatchInteractionsRequestUpdate[] Answers => answers.ToArray();
    public UploadFileChunk[][] Uploads => uploads.ToArray();
    public ConsoleSubscription[] Consoles => consoles.ToArray();
    /// <summary>The resource commands the window ran, each answered as done.</summary>
    public ResourceCommandRequest[] Commands => commands.ToArray();

    public void PushResource(Resource resource)
    {
        resources[resource.Name] = resource.Clone();
        resourceUpdates.Writer.TryWrite(new WatchResourcesUpdate
        {
            Changes = new WatchResourcesChanges { Value = { new WatchResourcesChange { Upsert = resource.Clone() } } },
        });
    }

    public static async Task<AspireInteractionHost> StartAsync(string keyFile)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http2);
            options.Limits.Http2.InitialStreamWindowSize = 65_535;
            options.Limits.Http2.InitialConnectionWindowSize = 65_535;
            // None, as Aspire's gRPC server has none for its streams: Kestrel's would end an idle watch's connection in seconds.
            options.Limits.MinRequestBodyDataRate = null;
            options.Limits.MinResponseDataRate = null;
        });
        AspireInteractionHost host = new(builder.Build(), keyFile);
        try
        {
            await host.server.StartAsync();
            string address = host.server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            host.Endpoint = new Uri(address);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public void Push(WatchInteractionsResponseUpdate update)
    {
        if (!updates.Writer.TryWrite(update.Clone()))
        {
            throw new InvalidOperationException("The interaction fixture is closed");
        }
    }

    public WireBarrier HoldNextAnswer()
    {
        WireBarrier barrier = new();
        if (Interlocked.CompareExchange(ref nextAnswer, barrier, null) is not null)
        {
            throw new InvalidOperationException("An interaction response is already held");
        }
        return barrier;
    }

    /// <summary>Ends every call the window has open, as a resource service that restarts or goes away ends them.</summary>
    public void Drop()
    {
        foreach (HttpContext context in open.Keys)
        {
            context.Abort();
        }
    }

    /// <summary>The key the stand-in AppHost wrote, or null while it has written none, read sharing delete: the AppHost's
    /// rename of it into place holds it with delete access a moment after its name appears, and Windows refuses a reader
    /// that shares less.</summary>
    private async Task<string?> KeyAsync(CancellationToken cancel)
    {
        try
        {
            await using FileStream stream = new(KeyFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            using StreamReader reader = new(stream);
            return await reader.ReadToEndAsync(cancel);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private RequestDelegate Guard(RequestDelegate handle) => async context =>
    {
        context.Response.ContentType = "application/grpc";
        context.Response.DeclareTrailer("grpc-status");
        // The IDE asks as soon as it has started the AppHost, which may not have written its key yet.
        string? key = null;
        await Waiting.UntilAsync(async () => (key = await KeyAsync(context.RequestAborted)) is not null, TimeSpan.FromSeconds(30), context.RequestAborted);
        if (key is not { Length: > 0 } || context.Request.Headers["x-resource-service-api-key"] != key)
        {
            await context.Response.StartAsync(context.RequestAborted);
            context.Response.AppendTrailer("grpc-status", "16");
            return;
        }
        open.TryAdd(context, 0);
        try
        {
            await handle(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (IOException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Errors.Enqueue(error);
            throw;
        }
        finally
        {
            open.TryRemove(context, out _);
        }
    };

    private async Task WatchAsync(HttpContext context)
    {
        await context.Response.StartAsync(context.RequestAborted);
        Connected.TrySetResult();
        try
        {
            await Task.WhenAll(ReadAnswers(), WriteUpdates());
        }
        finally
        {
            Disconnected.TrySetResult();
        }

        async Task ReadAnswers()
        {
            while (await ReadFrameAsync(context.Request.Body, context.RequestAborted, async () =>
            {
                if (Interlocked.Exchange(ref nextAnswer, null) is { } held)
                {
                    held.Entered.TrySetResult();
                    await held.Release.Task.WaitAsync(context.RequestAborted);
                }
            }) is { } frame)
            {
                answers.Enqueue(WatchInteractionsRequestUpdate.Parser.ParseFrom(frame));
            }
        }

        async Task WriteUpdates()
        {
            await foreach (WatchInteractionsResponseUpdate update in updates.Reader.ReadAllAsync(context.RequestAborted))
            {
                await WriteFrameAsync(context, update);
            }
        }
    }

    private async Task UploadAsync(HttpContext context)
    {
        List<UploadFileChunk> chunks = [];
        while (await ReadFrameAsync(context.Request.Body, context.RequestAborted) is { } frame)
        {
            chunks.Add(UploadFileChunk.Parser.ParseFrom(frame));
        }
        uploads.Enqueue(chunks.ToArray());
        int number = uploads.Count;
        if (number == 1)
        {
            FirstUpload.Entered.TrySetResult();
            await FirstUpload.Release.Task.WaitAsync(context.RequestAborted);
        }
        await WriteFrameAsync(context, new UploadFileResponse { FileId = number == 1 ? "stale-file-id" : "current-file-id" });
        context.Response.AppendTrailer("grpc-status", "0");
    }

    private async Task WatchConsoleAsync(HttpContext context)
    {
        byte[] frame = await ReadFrameAsync(context.Request.Body, context.RequestAborted)
            ?? throw new InvalidDataException("Missing console subscription request");
        ConsoleSubscription subscription = new(WatchResourceConsoleLogsRequest.Parser.ParseFrom(frame).ResourceName);
        await context.Response.StartAsync(context.RequestAborted);
        consoles.Enqueue(subscription);
        try
        {
            await foreach (WatchResourceConsoleLogsUpdate update in subscription.Updates.Reader.ReadAllAsync(context.RequestAborted))
            {
                await WriteFrameAsync(context, update);
            }
        }
        finally
        {
            subscription.Closed.TrySetResult();
        }
    }

    private static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancel, Func<Task>? afterHeader = null)
    {
        byte[] header = new byte[5];
        if (await stream.ReadAsync(header.AsMemory(0, 1), cancel) == 0)
        {
            return null;
        }
        await stream.ReadExactlyAsync(header.AsMemory(1), cancel);
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (header[0] != 0 || length is < 0 or > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("Invalid uncompressed test gRPC frame");
        }
        if (afterHeader is not null)
        {
            await afterHeader();
        }
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, cancel);
        return body;
    }

    private static async Task WriteFrameAsync(HttpContext context, IMessage message)
    {
        byte[] body = message.ToByteArray();
        byte[] header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), body.Length);
        await context.Response.Body.WriteAsync(header, context.RequestAborted);
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        nextAnswer?.Release.TrySetResult();
        FirstUpload.Release.TrySetResult();
        updates.Writer.TryComplete();
        resourceUpdates.Writer.TryComplete();
        await server.StopAsync();
        await server.DisposeAsync();
    }

    internal sealed class ConsoleSubscription(string resourceName)
    {
        public string ResourceName { get; } = resourceName;
        public Channel<WatchResourceConsoleLogsUpdate> Updates { get; } = Channel.CreateUnbounded<WatchResourceConsoleLogsUpdate>();
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Push(params ConsoleLogLine[] lines) => Updates.Writer.TryWrite(new WatchResourceConsoleLogsUpdate { LogLines = { lines } });
    }

    internal sealed class WireBarrier
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

}
