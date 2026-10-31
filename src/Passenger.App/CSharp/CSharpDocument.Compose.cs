using System.Text.Json;
using Passenger.App.Containers;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.CSharp;

public sealed partial class CSharpDocument
{
    private const string ComposeDiagnosticSource = "Passenger.Compose";

    /// <summary>Whether an open compose file's check found a connection string its .NET service's environment does not set
    /// that this file reads.</summary>
    internal bool ReadsForCompose => Gaps().Count > 0;

    /// <summary>The open compose files' connection-string gaps this file reads, as their checks last found them.</summary>
    private IReadOnlyList<ComposeGap> Gaps() =>
        [.. (service.ComposeGaps?.Invoke() ?? []).Where(g => g.ReadIn.Any(path => PathNames.Same(path, document.Path)))];

    /// <summary>What the open compose files find missing changed: pulled again when the file reads such a name or was marked
    /// for one.</summary>
    internal void ComposeChanged()
    {
        if (ReadsForCompose || diagnostics.Placed.Any(d => d.Source == ComposeDiagnosticSource))
        {
            Pull(TimeSpan.Zero);
        }
    }

    /// <summary>The gaps the file's compose warnings mark on a server's line of the text as it stands (any line for null): each
    /// with where its read is and the gap as the compose file's check last found it, from which the fix is worked out.</summary>
    internal IReadOnlyList<(TextRange Range, ComposeGap Gap)> ComposeGapsOn(int? line)
    {
        IReadOnlyList<ComposeGap> gaps = service.ComposeGaps?.Invoke() ?? [];
        List<(TextRange, ComposeGap)> marked = [];
        foreach ((Diagnostic diagnostic, TextRange range) in diagnostics.PlacedRanges)
        {
            if (diagnostic.Source == ComposeDiagnosticSource
                && (line is not { } on || diagnostic.Range.Start.Line <= on && on <= diagnostic.Range.End.Line)
                && diagnostic.Data is { ValueKind: JsonValueKind.Object } data
                && JsonMembers.Text(data, "compose") is { } compose && JsonMembers.Text(data, "service") is { } named && JsonMembers.Text(data, "name") is { } name
                && gaps.FirstOrDefault(g => PathNames.Same(g.Compose, compose) && g.Service == named && g.Name == name) is { } gap)
            {
                marked.Add((range, gap));
            }
        }
        return marked;
    }

    /// <summary>The open compose files' warnings of a connection string a .NET service reads that its environment does not set,
    /// on each place this file reads it: the places asked of Passenger's extension now (<see cref="Handlers.ConnectionReads"/>,
    /// the reading the compose check made), for the text as it stands; none for a file no gap is read in.</summary>
    private async Task<IReadOnlyList<Diagnostic>> ComposeFindingsAsync(CancellationToken cancel)
    {
        IReadOnlyList<ComposeGap> gaps = Gaps();
        if (gaps.Count == 0 || service.Session is not { } roslyn)
        {
            return [];
        }
        ConnectionReadsFound reads;
        try
        {
            reads = await roslyn.ExtensionAsync<ConnectionReadsFound>(Handlers.ConnectionReads,
                new ConnectionReadsRequest([.. gaps.Select(g => g.Project).Distinct(PathNames.Comparer)]), cancel);
        }
        catch (LanguageServerFailed)
        {
            return [];
        }
        List<Diagnostic> found = [];
        foreach (ConnectionRead read in reads.Reads.Where(r => PathNames.Same(r.Path, document.Path)))
        {
            foreach (ComposeGap gap in gaps.Where(g => PathNames.Same(g.Project, read.Project) && g.Name.Equals(read.Name, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new Diagnostic
                {
                    Range = new LspRange(new LspPosition(read.At.Line, read.At.Character), new LspPosition(read.End.Line, read.End.Character)),
                    Severity = gap.Weak ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning,
                    Source = ComposeDiagnosticSource,
                    Message = $"{Path.GetFileName(gap.Compose)}: {gap.Said}",
                    Data = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["compose"] = gap.Compose, ["service"] = gap.Service, ["name"] = gap.Name }),
                });
            }
        }
        return found;
    }
}
