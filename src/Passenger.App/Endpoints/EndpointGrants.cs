using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Endpoints;

/// <summary>What the folder's requests may take from the person's computer, each to one host: an environment variable
/// or a file outside the folder ("localhost:5000 env API_TOKEN"), as Always for this folder kept it; and the OpenAPI
/// document the person named for each web project whose code names none (a path, or a URL on this machine).</summary>
public sealed record EndpointsState : IStateDocument<EndpointsState>
{
    public List<string> Allowed { get; init; } = [];

    /// <summary>The OpenAPI document named for each web project, by its path.</summary>
    public Dictionary<string, string> OpenApiDocuments { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public static IReadOnlyList<string> Sets { get; } = [nameof(Allowed)];

    public EndpointsState Clamped() => this with
    {
        Allowed = [.. (Allowed ?? []).Where(a => a is not null).Distinct()],
        OpenApiDocuments = (OpenApiDocuments ?? []).Where(d => d.Key is { Length: > 0 } && OpenApiAddresses.Named(d.Value).Document is not null)
            .ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal),
    };
}

/// <summary>The open folder's answers to the question a request asks before it takes something from the person's
/// computer to a host: an Allow lasts while the folder is open, and Always for this folder is kept in the folder's state
/// when the window keeps state. A Don't allow is never kept, so the next send asks again.</summary>
public sealed class EndpointGrants
{
    private readonly HashSet<string> allowed = new(StringComparer.Ordinal);
    private StateDocument<EndpointsState>? kept;

    /// <summary>The folder's documents from now on: what it allowed always, allowed again.</summary>
    public void Keep(StateDocument<EndpointsState> state)
    {
        kept = state;
        allowed.UnionWith(state.Value.Allowed);
    }

    /// <summary>The reads of a call the folder has not allowed to its host, which the person is asked about.</summary>
    public IReadOnlyList<EndpointRead> Unallowed(EndpointCall call) => [.. call.Reads.Where(r => !allowed.Contains(r.Key(call.Host)))];

    /// <summary>Allows a call's reads to its host while the folder is open, and from now on when <paramref name="always"/>.</summary>
    public void Allow(EndpointCall call, bool always)
    {
        UiThread.Check();
        string[] keys = [.. call.Reads.Select(r => r.Key(call.Host))];
        allowed.UnionWith(keys);
        if (always && kept is not null)
        {
            EndpointsState now = kept.Value;
            kept.Set(now with { Allowed = [.. now.Allowed.Union(keys, StringComparer.Ordinal)] });
        }
    }
}

/// <summary>The OpenAPI document the person named for each of the folder's web projects whose code names none, kept with the
/// folder when the window keeps state (<see cref="EndpointsState.OpenApiDocuments"/>), else while it is open.</summary>
public sealed class OpenApiNames
{
    private readonly Dictionary<string, string> named = new(Core.PathNames.Comparer);
    private StateDocument<EndpointsState>? kept;

    /// <summary>The folder's documents from now on: the documents it named, named again.</summary>
    public void Keep(StateDocument<EndpointsState> state)
    {
        kept = state;
        foreach ((string project, string document) in state.Value.OpenApiDocuments)
        {
            named[project] = document;
        }
    }

    /// <summary>The document named for a project, or null.</summary>
    public string? Of(string project) => named.GetValueOrDefault(project);

    /// <summary>Names a project's document (a path, or a URL on this machine), kept with the folder.</summary>
    public void Name(string project, string document)
    {
        UiThread.Check();
        named[project] = document;
        if (kept is not null)
        {
            EndpointsState now = kept.Value;
            kept.Set(now with { OpenApiDocuments = new Dictionary<string, string>(now.OpenApiDocuments, StringComparer.Ordinal) { [project] = document } });
        }
    }
}
