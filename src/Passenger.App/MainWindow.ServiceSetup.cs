using System.Text.Json.Nodes;
using Passenger.App.Aspire;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.Templates;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The set-up New Solution and New Project make for the service they made, once it is open and read: a Dockerfile
/// or the .NET SDK's container with a launch profile that runs it there, a service of the solution's compose file, or a
/// resource of an Aspire AppHost, each written by Add's own action (Docker Support, Docker Compose, Aspire Orchestration)
/// into the command given, then the configuration that runs it so chosen, so the first Run or Debug launches it that way.</summary>
public partial class MainWindow
{
    /// <summary>The set-up of the last service New Solution or New Project made in this window: null once it is made and
    /// its configuration chosen, else why not, which the status bar says too.</summary>
    public Task<string?>? ServiceSetUp { get; private set; }

    /// <summary>The longest a new project's first read is waited for before its set-up is given up, saying why.</summary>
    private static readonly TimeSpan SetUpRead = TimeSpan.FromMinutes(2);

    /// <summary>The launch profile a container set-up adds, named by its command as Visual Studio names it.</summary>
    private static string ContainerProfile(ProjectSetup setup) => setup == ProjectSetup.SdkContainer ? "Container (.NET SDK)" : "Container (Dockerfile)";

    /// <summary>Sets a new project up to run as asked, into <paramref name="made"/>, and chooses the configuration that so
    /// runs it: null once chosen, else why not, which the status bar says.</summary>
    private async Task<string?> SetUpAsync(string project, ProjectSetup setup, CommandHistory.Recording made)
    {
        using IDisposable held = BackgroundWork.Scope();
        string name = Path.GetFileNameWithoutExtension(project);
        status.Text = $"Setting {name} up to run with {SetupWords(setup)}…";
        // What the command wrote so far is taken before it waits for the project's read, through which the person types.
        made.TakeOpen();
        if (await ReadProjectAsync(project) is not { } model)
        {
            return status.Text = $"{name} was not set up to run with {SetupWords(setup)}: it was not read within {Waits.Of(SetUpRead).TotalMinutes:0} minutes";
        }
        if (!model.IsRunnable)
        {
            return status.Text = $"{name} was not set up to run with {SetupWords(setup)}: it makes no program to run";
        }
        string? problem;
        Func<RunConfiguration, bool> runs;
        switch (setup)
        {
            case ProjectSetup.Dockerfile or ProjectSetup.SdkContainer:
                problem = await AddDockerSupportAsync(model.Path, setup == ProjectSetup.Dockerfile ? DockerSupportKind.Dockerfile : DockerSupportKind.Sdk, made)
                    ?? await AddContainerProfileAsync(model, setup, made);
                string profile = RunConfiguration.NameOf(model, ContainerProfile(setup));
                runs = c => string.Equals(c.Name, profile, StringComparison.OrdinalIgnoreCase);
                break;
            case ProjectSetup.Compose:
                string context = DockerContext!;
                problem = ComposeFileIn(context) is null ? await AddDockerComposeAsync([model.Path], [], made) : await AddComposeServiceAsync(model.Path, made);
                runs = c => c.Kind == RunKind.Compose && c.ComposeFiles.Count > 0 && ComposeFileIn(context) is { } compose && PathNames.Same(c.ComposeFiles[0], compose);
                break;
            case ProjectSetup.Aspire:
                (problem, string? host) = await EnlistAsync(model, made);
                runs = c => c.Aspire && host is not null && PathNames.Same(c.Project, host);
                break;
            default:
                return null;
        }
        if (problem is not null)
        {
            return status.Text = $"{name} was not set up to run with {SetupWords(setup)}: {problem}";
        }
        session.Configurations.Configure(Projects);
        if (Configurations.FirstOrDefault(runs) is not { } chosen || !SelectConfiguration(chosen.Name))
        {
            return status.Text = $"{name} was set up to run with {SetupWords(setup)}, but no run configuration runs it so";
        }
        status.Text = $"{name} runs with {SetupWords(setup)}: Run and Debug launch {chosen.Name}";
        return null;
    }

    private static string SetupWords(ProjectSetup setup) => setup switch
    {
        ProjectSetup.Dockerfile => "a Dockerfile",
        ProjectSetup.SdkContainer => "the .NET SDK's container",
        ProjectSetup.Compose => "Docker Compose",
        _ => "Aspire",
    };

    /// <summary>A container launch profile of the project's launchSettings.json, beside the profiles it has: the Docker
    /// command for its Dockerfile, SdkContainer for the SDK's image, every port its image exposes published, a web app's
    /// browser opened at the first. Null once written and landed, else why not.</summary>
    private async Task<string?> AddContainerProfileAsync(ProjectModel project, ProjectSetup setup, CommandHistory.Recording made)
    {
        string path = LaunchProfile.SettingsPath(project);
        if (SaveBeforeWriting([path]) is { } unsaved)
        {
            return $"{Path.GetFileName(path)} could not be changed: {unsaved}";
        }
        bool web = Containers.ContainerConfigurations.IsWeb(project, Projects);
        JsonObject profile = new() { ["commandName"] = setup == ProjectSetup.SdkContainer ? LaunchProfile.SdkContainerCommand : LaunchProfile.DockerCommand };
        if (web)
        {
            profile["launchBrowser"] = true;
            profile["launchUrl"] = Containers.ContainerConfigurations.ServiceUrl;
        }
        profile["publishAllPorts"] = true;
        made.Watch(path);
        string? problem;
        try
        {
            problem = Following([path], () => LaunchSettingsEdits.Add(path, ContainerProfile(setup), profile));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problem = error.Message;
        }
        finally
        {
            // Open, it takes the text now, inside the command, so the command's undo takes it back too.
            FollowWritten([path]);
        }
        return problem ?? (await UnlandedAsync(made) is { } unlanded ? unlanded.Message : null);
    }

