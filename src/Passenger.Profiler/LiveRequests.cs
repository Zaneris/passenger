using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>Something a web app told of its requests: each is of one request, known by its activity's root id
/// (<see cref="Trace"/>), the time it was told (UTC).</summary>
public abstract record WebEvent(string Trace, DateTime At);

/// <summary>ASP.NET Core began a request, its own id (<c>HttpContext.TraceIdentifier</c>) in <paramref name="Request"/>.</summary>
public sealed record RequestBegan(string Trace, DateTime At, string Request, string Method, string Path, string Query) : WebEvent(Trace, At)
{
    /// <summary>The id of the request's own activity, which what it runs runs under; empty where the app made none.</summary>
    public string Activity { get; init; } = string.Empty;
}

/// <summary>ASP.NET Core ended a request with its status: told by the request's own id, since a request an exception ended
/// is ended outside its activity.</summary>
public sealed record RequestEnded(string Trace, DateTime At, string Request, int Status) : WebEvent(Trace, At);

/// <summary>A request ended in an exception nothing handled: its words and stack, as the exception writes itself.</summary>
public sealed record RequestFailed(string Trace, DateTime At, string Request, string Error) : WebEvent(Trace, At);

/// <summary>EF Core ran a command for a request: its id, text, time and the rows a non-query changed; or why it failed.</summary>
public sealed record CommandRan(string Trace, DateTime At, string Id, string Text, double Milliseconds, int? Rows, string? Error) : WebEvent(Trace, At)
{
    /// <summary>The ids of the activity it ran under and of that activity's parents, the nearest first: its request's among them.</summary>
    public IReadOnlyList<string> Under { get; init; } = [];
}

/// <summary>A command's reader closed after so many reads (EF Core's ReadCount: each Read asked, the last that found no row too).</summary>
public sealed record CommandRead(string Trace, DateTime At, string Id, int Reads) : WebEvent(Trace, At)
{
    /// <inheritdoc cref="CommandRan.Under"/>
    public IReadOnlyList<string> Under { get; init; } = [];
}

/// <summary>EF Core logged a command it ran (event 20101, as the app's own logging writes it): its words, the parameters among
/// them, with their values only where the app logs those (EF's sensitive data logging), else as '?'.</summary>
public sealed record CommandLogged(string Trace, DateTime At, string Message) : WebEvent(Trace, At)
{
    /// <summary>The span id of the activity it was logged under; empty where there was none.</summary>
    public string Span { get; init; } = string.Empty;
}

/// <summary>A running web app's requests and the database commands each issued, heard live with no change to the app: ASP.NET
/// Core's hosting events and EF Core's command events through the runtime's DiagnosticSource bridge
/// (<c>Microsoft-Diagnostics-DiagnosticSource</c>) over an EventPipe session at the program's own diagnostic port, each told
/// with the activity it ran under: a command is tied to the request whose activity is that one or a parent of it, else to
/// the latest request of the activity's root id. Nothing is asked of the app while no one listens: the session lasts from
/// <see cref="AttachAsync"/> to <see cref="DisposeAsync"/>.</summary>
public sealed class LiveRequests : IAsyncDisposable
{
    private const string Bridge = "Microsoft-Diagnostics-DiagnosticSource";

    private const string Logging = "Microsoft-Extensions-Logging";

    /// <summary>The activity a command ran under and its parents, as many as a command is looked for its request in: an app's
    /// own activities between a request's and its commands are seldom more.</summary>
    private const string Under = "Under0=*Activity.Id;Under1=*Activity.Parent.Id;Under2=*Activity.Parent.Parent.Id;Under3=*Activity.Parent.Parent.Parent.Id";

    private static readonly string[] UnderNames = ["Under0", "Under1", "Under2", "Under3"];

    /// <summary>The events asked for and the values each takes from its payload (<c>*Activity</c> is the activity it ran under).</summary>
    internal static readonly string Specs = string.Join("\n",
    [
        // Asked by its name, so ASP.NET Core makes each request's activity for its listeners too.
        "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.HttpRequestIn:-",
        "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.HttpRequestIn.Start:-Method=Request.Method;Path=Request.Path;Query=Request.QueryString;Request=TraceIdentifier;Trace=*Activity.RootId;Activity=*Activity.Id",
        "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.HttpRequestIn.Stop:-Status=Response.StatusCode;Request=TraceIdentifier;Trace=*Activity.RootId",
        "Microsoft.AspNetCore/Microsoft.AspNetCore.Hosting.UnhandledException:-Error=exception;Request=httpContext.TraceIdentifier;Trace=*Activity.RootId",
        "Microsoft.AspNetCore/Microsoft.AspNetCore.Diagnostics.UnhandledException:-Error=exception;Request=httpContext.TraceIdentifier;Trace=*Activity.RootId",
        $"Microsoft.EntityFrameworkCore/Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted:-Id=CommandId;Text=Command.CommandText;Ms=Duration.TotalMilliseconds;Result=Result;Trace=*Activity.RootId;{Under}",
        $"Microsoft.EntityFrameworkCore/Microsoft.EntityFrameworkCore.Database.Command.CommandError:-Id=CommandId;Text=Command.CommandText;Ms=Duration.TotalMilliseconds;Error=Exception.Message;Trace=*Activity.RootId;{Under}",
        $"Microsoft.EntityFrameworkCore/Microsoft.EntityFrameworkCore.Database.Command.DataReaderDisposing:-Id=CommandId;Reads=ReadCount;Trace=*Activity.RootId;{Under}",
    ]);

