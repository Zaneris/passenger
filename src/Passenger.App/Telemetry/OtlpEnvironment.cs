using Passenger.Core;

namespace Passenger.App.Telemetry;

/// <summary>What tells a .NET program to send a dashboard its logs and traces: the variables told it, over what it sets
/// itself, and the names of those it sets that are kept as its own.</summary>
public sealed record OtlpEnvironment(IReadOnlyDictionary<string, string> Told, IReadOnlyList<string> Kept)
{
    /// <summary>The variables for a program whose own environment is <paramref name="own"/>: the OTLP endpoint of the protocol
    /// it uses (<paramref name="http"/>'s for http/protobuf, else <paramref name="grpc"/>'s), the dashboard's key in its headers
    /// beside any of its own, its records' name and batches every second, so a request shows at once. Each it sets itself is
    /// kept, named in <see cref="Kept"/>; null when it names its own endpoint, which its records go to. Names are matched as
    /// .NET's configuration reads them, letter case aside.</summary>
    public static OtlpEnvironment? Of(string grpc, string http, string key, string name, IReadOnlyDictionary<string, string> own)
    {
        if (Own(own, OtelVariables.ExporterEndpoint) is not null)
        {
            return null;
        }
        bool overHttp = Own(own, OtelVariables.ExporterProtocol) is { } protocol && protocol.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        string header = $"x-otlp-api-key={key}";
        Dictionary<string, string> told = new(StringComparer.Ordinal)
        {
            [OtelVariables.ExporterEndpoint] = overHttp ? http : grpc,
            [OtelVariables.ExporterHeaders] = Own(own, OtelVariables.ExporterHeaders) is { Length: > 0 } headers ? $"{headers},{header}" : header,
        };
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
        return new OtlpEnvironment(told, kept);
    }

    /// <summary>The name the program's records go by: its own, where it sets one, else the one told.</summary>
    public static string NameOf(string name, IReadOnlyDictionary<string, string> own) => Own(own, OtelVariables.ServiceName) is { Length: > 0 } its ? its : name;

    /// <summary>A variable's value in a program's own environment, or null where it sets none.</summary>
    public static string? Own(IReadOnlyDictionary<string, string> own, string variable) =>
        own.FirstOrDefault(set => string.Equals(set.Key, variable, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found ? found.Value : null;
}
