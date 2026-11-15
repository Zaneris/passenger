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
/// "Reqnroll.DataTable"), and an enum's member names.</summary>
public sealed record StepParameter(string Name, string Type, IReadOnlyList<string>? Members = null)
{
    public bool IsEnum => Members is not null;

    /// <summary>The type's name without its namespace ("Int32"), as Reqnroll names a parameter type.</summary>
    public string ShortType => Type[(Type.LastIndexOfAny(['.', '+']) + 1)..];
}

/// <summary>A <c>[Scope]</c>: the tag (with or without its @), feature title and scenario title a step's context must all
/// have, those given.</summary>
public sealed record StepScope(string? Tag, string? Feature, string? Scenario)
{
    /// <summary>How many of its restrictions it names, the more the closer a fit.</summary>
    public int Restrictions => (Tag is null ? 0 : 1) + (Feature is null ? 0 : 1) + (Scenario is null ? 0 : 1);
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

    /// <summary>Its type's name without its namespace.</summary>
    public string ShortType => Type[(Type.LastIndexOfAny(['.', '+']) + 1)..];

    /// <summary>How it is shown: <c>CalculatorSteps.GivenTheFirstNumberIs(int)</c>.</summary>
    public string Shown => $"{ShortType}.{Method}({string.Join(", ", Parameters.Select(p => p.ShortType))})";

    public bool Binds(StepKind kind) => Kind == BindingKind.StepDefinition || (int)Kind == (int)kind;

    /// <summary>Whether two readings name one definition (each project that sees it reads it apart): its type, method, kind,
    /// expression and parameters' types.</summary>
    public bool SameAs(StepBinding other) => Method == other.Method && Type == other.Type && Kind == other.Kind && Expression == other.Expression
        && Parameters.Select(p => p.Type).SequenceEqual(other.Parameters.Select(p => p.Type));
}

/// <summary>A <c>[StepArgumentTransformation]</c>: its regular expression (none takes any text), its name, by which a
/// Cucumber Expression names it, and the type it converts to.</summary>
public sealed record StepTransformation(string? Regex, string? Name, string ReturnType)
{
    public BindingPlace? MethodPlace { get; init; }
}

/// <summary>A hook: its kind's attribute name ("BeforeScenario"), its tags and scopes, its order, its method and type, and
/// where its method is written.</summary>
public sealed record StepHook(string Kind, string Method, string Type, IReadOnlyList<StepScope> Scopes, int Order)
{
    public BindingPlace? MethodPlace { get; init; }

    public string? Project { get; init; }

    public string ShortType => Type[(Type.LastIndexOfAny(['.', '+']) + 1)..];
}

/// <summary>Where a step runs: its feature's and scenario's titles and every tag it takes (its feature's, rule's, scenario's
/// and examples'), each with its @.</summary>
public sealed record StepContext(string? FeatureTitle, string? ScenarioTitle, IReadOnlyList<string> Tags);
