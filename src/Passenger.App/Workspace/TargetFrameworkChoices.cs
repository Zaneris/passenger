using Passenger.App.State;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>The target framework chosen for each multi-targeted project of the folder, one choice per project: the one its
/// XAML preview draws the build of, its XAML is read against, and the language server answers for its C# files in. A
/// project's first framework Passenger runs (no .NET Framework's) is chosen until another is, and a choice is kept with
/// the folder (<see cref="WorkspaceState.TargetFrameworks"/>) while the window keeps state. A UI-thread object.</summary>
public sealed class TargetFrameworkChoices
{
    private readonly Dictionary<string, string> chosen = new(PathNames.Comparer);
    private StateDocument<WorkspaceState>? kept;

    /// <summary>A project's choice changed, by its path; null for every project's at once (the folder's kept choices taken).</summary>
    public event EventHandler<string?>? Changed;

    /// <summary>The frameworks a project offers to choose from, as it names them: those of a multi-targeted project Passenger
    /// runs, in the project's order; none for a project of one framework.</summary>
    public static IReadOnlyList<string> Offered(ProjectModel project) =>
        project.TargetFrameworks.Count < 2 ? [] : [.. project.TargetFrameworks.Where(f => !FrameworkNames.IsNetFramework(f))];

    /// <summary>The framework chosen for a project: the one kept while the project still targets it, else its first offered;
    /// null for a project of one framework, or none Passenger runs.</summary>
    public string? Of(ProjectModel project)
    {
        IReadOnlyList<string> offered = Offered(project);
        if (offered.Count == 0)
        {
            return null;
        }
        return chosen.TryGetValue(project.Path, out string? named) && offered.FirstOrDefault(f => f.Equals(named, StringComparison.OrdinalIgnoreCase)) is { } still
            ? still
            : offered[0];
    }

    /// <summary>Chooses a framework for a project, kept with the folder; nothing changes where it was chosen already.</summary>
    public void Choose(ProjectModel project, string framework)
    {
        UiThread.Check();
        if (string.Equals(Of(project), framework, StringComparison.OrdinalIgnoreCase)
            || !Offered(project).Contains(framework, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        chosen[project.Path] = framework;
        if (kept is { } document)
        {
            document.Set(document.Value with { TargetFrameworks = Kept });
        }
        Changed?.Invoke(this, project.Path);
    }

    /// <summary>The choices made, by project, as the folder keeps them.</summary>
    public Dictionary<string, string> Kept => new(chosen, StringComparer.Ordinal);

    /// <summary>The folder's documents from now on: the choices it kept taken first, each project following its own.</summary>
    public void Keep(StateDocument<WorkspaceState> document)
    {
        UiThread.Check();
        kept = document;
        foreach ((string project, string framework) in document.Value.TargetFrameworks)
        {
            chosen[project] = framework;
        }
        if (document.Value.TargetFrameworks.Count > 0)
        {
            Changed?.Invoke(this, null);
        }
    }
}
