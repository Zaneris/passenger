using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Add > Docker Support on a project and Add > Docker Compose on the solution: the dialogs, then the files they
/// write (<see cref="DockerFiles"/>), each Add one command of global undo, so Undo deletes what it made and puts a project
/// or solution file back. The build's context is the solution's folder, or the folder open when there is no solution.</summary>
public partial class MainWindow
{
    /// <summary>The folder Docker builds from: the solution's, else the one open.</summary>
    private string? DockerContext => Solution is { } solution ? Path.GetDirectoryName(solution.Path) : Folder;

    /// <summary>The dialog choosing how a project's image is built; OK writes it. Null, saying why, for a project the
    /// solution does not have.</summary>
    public DockerSupportDialog? ShowDockerSupport(string project)
    {
        if (SolutionProject(project) is not { } model)
        {
            status.Text = $"{Path.GetFileName(project)} is not one of the solution's projects";
            return null;
        }
        if (Unread([model]) is { } unread)
        {
            status.Text = unread;
            return null;
        }
        DockerProject described = DockerFiles.Describe(model, session.Workspace?.Projects ?? []);
        DockerSupportDialog dialog = new(described.Name, described.ExistingDockerfile is not null);
        IdeErrors.Observe(ApplyAsync());
        return dialog;

        async Task ApplyAsync()
        {
            if (await dialog.ShowDialog<DockerSupportKind?>(this) is { } kind)
            {
                AddDockerSupport(project, kind);
            }
        }
    }

