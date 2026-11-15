using Passenger.App.Bdd;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.CSharp;

internal sealed partial class GherkinAssist
{
    /// <summary>A step's card: the definition binding it, with its documentation's summary, or why none binds it.</summary>
    public override async Task<bool> HoverAsync(IHoverCard card, OpenDocument document, int offset, CancellationToken cancel)
    {
        if (!IsFeature(document) || Bdd is not { } bdd || await bdd.AnalysisAsync(document) is not { } analysis)
        {
            return false;
        }
        cancel.ThrowIfCancellationRequested();
        if (analysis.StepAt(offset) is not { } step)
        {
            return false;
        }
        if (!analysis.Matches.TryGetValue(step, out StepMatch? match))
        {
            card.AddText(analysis.Steps is null ? "No Reqnroll project holds this feature, or its step definitions are still being read" : "This step is not read yet");
            return true;
        }
        switch (match.Status)
        {
            case MatchStatus.Defined:
                foreach (BoundStep bound in match.Bindings)
                {
                    card.AddCode($"[{bound.Binding.Kind}({Quoted(bound.Binding.Expression)})]\n{bound.Binding.Shown}");
                    if (bound.Binding.Summary is { } summary)
                    {
                        card.AddText(summary);
                    }
                }
                card.AddText(match.Bindings.Count > 1 ? "Each example of the outline binds one of these" : $"The step definition that binds this {step.Kind} step");
                break;
            case MatchStatus.Ambiguous:
                card.AddCode(string.Join('\n', match.Bindings.Select(b => b.Binding.Shown)));
                card.AddText("Several step definitions bind this step equally: Reqnroll refuses to run it");
                break;
            default:
                card.AddText($"No step definition binds this {step.Kind} step{(match.Why is { } why ? $": {why}" : string.Empty)}");
                break;
        }
        return true;
    }

    private static string Quoted(string? expression) => expression is null ? string.Empty : $"\"{expression.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    public override Task<bool> DefinitionAsync(int offset) => GoToDeclarationAsync(context, navigation, offset, IsFeature);

    /// <summary>From a step, the definitions that bind it (every example's for an outline's); from a feature's, rule's,
    /// scenario's or examples' header or tags, the hooks that run for it; null in any other file.</summary>
    public override async Task<Declared?> DeclaredAsync(OpenDocument document, int offset)
    {
        if (!IsFeature(document) || Bdd is not { } bdd || await bdd.AnalysisAsync(document) is not { } analysis)
        {
            return null;
        }
        if (analysis.StepAt(offset) is { } step)
        {
            if (!analysis.Matches.TryGetValue(step, out StepMatch? match) || match.Bindings.Count == 0)
            {
                return new Declared([], analysis.Steps is null ? "No Reqnroll project's step definitions are read for this feature" : "No step definition binds this step");
            }
            BoundStep[] placed = [.. match.Bindings.Where(b => b.Binding.MethodPlace is not null)];
            return new Declared([.. placed.Select(b => BddSource.PlaceOf(b.Binding.MethodPlace)!)], "The step definition is in no file of the solution")
            {
                Names = [.. placed.Select(b => (string?)b.Binding.Shown)],
                Title = "Step definitions",
            };
        }
        int line = analysis.Text.GetLineFromOffset(Math.Min(offset, analysis.Text.Length));
        if (HooksAt(analysis, line) is not { } hooks)
        {
            return null;
        }
        StepHook[] found = [.. hooks.Where(h => h.MethodPlace is not null)];
        return new Declared([.. found.Select(h => BddSource.PlaceOf(h.MethodPlace)!)], "No hook runs for this")
        {
            Names = [.. found.Select(h => (string?)$"[{h.Kind}] {h.ShortType}.{h.Method}")],
            Title = "Hooks",
        };
    }

    /// <summary>The hooks that run for the part whose header or tags are on a line, in the order they run; null for a line of
    /// no header.</summary>
    private static IReadOnlyList<StepHook>? HooksAt(FeatureAnalysis analysis, int line)
    {
        FeatureReading reading = analysis.Reading;
        if (analysis.Steps is not { } steps)
        {
            return null;
        }
        string[] kinds;
        IReadOnlyList<string> tags;
        string? scenarioTitle = null;
        if (reading.Scenarios.FirstOrDefault(s => s.Line == line || s.Tags.Any(t => t.Line == line)) is { } scenario)
        {
            kinds = ["BeforeScenario", "BeforeScenarioBlock", "BeforeStep", "AfterStep", "AfterScenarioBlock", "AfterScenario"];
            tags = scenario.AllTags;
            scenarioTitle = scenario.Title;
        }
        else if (reading.Feature is { } feature && (feature.Line == line || feature.Tags.Any(t => t.Line == line)))
        {
            kinds = ["BeforeTestRun", "BeforeFeature", "AfterFeature", "AfterTestRun"];
            tags = [.. feature.Tags.Select(t => t.Name)];
        }
        else
        {
            return null;
        }
        StepContext context = new(reading.Feature?.Title, scenarioTitle, tags);
        return [.. steps.Hooks.Where(h => kinds.Contains(h.Kind) && Fits(h, context))
            .OrderBy(h => Array.IndexOf(kinds, h.Kind)).ThenBy(h => h.Order)];
    }

