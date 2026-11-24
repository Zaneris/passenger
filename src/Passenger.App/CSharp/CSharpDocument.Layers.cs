using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>What a warning of a use breaking the folder's layer rules carries for its fix: the layer of the code using and
/// the layer of the code used.</summary>
internal sealed record LayerMark(string From, string To);

public sealed partial class CSharpDocument
{
    private const string LayersDiagnosticSource = "Passenger.Layers";

    /// <summary>The file's uses that break the folder's layer rules (<see cref="CSharpService.Layers"/>), each a warning where it
    /// is written, by Passenger's extension's binding of the code; none while the folder has no rules.</summary>
    internal async Task<IReadOnlyList<Diagnostic>> LayerFindingsAsync(LspDocument lsp, CancellationToken cancel)
    {
        if (lsp.IsRazor || service.Layers is not { } rules)
        {
            return [];
        }
        IReadOnlyList<LayerBreak> broken;
        try
        {
            broken = await lsp.LayersBrokenAsync(rules, cancel);
        }
        catch (LanguageServerFailed)
        {
            // The file's other findings stand; the rules are checked again at its next pull.
            return [];
        }
        return [.. broken.Select(b => new Diagnostic
        {
            Range = new LspRange(new LspPosition(b.Start.Line, b.Start.Character), new LspPosition(b.End.Line, b.End.Character)),
            Severity = DiagnosticSeverity.Warning,
            Source = LayersDiagnosticSource,
            Message = Said(rules, b),
            Data = DataOf(new LayerMark(b.From, b.To)),
        })];

        static string Said(LayerRules rules, LayerBreak broken)
        {
            IReadOnlyList<string> may = rules.Layers.FirstOrDefault(l => l.Name == broken.From)?.MayUse ?? [];
            string lets = may.Count == 0 ? "no other layer" : Core.Plural.Listed(may);
            return $"The {broken.From} layer may not use the {broken.To} layer, which {broken.Used} is in: {LayerRules.FileName} lets it use {lets}";
        }
    }

    /// <summary>The layers the file's warnings say a use breaks the rules between, on a server's line of the text as it stands
    /// (any line for null), each with where it is written.</summary>
    internal IReadOnlyList<(TextRange Range, string From, string To)> LayersBrokenOn(int? line) =>
        [.. OwnOn<LayerMark>(LayersDiagnosticSource, line).Select(m => (m.Range, m.Data.From, m.Data.To))];
}
