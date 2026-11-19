using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CucumberExpressions;
using Passenger.Core;
using Passenger.Text;

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
/// that Reqnroll would still refuse it (their parameters, their scope), with why; and the arguments a definition is refused
/// for, where they are written.</summary>
public sealed record StepMatch(MatchStatus Status, IReadOnlyList<BoundStep> Bindings, string? Why = null)
{
    public static readonly StepMatch None = new(MatchStatus.Undefined, []);

    public IReadOnlyList<RefusedArgument> Refused { get; init; } = [];
}

/// <summary>An argument a definition is refused for: where it is written (a line and column of the file; of a text matched
/// alone, line 0 and its place in the text), why, and whether a run binds the step and fails converting it (else no
/// definition binds the step, one whose expression would but for this argument); or, <see cref="Unjudged"/>, one whose check
/// could not finish, which neither takes nor refuses it.</summary>
public sealed record RefusedArgument(int Line, int Column, int Length, string Why, bool Fails)
{
    /// <summary>Whether the argument's check stopped at a regex's time limit, so it is said unchecked, not refused.</summary>
    public bool Unjudged { get; init; }
}

/// <summary>Steps matched to step definitions as Reqnroll's runtime matches them (its StepDefinitionMatchService): each
/// definition of the step's kind whose regular expression matches the whole text, takes as many parameters as the text's
/// arguments and the step's doc string and table, can convert each argument in the binding culture
/// (<see cref="StepConversions"/>), and fits the step's context by one of its scopes; of several, those that fit by the
/// most of a scope's restrictions; of none, the one whose only fault is an argument it cannot convert, which a run binds and
/// fails. Made once for a set of definitions, each expression made once.</summary>
public sealed class StepMatcher
{
    private readonly (StepBinding Binding, Regex? Regex, string? Error)[] compiled;

    /// <summary>Each Cucumber Expression's regular expression with every parameter taking any text, and its parameters' types,
    /// to tell where a step it would bind gives an argument its parameter type refuses.</summary>
    private readonly Dictionary<StepBinding, (Regex Regex, IParameterType[] Types)> loose = new(ReferenceEqualityComparer.Instance);
    private readonly CultureInfo culture;

