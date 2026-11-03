using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>An instrument's value over its last interval, as the runtime's metrics told it (one series of its tags): a
/// counter's rate a second, a gauge's or an up-down counter's value, a histogram's median and 95th percentile, each in the
/// instrument's unit, with how many it measured (<paramref name="Count"/>; none measured, no median).</summary>
public sealed record MetricReading(string Meter, string Instrument, string Unit, MetricKind Kind, string Tags, double Value, double? Upper, double? Count = null);

/// <summary>What an instrument measures, as its readings say it.</summary>
public enum MetricKind
{
    /// <summary>A counter: its rate a second.</summary>
    Rate,

    /// <summary>A gauge or an up-down counter: its value.</summary>
    Gauge,

    /// <summary>A histogram: its median, its 95th percentile beside.</summary>
    Histogram,
}

/// <summary>What a program's metrics told of their session, on the thread that reads them: each interval's readings together,
/// a meter of the program's that the session does not read, and why the runtime refused the session.</summary>
public sealed record MetricsHeard(Action<IReadOnlyList<MetricReading>> Collected, Action<string> Published, Action<string> Refused);

/// <summary>A running .NET program's System.Diagnostics.Metrics instruments heard live with no change to it: the runtime's
/// MetricsEventSource over an EventPipe session at its own diagnostic port, every second, for the meters a caller names.
/// Nothing is asked of the program while no one looks: the session lasts from <see cref="AttachAsync"/> to
/// <see cref="DisposeAsync"/>. Where another tool reads them already, the runtime refuses the session, saying so.</summary>
public sealed class LiveMetrics : IAsyncDisposable
{
    private const string Provider = "System.Diagnostics.Metrics";

    /// <summary>The meters every service has as .NET and ASP.NET Core ship them.</summary>
    public static readonly IReadOnlyList<string> Shipped =
        ["Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Routing", "System.Net.Http", "System.Runtime"];

    private readonly EventPipeEventSource source;
    private readonly Func<Task> stop;

    private LiveMetrics(EventPipeEventSource source, Task ended, Func<Task> stop)
    {
        this.source = source;
        this.stop = stop;
        Ended = ended;
    }

    /// <summary>Done once the stream ends: the program gone, or the session stopped.</summary>
    public Task Ended { get; }

    /// <summary>The session id a program in a container is read under, the same for every Passenger there.</summary>
    public const string ContainerSession = "passenger-container-metrics";

