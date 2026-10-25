using Passenger.App.Explorer;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>New Solution (File, the Welcome screen, the project widget) and New Project (Add on the solution and a
/// solution folder in the Solution view, and its keys), both in <see cref="NewProjectDialog"/> from the templates
/// dotnet new has installed and those of the known packages it can install (<see cref="TemplatePacks"/>). A new
/// solution opens as the workspace through <see cref="Open"/>, kept in Recent Solutions, and builds in the window it
/// opens in; a new project goes into the open solution, in the solution folder chosen, by
/// <see cref="SolutionEdits"/>.</summary>
public partial class MainWindow
{
    private void AddNewProjectActions()
    {
        actions!.Add(new IdeAction(AppActions.NewSolution, "New Solution…", "IDE", _ => ShowNewSolution())
        {
            Menus = [new MenuPlace("File", 0, -1)],
        });
        actions.Add(new IdeAction(AppActions.NewProject, "New Project…", "IDE", _ => ShowNewProject(ChosenSolutionFolder()))
        {
            WhyNot = _ => Solution is null ? Refusals.NoSolution : null,
        });
    }

    /// <summary>dotnet new's templates read ahead of New Solution (where it puts a solution) once a person's window is up,
    /// so the dialog opens with them; a process reads them once and keeps them fresh (<see cref="ProjectTemplates.Preload"/>).
    /// An agent's headless IDE and a screenshot, which keep no settings, read them only when their dialog opens.</summary>
    private void PreloadTemplates()
    {
        if (KeepsSettings)
        {
            ProjectTemplates.Preload(BesideLast());
        }
    }

    /// <summary>New Solution: the dialog over this window, and the solution opened, built, once it is made.</summary>
    public NewProjectDialog ShowNewSolution()
    {
        NewProjectDialog dialog = new(BesideLast(), null);
        dialog.Closed += (_, _) =>
        {
            if (dialog.Created is { Solution: { } solution } made)
            {
                OpenNewSolution(solution, made.Projects);
            }
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    /// <summary>Add, New Project: the dialog over this window for a project in the open solution, in a solution folder
    /// (by its path in the solution) or at its top; null with no solution open.</summary>
    public NewProjectDialog? ShowNewProject(string? into)
    {
        if (Solution is not { } solution)
        {
            status.Text = Refusals.NoSolution;
            return null;
        }
        ProjectTarget target = new(solution.Path, into, [.. solution.Projects.Select(p => Path.GetFileNameWithoutExtension(p.Path))]);
        NewProjectDialog dialog = new(ProjectFolderFor(solution, into), target);
        dialog.Closed += (_, _) =>
        {
            if (dialog.Created is { } made && Solution?.Path == solution.Path
                && AddEach(made.Projects, (file, project) => SolutionEdits.AddProject(file, into, project), Path.GetFileNameWithoutExtension) is { } last)
            {
                Reveal(into, last);
            }
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;
    }

    /// <summary>The new solution with its project in it, opened as the workspace and built, ready to run: by the window
    /// it opens in once its session there has begun, since Open may first ask where, open it in another window, or
    /// leave this one's solution first.</summary>
    private void OpenNewSolution(string solution, IReadOnlyList<string> projects)
    {
        List<string> refused = [.. projects.Select(project => Change(() => SolutionEdits.AddProject(solution, null, project), Path.GetFileNameWithoutExtension(solution)))
            .OfType<string>().Where(problem => !problem.EndsWith("is already in the solution", StringComparison.Ordinal))];
        Open(solution, opened =>
        {
            if (refused.Count > 0)
            {
                opened.status.Text = string.Join("; ", refused);
                return;
            }
            opened.BuildSolution();
        });
    }

    /// <summary>Where a new project goes by default: the folder on disk a solution folder is named after (src for /src/),
    /// when the solution's folder has it, else the solution's folder.</summary>
    private static string ProjectFolderFor(SolutionModel solution, string? into)
    {
        string root = Path.GetDirectoryName(solution.Path)!;
        if (into is null)
        {
            return root;
        }
        string named = Path.Combine([root, .. into.Split('/', StringSplitOptions.RemoveEmptyEntries)]);
        return Directory.Exists(named) ? PathNames.Canonical(named) : root;
    }

    /// <summary>The solution folder chosen in the Solution view, where New Project's keys put a project.</summary>
    private string? ChosenSolutionFolder() =>
        Explorer.Selected is { Kind: NodeKind.SolutionFolder, SolutionPath: { } path } ? path : null;
}
