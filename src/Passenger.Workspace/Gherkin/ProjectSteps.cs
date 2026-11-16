using System.Globalization;

namespace Passenger.Workspace.Gherkin;

/// <summary>A Reqnroll or SpecFlow project's bindings as its code declares them (its own, its referenced projects' and its
/// binding assemblies'), and the matcher of its steps, made once in its binding culture.</summary>
public sealed class ProjectSteps(string project, bool specFlow, IReadOnlyList<StepBinding> bindings, IReadOnlyList<StepHook> hooks,
    IReadOnlyList<StepTransformation> transformations)
{
    private readonly Lock made = new();
    private readonly Dictionary<string, StepMatcher> matchers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The project file.</summary>
    public string Project { get; } = project;

    public bool IsSpecFlow { get; } = specFlow;

    public IReadOnlyList<StepBinding> Bindings { get; } = bindings;

    public IReadOnlyList<StepHook> Hooks { get; } = hooks;

    public IReadOnlyList<StepTransformation> Transformations { get; } = transformations;

    /// <summary>The matcher of the project's steps in a culture (its configuration's binding culture, else its features'
    /// language, as Reqnroll takes it), made once for each; for any thread.</summary>
    public StepMatcher Matcher(string? culture)
    {
        string name = culture ?? ReqnrollConfig.DefaultFeatureLanguage;
        lock (made)
        {
            if (!matchers.TryGetValue(name, out StepMatcher? matcher))
            {
                matchers[name] = matcher = new StepMatcher(Bindings, Transformations, CultureOf(name));
            }
            return matcher;
        }
    }

    /// <summary>A binding culture by its name (en-US where none is named), the invariant culture for one .NET does not know.</summary>
    public static CultureInfo CultureOf(string? name)
    {
        try
        {
            return CultureInfo.GetCultureInfo(name ?? ReqnrollConfig.DefaultFeatureLanguage);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