    /// <summary>The logs asked of the app's logging: EF Core's commands' category, with no level of its own, so the
    /// session's (Informational) gives Information: before .NET 11, a session begun before the app's LoggingEventSource
    /// is made reads a ":Information" as part of the category's name, which no logger's matches.</summary>
    internal const string LogsSpec = "Microsoft.EntityFrameworkCore.Database.Command";

    private readonly EventPipeEventSource source;
    private readonly Func<Task> stop;

    private LiveRequests(EventPipeEventSource source, Task ended, Func<Task> stop)
    {
        this.source = source;
        this.stop = stop;
        Ended = ended;
    }

    /// <summary>Done once the stream ends: the program gone, or the session stopped.</summary>
    public Task Ended { get; }

    /// <summary>The providers a session asks: the bridge's events and messages, with <see cref="Specs"/>.</summary>
    internal static EventPipeProvider[] Providers =>
    [
        new EventPipeProvider(Bridge, EventLevel.Verbose, 0x3, new Dictionary<string, string> { ["FilterAndPayloadSpecs"] = Specs }),
        // Counters once a second, passed over: more coming hands on a request's last events within a second.
        new EventPipeProvider("System.Runtime", EventLevel.Informational, 0, new Dictionary<string, string> { ["EventCounterIntervalSec"] = "1" }),
        // EF Core's log of each command, for its parameters, which the bridge's events cannot name.
        new EventPipeProvider(Logging, EventLevel.Informational, 0x4, new Dictionary<string, string> { ["FilterSpecs"] = LogsSpec }),
    ];

    /// <summary>Starts hearing a running program at its diagnostic port, each event told to <paramref name="heard"/> on the
    /// thread that reads the stream. A program gone, or with no port to reach, is a <see cref="ProfilingException"/> saying why.</summary>
    public static async Task<LiveRequests> AttachAsync(int processId, Action<WebEvent> heard, CancellationToken cancel = default)
    {
        EventPipeSession session;
        try
        {
            session = await new DiagnosticsClient(processId).StartEventPipeSessionAsync(Providers, requestRundown: false, circularBufferMB: 64, cancel);
        }
        catch (Exception error) when (error is DiagnosticsClientException or IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or TimeoutException)
        {
            throw new ProfilingException($"Its requests could not be followed: {error.Message}", error);
        }
        return Hear(session.EventStream, heard, async () =>
        {
            try
            {
                await session.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) when (error is DiagnosticsClientException or IOException or ObjectDisposedException or TimeoutException or ServerNotAvailableException)
            {
                // A program gone has nothing left to stop.
            }
            session.Dispose();
        });
    }