    /// <summary>The runtime's metrics of <paramref name="meters"/> asked under a session id: its messages, time series and
    /// instruments published, at the interval and limits dotnet-counters asks for.</summary>
    public static EventPipeProvider[] Providers(string session, IReadOnlyCollection<string> meters) =>
    [
        new EventPipeProvider(Provider, EventLevel.Informational, 0x7, new Dictionary<string, string>
        {
            ["SessionId"] = session,
            ["Metrics"] = string.Join(',', meters.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)),
            ["RefreshInterval"] = "1",
            ["MaxTimeSeries"] = "1000",
            ["MaxHistograms"] = "10",
        }),
    ];

    /// <summary>Starts hearing a running program's instruments of <paramref name="meters"/> at its diagnostic port. A program
    /// gone, or with no port to reach, is a <see cref="ProfilingException"/> saying why.</summary>
    public static async Task<LiveMetrics> AttachAsync(int processId, IReadOnlyCollection<string> meters, MetricsHeard heard, CancellationToken cancel = default)
    {
        string session = Guid.NewGuid().ToString();
        EventPipeSession started;
        try
        {
            started = await new DiagnosticsClient(processId).StartEventPipeSessionAsync(Providers(session, meters), requestRundown: false, circularBufferMB: 16, cancel);
        }
        catch (Exception error) when (DiagnosticsFailures.Of(error))
        {
            throw new ProfilingException($"Its metrics could not be read: {error.Message}", error);
        }
        return Hear(started.EventStream, session, meters, heard, async () =>
        {
            await EventPipeStops.StopAsync(started, TimeSpan.FromSeconds(5));
            started.Dispose();
        });
    }

    /// <summary>Starts hearing a program in a container (<paramref name="place"/>) through Passenger's tools there (their
    /// <see cref="ContainerToolsWords.Trace"/> with <see cref="ContainerToolsWords.Metrics"/>: the meters .NET ships, at the
    /// program's own diagnostic port, its stream on their output). Tools that cannot start or reach it are a
    /// <see cref="ProfilingException"/> with their last word.</summary>
    public static async Task<LiveMetrics> AttachAsync(int processId, ProfilePlace place, MetricsHeard heard, CancellationToken cancel = default)
    {
        ContainerTrace recording = await ContainerTrace.StartAsync(place, processId, resume: false, ContainerToolsWords.Metrics, "read its metrics", "reading its metrics", cancel);
        Process tools = recording.Tools;
        return Hear(tools.StandardOutput.BaseStream, ContainerSession, Shipped, heard, async () =>
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
            await Task.WhenAny(recording.Heard, Task.Delay(TimeSpan.FromSeconds(2)));
            tools.Dispose();
        });
    }

    /// <summary>A stream's events read on a thread of their own, each interval's readings told together, until it ends.</summary>
    private static LiveMetrics Hear(Stream stream, string session, IReadOnlyCollection<string> meters, MetricsHeard heard, Func<Task> stop)
    {
        HashSet<string> asked = new(meters, StringComparer.Ordinal);
        EventPipeEventSource source = new(stream);
        List<MetricReading> interval = [];
        HashSet<string> told = new(StringComparer.Ordinal);
        source.Dynamic.All += data =>
        {
            if (data.ProviderName != Provider)
            {
                return;
            }
            if (Read(data, session) is { } reading)
            {
                interval.Add(reading);
            }
            // CollectionStop, a stop of its task as the trace names it.
            else if (data.EventName is "Collection/Stop" or "CollectionStop" && Ours(data, session) && interval.Count > 0)
            {
                heard.Collected([.. interval]);
                interval.Clear();
            }
            else if (data.EventName == "InstrumentPublished" && Ours(data, session) && data.PayloadByName("meterName") is string meter
                && !asked.Contains(meter) && told.Add(meter))
            {
                heard.Published(meter);
            }
            else if (Refusal(data, session) is { } why)
            {
                heard.Refused(why);
            }
        };
        // Not held, on a thread of its own: the trace is read until the program ends or no one looks.
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
        return new LiveMetrics(source, ended, stop);
    }

    private static bool Ours(TraceEvent data, string session) => data.PayloadByName("sessionId") as string == session;

    /// <summary>One of MetricsEventSource's readings of this session; null for another event, or one with no value.</summary>
    internal static MetricReading? Read(TraceEvent data, string session)
    {
        if (!Ours(data, session) || data.PayloadByName("meterName") is not string meter || data.PayloadByName("instrumentName") is not string instrument)
        {
            return null;
        }
        string unit = data.PayloadByName("unit") as string ?? string.Empty;
        string tags = data.PayloadByName("tags") as string ?? string.Empty;
        return data.EventName switch
        {
            "CounterRateValuePublished" when Number(data.PayloadByName("rate")) is double rate => new MetricReading(meter, instrument, unit, MetricKind.Rate, tags, rate, null),
            // An up-down counter's level is its value (.NET 8 on); a runtime before told its change alone.
            "UpDownCounterRateValuePublished" when Number(data.PayloadByName("value")) is double level => new MetricReading(meter, instrument, unit, MetricKind.Gauge, tags, level, null),
            "UpDownCounterRateValuePublished" when Number(data.PayloadByName("rate")) is double rate => new MetricReading(meter, instrument, unit, MetricKind.Rate, tags, rate, null),
            "GaugeValuePublished" when Number(data.PayloadByName("lastValue")) is double last => new MetricReading(meter, instrument, unit, MetricKind.Gauge, tags, last, null),
            "HistogramValuePublished" when data.PayloadByName("quantiles") is string quantiles && Quantile(quantiles, "0.5") is double median
                => new MetricReading(meter, instrument, unit, MetricKind.Histogram, tags, median, Quantile(quantiles, "0.95"), Number(data.PayloadByName("count"))),
            "HistogramValuePublished" when Number(data.PayloadByName("count")) is 0 => new MetricReading(meter, instrument, unit, MetricKind.Histogram, tags, 0, null, 0),
            _ => null,
        };
    }

    /// <summary>Why the runtime refused this session, in its own terms; null for any other event.</summary>
    private static string? Refusal(TraceEvent data, string session) => data.EventName switch
    {
        "MultipleSessionsNotSupportedError" when data.PayloadByName("runningSessionId") is string running && running != session
            => "Another tool reads this program's metrics, which its runtime lets one session at a time do",
        "Error" when Ours(data, session) && data.PayloadByName("errorMessage") is string { Length: > 0 } message => message.Split('\n')[0].Trim(),
        _ => null,
    };

    /// <summary>A quantile of a histogram's ("0.5=1.2;0.95=3.4;0.99=5.6"), or null where it says none.</summary>
    private static double? Quantile(string quantiles, string named)
    {
        foreach (string part in quantiles.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0 && part[..equals] == named)
            {
                return Number(part[(equals + 1)..]);
            }
        }
        return null;
    }

    /// <summary>A value as the event writes it (a number, or a number in words); null for none, or a number that is none.</summary>
    private static double? Number(object? value) => value switch
    {
        int number => number,
        long number => number,
        double number when double.IsFinite(number) => number,
        string said when double.TryParse(said, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) => number,
        _ => null,
    };

    public async ValueTask DisposeAsync()
    {
        await stop();
        source.StopProcessing();
        await Ended.WaitAsync(TimeSpan.FromSeconds(5)).Ended();
        source.Dispose();
    }
}
