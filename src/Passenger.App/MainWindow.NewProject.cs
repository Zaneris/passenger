using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.MSBuild;
using Passenger.App.Run;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>New Solution (File, the Welcome screen, the project widget) and New Project (Add on the solution and a
/// solution folder in the Solution view, and its keys), both in <see cref="NewProjectDialog"/> from the templates
/// dotnet new has installed and those of the known packages it can install (<see cref="TemplatePacks"/>). A new
/// solution opens as the workspace through <see cref="Open"/>, kept in Recent Solutions, and builds in the window it
/// opens in; a new project goes into the open solution, in the solution folder chosen, and the solution filter open, by
/// <see cref="SolutionEdits"/>, as one command Undo takes back whole. A service is set up to run as the dialog chose
/// (<see cref="SetUpAsync"/>) once it is open.</summary>
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
        NewProjectDialog dialog = new(BesideLast(), null, ContainersWhyNot());
        dialog.Closed += (_, _) =>
        {
            if (dialog.Created is { Solution: { } solution } made)
            {
                IdeErrors.Observe(OpenNewSolutionAsync(solution, made));
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
        ProjectTarget target = new(solution.Path, into, [.. solution.Projects.Select(p => Path.GetFileNameWithoutExtension(p.Path))],
            Projects.FirstOrDefault(p => p.Exists && p.IsAspireHost)?.Name);
        NewProjectDialog dialog = new(ProjectFolderFor(solution, into), target, ContainersWhyNot());
        dialog.Closed += (_, _) =>
        {
            if (dialog.Created is { } made && Solution?.Path == solution.Path)
            {
                Task<string?> adding = AddMadeAsync(made);
                ServiceSetUp = adding;
                IdeErrors.Observe(adding);
            }
        };
        IdeErrors.Observe(dialog.ShowDialog(this));
        return dialog;

        async Task<string?> AddMadeAsync(CreatedProjects made)
        {
            using CommandHistory.Recording recording = Editors.Commands.RecordWrites($"New Project {Path.GetFileNameWithoutExtension(made.Projects[0])}");
            string? wrote = TemplateWrote(made, recording);
            string? unrestored = made.Central.Count > 0 ? await CentralizeAsync(made, recording) : made.Unrestored;
            if (await AddEachAsync(made.Projects, (file, project) => SolutionEdits.AddProject(file, into, project), Path.GetFileNameWithoutExtension, recording,
                projects: true, besides: wrote) is not { } last)
            {
                return status.Text;
            }
            Reveal(into, last);
            string? problem = null;
            if (made.Setup != ProjectSetup.None)
            {
                (problem, string said) = await SetUpAsync(made.Projects[0], made.Setup, recording);
                if (wrote is not null)
                {
                    status.Say($"{said}; {wrote}", problem is null ? null : ProblemSeverity.Warning);
                }
            }
            if (problem is null && await UnlandedAsync(recording) is { } unlanded)
            {
                problem = status.Text = $"{Path.GetFileNameWithoutExtension(made.Projects[0])} could not be set up: {unlanded.Message}";
            }
            if (problem is null && unrestored is not null)
            {
                problem = unrestored;
                status.Text = wrote is null ? unrestored : $"{unrestored}; {wrote}";
            }
            // What the template and the set-up made in the project's folder goes and comes back with the folder.
            recording.Leave(made.Folder);
            recording.Also(await MadeFolderAsync(made.Folder));
            return problem;
        }
    }

    /// <summary>What a new project's template wrote outside its folder (a test template's global.json) made part of New
    /// Project's command, which its Undo takes back, as the status bar says it ("its template made global.json"); null for
    /// nothing. A file open in the editor follows the disk as it does, its change no part of the command.</summary>
    private string? TemplateWrote(CreatedProjects made, CommandHistory.Recording recording)
    {
        List<string> madeThere = [];
        List<string> changed = [];
        foreach ((string path, FileText? before) in made.Outside)
        {
            if (Editors.Documents.Any(d => PathNames.Same(d.Path, path)))
            {
                (before is null ? madeThere : changed).Add(PathNames.Shown(path, Folder));
            }
            else if (before is null)
            {
                recording.Made(path);
                madeThere.Add(PathNames.Shown(path, Folder));
            }
            else if (File.Exists(path) && FileText.Load(path) is { } now)
            {
                recording.Wrote(path, TextSnapshot.From(before.Text), TextSnapshot.From(now.Text));
                changed.Add(PathNames.Shown(path, Folder));
            }
        }
        List<string> said = [];
        if (madeThere.Count > 0)
        {
            said.Add($"made {Together(madeThere)}");
        }
        if (changed.Count > 0)
        {
            said.Add($"changed {Together(changed)}");
        }
        return said.Count == 0 ? null : $"its template {string.Join(" and ", said)}";
    }

    /// <summary>The projects New Project or New Solution made where packages are managed centrally put in that form (their
    /// versions moved into the Directory.Packages.props that manages them, <see cref="CentralPackages"/>), within the command
    /// given (else on their own), then what was made restored: why one could not be written or restored, else null.</summary>
    private async Task<string?> CentralizeAsync(CreatedProjects made, CommandHistory.Recording? into)
    {
        string name = Path.GetFileNameWithoutExtension(made.Projects[0]);
        List<string> paths = [.. made.Central.Select(c => c.Project), .. made.Central.Select(c => c.Central.Props).Distinct(PathNames.Comparer)];
        if (SaveBeforeWriting(paths) is { } unsaved)
        {
            return $"{name} did not take central package management's form: {unsaved}";
        }
        foreach (string path in paths)
        {
            into?.Watch(path);
        }
        try
        {
            Following(paths, () =>
            {
                Dictionary<string, List<(string Id, string Version)>> pins = new(PathNames.Comparer);
                foreach ((string project, CentralPackages central) in made.Central)
                {
                    (string text, IReadOnlyList<(string Id, string Version)> pinned) = ProjectEdits.Centralized(Written(project).Text, central.Versions, central.Overrides);
                    Write(project, text);
                    (pins.TryGetValue(central.Props, out List<(string Id, string Version)>? held) ? held : pins[central.Props] = []).AddRange(pinned);
                }
                foreach ((string props, List<(string Id, string Version)> pinned) in pins.Where(p => p.Value.Count > 0))
                {
                    Write(props, CentralPackages.Pinned(Written(props).Text, pinned));
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{name} did not take central package management's form: {error.Message}";
        }
        finally
        {
            FollowWritten(paths);
        }
        if (await (into is null ? UnlandedAsync(paths) : UnlandedAsync(into)) is { } unlanded)
        {
            return $"{name} did not take central package management's form: {unlanded.Message}";
        }
        foreach (string project in made.Projects)
        {
            status.Text = $"Restoring {Path.GetFileNameWithoutExtension(project)}…";
            if (await ProjectTemplates.RestoreAsync(project, made.Folder) is { } said)
            {
                return $"{Path.GetFileNameWithoutExtension(project)} did not restore: {said}";
            }
        }
        return null;

        static FileText Written(string path) => FileText.Load(path) ?? throw new IOException($"{Path.GetFileName(path)} is not a text file");

        static void Write(string path, string text)
        {
            FileText file = Written(path);
            if (file.Text != text)
            {
                FileText.SaveOver(path, file, text);
            }
        }
    }

    /// <summary>Why no container can be set up here, asked off the window's thread: no container engine's command.</summary>
    private Task<string?> ContainersWhyNot() =>
        BackgroundWork.Run(() => Engine.Cli is null ? "No container engine's docker command is installed here" : null);

    /// <summary>The new solution with its project in it, opened as the workspace and built, ready to run: by the window
    /// it opens in once its session there has begun, since Open may first ask where, open it in another window, or
    /// leave this one's solution first, once the solution's file holds them.</summary>
    private async Task OpenNewSolutionAsync(string solution, CreatedProjects made)
    {
        IReadOnlyList<string> projects = made.Projects;
        string? unrestored = made.Central.Count > 0 ? await CentralizeAsync(made, null) : made.Unrestored;
        List<string> refused = [.. projects.Select(project => Change(() => SolutionEdits.AddProject(solution, null, project), Path.GetFileNameWithoutExtension(solution)))
            .OfType<string>().Where(problem => !problem.EndsWith("is already in the solution", StringComparison.Ordinal))];
        if (unrestored is not null)
        {
            refused.Add(unrestored);
        }
        if (await UnlandedAsync([solution]) is { } unlanded)
        {
            refused.Add($"{Path.GetFileNameWithoutExtension(solution)} could not be changed: {unlanded.Message}");
        }
        Open(solution, opened =>
        {
            if (refused.Count > 0)
            {
                opened.status.Text = string.Join("; ", refused);
                return;
            }
            if (made.Setup != ProjectSetup.None && projects.Count > 0)
            {
                Task<string?> setUp = opened.SetUpNewAsync(projects[0], made.Setup);
                opened.ServiceSetUp = setUp;
                IdeErrors.Observe(setUp);
                return;
            }
            opened.BuildSolution();
        });
    }

    /// <summary>A new solution's service set up to run as New Solution chose, one command Undo takes back, then the solution
    /// built, how the set-up went said again before the build's words once it ends, a failure marked as one: null once its
    /// configuration is chosen, else why not.</summary>
    private async Task<string?> SetUpNewAsync(string project, ProjectSetup setup)
    {
        string? problem;
        string said;
        using (CommandHistory.Recording recording = Editors.Commands.RecordWrites($"Set Up {Path.GetFileNameWithoutExtension(project)} with {SetupWords(setup)}"))
        {
            (problem, said) = await SetUpAsync(project, setup, recording);
            if (problem is null && await UnlandedAsync(recording) is { } unlanded)
            {
                (problem, said) = NotSetUp($"{Path.GetFileNameWithoutExtension(project)} could not be set up: {unlanded.Message}");
            }
        }
        TaskCompletionSource<BuildOutcome> building = new(TaskCreationOptions.RunContinuationsAsynchronously);
        BuildSolution(ended => building.TrySetResult(ended));
        BuildOutcome built = await building.Task;
        status.Say($"{said}; {built.Message}", problem is null ? null : ProblemSeverity.Warning, buildRun.Place(built));
        return problem;
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
