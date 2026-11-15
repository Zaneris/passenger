using Passenger.App.Bdd;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace.Gherkin;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

internal sealed partial class GherkinAssist
{
    public const string RenameStepTitle = "Rename Step";

    /// <summary>Rename on a step, or on a step definition's expression in C#: the box holds the expression, and the definition's
    /// attribute and every step it binds are written anew as one command of Undo.</summary>
    public override bool Rename(int caret)
    {
        if (context.Editors.Active is not { } document || Bdd is not { } bdd || (!IsFeature(document) && !PathNames.HasExtension(document.Path, [".cs"])))
        {
            return false;
        }
        // Bindings read before the C# file's last change may be placed where it no longer has them: C#'s Rename is asked then.
        StepBinding? binding = IsFeature(document) ? BindingOfStep(bdd, document, caret)
            : !bdd.Session.Steps.Wants && bdd.BindingAt(document, caret) is { ExpressionPlace: not null } at && OnExpression(document.Text.Snapshot, at, caret) ? at : null;
        if (binding?.Expression is not { } expression)
        {
            return false;
        }
        RenameBox box = new(expression, RenameStepTitle);
        popups.ShowModal(box, caret, async renamed =>
        {
            if (renamed is null || renamed == expression)
            {
                return;
            }
            RefactorOutcome made = await RenameStepAsync(bdd, binding, renamed);
            context.Say(made.Problem ?? made.Said);
        });
        return true;
    }

    private static StepBinding? BindingOfStep(BddSource bdd, OpenDocument document, int caret) =>
        bdd.Checks.Of(document) is { } analysis && analysis.StepAt(caret) is { } step && analysis.Matches.TryGetValue(step, out StepMatch? match)
            && match is { Status: MatchStatus.Defined, Bindings: [{ } bound] } ? bound.Binding : null;

    /// <summary>Whether an offset is on a binding's expression's string, not its method's name (which C# renames).</summary>
    private static bool OnExpression(TextSnapshot text, StepBinding binding, int offset)
    {
        TextPosition place = ServerPlaces.PositionOf(text, new LspPosition(binding.ExpressionPlace!.Line, binding.ExpressionPlace.Column));
        int start = text.GetLineStart(place.Line) + place.Column;
        return offset >= start && offset <= start + binding.ExpressionLength;
    }

    /// <summary>A step definition's expression renamed, the window's and an agent's alike: its attribute's string and every step
    /// it binds in the features of the projects that see it, as one command of Undo; refused where Reqnroll's Rename Step
    /// would refuse it, an outline's steps with placeholders (and any whose arguments are not known) left as they are and said.</summary>
    internal async Task<RefactorOutcome> RenameStepAsync(BddSource bdd, StepBinding binding, string renamed)
    {
        bool cucumber = binding.CucumberExpression ?? StepExpressions.IsCucumberExpression(binding.Expression!);
        StepRename rename = StepRename.Of(binding.Expression!, renamed, cucumber);
        if (rename.Refused is { } why)
        {
            return RefactorOutcome.Refused($"{RenameStepTitle}: {why}");
        }
        if (binding.ExpressionPlace is not { } place)
        {
            return RefactorOutcome.Refused($"{RenameStepTitle}: {binding.Shown}'s expression is in no file of the solution");
        }
        List<XamlFileChange> changes = [new(place.Path, text =>
        {
            TextSnapshot snapshot = TextSnapshot.From(text);
            TextPosition at = ServerPlaces.PositionOf(snapshot, new LspPosition(place.Line, place.Column));
            int start = snapshot.GetLineStart(at.Line) + at.Column;
            string written = start + binding.ExpressionLength <= text.Length ? text.Substring(start, binding.ExpressionLength) : string.Empty;
            bool verbatim = written.StartsWith('@');
            // Only the literal the definition was read with is rewritten: a file changed since leaves the rename refused.
            return written.Length > 0 && written == StepRename.Literal(binding.Expression!, verbatim)
                ? [new TextEdit(start, binding.ExpressionLength, StepRename.Literal(renamed, verbatim))] : [];
        })];
        int skipped = 0;
        foreach ((string path, FeatureAnalysis analysis) in await bdd.FeaturesAsync(bdd.ProjectsSeeing(binding).DistinctBy(p => p.Project)))
        {
            List<TextEdit> edits = [];
            foreach ((FeatureStep step, StepMatch match) in analysis.Matches)
            {
                if (match.Bindings.FirstOrDefault(b => b.Binding.SameAs(binding)) is not { } bound)
                {
                    continue;
                }
                if (step.Placeholders.Count > 0 || match.Bindings.Count > 1)
                {
                    skipped++;
                    continue;
                }
                if (rename.Step(step.Text, bound.Arguments) is not { } rewritten)
                {
                    skipped++;
                    continue;
                }
                edits.Add(new TextEdit(analysis.TextOf(step).Start, step.Text.Length, rewritten));
            }
            if (edits.Count > 0)
            {
                string before = analysis.Text.ToString();
                changes.Add(new XamlFileChange(path, text => text == before ? edits : []));
            }
        }
        if (await actions.ApplyFileChangesAsync(RenameStepTitle, changes) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        string left = skipped > 0 ? $"; {skipped} {(skipped == 1 ? "step" : "steps")} with placeholders or no arguments to keep left as written" : string.Empty;
        return new RefactorOutcome($"Renamed the step to \"{renamed}\" in {changes.Count} {(changes.Count == 1 ? "file" : "files")}{left}", null,
            [.. changes.Select(c => new RefactoredFile(c.Path, "changed", []))], []);
    }
}
