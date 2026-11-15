using System.Globalization;
using System.Text.RegularExpressions;

namespace Passenger.Workspace.Gherkin;

/// <summary>How a step stands against the project's step definitions.</summary>
public enum MatchStatus
{
    /// <summary>One definition binds it (an outline's step: each example's has one).</summary>
    Defined,

    /// <summary>Several fit equally: Reqnroll refuses to run it.</summary>
    Ambiguous,

    /// <summary>None binds it.</summary>
    Undefined,
}

/// <summary>An argument a step's text gives its definition: where it is in the text.</summary>
public readonly record struct StepArgument(int Start, int Length);

/// <summary>A definition a step binds to, with the arguments its text gives it (none for an outline's step, whose
/// placeholders stand for them).</summary>
public sealed record BoundStep(StepBinding Binding, IReadOnlyList<StepArgument> Arguments);

/// <summary>What a step binds to: its definitions, and for one with none, the definitions whose expression fits its text but
/// that Reqnroll would still refuse it (their parameters, their scope), with why.</summary>
public sealed record StepMatch(MatchStatus Status, IReadOnlyList<BoundStep> Bindings, string? Why = null)
{
    public static readonly StepMatch None = new(MatchStatus.Undefined, []);
}

/// <summary>Steps matched to step definitions as Reqnroll's runtime matches them (its StepDefinitionMatchService): each
/// definition of the step's kind whose regular expression matches the whole text, takes as many parameters as the text's
/// arguments and the step's doc string and table, can convert each argument in the binding culture, and fits the step's
/// context by one of its scopes; of several, those that fit by the most of a scope's restrictions. Made once for a set of
/// definitions, each expression made once.</summary>
public sealed class StepMatcher
{
    private readonly (StepBinding Binding, Regex? Regex, string? Error)[] compiled;
    private readonly CultureInfo culture;

    public StepMatcher(IReadOnlyList<StepBinding> bindings, IReadOnlyList<StepTransformation> transformations, CultureInfo culture)
    {
        Bindings = bindings;
        Transformations = transformations;
        Types = new ReqnrollParameterTypes(bindings, transformations);
        this.culture = culture;
        compiled = [.. bindings.Select(b =>
        {
            (Regex? regex, string? error) = StepExpressions.RegexOf(b, Types);
            return (b, regex, b.Error ?? error);
        })];
    }

    public IReadOnlyList<StepBinding> Bindings { get; }

    public IReadOnlyList<StepTransformation> Transformations { get; }

    public ReqnrollParameterTypes Types { get; }

    /// <summary>Why a definition cannot bind any step (its own error, or its expression's); null where it can.</summary>
    public string? ErrorOf(StepBinding binding) => compiled.FirstOrDefault(c => ReferenceEquals(c.Binding, binding)).Error;

    /// <summary>A step of a file read as it runs: an outline's once for each example row with its values (the context
    /// taking the row's examples' tags), a background's in the context of each scenario after it; matched where every
    /// run binds it, each definition listed once.</summary>
    public StepMatch Match(FeatureStep step, FeatureReading reading)
    {
        List<StepMatch> runs = [];
        foreach ((string text, StepContext context) in RunsOf(step, reading))
        {
            runs.Add(Match(step.Kind, text, step.DocString is not null, step.Table is not null, context));
        }
        if (runs.Count == 1)
        {
            return runs[0];
        }
        if (runs.FirstOrDefault(r => r.Status == MatchStatus.Undefined) is { } undefined)
        {
            return undefined with { Bindings = [] };
        }
        if (runs.FirstOrDefault(r => r.Status == MatchStatus.Ambiguous) is { } ambiguous)
        {
            return ambiguous with { Bindings = [.. ambiguous.Bindings.Select(b => b with { Arguments = [] })] };
        }
        return new StepMatch(MatchStatus.Defined, [.. runs.SelectMany(r => r.Bindings).Select(b => b.Binding).Distinct().Select(b => new BoundStep(b, []))]);
    }

    /// <summary>A step's text matched in a context.</summary>
    public StepMatch Match(StepKind kind, string text, bool hasDocString, bool hasTable, StepContext context)
    {
        List<(StepBinding Binding, Match Match, int Fit)> fitting = [];
        List<string> refused = [];
        foreach ((StepBinding binding, Regex? regex, string? error) in compiled)
        {
            if (regex is null || error is not null || !binding.Binds(kind) || regex.Match(text) is not { Success: true } match)
            {
                continue;
            }
            Group[] captured = [.. match.Groups.Cast<Group>().Skip(1).Where(g => g.Success)];
            int arguments = captured.Length + (hasDocString ? 1 : 0) + (hasTable ? 1 : 0);
            if (arguments != binding.Parameters.Count)
            {
                refused.Add($"{binding.Shown} takes {binding.Parameters.Count} parameters, and the step gives {arguments}");
                continue;
            }
            if (captured.Select((g, i) => (g, i)).FirstOrDefault(c => !Converts(c.g.Value, binding.Parameters[c.i])) is { g: not null } wrong)
            {
                refused.Add($"{binding.Shown} cannot take \"{wrong.g.Value}\" as {binding.Parameters[wrong.i].ShortType}");
                continue;
            }
            if (Fit(binding, context) is not { } fit)
            {
                refused.Add($"{binding.Shown} is scoped to another feature, scenario or tag");
                continue;
            }
            fitting.Add((binding, match, fit));
        }
        if (fitting.Count == 0)
        {
            return new StepMatch(MatchStatus.Undefined, [], refused.Count > 0 ? string.Join("\n", refused) : null);
        }
        int best = fitting.Max(f => f.Fit);
        BoundStep[] bound = [.. fitting.Where(f => f.Fit == best).Select(f => new BoundStep(f.Binding, ArgumentsOf(f.Match)))];
        return new StepMatch(bound.Length == 1 ? MatchStatus.Defined : MatchStatus.Ambiguous, bound);
    }

