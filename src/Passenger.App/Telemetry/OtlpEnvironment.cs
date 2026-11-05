using Passenger.Core;

namespace Passenger.App.Telemetry;

/// <summary>What tells a .NET program to send a dashboard its logs and traces: the variables told it, over what it sets
/// itself, the names of those it sets that are kept as its own, and the endpoints it sets for a signal of its own, which
/// the dashboard's key never reaches.</summary>
public sealed record OtlpEnvironment(IReadOnlyDictionary<string, string> Told, IReadOnlyList<string> Kept)
{
    /// <summary>What it sends and what names where each goes, as OpenTelemetry's exporters read them.</summary>
    private sealed record Signal(string Name, string Path, string Endpoint, string Headers, string Protocol);

    private static readonly Signal[] Signals =
    [
        new("logs", "/v1/logs", OtelVariables.LogsEndpoint, OtelVariables.LogsHeaders, OtelVariables.LogsProtocol),
        new("traces", "/v1/traces", OtelVariables.TracesEndpoint, OtelVariables.TracesHeaders, OtelVariables.TracesProtocol),
        new("metrics", "/v1/metrics", OtelVariables.MetricsEndpoint, OtelVariables.MetricsHeaders, OtelVariables.MetricsProtocol),
    ];

    /// <summary>The endpoint variables of the signals it sends elsewhere, each left as it set it.</summary>
    public IReadOnlyList<string> Elsewhere { get; private init; } = [];

    /// <summary>What it sends the dashboard, by name; all of it unless <see cref="Elsewhere"/> says otherwise.</summary>
    public IReadOnlyList<string> Sent { get; private init; } = [.. Signals.Select(signal => signal.Name)];

    /// <summary>The variables for a program whose own environment is <paramref name="own"/>: each signal to the endpoint of the
    /// protocol it uses (<paramref name="http"/>'s for http/protobuf, else <paramref name="grpc"/>'s), the dashboard's key beside
    /// its own headers, its records' name and batches every second, so a request shows at once; all going one way by the
    /// common variables, but common headers reach a signal's own endpoint, so where one is set the rest are told apart. What it
    /// sets itself is kept (<see cref="Kept"/>); null when nothing comes (<see cref="NoneComes"/>). Letter case is passed over.</summary>
    public static OtlpEnvironment? Of(string grpc, string http, string key, string name, IReadOnlyDictionary<string, string> own)
    {
        if (NoneComes(own).Count > 0)
        {
            return null;
        }
        Signal[] coming = [.. Signals.Where(signal => Named(own, signal.Endpoint) is null)];
        string header = $"x-otlp-api-key={key}";
        string WithKey(string? headers) => headers is { Length: > 0 } ? $"{headers},{header}" : header;
        bool OverHttp(Signal signal) => (Named(own, signal.Protocol) ?? Named(own, OtelVariables.ExporterProtocol)) is { } protocol
            && protocol.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        Dictionary<string, string> told = new(StringComparer.Ordinal);
        if (coming.Length == Signals.Length && coming.All(signal => OverHttp(signal) == OverHttp(coming[0])))
        {
            told[OtelVariables.ExporterEndpoint] = OverHttp(coming[0]) ? http : grpc;
            told[OtelVariables.ExporterHeaders] = WithKey(Own(own, OtelVariables.ExporterHeaders));
            foreach (Signal signal in coming)
            {
                if (Named(own, signal.Headers) is { } headers)
                {
                    told[signal.Headers] = WithKey(headers);
                }
            }
        }
        else
        {
            foreach (Signal signal in coming)
            {
                told[signal.Endpoint] = OverHttp(signal) ? http + signal.Path : grpc;
                told[signal.Headers] = WithKey(Named(own, signal.Headers) ?? Own(own, OtelVariables.ExporterHeaders));
            }
        }
        List<string> kept = [];
        foreach ((string variable, string value) in (ReadOnlySpan<(string, string)>)[(OtelVariables.ServiceName, name), (OtelVariables.LogsDelay, "1000"),
            (OtelVariables.SpansDelay, "1000"), (OtelVariables.MetricsInterval, "1000")])
        {
            if (Own(own, variable) is not null)
            {
                kept.Add(variable);
            }
            else
            {
                told[variable] = value;
            }
        }
        return new OtlpEnvironment(told, kept)
        {
            Elsewhere = [.. Signals.Except(coming).Select(signal => signal.Endpoint)],
            Sent = [.. coming.Select(signal => signal.Name)],
        };
    }

    /// <summary>The variables that send all a program's records elsewhere, its common endpoint or each signal's own: empty
    /// where something comes to a dashboard.</summary>
    public static IReadOnlyList<string> NoneComes(IReadOnlyDictionary<string, string> own) =>
        Named(own, OtelVariables.ExporterEndpoint) is not null ? [OtelVariables.ExporterEndpoint]
        : Signals.All(signal => Named(own, signal.Endpoint) is not null) ? [.. Signals.Select(signal => signal.Endpoint)]
        : [];

    /// <summary>The environment a program runs in: what it inherits, with its own set over it, a name that differs only in
    /// letter case replacing the one inherited, as .NET's configuration takes them for one.</summary>
    public static IReadOnlyDictionary<string, string> Effective(IEnumerable<KeyValuePair<string, string>> inherited, IReadOnlyDictionary<string, string> own)
    {
        Dictionary<string, string> effective = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string variable, string value) in inherited.Concat(own))
        {
            effective[variable] = value;
        }
        return effective;
    }

    /// <summary>The name the program's records go by: its own, where it sets one, else the one told.</summary>
    public static string NameOf(string name, IReadOnlyDictionary<string, string> own) => Own(own, OtelVariables.ServiceName) is { Length: > 0 } its ? its : name;

    /// <summary>A variable's value in a program's own environment, or null where it sets none.</summary>
    private static string? Own(IReadOnlyDictionary<string, string> own, string variable) =>
        own.FirstOrDefault(set => string.Equals(set.Key, variable, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found ? found.Value : null;

    /// <summary>A variable a program sets to something: OpenTelemetry reads a blank one as unset.</summary>
    private static string? Named(IReadOnlyDictionary<string, string> own, string variable) =>
        Own(own, variable) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
}
