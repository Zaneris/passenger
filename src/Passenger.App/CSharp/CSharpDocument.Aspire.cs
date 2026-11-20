using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>What an Aspire warning of a reference an AppHost lacks carries for its fix: the service and the resource.</summary>
internal sealed record AspireReferenceMark(string Service, string Resource);

/// <summary>What an Aspire warning of a connection string no resource names carries for its fix: the name read and the
/// resources' names near it, nearest first (none where the name is not written plainly, so cannot be changed).</summary>
internal sealed record AspireConnectionMark(string Connection, IReadOnlyList<string> Near);

/// <summary>What an Aspire warning of a parameter with no value carries for its fix: its name and whether it is a secret.</summary>
internal sealed record AspireParameterMark(string Parameter, bool Secret);

public sealed partial class CSharpDocument
{
    private const string AspireDiagnosticSource = "Passenger.Aspire";

    /// <summary>What the file is to Aspire now: only an AppHost's and a service's ask Passenger's extension for its help.</summary>
    internal Aspire.AspireRole AspireRole => service.AspireRoleOf?.Invoke(document.Path) ?? Aspire.AspireRole.None;

    /// <summary>What the file was to Aspire as its last pull asked for Aspire's findings; null before one has.</summary>
    private Aspire.AspireRole? pulledAs;

    /// <summary>The AppHost it is a service of changed: its Aspire warnings go at once and the file's diagnostics are pulled again.</summary>
    internal void AspireChanged()
    {
        ClearAspireDiagnostics();
        Pull(pause);
    }

    /// <summary>The folder's projects as MSBuild evaluated them changed what the file is to Aspire since its last pull asked
    /// (its AppHost evaluated after the file was shown, a reference to its project added or taken out): pulled again as
    /// that role's, as an AppHost's edit pulls its services' files.</summary>
    internal void AspireRoleChanged()
    {
        if (pulledAs is { } asked && asked != AspireRole)
        {
            AspireChanged();
        }
    }

    /// <summary>The references the file's Aspire warnings say its AppHost lacks, on a server's line of the text as it stands
    /// (any line for null): each with the place it is warned at, the service and the resource, from which the WithReference
    /// fix is worked out as it is chosen.</summary>
    internal IReadOnlyList<(TextRange Range, string Service, string Resource)> AspireMissingOn(int? line) =>
        [.. OwnOn<AspireReferenceMark>(AspireDiagnosticSource, line).Select(m => (m.Range, m.Data.Service, m.Data.Resource))];

    /// <summary>The connection strings the file's Aspire warnings say it reads by a name no resource of its AppHost has, on a
    /// server's line of the text as it stands (any line for null): each with where its name is, the name and the names of the
    /// AppHost's resources near it, nearest first (none where the name is not written plainly, so cannot be changed).</summary>
    internal IReadOnlyList<(TextRange Range, string Name, IReadOnlyList<string> Near)> AspireUnknownOn(int? line) =>
        [.. OwnOn<AspireConnectionMark>(AspireDiagnosticSource, line).Select(u => (u.Range, u.Data.Connection, u.Data.Near))];

    /// <summary>The parameters the file's Aspire warnings say have no value in its AppHost's configuration, on a server's line
    /// of the text as it stands (any line for null): each with where its name is, its name and whether it is a secret.</summary>
    internal IReadOnlyList<(TextRange Range, string Name, bool Secret)> AspireUnsetOn(int? line) =>
        [.. OwnOn<AspireParameterMark>(AspireDiagnosticSource, line).Select(p => (p.Range, p.Data.Parameter, p.Data.Secret))];