    /// <summary>The steps of the files given that a definition binds, each with its file's reading.</summary>
    public IEnumerable<(FeatureReading Reading, FeatureStep Step)> UsesOf(StepBinding binding, IEnumerable<FeatureReading> readings) =>
        readings.SelectMany(r => r.Steps.Select(s => (r, s))).Where(u => Match(u.s, u.r).Bindings.Any(b => b.Binding.SameAs(binding)));

    private static StepArgument[] ArgumentsOf(Match match) =>
        [.. match.Groups.Cast<Group>().Skip(1).Where(g => g.Success).Select(g => new StepArgument(g.Index, g.Length))];

    /// <summary>How closely a definition fits a context: the most restrictions of a scope that fits (0 for one with no
    /// scope), or null where no scope fits.</summary>
    private static int? Fit(StepBinding binding, StepContext context)
    {
        if (binding.Scopes.Count == 0)
        {
            return 0;
        }
        int? best = null;
        foreach (StepScope scope in binding.Scopes)
        {
            bool fits = (scope.Tag is null || context.Tags.Any(t => t.TrimStart('@') == scope.Tag.TrimStart('@')))
                && (scope.Feature is null || string.Equals(scope.Feature, context.FeatureTitle, StringComparison.CurrentCultureIgnoreCase))
                && (scope.Scenario is null || string.Equals(scope.Scenario, context.ScenarioTitle, StringComparison.CurrentCultureIgnoreCase));
            if (fits)
            {
                best = Math.Max(best ?? 0, scope.Restrictions);
            }
        }
        return best;
    }

    /// <summary>Whether Reqnroll's converter could make an argument's text the parameter's type in the binding culture; a type
    /// it converts by a transformation or its own rules is taken as converting.</summary>
    private bool Converts(string value, StepParameter parameter)
    {
        string type = parameter.Type.StartsWith("System.Nullable", StringComparison.Ordinal) && parameter.Type.Contains('[', StringComparison.Ordinal)
            ? parameter.Type[(parameter.Type.IndexOf('[', StringComparison.Ordinal) + 1)..].TrimEnd(']')
            : parameter.Type;
        if (Transformations.Any(t => t.ReturnType == type))
        {
            return true;
        }
        if (parameter.Members is { } members)
        {
            string squeezed = value.Replace(" ", "", StringComparison.Ordinal);
            return members.Any(m => string.Equals(m, squeezed, StringComparison.OrdinalIgnoreCase)) || int.TryParse(value, NumberStyles.Integer, culture, out _);
        }
        return type switch
        {
            "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" =>
                long.TryParse(value, NumberStyles.Integer | NumberStyles.AllowThousands, culture, out _) || ulong.TryParse(value, NumberStyles.Integer, culture, out _),
            "System.Single" or "System.Double" or "System.Decimal" => double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, culture, out _),
            "System.Boolean" => bool.TryParse(value, out _),
            "System.Guid" => Guid.TryParse(value, out _),
            "System.DateTime" or "System.DateTimeOffset" => DateTime.TryParse(value, culture, DateTimeStyles.None, out _),
            "System.TimeSpan" => TimeSpan.TryParse(value, culture, out _),
            "System.Char" => value.Length == 1,
            _ => true,
        };
    }

    /// <summary>Each run of a step: its text with an example's values in place of its placeholders, in that run's context.</summary>
    private static IEnumerable<(string Text, StepContext Context)> RunsOf(FeatureStep step, FeatureReading reading)
    {
        FeatureScenario scenario = step.Scenario;
        string? feature = reading.Feature?.Title;
        if (scenario.Kind == ScenarioKind.Background)
        {
            FeatureScenario[] after = [.. reading.Scenarios.Where(s => s.Kind != ScenarioKind.Background && s.Line > scenario.Line && s.Rule == scenario.Rule)];
            if (after.Length == 0)
            {
                return [(step.Text, new StepContext(feature, null, scenario.AllTags))];
            }
            return after.SelectMany(s => RunsOf(step.Text, s, feature));
        }
        return RunsOf(step.Text, scenario, feature);
    }

    private static IEnumerable<(string Text, StepContext Context)> RunsOf(string text, FeatureScenario scenario, string? feature)
    {
        if (scenario.Kind != ScenarioKind.Outline || scenario.Examples.All(e => e.Header is null || e.Rows.Count == 0))
        {
            yield return (text, new StepContext(feature, scenario.Title, scenario.AllTags));
            yield break;
        }
        foreach (FeatureExamples examples in scenario.Examples.Where(e => e.Header is not null))
        {
            IReadOnlyList<string> tags = [.. scenario.AllTags.Concat(examples.Tags.Select(t => t.Name)).Distinct(StringComparer.Ordinal)];
            foreach (FeatureRow row in examples.Rows)
            {
                string filled = text;
                for (int i = 0; i < examples.Header!.Cells.Count && i < row.Cells.Count; i++)
                {
                    filled = filled.Replace($"<{examples.Header.Cells[i].Value}>", row.Cells[i].Value, StringComparison.Ordinal);
                }
                yield return (filled, new StepContext(feature, scenario.Title, tags));
            }
        }
    }
}
