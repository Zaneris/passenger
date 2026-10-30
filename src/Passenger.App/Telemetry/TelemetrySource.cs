namespace Passenger.App.Telemetry;

/// <summary>Where a run's structured logs and traces are read: an Aspire dashboard's telemetry API on this machine and
/// its API key.</summary>
public sealed record TelemetrySource(Uri Endpoint, string ApiKey)
{
    /// <summary>Whether a record is the resource or Compose service named: by its service's name alone (every instance of
    /// it), by the resource as the dashboard names it (the service's, then its instance after a dash), or by its instance
    /// alone, which DCP may make its replica's whole name. Never a prefix of either: a sibling service's name can begin
    /// with this one's and a dash (api and api-worker).</summary>
    public static bool Of(string? service, string? resource, string named) => service == named || resource == named
        || (service is not null && resource is not null && resource.Length > service.Length + 1 && resource.StartsWith(service, StringComparison.Ordinal)
            && resource[service.Length] == '-' && resource.AsSpan(service.Length + 1).SequenceEqual(named));

    /// <summary>Why a run's logs and traces cannot be read, every kind's in the same words: its dashboard is not up.</summary>
    public static string NoDashboard(string run) => $"{run}'s dashboard is not up: it has no logs or traces to read";

    /// <summary>Whether a failure of a dashboard's read is the dashboard's to say (its answer, its connection, its time),
    /// never Passenger's own.</summary>
    public static bool Said(Exception error) => error is IOException or InvalidDataException or TimeoutException or NotSupportedException
        or System.Net.Http.HttpRequestException;

    /// <summary>What a dashboard's failed read says, in the same words wherever it is read: what failed, its key and any
    /// secret masked, one line of 512 characters at most.</summary>
    public string Unread(Exception error)
    {
        string reason = Containers.ContainerSecrets.InValue(error.Message.Replace(ApiKey, Containers.ContainerSecrets.Masked, StringComparison.Ordinal))
            .Replace('\r', ' ').Replace('\n', ' ').TrimEnd('.');
        return $"The dashboard's logs and traces could not be read: {(reason.Length > 512 ? reason[..512] : reason)}";
    }
}
