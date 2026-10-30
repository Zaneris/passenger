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
        if (DockerSupportWhyNot(project) is { } refused)
        {
            status.Text = refused;
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
                await AddDockerSupportAsync(project, kind);
            }
        }
    }

    /// <summary>Gives a project its image: a Dockerfile beside it (and a .dockerignore at the context, or the line keeping
    /// nuget.config out of the one there: <see cref="KeepNuGetConfigOut"/>), or the SDK's container properties in its
    /// project file. Null once done and landed, else why not, which the status bar says too. Given a command
    /// (<paramref name="into"/>), it is part of that one, else a command of its own.</summary>
    public async Task<string?> AddDockerSupportAsync(string project, DockerSupportKind kind, CommandHistory.Recording? into = null)
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
            return await AddContainerPropertiesAsync(described, into);
        }
        if (described.ExistingDockerfile is { } had)
        {
            return DockerSaid($"{described.Name} has a Dockerfile already: {DockerShown(had)}");
        }
        string ignore = Path.Combine(context, ".dockerignore");
        if (SaveBeforeWriting([ignore]) is { } unsaved)
        {
            return DockerSaid($"Docker Support could not be added to {described.Name}: {unsaved}");
        }
        IgnoreWritten ignoring = IgnoreWritten.Nothing;
        using CommandHistory.Recording? own = into is null ? Editors.Commands.Record("Add Docker Support") : null;
        CommandHistory.Recording made = into ?? own!;
        try
        {
            Following([described.Dockerfile, ignore], () =>
            {
                WriteNew(made, described.Dockerfile, DockerFiles.Dockerfile(described, context));
                ignoring = KeepNuGetConfigOut(made, ignore);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return DockerSaid($"Docker Support could not be added to {described.Name}: {error.Message}");
        }
        if (await UnlandedAsync(made) is { } unlanded)
        {
            return DockerSaid($"Docker Support could not be added to {described.Name}: {unlanded.Message}");
        }
        OpenFile(described.Dockerfile);
        string at = Solution is { } solution ? $"{solution.Name}'s folder" : DockerShown(context);
        return DockerSaid(null, ignoring switch
        {
            IgnoreWritten.Made => $"Added a Dockerfile to {described.Name}, and a .dockerignore at {at}",
            IgnoreWritten.NuGetConfigLeftOut => $"Added a Dockerfile to {described.Name}, and to the .dockerignore at {at} {NuGetConfigLine}",
            _ => $"Added a Dockerfile to {described.Name}",
        });
    }

    /// <summary>The SDK's container support in a project file: EnableSdkContainerSupport where the SDK does not turn it on
    /// itself (a project that is not ASP.NET Core's), and the image's repository, its service name.</summary>
    private async Task<string?> AddContainerPropertiesAsync(DockerProject project, CommandHistory.Recording? into)
    {
        if (SaveBeforeWriting([project.Path]) is { } unsaved)
        {
            return DockerSaid($"{project.Name} could not be changed: {unsaved}");
        }
        string? problem = null;
        try
        {
            using CommandHistory.Recording? own = into is null ? Editors.Commands.Record("Add Docker Support") : null;
            CommandHistory.Recording made = into ?? own!;
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
            if (problem is null && await UnlandedAsync(made) is { } unlanded)
            {
                problem = unlanded.Message;
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
                await AddDockerComposeAsync([.. dialog.Projects.Where(p => p.IsChosen).Select(p => (string)p.Value)], [.. dialog.Services.Where(s => s.IsChosen).Select(s => (string)s.Value)]);
            }
        }
    }

    /// <summary>Writes the solution's compose.yaml for the projects given, beside the backing services named (postgres,
    /// redis...) that their packages point to, listed in the solution's Solution Items; a project with no Dockerfile gets
    /// one, and the context a .dockerignore (<see cref="KeepNuGetConfigOut"/>). Null once done and landed, else why not.
    /// Given a command (<paramref name="into"/>), it is part of that one, else a command of its own.</summary>
    public async Task<string?> AddDockerComposeAsync(IReadOnlyList<string> projects, IReadOnlyList<string> services, CommandHistory.Recording? into = null)
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
        string compose = Path.Combine(context, "compose.yaml");
        string ignore = Path.Combine(context, ".dockerignore");
        if (SaveBeforeWriting([solution.Path, ignore]) is { } unsaved)
        {
            return DockerSaid($"{solution.Name} could not be changed: {unsaved}");
        }
        IgnoreWritten ignoring = IgnoreWritten.Nothing;
        List<string> dockerfiles = [];
        string? problem = null;
        using CommandHistory.Recording? own = into is null ? Editors.Commands.Record("Add Docker Compose") : null;
        CommandHistory.Recording made = into ?? own!;
        try
        {
            Following([.. composed.Select(c => c.Project.Dockerfile), ignore, compose, solution.Path], () =>
            {
                foreach ((DockerProject project, _, _) in composed.Where(c => c.Existing is null))
                {
                    WriteNew(made, project.Dockerfile, DockerFiles.Dockerfile(project, context));
                    dockerfiles.Add(project.Name);
                }
                ignoring = KeepNuGetConfigOut(made, ignore);
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
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problem = error.Message;
        }
        if (problem is null && await UnlandedAsync(made) is { } unlanded)
        {
            problem = unlanded.Message;
        }
        ReloadSolution();
        if (problem is not null)
        {
            return DockerSaid($"compose.yaml could not be added to {solution.Name}: {problem}");
        }
        OpenFile(compose);
        return DockerSaid(null, (dockerfiles.Count == 0 ? $"Added compose.yaml to {solution.Name}"
            : $"Added compose.yaml to {solution.Name}, and a Dockerfile to {string.Join(", ", dockerfiles)}")
            + (ignoring == IgnoreWritten.NuGetConfigLeftOut ? $"; added to its .dockerignore {NuGetConfigLine}" : string.Empty));
    }

    /// <summary>A project joins the compose file the solution's folder has as a service of it, after those there, built as
    /// Add Docker Compose builds one (<see cref="DockerFiles.Compose"/>), a Dockerfile made for it when it has none and the
    /// context a .dockerignore (<see cref="KeepNuGetConfigOut"/>); part of the command given. Null once done and landed,
    /// else why not.</summary>
    public async Task<string?> AddComposeServiceAsync(string project, CommandHistory.Recording made)
    {
        if (Solution is not { } solution || DockerContext is not { } context || ComposeFileIn(context) is not { } compose)
        {
            return DockerSaid("The solution has no compose file to add a service to");
        }
        if (SolutionProject(project) is not { } model)
        {
            return DockerSaid($"{Path.GetFileName(project)} is not one of the solution's projects");
        }
        if (Unread([model]) is { } unread)
        {
            return DockerSaid(unread);
        }
        DockerProject described = DockerFiles.Describe(model, session.Workspace?.Projects ?? []);
        string ignore = Path.Combine(context, ".dockerignore");
        if (SaveBeforeWriting([compose, ignore]) is { } unsaved)
        {
            return DockerSaid($"{Path.GetFileName(compose)} could not be changed: {unsaved}");
        }
        IgnoreWritten ignoring = IgnoreWritten.Nothing;
        string? problem = null;
        try
        {
            FileText file = FileText.Load(compose) ?? throw new IOException($"{Path.GetFileName(compose)} is not a text file");
            string block = DockerFiles.Compose(context, [(described, described.ExistingDockerfile, BackingService.Needs(model, []))]);
            (string? text, string? refused) = DockerFiles.WithService(file.Text, described.Service, block);
            if (text is null)
            {
                return DockerSaid($"{described.Name} could not join {Path.GetFileName(compose)}: {refused}");
            }
            Following([described.Dockerfile, ignore, compose], () =>
            {
                if (described.ExistingDockerfile is null)
                {
                    WriteNew(made, described.Dockerfile, DockerFiles.Dockerfile(described, context));
                }
                ignoring = KeepNuGetConfigOut(made, ignore);
                made.Watch(compose);
                try
                {
                    FileText.SaveOver(compose, file, text);
                }
                finally
                {
                    // Open, it takes the text now, inside the command, so the command's undo takes it back too.
                    FollowWritten([compose]);
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problem = error.Message;
        }
        if (problem is null && await UnlandedAsync(made) is { } unlanded)
        {
            problem = unlanded.Message;
        }
        return problem is null
            ? DockerSaid(null, $"{described.Name} joined {Path.GetFileName(compose)} as the service {described.Service}"
                + (ignoring == IgnoreWritten.NuGetConfigLeftOut ? $"; added to the .dockerignore {NuGetConfigLine}" : string.Empty))
            : DockerSaid($"{described.Name} could not join {Path.GetFileName(compose)}: {problem}");
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

    /// <summary>The compose file Compose reads in a folder; null with none.</summary>
    private static string? ComposeFileIn(string folder) => Containers.ComposeFiles.In(folder);

    private ProjectModel? SolutionProject(string project) => session.Workspace?.Projects.FirstOrDefault(p => PathNames.Same(p.Path, project));

    /// <summary>Why Add Docker Support is greyed on a project of the solution: it is still being read, or it is no service,
    /// making no program to run (a library, a test project); null for one that runs, or one MSBuild could not read.</summary>
    private string? DockerSupportWhyNot(string project) => SolutionProject(project) switch
    {
        { } model when Unread([model]) is { } unread => unread,
        { IsEvaluated: true, IsRunnable: false } model => $"{model.Name} is not a service: it makes no program to run in a container",
        _ => null,
    };

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
        FileText.SaveNew(path, text);
    }

    /// <summary>What an Add did to the context's .dockerignore.</summary>
    private enum IgnoreWritten
    {
        Nothing,
        Made,
        NuGetConfigLeftOut,
    }

    /// <summary>What the status bar says of the line an Add gives a .dockerignore that kept nuget.config.</summary>
    private const string NuGetConfigLine = "the line keeping nuget.config, which may hold a feed's password, out of the image";

    /// <summary>The context's .dockerignore made where it has none, else given the line keeping nuget.config out of the
    /// image where nothing in it does (<see cref="DockerFiles.IgnoringNuGetConfig"/>), its other characters as they were,
    /// as part of the command (an open one taking the text inside it, so its Undo takes the line back too).</summary>
    private IgnoreWritten KeepNuGetConfigOut(CommandHistory.Recording made, string ignore)
    {
        if (!File.Exists(ignore))
        {
            WriteNew(made, ignore, DockerFiles.Ignore);
            return IgnoreWritten.Made;
        }
        if (FileText.Load(ignore) is not { IsBinary: false } file || DockerFiles.IgnoringNuGetConfig(file.Text) is not { } text)
        {
            return IgnoreWritten.Nothing;
        }
        made.Watch(ignore);
        try
        {
            FileText.SaveOver(ignore, file, text);
        }
        finally
        {
            FollowWritten([ignore]);
        }
        return IgnoreWritten.NuGetConfigLeftOut;
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
