using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.App.Endpoints;

/// <summary>The folder's web routes, as the C# server's extension last read them (<see cref="Handlers.WebRoutes"/>), by
/// project: a project's are read again only once a file of it changed (<see cref="Stale"/>), and every project's at first.
/// A read's answer clears only what no change made due again while it read (<see cref="Changes"/>).</summary>
public sealed class RouteCatalog
{
    private readonly Dictionary<string, IReadOnlyList<WebRoute>> byProject = new(PathNames.Comparer);

    /// <summary>The projects whose routes are due again, each with the change that last made it so.</summary>
    private readonly Dictionary<string, int> stale = new(PathNames.Comparer);
    private bool unread = true;

    /// <summary>The change that last made every project due.</summary>
    private int forgotten;

    /// <summary>Every route read, by project then as their code declares them.</summary>
    public IReadOnlyList<WebRoute> All { get; private set; } = [];

    /// <summary>Whether the routes have been read once.</summary>
    public bool HasRead => !unread;

    /// <summary>Whether a read is due: none yet, or a project's file changed since.</summary>
    public bool Wants => unread || stale.Count > 0;

    /// <summary>How many changes have made routes due, each <see cref="Stale"/> and <see cref="Forget"/>.</summary>
    public int Changes { get; private set; }

    /// <summary>The web projects read, with their routes or none.</summary>
    public IEnumerable<string> Projects => byProject.Keys;

    /// <summary>What the next read asks for: every project (none named) until the first, then the stale ones; as of the
    /// changes made so far.</summary>
    public RoutesAsked ToRead() => new(unread ? [] : [.. stale.Keys], Changes);

    /// <summary>A read's answer for what it asked: the projects asked for that it no longer finds are gone, and what a change
    /// made due after it was asked stays due.</summary>
    public void Took(RoutesFound found, RoutesAsked asked)
    {
        if (asked.Projects.Count == 0)
        {
            byProject.Clear();
            unread = forgotten > asked.Changes;
        }
        foreach (string project in asked.Projects.Count == 0 ? [.. stale.Keys] : asked.Projects)
        {
            byProject.Remove(project);
            if (stale.TryGetValue(project, out int since) && since <= asked.Changes)
            {
                stale.Remove(project);
            }
        }
        foreach (string project in found.Projects)
        {
            byProject[project] = [.. found.Routes.Where(r => PathNames.Same(r.Project, project))];
        }
        All = [.. byProject.OrderBy(p => Path.GetFileNameWithoutExtension(p.Key), StringComparer.OrdinalIgnoreCase).SelectMany(p => p.Value)];
    }

    /// <summary>A project's file changed: its routes are read again at the next read (none for a project that is no web
    /// project, which the read finds out cheaply).</summary>
    public void Stale(string project) => stale[project] = ++Changes;

    /// <summary>Every project is read again, as at first (a new language server, a project come or gone).</summary>
    public void Forget()
    {
        unread = true;
        forgotten = ++Changes;
        stale.Clear();
    }

    public bool IsWeb(string project) => byProject.ContainsKey(project);
}

/// <summary>What a read of the routes asks for (every project when it names none), and how many changes it reads as of
/// (<see cref="RouteCatalog.Changes"/>).</summary>
public sealed record RoutesAsked(IReadOnlyList<string> Projects, int Changes);

/// <summary>How a route and an .http file's requests meet: a request calls a route when its method is one the route takes
/// and its URL's path, after its host, fits the route's template (a parameter's segment taking any value, a
/// <c>{{variable}}</c> too); and the request the gutter's send makes for a route that none calls.</summary>
public static partial class RouteRequests
{
    /// <summary>The method a route's request is sent with: its first, else GET (POST for a hub's negotiation).</summary>
    public static string MethodOf(WebRoute route) => route.Kind == RouteKinds.SignalR ? "POST" : route.Methods.FirstOrDefault() ?? "GET";

    /// <summary>A route's title: "GET /todos/{id}".</summary>
    public static string Title(WebRoute route) =>
        $"{(route.Kind == RouteKinds.SignalR ? "HUB" : route.Methods.Count == 0 ? "ANY" : string.Join("|", route.Methods))} {route.Template}{(route.Query is { } query ? "?" + query : string.Empty)}";

    /// <summary>Whether a request sent from the gutter can reach the route: a gRPC method needs a gRPC client.</summary>
    public static bool Sendable(WebRoute route) => route.Kind != RouteKinds.Grpc;

