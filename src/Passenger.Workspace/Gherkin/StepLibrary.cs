using Passenger.Core;

namespace Passenger.Workspace.Gherkin;

/// <summary>A step definition as the Step Library lists it: its binding (the first reading of it where several projects see
/// it), the phrase completion writes for it with its slots, and how many feature steps it binds (null until counted).</summary>
public sealed record LibraryStep(StepBinding Binding, StepSample Sample, int? Uses = null)
{
    /// <summary>The tags its scopes name, each with its @, once each.</summary>
    public IReadOnlyList<string> Tags => StepLibrary.TagsOf(Binding.Scopes);
}

/// <summary>Every step definition and hook the solution's Reqnroll and SpecFlow projects see, each once though several
/// projects see it, in the Step Library's order: definitions by the kind of step they bind (Given, When, Then, then those
/// binding any) and their phrase; hooks in the order they run. Each definition is counted by the feature steps it binds.</summary>
public static class StepLibrary
{
    public static IReadOnlyList<LibraryStep> Definitions(IEnumerable<ProjectSteps> projects)
    {
        List<LibraryStep> found = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (StepBinding binding in projects.SelectMany(p => p.Bindings))
        {
            if (seen.Add(KeyOf(binding)))
            {
                found.Add(new LibraryStep(binding, SampleOf(binding)));
            }
        }
        return [.. found.OrderBy(s => s.Binding.Kind).ThenBy(s => s.Sample.Text, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Binding.Shown, StringComparer.Ordinal)];
    }

    public static IReadOnlyList<StepHook> Hooks(IEnumerable<ProjectSteps> projects) =>
        [.. projects.SelectMany(p => p.Hooks).DistinctBy(h => (h.Type, h.Method, h.Kind, h.Order))
            .OrderBy(h => RunOrder(h.Kind)).ThenBy(h => h.Order).ThenBy(h => h.Type, StringComparer.Ordinal).ThenBy(h => h.Method, StringComparer.Ordinal)];

    /// <summary>What completion writes for a definition: its sample, else its expression or method as written.</summary>
    public static StepSample SampleOf(StepBinding binding) => StepSamples.Of(binding) ?? new StepSample(binding.Expression ?? binding.Method, []);

    /// <summary>The definitions each with how many feature steps bind it, given the definition each step binds (a step
    /// bound by several, an outline's or an ambiguous one, once for each).</summary>
    public static IReadOnlyList<LibraryStep> Counted(IReadOnlyList<LibraryStep> steps, IEnumerable<StepBinding> bound)
    {
        Dictionary<string, int> counts = new(StringComparer.Ordinal);
        foreach (StepBinding binding in bound)
        {
            string key = KeyOf(binding);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return [.. steps.Select(s => s with { Uses = counts.GetValueOrDefault(KeyOf(s.Binding)) })];
    }

    /// <summary>The definitions a feature's matched steps bind, each once for each step it binds.</summary>
    public static IReadOnlyList<StepBinding> BoundBy(IEnumerable<StepMatch> matches) =>
        [.. matches.SelectMany(m => m.Bindings.Select(b => b.Binding).DistinctBy(KeyOf))];

    /// <summary>A definition's identity, which two readings of it share (<see cref="StepBinding.SameAs"/>).</summary>
    public static string KeyOf(StepBinding binding) =>
        $"{binding.Type}\n{binding.Method}\n{binding.Kind}\n{(binding.Expression is null ? "\0" : binding.Expression)}\n{string.Join(',', binding.Parameters.Select(p => p.Type))}";

    /// <summary>The tags scopes name, each with its @, once each.</summary>
    public static IReadOnlyList<string> TagsOf(IEnumerable<StepScope> scopes) =>
        [.. scopes.Select(s => s.Tag).OfType<string>().Select(t => "@" + t.TrimStart('@')).Distinct(StringComparer.Ordinal)];

    /// <summary>A hook's kind in words: when it runs.</summary>
    public static string HookWords(string kind) => kind switch
    {
        "BeforeTestRun" => "Before the test run",
        "BeforeFeature" => "Before each feature",
        "BeforeScenario" => "Before each scenario",
        "BeforeScenarioBlock" => "Before each block of steps",
        "BeforeStep" => "Before each step",
        "AfterStep" => "After each step",
        "AfterScenarioBlock" => "After each block of steps",
        "AfterScenario" => "After each scenario",
        "AfterFeature" => "After each feature",
        "AfterTestRun" => "After the test run",
        _ => kind,
    };

    private static readonly string[] HookKinds =
        ["BeforeTestRun", "BeforeFeature", "BeforeScenario", "BeforeScenarioBlock", "BeforeStep", "AfterStep", "AfterScenarioBlock", "AfterScenario", "AfterFeature", "AfterTestRun"];

    private static int RunOrder(string kind) => Array.IndexOf(HookKinds, kind) is var at and >= 0 ? at : HookKinds.Length;
}

/// <summary>The Step Library's filter as typed: words, each of which a definition's phrase, expression, method or class
/// must hold, and tags (written with their @), each of which narrows to the definitions and hooks scoped to a tag it
/// begins; a hook's words are when it runs, its method and its class. Letter case is passed over.</summary>
public sealed record StepFilter(IReadOnlyList<string> Words, IReadOnlyList<string> Tags)
{
    public static StepFilter Parse(string? text)
    {
        string[] terms = (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return new StepFilter([.. terms.Where(t => !t.StartsWith('@'))], [.. terms.Where(t => t.StartsWith('@')).Select(t => t[1..])]);
    }

    public bool IsEmpty => Words.Count == 0 && Tags.Count == 0;

    public bool Fits(LibraryStep step) =>
        Holds([step.Sample.Text, step.Binding.Expression, step.Binding.Method, TypeNames.Short(step.Binding.Type)]) && Scoped(step.Binding.Scopes);

    public bool Fits(StepHook hook) => Holds([StepLibrary.HookWords(hook.Kind), hook.Method, TypeNames.Short(hook.Type)]) && Scoped(hook.Scopes);

    private bool Holds(string?[] said) => Words.All(word => said.Any(s => s?.Contains(word, StringComparison.OrdinalIgnoreCase) == true));

    private bool Scoped(IReadOnlyList<StepScope> scopes) =>
        Tags.All(tag => scopes.Any(s => s.Tag is { } named && named.TrimStart('@').StartsWith(tag, StringComparison.OrdinalIgnoreCase)));
}