    /// <summary>Starts hearing a program in a container (<paramref name="place"/>) through Passenger's tools there
    /// (<c>trace --requests</c>, the same session at the program's own diagnostic port, its stream on their output).
    /// Tools that cannot start or reach it are a <see cref="ProfilingException"/> with their last word.</summary>
    public static async Task<LiveRequests> AttachAsync(int processId, ProfilePlace place, Action<WebEvent> heard, CancellationToken cancel = default)
    {
        Process tools;
        try
        {
            ProcessStartInfo start = place.Tools(["trace", "--pid", processId.ToString(CultureInfo.InvariantCulture), "--requests"]);
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            tools = ChildProcesses.Start(start);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            throw new ProfilingException($"Could not start Passenger's tools in {place.Where} to follow its requests: {error.Message}", error);
        }
        TaskCompletionSource recording = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string? said = null;
        // Not held: read for as long as the tools run.
        Task reading = BackgroundWork.Unheld(() => ErrorLines.ReadAsync(tools.StandardError, line =>
        {
            if (line.Trim() == InContainer.Recording)
            {
                recording.TrySetResult();
            }
            else if (line.Trim() is { Length: > 0 } words)
            {
                Volatile.Write(ref said, words);
            }
        }), CancellationToken.None);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        if (await Task.WhenAny(recording.Task, tools.WaitForExitAsync(CancellationToken.None), Task.Delay(Timeout.Infinite, limit.Token)) != recording.Task)
        {
            bool ended = tools.HasExited;
            ChildProcesses.Kill(tools);
            tools.Dispose();
            cancel.ThrowIfCancellationRequested();
            throw new ProfilingException(ended
                ? Volatile.Read(ref said) ?? $"Passenger's tools in {place.Where} could not follow its requests"
                : $"Passenger's tools in {place.Where} did not begin following its requests in 30 s");
        }
        return Hear(tools.StandardOutput.BaseStream, heard, async () =>
        {
            // Their input's end stops the session there; tools that do not end are ended.
            try
            {
                tools.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            await Task.WhenAny(tools.WaitForExitAsync(CancellationToken.None), Task.Delay(TimeSpan.FromSeconds(5)));
            if (!tools.HasExited)
            {
                ChildProcesses.Kill(tools);
            }
            await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(2)));
            tools.Dispose();
        });
    }

    /// <summary>A stream's events read on a thread of their own, the bridge's told as they come, until it ends.</summary>
    private static LiveRequests Hear(Stream stream, Action<WebEvent> heard, Func<Task> stop)
    {
        EventPipeEventSource source = new(stream);
        source.Dynamic.All += data =>
        {
            if (data.ProviderName is Bridge or Logging && Read(data) is { } told)
            {
                heard(told);
            }
        };
        // Not held, on a thread of its own: the trace is read until the program ends or the requests are put away.
        Task ended = BackgroundWork.UnheldThread(() =>
        {
            try
            {
                source.Process();
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or EndOfStreamException or FormatException)
            {
            }
        });
        return new LiveRequests(source, ended, stop);
    }

    /// <summary>One of the bridge's events, or EF Core's command log, as what it tells; null for another.</summary>
    internal static WebEvent? Read(TraceEvent data)
    {
        if (data.ProviderName == Logging)
        {
            // EF Core's CommandExecuted (20101), tied to its request by the activity the app's logging names.
            return data.EventName == "FormattedMessage" && data.PayloadByName("EventId") is int and 20101 && data.PayloadByName("FormattedMessage") is string message
                ? new CommandLogged(data.PayloadByName("ActivityTraceId") as string ?? string.Empty, data.TimeStamp.ToUniversalTime(), message)
                {
                    Span = data.PayloadByName("ActivitySpanId") as string is { } span && span.Trim('0').Length > 0 ? span : string.Empty,
                }
                : null;
        }
        if (data.PayloadByName("EventName") is not string name || data.PayloadByName("Arguments") is not IEnumerable<object> arguments)
        {
            return null;
        }
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (object pair in arguments)
        {
            if (pair is IDictionary<string, object> entry && entry.TryGetValue("Key", out object? key) && key is string k)
            {
                values[k] = entry.TryGetValue("Value", out object? value) ? value?.ToString() ?? string.Empty : string.Empty;
            }
        }
        string trace = values.GetValueOrDefault("Trace") ?? string.Empty;
        string request = values.GetValueOrDefault("Request") ?? string.Empty;
        DateTime at = data.TimeStamp.ToUniversalTime();
        string[] under = [.. UnderNames.Select(n => values.GetValueOrDefault(n)).OfType<string>().Where(id => id.Length > 0)];
        return name switch
        {
            "Microsoft.AspNetCore.Hosting.HttpRequestIn.Start" => new RequestBegan(trace, at, request, values.GetValueOrDefault("Method") ?? string.Empty,
                values.GetValueOrDefault("Path") ?? string.Empty, values.GetValueOrDefault("Query") ?? string.Empty) { Activity = values.GetValueOrDefault("Activity") ?? string.Empty },
            "Microsoft.AspNetCore.Hosting.HttpRequestIn.Stop" => new RequestEnded(trace, at, request, Number(values.GetValueOrDefault("Status")) ?? 0),
            "Microsoft.AspNetCore.Hosting.UnhandledException" or "Microsoft.AspNetCore.Diagnostics.UnhandledException" =>
                new RequestFailed(trace, at, request, values.GetValueOrDefault("Error") ?? string.Empty),
            "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted" or "Microsoft.EntityFrameworkCore.Database.Command.CommandError" =>
                new CommandRan(trace, at, values.GetValueOrDefault("Id") ?? string.Empty, values.GetValueOrDefault("Text") ?? string.Empty,
                    double.TryParse(values.GetValueOrDefault("Ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) ? ms : 0,
                    Number(values.GetValueOrDefault("Result")), values.GetValueOrDefault("Error")) { Under = under },
            "Microsoft.EntityFrameworkCore.Database.Command.DataReaderDisposing" =>
                new CommandRead(trace, at, values.GetValueOrDefault("Id") ?? string.Empty, Number(values.GetValueOrDefault("Reads")) ?? 0) { Under = under },
            _ => null,
        };

        static int? Number(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;
    }

    /// <summary>Stops the session and lets go of the program, which runs on as it was.</summary>
    public async ValueTask DisposeAsync()
    {
        await stop();
        source.StopProcessing();
        await Ended.WaitAsync(TimeSpan.FromSeconds(5)).ContinueWith(_ => { }, TaskScheduler.Default);
        source.Dispose();
    }
}