    /// <summary>Whether a request calls a route.</summary>
    public static bool Calls(EndpointRequest request, WebRoute route)
    {
        if (route.Kind == RouteKinds.Grpc || (route.Methods.Count > 0 && !route.Methods.Contains(request.Method, StringComparer.OrdinalIgnoreCase)
            && !(route.Kind == RouteKinds.SignalR && request.Method == "POST")))
        {
            return false;
        }
        if (PathOf(request.Url) is not { } path)
        {
            return false;
        }
        string template = route.Kind == RouteKinds.SignalR && path.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase) ? route.Template.TrimEnd('/') + "/negotiate" : route.Template;
        return Fits(template, path) && (route.Query is not { } query || request.Url.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The path a request's URL asks for: after a leading <c>{{variable}}</c> or <c>scheme://host</c>, before its
    /// query; null when it has neither and is no path.</summary>
    public static string? PathOf(string url)
    {
        string rest = url.Trim();
        if (rest.StartsWith("{{", StringComparison.Ordinal))
        {
            int close = rest.IndexOf("}}", StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }
            rest = rest[(close + 2)..];
        }
        else if (rest.IndexOf("://", StringComparison.Ordinal) is int scheme and > 0)
        {
            int slash = rest.IndexOf('/', scheme + 3);
            rest = slash < 0 ? "/" : rest[slash..];
        }
        else if (!rest.StartsWith('/'))
        {
            return null;
        }
        int end = rest.IndexOfAny(['?', '#']);
        return "/" + (end < 0 ? rest : rest[..end]).Trim('/');
    }

    /// <summary>Whether a path fits a template, segment by segment as ASP.NET Core's matcher reads it: a literal by its
    /// letters in any case, a parameter by any value, an optional one by none, a catch-all by the rest.</summary>
    public static bool Fits(string template, string path)
    {
        string[] want = Segments(template);
        string[] got = Segments(path);
        for (int i = 0; i < want.Length; i++)
        {
            string segment = want[i];
            if (segment.StartsWith("{*", StringComparison.Ordinal) || segment.StartsWith("{**", StringComparison.Ordinal))
            {
                return true;
            }
            if (i >= got.Length)
            {
                return want[i..].All(w => w.StartsWith('{') && (w.EndsWith("?}", StringComparison.Ordinal) || w.Contains('=', StringComparison.Ordinal)));
            }
            if (!SegmentFits(segment, got[i]))
            {
                return false;
            }
        }
        return got.Length == want.Length;
    }

    private static string[] Segments(string path) => path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static bool SegmentFits(string segment, string got)
    {
        if (!segment.Contains('{', StringComparison.Ordinal))
        {
            return string.Equals(segment, got, StringComparison.OrdinalIgnoreCase);
        }
        // A segment of parameters and literals ("{name}.{ext}"): each parameter takes some characters, a {{variable}} any.
        string pattern = "^" + string.Concat(Parameter().Split(segment).Select((part, i) => i % 2 == 1 ? ".+" : Regex.Escape(part))) + "$";
        return Regex.IsMatch(got, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"(\{[^{}]+\})")]
    private static partial Regex Parameter();

    /// <summary>The variable a project's requests name its address by, as <c>dotnet new webapi</c>'s .http file does.</summary>
    public static string HostVariable(string project) => Path.GetFileNameWithoutExtension(project) + "_HostAddress";

    /// <summary>The .http file a project's generated requests go to: the one beside it named for it.</summary>
    public static string FileFor(string project) => Path.Combine(Path.GetDirectoryName(project)!, Path.GetFileNameWithoutExtension(project) + ".http");

    /// <summary>A route's request as the gutter writes it into its project's .http file: a separator naming its handler,
    /// a variable for each route value the file does not set yet, the method and the URL from the project's address with
    /// each route value as a <c>{{variable}}</c>, an Accept header, and a JSON body shaped from the body's type. A file
    /// made for it begins with the address variable, at <paramref name="address"/>.</summary>
    public static string Entry(WebRoute route, string existing, string? address)
    {
        StringBuilder written = new();
        string host = HostVariable(route.Project);
        HashSet<string> set = [.. HttpFile.Parse(string.Empty, existing).Variables.Keys];
        if (existing.Trim().Length == 0)
        {
            written.Append('@').Append(host).Append(" = ").Append(address ?? "http://localhost:5000").Append("\n\n");
            set.Add(host);
        }
        else if (!existing.EndsWith('\n'))
        {
            written.Append('\n');
        }
        if (existing.Trim().Length > 0 && !existing.TrimEnd().EndsWith("###", StringComparison.Ordinal))
        {
            written.Append("###\n");
        }
        written.Append("# ").Append(route.Kind).Append(": ").Append(route.Handler).Append('\n');
        // A file of the person's own that names the project's address otherwise gains the variable the request uses.
        if (set.Add(host))
        {
            written.Append('@').Append(host).Append(" = ").Append(address ?? "http://localhost:5000").Append('\n');
        }
        foreach (RouteParameter parameter in route.Parameters.Where(p => p.Source is "route" or "query"))
        {
            if (set.Add(parameter.Name))
            {
                written.Append('@').Append(parameter.Name).Append(" = ").Append(SampleValue(parameter)).Append('\n');
            }
        }
        string path = Parameter().Replace(route.Template, match => "{{" + NameIn(match.Value) + "}}");
        // The query a route takes: a page's handler as it is, each query parameter as the variable just set.
        List<string> asked = [.. route.Query is { } named ? [named] : Array.Empty<string>(),
            .. route.Parameters.Where(p => p.Source == "query").Select(p => $"{Uri.EscapeDataString(p.Name)}={{{{{p.Name}}}}}")];
        string query = route.Kind == RouteKinds.SignalR ? "?negotiateVersion=1" : asked.Count > 0 ? "?" + string.Join("&", asked) : string.Empty;
        if (route.Kind == RouteKinds.SignalR)
        {
            path = path.TrimEnd('/') + "/negotiate";
        }
        written.Append(MethodOf(route)).Append(" {{").Append(host).Append("}}").Append(path).Append(query).Append('\n');
        written.Append("Accept: ").Append(route.Kind is RouteKinds.RazorPage or RouteKinds.Blazor ? "text/html" : "application/json").Append('\n');
        if (route.Body is { } body)
        {
            written.Append("Content-Type: application/json\n\n").Append(body).Append('\n');
        }
        written.Append("\n###\n");
        return written.ToString();
    }

    /// <summary>A parameter's name in a template's braces: <c>{*rest:int=1?}</c>'s is rest.</summary>
    private static string NameIn(string braced) => braced.Trim('{', '}').TrimStart('*').Split(':', '=', '?')[0];

    /// <summary>A value a route parameter takes, by its constraints or type, for the variable the request names it by.</summary>
    private static string SampleValue(RouteParameter parameter)
    {
        string kind = (parameter.Constraints ?? parameter.Type ?? string.Empty).ToLowerInvariant();
        return kind.Contains("guid", StringComparison.Ordinal) ? Guid.Empty.ToString()
            : kind.Contains("int", StringComparison.Ordinal) || kind.Contains("long", StringComparison.Ordinal) || kind.Contains("decimal", StringComparison.Ordinal)
                || kind.Contains("double", StringComparison.Ordinal) || kind.Contains("float", StringComparison.Ordinal) || kind.Contains("min(", StringComparison.Ordinal) ? "1"
            : kind.Contains("bool", StringComparison.Ordinal) ? "true"
            : kind.Contains("datetime", StringComparison.Ordinal) || kind.Contains("dateonly", StringComparison.Ordinal) ? "2000-01-01"
            : parameter.Name;
    }

    /// <summary>A request's URL sent to another address: its leading <c>{{variable}}</c> or <c>scheme://host</c> replaced, a
    /// path alone put after it.</summary>
    public static string WithHost(string url, string address)
    {
        string trimmed = url.Trim();
        string root = address.TrimEnd('/');
        if (trimmed.StartsWith("{{", StringComparison.Ordinal) && trimmed.IndexOf("}}", StringComparison.Ordinal) is int close and > 0)
        {
            return root + trimmed[(close + 2)..];
        }
        if (trimmed.IndexOf("://", StringComparison.Ordinal) is int scheme and > 0)
        {
            int slash = trimmed.IndexOf('/', scheme + 3);
            return root + (slash < 0 ? string.Empty : trimmed[slash..]);
        }
        return root + (trimmed.StartsWith('/') ? trimmed : "/" + trimmed);
    }

    /// <summary>The address a program serves first, by the URLs it was given (ASP.NET Core's <c>ASPNETCORE_URLS</c> or a
    /// profile's <c>applicationUrl</c>), HTTP's before HTTPS's: a wildcard host read as this machine's.</summary>
    public static string? AddressOf(string? urls)
    {
        string[] each = (urls ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? chosen = each.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? each.FirstOrDefault();
        if (chosen is null)
        {
            return null;
        }
        return Wildcard().Replace(chosen, "${scheme}://localhost${port}").TrimEnd('/');
    }

    [GeneratedRegex(@"^(?<scheme>https?)://(?:\*|\+|0\.0\.0\.0|\[::\])(?<port>:\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex Wildcard();

    /// <summary>The line a request is on in the text of its file as given.</summary>
    public static EndpointRequest? RequestAt(string path, string text, int line) =>
        HttpFile.Parse(path, text).Requests.LastOrDefault(r => r.Line <= line);

    /// <summary>Where a route is written, as the editor counts the place (the server's place converted by the caller).</summary>
    public static string Where(WebRoute route, string? root) =>
        string.Create(CultureInfo.InvariantCulture, $"{PathNames.Shown(route.Path, root)}:{route.At.Line + 1}");
}