    /// <summary>The definitions whose expression ran past its time on a step, which match nothing from then on.</summary>
    private readonly ConcurrentDictionary<StepBinding, string> stopped = new(ReferenceEqualityComparer.Instance);

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
        foreach ((StepBinding binding, Regex? regex, string? error) in compiled)
        {
            if (regex is not null && error is null && StepExpressions.LooseOf(binding, Types) is { } near)
            {
                loose[binding] = near;
            }
        }
    }

    public IReadOnlyList<StepBinding> Bindings { get; }

    public IReadOnlyList<StepTransformation> Transformations { get; }

    public ReqnrollParameterTypes Types { get; }

    /// <summary>Why a definition cannot bind any step (its own error, or its expression's); null where it can.</summary>
    public string? ErrorOf(StepBinding binding) =>
        stopped.TryGetValue(binding, out string? why) ? why : compiled.FirstOrDefault(c => ReferenceEquals(c.Binding, binding)).Error;

    /// <summary>A step of a file read as it runs: an outline's once for each example row with its values (the context
    /// taking the row's examples' tags), a background's in the context of each scenario after it; matched where every
    /// run binds it, each definition listed once, every run's refused arguments placed where they are written (an example's
    /// value in its cell).</summary>
    public StepMatch Match(FeatureStep step, FeatureReading reading)
    {
        if (!Matchable(step))
        {
            return StepMatch.None;
        }
        List<StepMatch> runs = [];
        List<RefusedArgument> refused = [];
        foreach (Run run in RunsOf(step, reading))
        {
            StepMatch match = Match(step.Kind, run.Text, step.DocString is not null, step.Table is not null, run.Context);
            runs.Add(match);
            refused.AddRange(match.Refused.Select(r => Placed(r, step, run)).Where(r => !refused.Contains(r)));
        }
        // Runs of one text (a background's, an outline's step with no placeholder) give their arguments where they are.
        bool alike = step.Placeholders.Count == 0;
        if (runs.Count == 1 && alike)
        {
            return runs[0] with { Refused = refused };
        }
        if (runs.FirstOrDefault(r => r.Status == MatchStatus.Undefined) is { } undefined)
        {
            return undefined with { Bindings = [], Refused = refused };
        }
        if (runs.FirstOrDefault(r => r.Status == MatchStatus.Ambiguous) is { } ambiguous)
        {
            return ambiguous with { Bindings = [.. ambiguous.Bindings.Select(b => b with { Arguments = [] })], Refused = refused };
        }
        List<BoundStep> bound = [];
        foreach (BoundStep each in runs.SelectMany(r => r.Bindings).Where(each => !bound.Any(b => b.Binding.SameAs(each.Binding))))
        {
            bound.Add(alike ? each : each with { Arguments = [] });
        }
        return new StepMatch(MatchStatus.Defined, bound) { Refused = refused };
    }

    /// <summary>Whether a step can be matched as it stands: one with placeholders needs its outline's examples to have rows,
    /// as it is said of nothing until then (an outline being written).</summary>
    public static bool Matchable(FeatureStep step) =>
        step.Placeholders.Count == 0 || step.Scenario.Examples.Any(e => e.Header is not null && e.Rows.Count > 0);

    /// <summary>A step's text matched in a context.</summary>
    public StepMatch Match(StepKind kind, string text, bool hasDocString, bool hasTable, StepContext context)
    {
        List<(StepBinding Binding, Match Match, int Fit)> fitting = [];
        List<string> refused = [];
        // Those whose expression and scope fit but not their parameters, and whether one fits all but its scope.
        List<(StepBinding Binding, Match Match, RefusedArgument[] Arguments)> unconverted = [];
        // Arguments of definitions that fit whose check could not finish, said unchecked where the step binds to them.
        List<(StepBinding Binding, RefusedArgument Argument)> unjudged = [];
        bool outOfScope = false;
        foreach ((StepBinding binding, Regex? regex, string? error) in compiled)
        {
            if (regex is null || error is not null || !binding.Binds(kind) || stopped.ContainsKey(binding) || MatchOf(binding, regex, text) is not { Success: true } match)
            {
                continue;
            }
            Group[] captured = [.. match.Groups.Cast<Group>().Skip(1).Where(g => g.Success)];
            int arguments = captured.Length + (hasDocString ? 1 : 0) + (hasTable ? 1 : 0);
            int? fit = Fit(binding, context);
            if (arguments != binding.Parameters.Count)
            {
                refused.Add($"{binding.Shown} takes {binding.Parameters.Count} parameters, and the step gives {arguments}");
                if (fit is not null)
                {
                    unconverted.Add((binding, match, []));
                }
                continue;
            }
            List<RefusedArgument> wrong = [];
            for (int i = 0; i < captured.Length; i++)
            {
                if (Refusal(binding, i, captured[i]) is not { } refusal)
                {
                    continue;
                }
                if (refusal.Unjudged)
                {
                    unjudged.Add((binding, refusal));
                    continue;
                }
                if (wrong.Count == 0)
                {
                    refused.Add($"{binding.Shown} cannot take \"{captured[i].Value}\" as {TypeNames.Short(binding.Parameters[i].Type)}");
                }
                wrong.Add(refusal);
            }
            if (wrong.Count > 0)
            {
                if (fit is not null)
                {
                    unconverted.Add((binding, match, [.. wrong]));
                }
                continue;
            }
            if (fit is null)
            {
                refused.Add($"{binding.Shown} is scoped to another feature, scenario or tag");
                outOfScope = true;
                continue;
            }
            fitting.Add((binding, match, fit.Value));
        }
        if (fitting.Count == 0)
        {
            string? why = refused.Count > 0 ? string.Join("\n", refused) : null;
            if (outOfScope)
            {
                return new StepMatch(MatchStatus.Undefined, [], why);
            }
            unconverted = [.. unconverted.DistinctBy(u => (u.Binding.Type, u.Binding.Method))];
            // Reqnroll runs the one definition refused only for its parameters, failing as it converts them.
            if (unconverted is [{ Arguments.Length: > 0 } only])
            {
                return new StepMatch(MatchStatus.Defined, [new BoundStep(only.Binding, ArgumentsOf(only.Match))]) { Refused = only.Arguments };
            }
            RefusedArgument[] arguments = unconverted.Count > 0 ? [.. unconverted.SelectMany(u => u.Arguments)] : NearlyBound(kind, text, hasDocString, hasTable, context);
            return new StepMatch(MatchStatus.Undefined, [], why) { Refused = arguments };
        }
        int best = fitting.Max(f => f.Fit);
        // One method under several attributes that match is one definition, as Reqnroll takes it.
        BoundStep[] bound = [.. fitting.Where(f => f.Fit == best).DistinctBy(f => (f.Binding.Type, f.Binding.Method))
            .Select(f => new BoundStep(f.Binding, ArgumentsOf(f.Match)))];
        return new StepMatch(bound.Length == 1 ? MatchStatus.Defined : MatchStatus.Ambiguous, bound)
        {
            Refused = [.. unjudged.Where(u => bound.Any(b => ReferenceEquals(b.Binding, u.Binding))).Select(u => u.Argument)],
        };
    }

    /// <summary>An argument a definition's parameter cannot take, as Reqnroll converts it; null where it takes it; unjudged
    /// where a transformation's expression stopped at its time limit.</summary>
    private RefusedArgument? Refusal(StepBinding binding, int index, Group argument)
    {
        StepParameter parameter = binding.Parameters[index];
        try
        {
            return StepConversions.Refusal(argument.Value, parameter, Transformations, culture) is { } takes
                ? new RefusedArgument(0, argument.Index, argument.Length, $"{binding.Shown} takes {parameter.Name} as {takes}, which \"{argument.Value}\" is not", true)
                : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return new RefusedArgument(0, argument.Index, argument.Length, $"Whether {binding.Shown} takes \"{argument.Value}\" as {parameter.Name} was not checked: {TextSearch.RegexStopped}", false)
            {
                Unjudged = true,
            };
        }
    }

    /// <summary>The arguments of a step no definition binds that a Cucumber Expression's parameter types refuse, where its
    /// expression would match with any text in their place: the step its definition was likely written for.</summary>
    private RefusedArgument[] NearlyBound(StepKind kind, string text, bool hasDocString, bool hasTable, StepContext context)
    {
        List<RefusedArgument> found = [];
        foreach ((StepBinding binding, (Regex regex, IParameterType[] types)) in loose)
        {
            if (!binding.Binds(kind) || stopped.ContainsKey(binding) || Fit(binding, context) is null || MatchOf(binding, regex, text) is not { Success: true } match
                || match.Groups.Count - 1 != types.Length || types.Length + (hasDocString ? 1 : 0) + (hasTable ? 1 : 0) != binding.Parameters.Count)
            {
                continue;
            }
            for (int i = 0; i < types.Length; i++)
            {
                Group argument = match.Groups[i + 1];
                try
                {
                    if (StepExpressions.ParameterRefusal(types[i], argument.Value) is { } takes)
                    {
                        found.Add(new RefusedArgument(0, argument.Index, argument.Length,
                            $"No step definition binds \"{text}\": {binding.Shown} would, but its {{{types[i].Name}}} takes {takes}, which \"{argument.Value}\" is not", false));
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    found.Add(new RefusedArgument(0, argument.Index, argument.Length,
                        $"No step definition binds \"{text}\": {binding.Shown} would, but whether its {{{types[i].Name}}} takes \"{argument.Value}\" was not checked: {TextSearch.RegexStopped}", false)
                    {
                        Unjudged = true,
                    });
                }
            }
        }
        return [.. found];
    }

    /// <summary>A refused argument of a run placed where it is written: an example's value in its cell, else the step's text,
    /// an argument across a placeholder spanning it whole.</summary>
    private static RefusedArgument Placed(RefusedArgument refused, FeatureStep step, Run run)
    {
        int start = refused.Column;
        int end = start + refused.Length;
        if (run.Values.FirstOrDefault(v => v.Start <= start && end <= v.Start + v.Cell.Value.Length) is { Row: { } row } value)
        {
            return refused with { Line = row.Line, Column = value.Cell.Column + start - value.Start };
        }
        int written = Written(start, run, end: false);
        return refused with { Line = step.Line, Column = step.TextColumn + written, Length = Written(end, run, end: true) - written };
    }

    /// <summary>A place of a run's text as a place of the step's text: one inside an example's value at its placeholder's
    /// start or end.</summary>
    private static int Written(int at, Run run, bool end)
    {
        int shift = 0;
        foreach ((int start, FeatureRow _, FeatureCell cell, int placeholder) in run.Values)
        {
            if (at <= start)
            {
                break;
            }
            if (at < start + cell.Value.Length || (at == start + cell.Value.Length && end))
            {
                return start - shift + (end ? placeholder : 0);
            }
            shift += cell.Value.Length - placeholder;
        }
        return at - shift;
    }

    /// <summary>The steps of the files given that a definition binds, each with its file's reading.</summary>
    public IEnumerable<(FeatureReading Reading, FeatureStep Step)> UsesOf(StepBinding binding, IEnumerable<FeatureReading> readings) =>
        readings.SelectMany(r => r.Steps.Select(s => (r, s))).Where(u => Match(u.s, u.r).Bindings.Any(b => b.Binding.SameAs(binding)));

    /// <summary>A definition's expression matched to a text; null where it ran past its time, after which it matches nothing.</summary>
    private Match? MatchOf(StepBinding binding, Regex regex, string text)
    {
        try
        {
            return regex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            stopped[binding] = $"Its expression: {TextSearch.RegexStoppedIn($"matching \"{text}\"")}";
            return null;
        }
    }

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
            if (scope.Fits(context))
            {
                best = Math.Max(best ?? 0, scope.Restrictions);
            }
        }
        return best;
    }

    /// <summary>A run of a step: its text, its context, and the example's values put in its placeholders' places, each with
    /// where it starts in the text, its row and cell, and its placeholder's length.</summary>
    private sealed record Run(string Text, StepContext Context, IReadOnlyList<(int Start, FeatureRow Row, FeatureCell Cell, int Placeholder)> Values);

    /// <summary>Each run of a step: its text with an example's values in place of its placeholders, in that run's context.</summary>
    private static IEnumerable<Run> RunsOf(FeatureStep step, FeatureReading reading)
    {
        FeatureScenario scenario = step.Scenario;
        string? feature = reading.Feature?.Title;
        if (scenario.Kind == ScenarioKind.Background)
        {
            // A feature's background runs before every scenario of it, those under its rules too; a rule's before its own.
            FeatureScenario[] after = [.. reading.Scenarios.Where(s => s.Kind != ScenarioKind.Background && s.Line > scenario.Line && (scenario.Rule is null || s.Rule == scenario.Rule))];
            if (after.Length == 0)
            {
                return [new Run(step.Text, new StepContext(feature, null, scenario.AllTags), [])];
            }
            return after.SelectMany(s => RunsOf(step, s, feature));
        }
        return RunsOf(step, scenario, feature);
    }

    private static IEnumerable<Run> RunsOf(FeatureStep step, FeatureScenario scenario, string? feature)
    {
        string text = step.Text;
        if (scenario.Kind != ScenarioKind.Outline || scenario.Examples.All(e => e.Header is null || e.Rows.Count == 0))
        {
            yield return new Run(text, new StepContext(feature, scenario.Title, scenario.AllTags), []);
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
                yield return new Run(filled, new StepContext(feature, scenario.Title, tags), ValuesOf(step, examples.Header, row, filled));
            }
        }
    }

    /// <summary>Where a row's values stand in a run's text, each placeholder in turn given its column's value; none where
    /// that reading differs from the text (a value naming another placeholder).</summary>
    private static List<(int Start, FeatureRow Row, FeatureCell Cell, int Placeholder)> ValuesOf(FeatureStep step, FeatureRow header, FeatureRow row, string filled)
    {
        List<(int Start, FeatureRow Row, FeatureCell Cell, int Placeholder)> values = [];
        StringBuilder made = new();
        int done = 0;
        foreach (FeaturePlaceholder placeholder in step.Placeholders)
        {
            int at = placeholder.Column - step.TextColumn;
            int column = header.Cells.ToList().FindIndex(c => c.Value == placeholder.Name);
            if (column < 0 || column >= row.Cells.Count || at < done)
            {
                continue;
            }
            made.Append(step.Text, done, at - done);
            values.Add((made.Length, row, row.Cells[column], placeholder.Name.Length + 2));
            made.Append(row.Cells[column].Value);
            done = at + placeholder.Name.Length + 2;
        }
        made.Append(step.Text, done, step.Text.Length - done);
        return made.ToString() == filled ? values : [];
    }
}