    /// <summary>An AppHost's parameters that take their value from its configuration alone and have none there (the settings
    /// files of the environments it runs in, its user secrets, as the editor has them when open), each a warning on its name;
    /// the files are read off the UI thread, values never.</summary>
    private async Task<IReadOnlyList<Diagnostic>> AspireUnsetParametersAsync(LspDocument lsp, CancellationToken cancel)
    {
        if (service.AspireAppHostOf?.Invoke(document.Path) is not { } host)
        {
            return [];
        }
        try
        {
            AspireParametersFound found = await lsp.ExtensionAsync<AspireParametersFound>(Handlers.AspireParameters, new AspireParametersRequest(), cancel);
            if (found.Parameters.Count == 0)
            {
                return [];
            }
            Editors.OpenTexts texts = service.OpenTexts();
            AspireParameter[] unset = await BackgroundWork.Run(() => found.Parameters.Where(p => !Aspire.AspireParameters.HasValue(host, p.Name, texts.Text)).ToArray());
            return [.. unset.Select(p => new Diagnostic
            {
                Range = new LspRange(new LspPosition(p.Start.Line, p.Start.Character), new LspPosition(p.End.Line, p.End.Character)),
                Severity = DiagnosticSeverity.Warning,
                Source = AspireDiagnosticSource,
                Message = $"Parameter '{p.Name}' has no value in the AppHost's configuration, so it asks for one as it runs. "
                    + (p.Secret ? $"Set Parameters:{p.Name} in its user secrets." : $"Set Parameters:{p.Name} in its appsettings.json."),
                Data = DataOf(new AspireParameterMark(p.Name, p.Secret)),
            })];
        }
        catch (LanguageServerFailed)
        {
            return [];
        }
    }

    private void ClearAspireDiagnostics()
    {
        if (diagnostics.Drop(d => d.Source == AspireDiagnosticSource, DiagnosticMarks.Marker) is { } markers)
        {
            document.Markers = markers;
            service.FileProblemsChanged(document);
        }
    }

    private static async Task<IReadOnlyList<Diagnostic>> AspireDiagnosticsAsync(LspDocument lsp, CancellationToken cancel)
    {
        try
        {
            AspireHelp help = await lsp.ExtensionAsync<AspireHelp>(Handlers.AspireCodeHelp, new AspireHelpRequest(null), cancel);
            // The service's own settings may give a name no resource has, in the environment an AppHost runs its services in.
            AspireUnknownConnection[] unknown = help.Unknown is { Count: > 0 } named
                ? await BackgroundWork.Run(() => named.Where(u => BackingService.Configured(Path.GetDirectoryName(u.Project)!, u.Name, "Development") is null).ToArray())
                : [];
            return [.. unknown.Select(Unknown), .. help.Missing.Select(m => new Diagnostic
            {
                Range = new LspRange(new LspPosition(m.Start.Line, m.Start.Character), new LspPosition(m.End.Line, m.End.Character)),
                Severity = DiagnosticSeverity.Warning,
                Source = AspireDiagnosticSource,
                Message = $"AppHost resource '{m.Service}' does not reference '{m.Resource}'. Add WithReference({m.Resource}) to '{m.Service}'.",
                Data = DataOf(new AspireReferenceMark(m.Service, m.Resource)),
            })];
        }
        catch (LanguageServerFailed)
        {
            return [];
        }
    }

    /// <summary>The warning of a connection string a service reads by a name no resource of its AppHost has and its
    /// settings do not give, with the declared names near it, which its fix changes it to
    /// (<see cref="Spelling.Near"/>).</summary>
    private static Diagnostic Unknown(AspireUnknownConnection read)
    {
        IReadOnlyList<string> near = Spelling.Near(read.Name, read.Declared);
        return new Diagnostic
        {
            Range = new LspRange(new LspPosition(read.Start.Line, read.Start.Character), new LspPosition(read.End.Line, read.End.Character)),
            Severity = DiagnosticSeverity.Warning,
            Source = AspireDiagnosticSource,
            Message = $"No resource of the AppHost is named '{read.Name}', and the service's appsettings give no connection string of that name."
                + (near.Count > 0 ? $" Did you mean '{near[0]}'?" : string.Empty),
            Data = DataOf(new AspireConnectionMark(read.Name, read.Exact ? near : [])),
        };
    }
}
