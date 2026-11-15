using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Bdd;

/// <summary>The folder's Reqnroll and SpecFlow projects' bindings, as the C# server's extension last read them
/// (<see cref="Handlers.StepBindings"/>), by project; read whole again after any change of their code, since a project's
/// steps may be bound in the projects it references. Of the window's thread.</summary>
public sealed class StepCatalog
{
    private Dictionary<string, ProjectSteps> byProject = new(PathNames.Comparer);
    private bool unread = true;
    private int readAs = -1;

    /// <summary>Whether the bindings have been read once: until then no step is known to be undefined.</summary>
    public bool HasRead => !unread;

    /// <summary>Whether a read is due: none yet, or code changed since the last.</summary>
    public bool Wants => unread || readAs < Changes;

    /// <summary>How many changes have made a read due.</summary>
    public int Changes { get; private set; }

    /// <summary>Each read taken, for what reads the bindings to know it is out of date.</summary>
    public int Version { get; private set; }

    public IEnumerable<ProjectSteps> Projects => byProject.Values;

    /// <summary>A Reqnroll or SpecFlow project's bindings; null for any other project, or before the first read.</summary>
    public ProjectSteps? For(string? project) => project is not null && byProject.TryGetValue(project, out ProjectSteps? steps) ? steps : null;

    /// <summary>Raised on the window's thread once a read is taken.</summary>
    public event EventHandler? Changed;

    /// <summary>A read's answer, as of <paramref name="changes"/>: a change after it was asked stays due.</summary>
    public void Took(StepsFound found, int changes)
    {
        Dictionary<string, ProjectSteps> read = new(PathNames.Comparer);
        foreach (ProjectStepsFound project in found.Projects)
        {
            read[PathNames.Canonical(project.Project)] = new ProjectSteps(PathNames.Canonical(project.Project), project.SpecFlow,
                [.. project.Steps.Select(Binding)], [.. project.Hooks.Select(Hook)], [.. project.Transformations.Select(Transformation)]);
        }
        byProject = read;
        readAs = Math.Max(readAs, changes);
        unread = false;
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Code that may declare bindings changed: they are read again at the next read.</summary>
    public void Stale() => Changes++;

    /// <summary>Every project is read again, as at first (a new language server, a project come or gone).</summary>
    public void Forget()
    {
        unread = true;
        Changes++;
    }

    private static StepBinding Binding(ProjectStep step)
    {
        StepDefinitionFound found = step.Definition;
        return new StepBinding(Enum.TryParse(found.Kind, out BindingKind kind) ? kind : BindingKind.StepDefinition, found.Expression, found.Method, found.Type,
            [.. found.Parameters.Select(p => new StepParameter(p.Name, p.Type, p.Members))])
        {
            CucumberExpression = found.ExpressionType switch
            {
                "CucumberExpression" => true,
                "RegularExpression" => false,
                _ => null,
            },
            Scopes = [.. found.Scopes.Select(Scope)],
            Error = found.Error,
            MethodPlace = Place(found.MethodAt),
            ExpressionPlace = Place(found.ExpressionAt),
            ExpressionLength = found.ExpressionAt?.Length ?? 0,
            Project = step.DeclaredIn is { } declared ? PathNames.Canonical(declared) : null,
            Summary = found.Summary,
        };
    }

    private static StepHook Hook(StepHookFound found) =>
        new(found.Kind, found.Method, found.Type, [.. found.Scopes.Select(Scope)], found.Order) { MethodPlace = Place(found.MethodAt) };

    private static StepTransformation Transformation(StepTransformationFound found) =>
        new(found.Regex, found.Name, found.ReturnType) { MethodPlace = Place(found.MethodAt) };

    private static StepScope Scope(StepScopeFound found) => new(found.Tag, found.Feature, found.Scenario);

    /// <summary>A place as the server names it (its lines and characters), turned to the editor's where it is gone to.</summary>
    private static BindingPlace? Place(StepPlace? at) => at is null ? null : new BindingPlace(PathNames.Canonical(at.Path), at.At.Line, at.At.Character);
}
