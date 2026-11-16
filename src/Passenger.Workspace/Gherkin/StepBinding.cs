using Passenger.Core;

namespace Passenger.Workspace.Gherkin;

/// <summary>What a step definition's attribute binds: Given, When or Then steps, or every kind
/// (<c>[StepDefinition]</c>).</summary>
public enum BindingKind
{
    Given,
    When,
    Then,
    StepDefinition,
}

/// <summary>A step definition method's parameter: its name, its type's full metadata name ("System.Int32",
/// "Reqnroll.DataTable"; a nullable value type's underlying type's), and an enum's member names.</summary>
public sealed record StepParameter(string Name, string Type, IReadOnlyList<string>? Members = null)
{
    public bool IsEnum => Members is not null;

    /// <summary>Whether its type is a nullable value type (of <see cref="Type"/>).</summary>
    public bool Nullable { get; init; }

    /// <summary>An enum's integer type ("System.Byte"); null for any other type.</summary>
    public string? Underlying { get; init; }

    /// <summary>Whether Reqnroll's converter makes none of its type from text, so only a transformation makes one.</summary>
    public bool Unconverted { get; init; }
}

/// <summary>A member a table helper may set from a column: its name, whether it is a property (else a field), and the
/// regular expressions of its <c>[TableAliases]</c>.</summary>
public sealed record TableMember(string Name, bool Property, IReadOnlyList<string> Aliases);

/// <summary>A table a binding's code reads into a type through Reqnroll's table helpers: the parameter it reads (by its
/// place), the helper (<c>CreateSet</c>, <c>CreateInstance</c>), the type's full name and the members a column may set.</summary>
public sealed record TableRead(int Parameter, string Helper, string Type, IReadOnlyList<TableMember> Members);

/// <summary>A <c>[Scope]</c>: the tag (with or without its @), feature title and scenario title a step's context must all
/// have, those given.</summary>
public sealed record StepScope(string? Tag, string? Feature, string? Scenario)
{
    /// <summary>How many of its restrictions it names, the more the closer a fit.</summary>
    public int Restrictions => (Tag is null ? 0 : 1) + (Feature is null ? 0 : 1) + (Scenario is null ? 0 : 1);

    /// <summary>Whether a step's context has all it names, titles in any letter case; a context of no scenario (a feature's
    /// hooks run for any) has any scenario's where <paramref name="anyScenario"/>.</summary>
    public bool Fits(StepContext context, bool anyScenario = false) =>
        (Tag is null || context.Tags.Any(t => t.TrimStart('@') == Tag.TrimStart('@')))
        && (Feature is null || string.Equals(Feature, context.FeatureTitle, StringComparison.CurrentCultureIgnoreCase))
        && (Scenario is null || (anyScenario && context.ScenarioTitle is null) || string.Equals(Scenario, context.ScenarioTitle, StringComparison.CurrentCultureIgnoreCase));
}

/// <summary>Where a binding is written: a file, and a line and character from zero as the C# server counts them.</summary>
public sealed record BindingPlace(string Path, int Line, int Column);

/// <summary>A step definition: the kind of step it binds, its expression (none where the method's name is its step), the
/// method and its type, its parameters, its scopes (any one of which fits; none, every context), whether its expression
/// was said to be a Cucumber Expression or a regular expression (null: Reqnroll tells by its text), why it cannot bind
/// where Reqnroll would refuse it, where its method's name and its expression's string are written, and its project.</summary>
public sealed record StepBinding(BindingKind Kind, string? Expression, string Method, string Type, IReadOnlyList<StepParameter> Parameters)
{
    public bool? CucumberExpression { get; init; }

    public IReadOnlyList<StepScope> Scopes { get; init; } = [];

    public string? Error { get; init; }

    public BindingPlace? MethodPlace { get; init; }

    /// <summary>Where its expression's string literal starts (its opening quote) and how long it is as written.</summary>
    public BindingPlace? ExpressionPlace { get; init; }

    public int ExpressionLength { get; init; }

    /// <summary>The project file of the code it is written in.</summary>
    public string? Project { get; init; }

    /// <summary>Its method's documentation comment's summary, as words; null for none.</summary>
    public string? Summary { get; init; }

    /// <summary>The tables its code reads through Reqnroll's table helpers.</summary>
    public IReadOnlyList<TableRead> Tables { get; init; } = [];

    /// <summary>How it is shown: <c>CalculatorSteps.GivenTheFirstNumberIs(Int32)</c>.</summary>
    public string Shown => $"{TypeNames.Short(Type)}.{Method}({string.Join(", ", Parameters.Select(p => TypeNames.Short(p.Type)))})";

    public bool Binds(StepKind kind) => Kind == BindingKind.StepDefinition || (int)Kind == (int)kind;

    /// <summary>Whether two readings name one definition (each project that sees it reads it apart): its type, method, kind,
    /// expression and parameters' types.</summary>
    public bool SameAs(StepBinding other) => Method == other.Method && Type == other.Type && Kind == other.Kind && Expression == other.Expression
        && Parameters.Select(p => p.Type).SequenceEqual(other.Parameters.Select(p => p.Type));
}

/// <summary>A <c>[StepArgumentTransformation]</c>: its regular expression (none takes any text), its name, by which a
/// Cucumber Expression names it, and the type it converts to (a nullable value type's with a <c>?</c>).</summary>
public sealed record StepTransformation(string? Regex, string? Name, string ReturnType)
{
    public BindingPlace? MethodPlace { get; init; }

    /// <summary>Its method's name, shown with its type's: <c>Transforms.ToMoney</c>.</summary>
    public string? Method { get; init; }

    /// <summary>Its parameters' types' full names.</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];

    /// <summary>The tables its code reads through Reqnroll's table helpers.</summary>
    public IReadOnlyList<TableRead> Tables { get; init; } = [];
}

/// <summary>A hook: its kind's attribute name ("BeforeScenario"), its tags and scopes, its order, its method and type, and
/// where its method is written.</summary>
public sealed record StepHook(string Kind, string Method, string Type, IReadOnlyList<StepScope> Scopes, int Order)
{
    public BindingPlace? MethodPlace { get; init; }

    public string? Project { get; init; }
}

/// <summary>Where a step runs: its feature's and scenario's titles and every tag it takes (its feature's, rule's, scenario's
/// and examples'), each with its @.</summary>
public sealed record StepContext(string? FeatureTitle, string? ScenarioTitle, IReadOnlyList<string> Tags);