    /// <summary>Gives a project its image: a Dockerfile beside it (and a .dockerignore at the context unless one is there),
    /// or the SDK's container properties in its project file. Null once done, else why not, which the status bar says too.</summary>
    public string? AddDockerSupport(string project, DockerSupportKind kind)
    {
        if (SolutionProject(project) is not { } model || DockerContext is not { } context)
        {
            return DockerSaid($"{Path.GetFileName(project)} is not one of the solution's projects");
        }
        if (Unread([model]) is { } unread)
        {
            return DockerSaid(unread);
        }
        DockerProject described = DockerFiles.Describe(model, session.Workspace?.Projects ?? []);
        if (kind == DockerSupportKind.Sdk)
        {
            return AddContainerProperties(described);
        }
        if (described.ExistingDockerfile is { } had)
        {
            return DockerSaid($"{described.Name} has a Dockerfile already: {DockerShown(had)}");
        }
        string ignore = Path.Combine(context, ".dockerignore");
        bool ignoring = !File.Exists(ignore);
        try
        {
            using (CommandHistory.Recording made = Editors.Commands.Record("Add Docker Support"))
            {
                WriteNew(made, described.Dockerfile, DockerFiles.Dockerfile(described, context));
                if (ignoring)
                {
                    WriteNew(made, ignore, DockerFiles.Ignore);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            FilesChanged();
            return DockerSaid($"Docker Support could not be added to {described.Name}: {error.Message}");
        }
        FilesChanged();
        OpenFile(described.Dockerfile);
        return DockerSaid(null, ignoring ? $"Added a Dockerfile to {described.Name}, and a .dockerignore at {(Solution is { } solution ? $"{solution.Name}'s folder" : DockerShown(context))}" : $"Added a Dockerfile to {described.Name}");
    }

    /// <summary>The SDK's container support in a project file: EnableSdkContainerSupport where the SDK does not turn it on
    /// itself (a project that is not ASP.NET Core's), and the image's repository, its service name.</summary>
    private string? AddContainerProperties(DockerProject project)
    {
        if (SaveBeforeWriting([project.Path]) is { } unsaved)
        {
            return DockerSaid($"{project.Name} could not be changed: {unsaved}");
        }
        string? problem = null;
        try
        {
            using CommandHistory.Recording made = Editors.Commands.Record("Add Docker Support");
            made.Watch(project.Path);
            try
            {
                if (!project.Web)
                {
                    problem = ProjectEdits.SetProperty(project.Path, "EnableSdkContainerSupport", "true");
                }
                problem ??= ProjectEdits.SetProperty(project.Path, "ContainerRepository", project.Service);
            }
            finally
            {
                // Open, it takes the text now, inside the command, so the command's undo takes it back too.
                FollowWritten([project.Path]);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problem = error.Message;
        }
        return problem is null
            ? DockerSaid(null, $"{project.Name} builds its image with the SDK: dotnet publish /t:PublishContainer makes {project.Service}")
            : DockerSaid($"{project.Name} could not be changed: {problem}");
    }

    /// <summary>The dialog over the solution's projects that run and the backing services their packages point to; OK
    /// writes compose.yaml. Null, saying why, without a solution, a project that runs, or with a compose file there already.</summary>
    public DockerComposeDialog? ShowDockerCompose()
    {
        if (Solution is not { } solution || DockerContext is not { } context)
        {
            status.Text = "Add Docker Compose needs a solution: open one, and its projects become the compose file's services";
            return null;
        }
        if (ComposeFileIn(context) is { } had)
        {
            status.Text = $"{solution.Name} has a compose file already: {DockerShown(had)}";
            return null;
        }
        IReadOnlyList<ProjectModel> all = session.Workspace?.Projects ?? [];
        if (Unread(all) is { } unread)
        {
            status.Text = unread;
            return null;
        }
        List<ProjectModel> running = [.. all.Where(p => p.IsRunnable).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
        if (running.Count == 0)
        {
            status.Text = $"{solution.Name} has no project that runs, for compose.yaml to run";
            return null;
        }
        List<ComposeChoice> projects = [.. running.Select(p => new ComposeChoice(
            Path.GetFileNameWithoutExtension(p.Path),
            DockerFiles.Describe(p, all).ExistingDockerfile is { } dockerfile ? DockerShown(dockerfile) : "no Dockerfile: Docker Support adds one",
            true, p.Path))];
        List<ComposeChoice> services = [.. BackingService.Known
            .Select(k => (Service: k, Users: running.Where(p => BackingService.For(p.Packages.Select(q => q.Name)).Contains(k)).ToList()))
            .Where(k => k.Users.Count > 0)
            .Select(k => new ComposeChoice(k.Service.Service,
                $"{k.Service.Image}, for {string.Join(", ", k.Users.Select(u => Path.GetFileNameWithoutExtension(u.Path)))}{(k.Service.Note is { } note ? $" ({note})" : string.Empty)}",
                true, k.Service.Service))];
        DockerComposeDialog dialog = new(solution.Name, projects, services);
        IdeErrors.Observe(ApplyAsync());
        return dialog;

        async Task ApplyAsync()
        {
            if (await dialog.ShowDialog<bool?>(this) == true)
            {
                AddDockerCompose([.. dialog.Projects.Where(p => p.IsChosen).Select(p => (string)p.Value)], [.. dialog.Services.Where(s => s.IsChosen).Select(s => (string)s.Value)]);
            }
        }
    }

    /// <summary>Writes the solution's compose.yaml for the projects given, beside the backing services named (postgres,
    /// redis...) that their packages point to, listed in the solution's Solution Items; a project with no Dockerfile gets
    /// one, and the context a .dockerignore unless it has one. Null once done, else why not.</summary>
    public string? AddDockerCompose(IReadOnlyList<string> projects, IReadOnlyList<string> services)
    {
        if (Solution is not { } solution || DockerContext is not { } context)
        {
            return DockerSaid("Add Docker Compose needs a solution");
        }
        if (ComposeFileIn(context) is { } had)
        {
            return DockerSaid($"{solution.Name} has a compose file already: {DockerShown(had)}");
        }
        IReadOnlyList<ProjectModel> all = session.Workspace?.Projects ?? [];
        List<ProjectModel> chosen = [.. projects.Select(SolutionProject).OfType<ProjectModel>()];
        if (Unread(chosen) is { } unread)
        {
            return DockerSaid(unread);
        }
        List<(DockerProject Project, string? Existing, BackingNeeds Needs)> composed = [];
        foreach (ProjectModel model in chosen)
        {
            DockerProject described = DockerFiles.Describe(model, all);
            composed.Add((described, described.ExistingDockerfile, BackingService.Needs(model, [.. services])));
        }
        if (composed.Count == 0)
        {
            return DockerSaid("No project was chosen for compose.yaml");
        }
        if (SaveBeforeWriting([solution.Path]) is { } unsaved)
        {
            return DockerSaid($"{solution.Name} could not be changed: {unsaved}");
        }
        string compose = Path.Combine(context, "compose.yaml");
        string ignore = Path.Combine(context, ".dockerignore");
        List<string> dockerfiles = [];
        string? problem = null;
        try
        {
            using CommandHistory.Recording made = Editors.Commands.Record("Add Docker Compose");
            foreach ((DockerProject project, _, _) in composed.Where(c => c.Existing is null))
            {
                WriteNew(made, project.Dockerfile, DockerFiles.Dockerfile(project, context));
                dockerfiles.Add(project.Name);
            }
            if (!File.Exists(ignore))
            {
                WriteNew(made, ignore, DockerFiles.Ignore);
            }
            WriteNew(made, compose, DockerFiles.Compose(context, composed));
            made.Watch(solution.Path);
            try
            {
                problem = SolutionItem(solution, PathNames.Canonical(compose));
            }
            finally
            {
                // Open, it takes the text now, inside the command, so the command's undo takes it back too.
                FollowWritten([solution.Path]);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problem = error.Message;
        }
        FilesChanged();
        ReloadSolution();
        if (problem is not null)
        {
            return DockerSaid($"compose.yaml could not be added to {solution.Name}: {problem}");
        }
        OpenFile(compose);
        return DockerSaid(null, dockerfiles.Count == 0 ? $"Added compose.yaml to {solution.Name}"
            : $"Added compose.yaml to {solution.Name}, and a Dockerfile to {string.Join(", ", dockerfiles)}");
    }

    /// <summary>A file listed in the solution's Solution Items, the folder made when the solution has none.</summary>
    private static string? SolutionItem(SolutionModel solution, string path)
    {
        const string Items = "Solution Items";
        string? folder = solution.Entries.OfType<SolutionFolder>().FirstOrDefault(f => f.Name == Items)?.Path;
        if (folder is null)
        {
            if (SolutionEdits.AddFolder(solution.Path, null, Items) is { } refused)
            {
                return refused;
            }
            folder = SolutionModel.Load(solution.Path)?.Entries.OfType<SolutionFolder>().FirstOrDefault(f => f.Name == Items)?.Path;
            if (folder is null)
            {
                return $"{solution.Name} has no {Items} folder after one was added";
            }
        }
        return SolutionEdits.AddFile(solution.Path, folder, path);
    }

    /// <summary>The compose file a folder has, by the names Compose looks for; null with none.</summary>
    private static string? ComposeFileIn(string folder) =>
        new[] { "compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml" }.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);

    private ProjectModel? SolutionProject(string project) => session.Workspace?.Projects.FirstOrDefault(p => PathNames.Same(p.Path, project));

    /// <summary>Why the files cannot be written yet: a project MSBuild is still evaluating, which has yet to say its image
    /// (aspnet or runtime), framework and assembly; null once every one has been (or could not be).</summary>
    private static string? Unread(IEnumerable<ProjectModel> projects) =>
        projects.FirstOrDefault(p => p.Exists && p.Evaluated is null) is { } unread
            ? $"{unread.Name} is still being read: Docker's files are written once its image, aspnet or runtime, is known"
            : null;

    /// <summary>A new file written as its folder's .editorconfig asks (its line breaks and charset), watched first so the
    /// command's Undo deletes it.</summary>
    private static void WriteNew(CommandHistory.Recording made, string path, string text)
    {
        made.Watch(path);
        EditorConfig conventions = EditorConfig.For(path);
        FileEncoding charset = conventions.Charset ?? FileEncoding.Utf8;
        LineSeparator separator = conventions.EndOfLine ?? LineSeparator.LF;
        FileText.Save(path, LineEndings.In(text, separator.Held()), charset.Encoding, charset.Bom, separator == LineSeparator.CR);
    }

    /// <summary>A path as the status bar names it: from the folder open when it is inside it.</summary>
    private string DockerShown(string path) => PathNames.Shown(path, Folder);

    /// <summary>What the status bar says of an Add: why it did not happen (returned), else what it did (null returned).</summary>
    private string? DockerSaid(string? problem, string? done = null)
    {
        status.Text = problem ?? done ?? string.Empty;
        return problem;
    }
}
