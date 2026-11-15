using Passenger.App.Bdd;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.CSharp;

/// <summary>A .feature file's steps and their step definitions as one thing: completion offers the keywords where the caret
/// is, the steps the project's definitions bind after a step's keyword (each parameter a hole to fill) and the project's
/// tags after an @; the hover card says which definition binds a step, or why none does; Go to Declaration goes from a
/// step to its definition (a list where several bind it) and from a scenario's or feature's header to the hooks that run
/// for it; Find Usages goes from a step, or a step definition's method or expression in C#, to every step it binds in the
/// features of the projects that see it; Show Context Actions on a step none binds writes its definition.</summary>
internal sealed partial class GherkinAssist(AssistContext context, CodeCompletion completion, CodeNavigation navigation, CodeActions actions) : LanguageAssist
{
    /// <summary>What the window gives the editor's commands of its BDD; null while none is set.</summary>
    public Func<BddSource?>? Source { get; set; }

    private readonly Dictionary<CompletionItem, (string Said, StepSample? Sample, string Insert)> offered = [];

    private BddSource? Bdd => Source?.Invoke();

    private static bool IsFeature(OpenDocument? document) => document is not null && FeatureChecks.IsFeature(document.Path);

    // Completion.

    public override bool Typed(string text)
    {
        if (context.Editors.Active is not { } shown || !IsFeature(shown))
        {
            return false;
        }
        TextSnapshot snapshot = shown.Text.Snapshot;
        int caret = context.Caret;
        int line = snapshot.GetLineFromOffset(caret);
        string before = snapshot.GetText(snapshot.GetLineStart(line), caret - snapshot.GetLineStart(line));
        string trimmed = before.TrimStart();
        bool opens = text.EndsWith('@')
            || (text.EndsWith(' ') && StepKeywordOf(DialectOf(shown), trimmed) is { } keyword && keyword.Length == trimmed.Length)
            || (trimmed.Length == 1 && char.IsLetter(trimmed[0]));
        if (opens)
        {
            IdeErrors.Observe(completion.Start(null));
        }
        return true;
    }

    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (context.Editors.Active is not { } shown || !IsFeature(shown) || Bdd is not { } bdd)
        {
            return false;
        }
        CancellationTokenSource cancel = completion.Asking();
        TextSnapshot asked = shown.Text.Snapshot;
        FeatureAnalysis? analysis = await bdd.AnalysisAsync(shown);
        if (analysis is null || cancel.IsCancellationRequested || context.Editors.Active != shown || shown.Text.Snapshot != asked)
        {
            return true;
        }
        int caret = context.Caret;
        int line = asked.GetLineFromOffset(caret);
        int lineStart = asked.GetLineStart(line);
        string before = asked.GetText(lineStart, caret - lineStart);
        string trimmed = before.TrimStart();
        int indent = before.Length - trimmed.Length;
        List<(CompletionItem Item, string Said, StepSample? Sample, string Insert)> items = [];
        int start;
        if (StepKeywordOf(analysis.Reading.Dialect, trimmed) is { } keyword && trimmed.Length >= keyword.Length)
        {
            start = lineStart + indent + keyword.Length;
            StepKind kind = KindAt(analysis, line, keyword);
            items.AddRange(StepItems(analysis, kind));
        }
        else if (trimmed.StartsWith('@') || trimmed.Contains(" @", StringComparison.Ordinal))
        {
            start = lineStart + before.LastIndexOf('@');
            items.AddRange((await TagsAsync(bdd, shown, analysis)).Select(t => (Item(t, CompletionItemKind.Constant, "tag", t), $"A tag of the project's features", (StepSample?)null, t)));
        }
        else if (trimmed.All(c => char.IsLetter(c) || c == ' '))
        {
            start = lineStart + indent;
            items.AddRange(KeywordItems(analysis, line));
        }
        else
        {
            completion.Close();
            return true;
        }
        if (items.Count == 0)
        {
            completion.Close();
            return true;
        }
        offered.Clear();
        foreach ((CompletionItem item, string said, StepSample? sample, string insert) in items)
        {
            offered[item] = (said, sample, insert);
        }
        completion.Open(new CodeCompletion.Completion([.. items.Select(i => i.Item)], start, [], false) { Own = this, WordCharacters = " @<>\"'[]{}-" });
        return true;
    }

    private static CompletionItem Item(string label, CompletionItemKind kind, string detail, string sort) =>
        new() { Label = label, Kind = kind, Detail = detail, FilterText = label, SortText = sort };

    /// <summary>The steps the project's definitions bind for a step of a kind, each written from its definition.</summary>
    private static IEnumerable<(CompletionItem, string, StepSample?, string)> StepItems(FeatureAnalysis analysis, StepKind kind)
    {
        HashSet<string> written = new(StringComparer.Ordinal);
        foreach (StepBinding binding in analysis.Steps?.Bindings.Where(b => b.Binds(kind)) ?? [])
        {
            StepSample sample = StepSamples.Of(binding) ?? new StepSample(binding.Expression ?? binding.Method, []);
            if (written.Add(sample.Text))
            {
                yield return (Item(sample.Text, CompletionItemKind.Method, binding.Shown, sample.Text), $"{binding.Shown}\n{binding.Expression}", sample, sample.Text);
            }
        }
    }

    /// <summary>The keywords Gherkin takes at a line: before the feature its header, after it the headers and steps.</summary>
    private static IEnumerable<(CompletionItem, string, StepSample?, string)> KeywordItems(FeatureAnalysis analysis, int line)
    {
        Gherkin.GherkinDialect dialect = analysis.Reading.Dialect;
        bool feature = analysis.Reading.Feature is { } read && read.Line < line;
        IEnumerable<(string Keyword, string Said, bool Header, int Rank)> keywords = feature
            ?
            [
                .. dialect.ScenarioKeywords.Select(k => (k, "A scenario: an example of the feature's behaviour", true, 1)),
                .. dialect.ScenarioOutlineKeywords.Select(k => (k, "A scenario run once for each of its examples", true, 1)),
                .. dialect.BackgroundKeywords.Select(k => (k, "Steps run before each scenario", true, 2)),
                .. dialect.ExamplesKeywords.Select(k => (k, "An outline's examples: a table of values, one run each", true, 2)),
                .. dialect.RuleKeywords.Select(k => (k, "A business rule the scenarios under it illustrate", true, 3)),
                .. dialect.GivenStepKeywords.Concat(dialect.WhenStepKeywords).Concat(dialect.ThenStepKeywords).Concat(dialect.AndStepKeywords).Concat(dialect.ButStepKeywords)
                    .Where(k => k.Trim() != "*").Select(k => (k.TrimEnd(), "A step", false, 0)),
            ]
            : dialect.FeatureKeywords.Select(k => (k, "The feature the file describes", true, 0));
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach ((string keyword, string said, bool header, int rank) in keywords)
        {
            if (seen.Add(keyword))
            {
                string insert = header ? keyword + ": " : keyword + " ";
                yield return (Item(keyword, CompletionItemKind.Keyword, header ? "header" : "step", $"{rank}{keyword}"), said, null, insert);
            }
        }
    }

    /// <summary>Every tag the project's features use, the open file's first.</summary>
    private static async Task<IReadOnlyList<string>> TagsAsync(BddSource bdd, OpenDocument shown, FeatureAnalysis analysis)
    {
        List<string> tags = [.. analysis.Reading.Tags.Select(t => t.Name)];
        if (bdd.StepsOf(shown.Path) is { } steps)
        {
            foreach ((string _, FeatureAnalysis other) in await bdd.FeaturesAsync([steps]))
            {
                tags.AddRange(other.Reading.Tags.Select(t => t.Name));
            }
        }
        return [.. tags.GroupBy(t => t, StringComparer.Ordinal).OrderByDescending(g => g.Count()).Select(g => g.Key)];
    }

    public override void Commit(CompletionItem item, int start)
    {
        if (!offered.TryGetValue(item, out (string Said, StepSample? Sample, string Insert) chosen))
        {
            return;
        }
        TextSnapshot text = context.Editor.Document.Snapshot;
        int caret = context.Caret;
        int line = text.GetLineFromOffset(caret);
        int end = chosen.Sample is null ? caret : text.GetLineStart(line) + text.GetLineText(line).TrimEnd('\r').Length;
        while (chosen.Sample is null && end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }
        SelectionSet after = chosen.Sample is { Holes: [{ } hole, ..] }
            ? SelectionSet.Single(new Selection(start + hole.Start, start + hole.Start + hole.Length))
            : SelectionSet.Single(start + chosen.Insert.Length);
        context.Editor.ApplyEdits([new TextEdit(start, Math.Max(0, end - start), chosen.Insert)], after);
    }

    public override string? Describe(CompletionItem item) => offered.TryGetValue(item, out (string Said, StepSample? Sample, string Insert) chosen) ? chosen.Said : null;

    /// <summary>The step keyword a line's text begins with, the longest of the dialect's ("And ", "Gegeben sei ").</summary>
    private static string? StepKeywordOf(Gherkin.GherkinDialect dialect, string text) =>
        dialect.StepKeywords.Where(k => text.StartsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault();

    private Gherkin.GherkinDialect DialectOf(OpenDocument document) =>
        Bdd?.Checks.Of(document)?.Reading.Dialect ?? GherkinDialects.Of(GherkinDialects.English);

    /// <summary>The kind a step written on a line binds as: its reading's, else its keyword's, an And or But taking the kind of
    /// the step above it in its scenario.</summary>
    private static StepKind KindAt(FeatureAnalysis analysis, int line, string keyword)
    {
        if (analysis.Reading.Steps.FirstOrDefault(s => s.Line == line) is { } step)
        {
            return step.Kind;
        }
        Gherkin.GherkinDialect dialect = analysis.Reading.Dialect;
        if (dialect.GivenStepKeywords.Contains(keyword) && keyword.Trim() != "*")
        {
            return StepKind.Given;
        }
        if (dialect.WhenStepKeywords.Contains(keyword))
        {
            return StepKind.When;
        }
        if (dialect.ThenStepKeywords.Contains(keyword))
        {
            return StepKind.Then;
        }
        return analysis.Reading.Steps.LastOrDefault(s => s.Line < line && analysis.Reading.ScenarioAt(line) == s.Scenario)?.Kind ?? StepKind.Given;
    }
}
