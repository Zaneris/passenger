using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Going between a web route and the .http requests that call it, from the window's routes: Go to Declaration from
/// a request to its route, Find Usages from a route to its requests.</summary>
internal sealed class HttpAssist(AssistContext context, CodeNavigation navigation) : LanguageAssist
{
    /// <summary>The routes the request on a line (the editor's, from zero) of an .http file calls.</summary>
    public Func<string, int, IReadOnlyList<LspLocation>>? HttpRouteTargets { get; set; }

    /// <summary>The .http requests that call the route a file declares at an offset, with the route's words; null where it
    /// declares none.</summary>
    public Func<string, int, (string Title, IReadOnlyList<LspLocation> Requests)?>? RouteCallers { get; set; }

    /// <summary>Whether the file shown is an .http or .rest file, whose requests go to their routes.</summary>
    public bool IsHttpFile(OpenDocument? file) => file is not null && HttpRouteTargets is not null && Endpoints.HttpFile.Is(file.Path);

    public override Task<bool> DefinitionAsync(int offset) => Task.FromResult(HttpGoTo(offset));

    public override Task<Declared?> DeclaredAsync(OpenDocument document, int offset) => Task.FromResult(RoutesOf(document, offset));

    public override Task<bool> UsagesAsync(int offset) => Task.FromResult(RouteUsages(offset));

    /// <summary>Go to Declaration in an .http file: the route the request at the caret calls.</summary>
    private bool HttpGoTo(int offset)
    {
        if (context.Editors.Active is not { } shown || RoutesOf(shown, offset) is not { } routes)
        {
            return false;
        }
        navigation.Go(routes, offset);
        return true;
    }

    /// <summary>The routes the request at an offset of an .http file calls; null for any other file.</summary>
    private Declared? RoutesOf(OpenDocument document, int offset)
    {
        if (!IsHttpFile(document))
        {
            return null;
        }
        TextSnapshot text = document.Text.Snapshot;
        int line = text.GetPosition(Math.Min(offset, text.Length)).Line;
        return new Declared(HttpRouteTargets!(document.Path, line), "No route of the folder's web projects takes this request") { Title = "Routes" };
    }

    /// <summary>Find Usages on a route's declaration: the .http requests that call it.</summary>
    private bool RouteUsages(int offset)
    {
        if (context.Editors.Active is not { } shown || RouteCallers?.Invoke(shown.Path, offset) is not { } callers)
        {
            return false;
        }
        if (callers.Requests.Count == 0)
        {
            context.Say($"No .http request calls {callers.Title}");
        }
        else
        {
            context.ShowUsages($"Requests to {callers.Title}", callers.Requests);
        }
        return true;
    }
}
