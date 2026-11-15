using Passenger.App.Navigation;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Editors;

/// <summary>A .feature file's outline from Gherkin's own reading of it (<see cref="FeatureReading"/>): its feature, its
/// rules under it, each background, scenario and outline under its rule or the feature, and an outline's examples under
/// it, each named by its title after its keyword, read off the window's thread in the language its project reads it in.</summary>
internal sealed class FeatureOutline(TextDocument document, Func<string?> projectFolder) : FileOutline
{
    public override Task<Outline<SymbolRow>> ReadAsync(string path, Outline<SymbolRow>? before, CancellationToken cancel)
    {
        TextSnapshot text = document.Snapshot;
        string? folder = projectFolder();
        return BackgroundWork.Run(() => Of(text, path, FeatureLanguage.Of(folder), before, cancel), cancel);
    }

    /// <summary>The outline of a feature's text, its rows leading to <paramref name="path"/>.</summary>
    public static Outline<SymbolRow> Of(TextSnapshot text, string path, string language, Outline<SymbolRow>? before = null, CancellationToken cancel = default)
    {
        FeatureReading reading = FeatureReading.Read(text.ToString(), language);
        if (reading.Feature is not { } feature)
        {
            return Outline<SymbolRow>.Of<Part>([], part => part.Children, (part, depth) => (part.Row, part.First, part.Last), before, cancel);
        }
        Part root = new(Row(feature.Keyword, feature.Title, SymbolKind.Class, feature.Line, feature.KeywordColumn), feature.Line, feature.EndLine);
        Dictionary<FeatureHeader, Part> rules = [];
        foreach (FeatureHeader rule in reading.Rules)
        {
            Part part = new(Row(rule.Keyword, rule.Title, SymbolKind.Namespace, rule.Line, rule.KeywordColumn), rule.Line, rule.EndLine);
            rules[rule] = part;
            root.Children.Add(part);
        }
        foreach (FeatureScenario scenario in reading.Scenarios)
        {
            Part part = new(Row(scenario.Keyword, scenario.Title, SymbolKind.Method, scenario.Line, scenario.KeywordColumn), scenario.Line, scenario.EndLine);
            part.Children.AddRange(scenario.Examples.Select(e => new Part(Row(e.Keyword, e.Title, SymbolKind.Field, e.Line, e.KeywordColumn), e.Line, e.EndLine)));
            (scenario.Rule is { } rule && rules.TryGetValue(rule, out Part? under) ? under : root).Children.Add(part);
        }
        return Outline<SymbolRow>.Of<Part>([root], part => part.Children, (part, depth) => (part.Row with { Depth = depth, EndLine = part.Last }, part.First, part.Last), before, cancel);

        SymbolRow Row(string keyword, string title, SymbolKind kind, int line, int column) =>
            new(title.Length > 0 ? title : keyword, title.Length > 0 ? keyword : null, null, kind, path, new TextPosition(line, column));
    }

    private sealed class Part(SymbolRow row, int first, int last)
    {
        public SymbolRow Row { get; } = row;

        public int First { get; } = first;

        public int Last { get; } = last;

        public List<Part> Children { get; } = [];
    }
}
