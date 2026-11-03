using Passenger.App.Endpoints;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>A web project's requests from its app's OpenAPI document: the document read from the app running on this machine,
/// else from what its build wrote (<see cref="OpenApiSources"/>), and a request written into the project's .http file for
/// each operation none of its requests calls yet, as one command of Undo.</summary>
public partial class MainWindow
{
    /// <summary>The web project whose requests Generate Requests from OpenAPI writes: the Endpoints window's row's, else the
    /// folder's one web project.</summary>
    private string? OpenApiProject() => endpoints?.ChosenProject
        ?? (Projects.Where(p => p.IsWeb && p.Exists).ToList() is [var only] ? only.Path : null);

    /// <summary>Generate Requests from OpenAPI for a web project. Null once written (or none were wanted), else why not.</summary>
    public async Task<string?> GenerateFromOpenApiAsync(string project)
    {
        string name = Path.GetFileNameWithoutExtension(project);
        if (work.Begin(AppActions.GenerateOpenApiRequests, $"Reading {name}'s OpenAPI document…") is not { } working)
        {
            return Said(ActionWork.Busy(work.Of(AppActions.GenerateOpenApiRequests)!));
        }
        using (working)
        using (BackgroundWork.Scope())
        {
            ProjectSession current = session;
            ProjectModel? model = Projects.FirstOrDefault(p => PathNames.Same(p.Path, project));
            string? address = await RunningAddressAsync(project);
            OpenApiDocument? document = null;
            if (address is not null && Uri.TryCreate(address, UriKind.Absolute, out Uri? running))
            {
                IReadOnlyList<string> code = await CodeOfAsync(model);
                document = current.OpenApiNames.Of(project) is { } named ? await OpenApiSources.FromNamedAsync(named, running, working.Stopping)
                    : await OpenApiSources.FromAppAsync(running, OpenApiSources.MappedIn(code), working.Stopping);
            }
            if (document is null && model is not null && !working.IsStopped)
            {
                working.Say($"Reading the OpenAPI document {name}'s build wrote…");
                try
                {
                    document = await OpenApiSources.FromBuildAsync(model, current.Workspace, working.Stopping);
                }
                catch (Run.LaunchException unread)
                {
                    return Told(false, $"{name}'s OpenAPI document could not be found: {unread.Message}");
                }
            }
            if (session != current || current.IsEnded || working.IsStopped)
            {
                return Told(false, current.IsEnded ? Refusals.SolutionClosed : $"Generating requests from {name}'s OpenAPI document was stopped");
            }
            if (document is null)
            {
                return Told(false, $"{name} has no OpenAPI document to read: run it, where MapOpenApi serves one at {OpenApiSources.Defaults[0]}, "
                    + "or have its build write one (Microsoft.Extensions.ApiDescription.Server)");
            }
            string path = PathNames.Canonical(RouteRequests.FileFor(project));
            int written = 0;
            (string? now, _, string? refused) = await AppendRequestsAsync(path, existing =>
            {
                IReadOnlyList<OpenApiOperation> uncalled = OpenApiRequests.Uncalled(document, existing);
                written = uncalled.Count;
                return uncalled.Count == 0 ? string.Empty : OpenApiRequests.Entries(document, uncalled, existing, RouteRequests.HostVariable(project), address ?? ProfileAddress(project));
            }, "Generate Requests from OpenAPI");
            if (now is null)
            {
                return Told(false, $"No request could be written from {name}'s OpenAPI document in {Path.GetFileName(path)}: {refused}");
            }
            return Told(true, written == 0
                ? $"Every operation of {name}'s OpenAPI document has a request in {PathNames.Shown(path, session.Folder)} already"
                : $"Wrote {Plural.Of(written, "request")} from {name}'s OpenAPI document in {PathNames.Shown(path, session.Folder)}");

            string? Told(bool succeeded, string said)
            {
                working.Tell(succeeded, said);
                status.Text = said;
                return succeeded ? null : said;
            }
        }

        string Said(string why)
        {
            status.Text = why;
            return why;
        }
    }

    /// <summary>The OpenAPI document of the app a request went to (<see cref="EndpointsParts.DocumentOf"/>): of a project the
    /// folder runs at that address whose code has it describe itself, asked of it there; null for any other.</summary>
    private async Task<OpenApiDocument?> RunningDocumentAsync(Uri sent, CancellationToken cancel)
    {
        string origin = sent.GetLeftPart(UriPartial.Authority);
        foreach (string project in session.Running.Select(p => p.Project).OfType<string>().Distinct(PathNames.Comparer).ToList())
        {
            if (await RunningAddressAsync(project) is { } address && Uri.TryCreate(address, UriKind.Absolute, out Uri? at)
                && string.Equals(at.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
            {
                if (session.OpenApiNames.Of(project) is { } named)
                {
                    return await OpenApiSources.FromNamedAsync(named, sent, cancel);
                }
                IReadOnlyList<string> code = await CodeOfAsync(Projects.FirstOrDefault(p => PathNames.Same(p.Path, project)));
                return OpenApiSources.Describes(code) ? await OpenApiSources.FromAppAsync(sent, OpenApiSources.MappedIn(code), cancel) : null;
            }
        }
        return null;
    }

    /// <summary>Name OpenAPI Document for a web project: the person asked where its app serves the document its code names
    /// nowhere, the answer kept with the folder; its answers are held to that document from then on.</summary>
    public async Task NameOpenApiDocumentAsync(string project)
    {
        ProjectSession current = session;
        string name = Path.GetFileNameWithoutExtension(project);
        OpenApiDocumentDialog dialog = new(name, current.OpenApiNames.Of(project));
        NamingDocument = dialog;
        try
        {
            if (await dialog.ShowDialog<bool>(this) && dialog.Document is { } document && session == current && !current.IsEnded)
            {
                current.OpenApiNames.Name(project, document);
                status.Text = $"{name}'s answers are held to the OpenAPI document at {document}";
            }
        }
        finally
        {
            NamingDocument = null;
        }
    }

    /// <summary>The Name OpenAPI Document dialog open, for the tests.</summary>
    public OpenApiDocumentDialog? NamingDocument { get; private set; }

    /// <summary>The text of a project's C# files, read off the window's thread, for how its app describes itself.</summary>
    private Task<IReadOnlyList<string>> CodeOfAsync(ProjectModel? model)
    {
        string[] code = model is null ? [] : [.. FolderWalk.ProjectFiles(model, Projects).Where(f => PathNames.HasExtension(f, [".cs"]))];
        return BackgroundWork.Run(() => (IReadOnlyList<string>)[.. code.Select(ReadCode)]);
    }

    /// <summary>A C# file's text for the paths its code gives MapOpenApi: none for one unread or too large to be a program's.</summary>
    private static string ReadCode(string path)
    {
        try
        {
            return new FileInfo(path).Length > 1024 * 1024 ? string.Empty : File.ReadAllText(path);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
