using Passenger.App.Bdd;
using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.CSharp;

internal sealed partial class GherkinAssist
{
    /// <summary>Why a step definition's phrase can't be written into the file the editor shows; null where it can.</summary>
    public string? WhyNotInsertStep() =>
        (context.Editors.Active is not { } shown ? Refusals.NoFile
        : !IsFeature(shown) ? Refusals.NoFeature
        : context.Editor.IsReadOnly ? shown.ReadOnlyWhy
        : null)?.Shown;

    /// <summary>Writes a step definition's phrase into the open feature at the caret as completion writes it, its first slot
    /// chosen to type over: after the keyword of a line holding one alone; as a step on an empty line; else as a step on a
    /// line of its own after the caret's line (after the table or doc string of a step it is on). Its keyword is the
    /// feature's language's for the kind it binds, And after a step of that kind in its scenario, its indent its steps'.
    /// False where it could not be written.</summary>
    public async Task<bool> InsertStepAsync(StepBinding binding)
    {
        if (context.Editors.Active is not { } shown || WhyNotInsertStep() is not null)
        {
            return false;
        }
        FeatureAnalysis? analysis = null;
        for (int turn = 0; turn < 3 && Bdd is { } bdd && analysis?.Text != shown.Text.Snapshot; turn++)
        {
            analysis = await bdd.AnalysisAsync(shown);
        }
        if (context.Editors.Active != shown || WhyNotInsertStep() is not null)
        {
            return false;
        }
        TextSnapshot text = context.Editor.Document.Snapshot;
        FeatureReading? reading = analysis?.Text == text ? analysis.Reading : null;
        Gherkin.GherkinDialect dialect = reading?.Dialect ?? DialectOf(shown);
        StepSample sample = StepLibrary.SampleOf(binding);
        int line = text.GetLineFromOffset(context.Caret);
        LineSpan span = text.GetLine(line);
        string written = text.GetText(span.Start, span.Length);
        string trimmed = written.TrimStart();
        int indent = written.Length - trimmed.Length;
        string head = trimmed.TrimEnd();
        if (head.Length > 0 && StepKeywordOf(dialect, head + " ") is { } alone && alone.TrimEnd() == head)
        {
            WriteStep(span.Start + indent + head.Length, span.End, alone.EndsWith(' ') ? " " : string.Empty, sample);
            return true;
        }
        if (head.Length == 0)
        {
            (string pad, string keyword) = StepPlace(reading, dialect, text, line - 1, binding.Kind);
            WriteStep(span.Start + indent, span.End, (indent > 0 ? string.Empty : pad) + keyword, sample);
            return true;
        }
        int last = reading?.Steps.FirstOrDefault(s => s.Line <= line && line <= s.EndLine)?.EndLine ?? line;
        (string under, string word) = StepPlace(reading, dialect, text, last, binding.Kind);
        int end = text.GetLine(last).End;
        WriteStep(end, end, context.Editor.Document.LineEnding + under + word, sample);
        return true;
    }

    /// <summary>The indent and keyword of a step written after a line: the indent of the scenario's step before it (else
    /// after it, else its header's and two spaces more), And after a step of the kind it binds, else its kind's keyword.</summary>
    private static (string Indent, string Keyword) StepPlace(FeatureReading? reading, Gherkin.GherkinDialect dialect, TextSnapshot text, int after, BindingKind kind)
    {
        FeatureScenario? scenario = reading?.ScenarioAt(Math.Max(0, after));
        FeatureStep? before = scenario?.Steps.LastOrDefault(s => s.Line <= after);
        string indent = before is not null ? IndentOf(text, before.Line)
            : scenario?.Steps.FirstOrDefault() is { } next ? IndentOf(text, next.Line)
            : scenario is not null ? IndentOf(text, scenario.Line) + "  "
            : "  ";
        string[] keywords = before is not null && (kind == BindingKind.StepDefinition || (int)kind == (int)before.Kind) ? dialect.AndStepKeywords
            : kind switch
            {
                BindingKind.When => dialect.WhenStepKeywords,
                BindingKind.Then => dialect.ThenStepKeywords,
                _ => dialect.GivenStepKeywords,
            };
        return (indent, keywords.FirstOrDefault(k => k.Trim() != "*") ?? keywords[0]);

        static string IndentOf(TextSnapshot text, int line)
        {
            string written = text.GetLineText(line);
            return written[..(written.Length - written.TrimStart().Length)];
        }
    }

    /// <summary>Find Usages of step definitions asked elsewhere than at a place (the Step Library's): every feature step they
    /// bind, listed as usages are.</summary>
    public async Task ShowUsesAsync(IReadOnlyList<StepBinding> bindings)
    {
        if (Bdd is not { } bdd)
        {
            return;
        }
        (string title, IReadOnlyList<LspLocation> places) = await UsesOfAsync(bdd, bindings);
        if (places.Count == 0)
        {
            context.Say($"No feature's step is bound to {title}");
        }
        else
        {
            context.ShowUsages($"Steps bound to {title}", places, null);
        }
    }
}