    /// <summary>The project enlisted in the solution's AppHost (one made where it has none) with its ServiceDefaults (one
    /// made where it has none the project can reference), as Add Aspire Orchestration enlists it: why not, or the AppHost
    /// once it is read.</summary>
    private async Task<(string? Problem, string? Host)> EnlistAsync(ProjectModel project, CommandHistory.Recording made)
    {
        if (Solution is not { } solution)
        {
            return (Refusals.NoSolution, null);
        }
        IReadOnlyList<ProjectModel> all = Projects;
        ProjectModel? host = all.FirstOrDefault(p => p.Exists && p.IsAspireHost);
        // The AppHost's services reference theirs; else one is known by its name or the service discovery it brings.
        bool Served(ProjectModel defaults) => host is not null && all.Any(s => host.ProjectReferences.Any(r => PathNames.Same(r, s.Path))
            && s.ProjectReferences.Any(r => PathNames.Same(r, defaults.Path)));
        ProjectModel? defaults = all.Where(p => p.Exists && !p.IsAspireHost && !p.IsRunnable && !p.IsTestProject
                && p.TargetFrameworks.Any(project.TargetFrameworks.Contains)
                && (Served(p) || p.Name.EndsWith("ServiceDefaults", StringComparison.OrdinalIgnoreCase) || p.Packages.Any(q => q.Name == "Microsoft.Extensions.ServiceDiscovery")))
            .OrderByDescending(Served).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        string said = await AddAspireOrchestrationAsync(new AspireOrchestrationChoice([project], host, defaults), made) ?? string.Empty;
        if (said != AspireAdded)
        {
            return (said, null);
        }
        string root = Path.GetDirectoryName(solution.Path)!;
        string hostPath = host?.Path ?? Path.Combine(root, $"{solution.Name}.AppHost", $"{solution.Name}.AppHost.csproj");
        return await ReadProjectAsync(hostPath) is { IsAspireHost: true } read
            ? (null, read.Path)
            : ($"its AppHost {Path.GetFileNameWithoutExtension(hostPath)} was not read as one within {Waits.Of(SetUpRead).TotalMinutes:0} minutes", null);
    }

    /// <summary>A project of the workspace once MSBuild has read it (its image, frameworks and kind known), waited for as
    /// the solution's read and its evaluation land; null when it is not there or not read in time.</summary>
    private async Task<ProjectModel?> ReadProjectAsync(string project)
    {
        ProjectSession reading = session;
        ProjectModel? Read() => reading.Workspace?.Projects.FirstOrDefault(p => PathNames.Same(p.Path, project) && p.Exists && p.Evaluated is not null);
        if (Read() is { } now)
        {
            return now;
        }
        TaskCompletionSource<ProjectModel?> read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Landed()
        {
            if (Read() is { } model)
            {
                read.TrySetResult(model);
            }
        }
        EventHandler changed = (_, _) => Landed();
        EventHandler<EvaluatedProjects> evaluated = (_, _) => Landed();
        reading.WorkspaceChanged += changed;
        reading.ProjectsEvaluated += evaluated;
        try
        {
            reading.Evaluate();
            return await read.Task.WaitAsync(Waits.Of(SetUpRead));
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            reading.WorkspaceChanged -= changed;
            reading.ProjectsEvaluated -= evaluated;
        }
    }

    /// <summary>The folder a command made whole, at its end, kept as its files' lengths and times read off the window's
    /// thread: Undo moves it aside under the window's state (its open files' tabs closed) once every file in it but what
    /// builds write is as the command left it and none holds edits not saved, saying which otherwise, and Redo moves it
    /// back from there; nothing of its bytes is read or held.</summary>
    private async Task<CommandHistory.Part> MadeFolderAsync(string folder)
    {
        string state = StateFolder;
        Dictionary<string, (long Length, DateTime Written)> made = await BackgroundWork.Run(() =>
        {
            MadeFolders.Sweep(state);
            return MadeFolders.Stamps(folder);
        });
        string name = Path.GetFileName(folder);
        string aside = MadeFolders.Aside(state, folder);
        return new CommandHistory.Part($"the folder {name}", redo =>
            redo ? Directory.Exists(folder) || File.Exists(folder) ? $"{name} is already there" : Directory.Exists(aside) ? null : $"{name} was not kept to be made again"
            : !Directory.Exists(folder) ? $"{name} is no longer there"
            : Editors.Documents.FirstOrDefault(d => d.IsModified && PathNames.IsUnder(d.Path, folder)) is { } edited ? $"{edited.Name} has edits not saved"
            : null, redo => Following([folder], () =>
        {
            if (!redo)
            {
                foreach (OpenDocument document in Editors.Documents.Where(d => PathNames.IsUnder(d.Path, folder)).ToList())
                {
                    Editors.CloseDeleted(document);
                }
            }
            try
            {
                MadeFolders.Move(redo ? aside : folder, redo ? folder : aside);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                status.Text = $"{name} could not be {(redo ? "put back" : "set aside")}: {error.Message}";
            }
        }))
        {
            Read = redo => redo ? Task.FromResult<string?>(null) : BackgroundWork.Run(() => MadeFolders.Unlike(folder, made)),
            Dropped = () => MadeFolders.Forget(aside),
        };
    }
}