    /// <summary>Whether a hook runs in a context: one of its scopes (its tags among them) fits, or it has none.</summary>
    private static bool Fits(StepHook hook, StepContext context) => hook.Scopes.Count == 0 || hook.Scopes.Any(scope => scope.Fits(context, anyScenario: true));

    public override async Task<bool> UsagesAsync(int offset, bool inPopup)
    {
        if (context.Editors.Active is not { } shown || Bdd is not { } bdd)
        {
            return false;
        }
        if (await UsesAsync(bdd, shown, offset) is not ({ } title, { } places))
        {
            return false;
        }
        if (places.Count == 0)
        {
            context.Say($"No feature's step is bound to {title}");
        }
        else
        {
            context.ShowUsages($"Steps bound to {title}", places, inPopup ? offset : null);
        }
        return true;
    }

    /// <summary>The steps bound to what is at an offset, the window's and an agent's alike: a step's definitions' (every step
    /// they bind), or a step definition's in C# (on its method's name or its expression); null where neither is there.</summary>
    public async Task<(string Title, IReadOnlyList<LspLocation> Places)?> UsesAsync(BddSource bdd, OpenDocument document, int offset)
    {
        IReadOnlyList<StepBinding> bindings;
        if (IsFeature(document))
        {
            if (await bdd.AnalysisAsync(document) is not { } analysis || analysis.StepAt(offset) is not { } step
                || !analysis.Matches.TryGetValue(step, out StepMatch? match) || match.Bindings.Count == 0)
            {
                return null;
            }
            bindings = [.. match.Bindings.Select(b => b.Binding)];
        }
        else if (PathNames.HasExtension(document.Path, [".cs"]))
        {
            // The bindings as last read, never waited for: every C# Find Usages passes through here, Reqnroll or not.
            if (bdd.Session.Steps.Wants || bdd.BindingAt(document, offset) is not { } binding)
            {
                return null;
            }
            bindings = [binding];
        }
        else
        {
            return null;
        }
        List<LspLocation> places = [];
        foreach ((string path, FeatureAnalysis analysis) in await bdd.FeaturesAsync(bindings.SelectMany(bdd.ProjectsSeeing).DistinctBy(p => p.Project)))
        {
            foreach ((FeatureStep step, StepMatch match) in analysis.Matches)
            {
                if (match.Bindings.Any(b => bindings.Any(b.Binding.SameAs)))
                {
                    places.Add(bdd.PlaceOf(path, analysis, step));
                }
            }
        }
        return (string.Join(", ", bindings.Select(b => b.Shown).Distinct()), places);
    }

    /// <summary>An agent's usages of a step or step definition (<see cref="UsesAsync"/>), the bindings read first where the
    /// solution has a Reqnroll project; null where neither is at the place.</summary>
    internal async Task<(string Title, IReadOnlyList<LspLocation> Places)?> AgentUsesAsync(OpenDocument document, int offset)
    {
        if (Bdd is not { } bdd || (!IsFeature(document) && !bdd.Session.Steps.Projects.Any()))
        {
            return null;
        }
        await bdd.StepsAsync();
        return await UsesAsync(bdd, document, offset);
    }

    /// <summary>Find Unused Step Definitions: every step definition of the solution's code that no step of the features of
    /// the projects seeing it binds, listed as usages are.</summary>
    public async Task ShowUnusedAsync()
    {
        if (Bdd is not { } bdd)
        {
            return;
        }
        IReadOnlyList<StepBinding> unused = await UnusedAsync(bdd);
        if (unused.Count == 0)
        {
            context.Say(bdd.Session.Steps.Projects.Any(p => p.Bindings.Count > 0) ? "There are no unused step definitions" : "No step definitions are in the solution");
            return;
        }
        context.ShowUsages("Unused step definitions", [.. unused.Select(b => BddSource.PlaceOf(b.MethodPlace)!)], null);
    }

    /// <summary>The step definitions written in the solution's code that no step binds, each once, the window's and an agent's alike.</summary>
    internal static async Task<IReadOnlyList<StepBinding>> UnusedAsync(BddSource bdd)
    {
        StepCatalog catalog = await bdd.StepsAsync();
        List<StepBinding> used = [];
        foreach ((string _, FeatureAnalysis analysis) in await bdd.FeaturesAsync(catalog.Projects))
        {
            used.AddRange(analysis.Matches.Values.SelectMany(m => m.Bindings).Select(b => b.Binding));
        }
        List<StepBinding> unused = [];
        foreach (StepBinding binding in catalog.Projects.SelectMany(p => p.Bindings).Where(b => b.MethodPlace is not null))
        {
            if (!unused.Any(binding.SameAs) && !used.Any(binding.SameAs))
            {
                unused.Add(binding);
            }
        }
        return unused;
    }
}
