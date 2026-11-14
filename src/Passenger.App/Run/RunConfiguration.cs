using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Passenger.App.Aspire;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>Where a run's program reads and writes: the Run or Debug window's console, or a tab of the Terminal window,
/// which gives it a real terminal (<see cref="TerminalRun"/>). Profile keeps to the Run window's console either
/// way.</summary>
public enum ConsoleKind
{
    Internal,
    Terminal,
}

/// <summary>What a run configuration launches: a .NET project on this machine, a Compose application, one project's
/// Dockerfile, or a project as the .NET SDK makes its container (<c>EnableSdkContainerSupport</c>), each container kind
/// run by the person's engine from Passenger's own build of its .NET services (<see cref="Containers.ContainerRun"/>); or
/// several of the solution's projects on this machine together, as a multi-project launch profile beside the solution
/// says (<see cref="SolutionLaunch"/>); or a Node package's script, run with its package manager (<see cref="NodeProjects"/>).</summary>
public enum RunKind
{
    Project,
    Compose,
    Dockerfile,
    SdkContainer,
    Projects,
    Script,
}

/// <summary>One project a multi-project configuration starts: its file (canonical), what Run and Debug do with it (null for
/// an action Passenger does not know, which keeps the configuration from starting), and the launch profile of its own it
/// starts in (empty for its default).</summary>
public sealed record ProjectStart(string Project, ProjectLaunchAction? Action, string LaunchProfile)
{
    /// <summary>It as its file held it when read, which a change is written over; null for one not written yet.</summary>
    [JsonIgnore]
    public SolutionLaunchProject? Launch { get; init; }
}

/// <summary>What a Compose configuration does with one .NET service: run it, debug it, or leave it unstarted.</summary>
public enum ServiceStart
{
    Run,
    Debug,
    DontStart,
}

/// <summary>How a container configuration's .NET services run: fast, in their image's runtime stage with Passenger's host
/// build mounted in (a code change needs no image rebuild), or from the images Compose builds, as deployed.</summary>
public enum ContainerMode
{
    Fast,
    Images,
}

/// <summary>Something Run, Debug and Profile launch: a project (its framework, or the first), the program's arguments,
/// environment and working directory, whether the project is built first, the console, and the browser a web project
/// opens; or containers (<see cref="Kind"/>): a Compose application's files, profiles and services, a Dockerfile, or an
/// SDK container project. Data alone: <see cref="LaunchPlan.ResolveAsync"/> and <see cref="Containers.ContainerRun"/>
/// work out what it launches. The folder's own are kept with its state; the generated ones come from its executable
/// projects, their launch profiles and its container files.</summary>
public sealed record RunConfiguration
{
    public string Name { get; init; } = string.Empty;

    /// <summary>What it launches: a project here, or containers.</summary>
    public RunKind Kind { get; init; }

    /// <summary>The project's file: a canonical path in the window, relative to the open folder in the store. A script's
    /// is its package's package.json.</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>For a Node package's, the script of its package.json it runs.</summary>
    public string Script { get; init; } = string.Empty;

    /// <summary>Whether Passenger coordinates this AppHost and its project resources.</summary>
    public bool Aspire { get; init; }

    /// <summary>Additional AppHosts started and stopped with this Aspire configuration.</summary>
    public IReadOnlyList<string> LinkedAppHosts { get; init; } = [];

    /// <summary>For a Godot project's (one Godot.NET.Sdk builds, with its project.godot), whether it starts Godot's editor
    /// on the project rather than its game; either runs on the Godot found (<see cref="GodotLaunch"/>) unless it names a
    /// program of its own.</summary>
    public bool GodotEditor { get; init; }

    /// <summary>For a Godot project's game, whether Godot's editor open on the project plays it, as its own Play does, rather
    /// than Passenger starting it: through Passenger's addon there where it is installed, else through the editor's IDE
    /// messaging (<see cref="MainWindow"/>'s Run in Godot Editor).</summary>
    public bool GodotInEditor { get; init; }

    /// <summary>For a Godot project's game, whether it draws in Passenger's Game window rather than a window of its own, its
    /// window held over the window's place as Godot's editor holds a game it embeds (<see cref="Godot.GameWindows"/>); a
    /// system or game that cannot runs it in its own, saying why.</summary>
    public bool GodotInPane { get; init; }

    /// <summary>For a Godot project's game, the scene it starts in: its project.godot's main scene (null), the scene open in the
    /// editor as it starts (<see cref="CurrentScene"/>: a scene, or one a script open runs in), or a scene's res:// path.</summary>
    public string? GodotScene { get; init; }

    /// <summary>What <see cref="GodotScene"/> is for the scene open in the editor.</summary>
    public const string CurrentScene = "current";

    /// <summary>For a Godot project's game, how many copies of it a launch starts side by side (a server and its clients),
    /// each a run of its own (<see cref="GameCopies"/>); one for one game.</summary>
    public int GodotCopies { get; init; } = 1;

    /// <summary>Each copy's own arguments after the configuration's, by its place from the first: a copy past them has none.</summary>
    public List<string> CopyArguments { get; init; } = [];

    /// <summary>The target framework run, for a project with several; empty for the first.</summary>
    public string Framework { get; init; } = string.Empty;

    /// <summary>The launch profile its values came from, if one did.</summary>
    public string LaunchProfile { get; init; } = string.Empty;

    /// <summary>A program run in place of the project's own (an <c>Executable</c> profile's); empty for the project's.
    /// MSBuild's <c>$(Property)</c> and the environment's <c>%NAME%</c> are expanded, as below.</summary>
    public string Program { get; init; } = string.Empty;

    /// <summary>The program's arguments as one command line; empty for the project's RunArguments, if it has any.</summary>
    public string Arguments { get; init; } = string.Empty;

    public Dictionary<string, string> Environment { get; init; } = [];

    /// <summary>Where the program runs, relative to the project's folder; empty for the project's RunWorkingDirectory, else
    /// its output folder.</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>The working folder of one made from a launch profile that names none, and of a file-based program's: where
    /// dotnet run runs it, the project's RunWorkingDirectory, which when empty leaves the project's own folder.</summary>
    public const string DotnetRunFolder = $"$({MSBuildNames.RunWorkingDirectory})";

    public bool BuildBeforeLaunch { get; init; } = true;

    /// <summary>The build configuration and platform its run builds, evaluates and starts as, by its key (Release|Any CPU,
    /// <see cref="Passenger.Workspace.BuildConfiguration.Key"/>), in place of the folder's choice; empty for the folder's
    /// (<see cref="ProjectSession.WorkspaceOfAsync"/>).</summary>
    public string BuildConfiguration { get; init; } = string.Empty;

    public ConsoleKind Console { get; init; }

    /// <summary>Whether the browser opens at <see cref="BrowserUrl"/> once the program listens there, as a launch profile's
    /// <c>launchBrowser</c> asks.</summary>
    public bool LaunchBrowser { get; init; }

    /// <summary>Where the browser opens: a profile's <c>launchUrl</c> against its first <c>applicationUrl</c>; for containers
    /// Visual Studio's <c>{Scheme}://{ServiceHost}:{ServicePort}</c> filled from the published port.</summary>
    public string BrowserUrl { get; init; } = string.Empty;

    /// <summary>A Compose configuration's files, the first the base and the rest its overrides, as <c>-f</c> gives them:
    /// canonical paths in the window, relative to the open folder in the store.</summary>
    public List<string> ComposeFiles { get; init; } = [];

    /// <summary>The env files Compose reads for its variables (<c>--env-file</c>); none for the .env beside the first file.</summary>
    public List<string> EnvFiles { get; init; } = [];

    /// <summary>The Compose profiles turned on (<c>--profile</c>).</summary>
    public List<string> Profiles { get; init; } = [];

    /// <summary>The services started, with what they depend on; none for every service the profiles turn on.</summary>
    public List<string> Services { get; init; } = [];

    /// <summary>What each .NET service does by its name (Run when unnamed): run, debug or stay unstarted.</summary>
    public Dictionary<string, ServiceStart> ServiceActions { get; init; } = [];

    /// <summary>The project a service runs where its Dockerfile does not say (no <c>ENTRYPOINT ["dotnet", "X.dll"]</c> nor
    /// <c>dotnet publish X.csproj</c>): the person's choice, kept, by the service's name (canonical in the window).</summary>
    public Dictionary<string, string> ServiceProjects { get; init; } = [];

    /// <summary>Fast (Passenger's build mounted into the runtime stage), or the images Compose builds.</summary>
    public ContainerMode Mode { get; init; }

    /// <summary>Whether Stop leaves the services that are not .NET's (a database, a cache) running for the next run; they
    /// still go with the folder and the IDE.</summary>
    public bool KeepBackingServices { get; init; }

    /// <summary>Whether a container run collects its .NET services' logs and traces, or a project run its program's (an AppHost's
    /// has a dashboard of its own): Aspire's dashboard run beside them, which they are told to send OpenTelemetry to. Off unless
    /// chosen, since its image is pulled the first time.</summary>
    public bool Telemetry { get; init; }

    /// <summary>The service whose published port fills the browser's <c>{ServicePort}</c>: Visual Studio's
    /// composeLaunchServiceName; the first .NET service when none is named.</summary>
    public string LaunchService { get; init; } = string.Empty;

    /// <summary>A Dockerfile configuration's Dockerfile (canonical in the window, relative in the store); empty for the
    /// project's <c>DockerfileFile</c>, else its own beside it.</summary>
    public string Dockerfile { get; init; } = string.Empty;

    /// <summary>A Dockerfile configuration's build context (canonical in the window, relative in the store); empty for the
    /// project's <c>DockerfileContext</c>, else the solution's folder.</summary>
    public string Context { get; init; } = string.Empty;

    /// <summary>A Dockerfile configuration's build arguments as one command line of <c>NAME=value</c> words.</summary>
    public string BuildArguments { get; init; } = string.Empty;

    /// <summary>A container configuration's <c>docker run</c> words for its container (<c>-p</c>, <c>-e</c>, <c>--env-file</c>,
    /// <c>-v</c>), as a launch profile's DockerfileRunArguments or containerRunArguments give them.</summary>
    public string RunArguments { get; init; } = string.Empty;

    /// <summary>A multi-project configuration's projects in the order they start, each with what Run and Debug do with it
    /// and the launch profile it starts in.</summary>
    public List<ProjectStart> Starts { get; init; } = [];

    /// <summary>For a multi-project configuration, whether the team shares it in the solution's <c>.slnLaunch</c>, rather
    /// than keeping it in the person's own <c>.slnLaunch.user</c>.</summary>
    public bool Shared { get; init; } = true;

    /// <summary>For a multi-project configuration, the profile as its file held it when read, which a change is written over;
    /// null for one not written yet.</summary>
    [JsonIgnore]
    public SolutionLaunchProfile? Launch { get; init; }

    /// <summary>Whether it runs containers rather than programs on this machine.</summary>
    [JsonIgnore]
    public bool IsContainer => Kind is RunKind.Compose or RunKind.Dockerfile or RunKind.SdkContainer;

    /// <summary>Whether it runs a Node package's script, which Run alone starts: nothing is built, debugged or profiled.</summary>
    [JsonIgnore]
    public bool IsScript => Kind == RunKind.Script;

    /// <summary>Whether it starts several of the solution's projects together, as a multi-project launch profile says.</summary>
    [JsonIgnore]
    public bool IsMultiple => Kind == RunKind.Projects;

    /// <summary>Made from the folder's projects and container files rather than by the person: changed in place, its
    /// changes kept with the folder over what its source makes (<see cref="Source"/>), or copied to be the person's own.</summary>
    [JsonIgnore]
    public bool Generated { get; init; }

    /// <summary>For a generated one the person changed in place, the configuration its source makes now, which every field
    /// they left alone follows (<see cref="ConfigurationEdit"/>); null for one unchanged and for the person's own.</summary>
    [JsonIgnore]
    public RunConfiguration? Source { get; init; }

    /// <summary>Where a generated container configuration came from, in words (a .dcproj, a compose file, a Dockerfile).</summary>
    [JsonIgnore]
    public string? Origin { get; init; }

    /// <summary>Why it cannot launch here (a launch profile of a kind Passenger does not run), or null.</summary>
    [JsonIgnore]
    public string? Unsupported { get; init; }

    /// <summary>Whether it runs a Godot project's game or editor on Godot among these projects (<see cref="GodotLaunch"/>):
    /// one of a project Godot.NET.Sdk builds that names no program of its own, or names Godot as a launch profile does
    /// (<see cref="NamesGodot"/>).</summary>
    public bool RunsGodot(IEnumerable<ProjectModel> projects) =>
        Kind == RunKind.Project && (Program.Length == 0 || NamesGodot) && (RunsScriptGame || projects.FirstOrDefault(p => Core.PathNames.Same(p.Path, Project))?.GodotFolder is not null);

    /// <summary>Whether it runs an Azure Functions app among these projects, which func runs (<see cref="Aspire.FunctionsHost"/>):
    /// one of a Functions project that names no program of its own.</summary>
    public bool RunsFunctions(IEnumerable<ProjectModel> projects) =>
        Kind == RunKind.Project && Program.Length == 0 && projects.FirstOrDefault(p => Core.PathNames.Same(p.Path, Project))?.IsFunctionsApp == true;

    /// <summary>Whether the program it names is Godot (%GODOT%, a path to a Godot executable: <see cref="GodotExecutables.NamesGodot"/>),
    /// which for a Godot project's runs its game as one naming no program does, on the Godot Settings names or the one found.</summary>
    [JsonIgnore]
    public bool NamesGodot => Program.Length > 0 && GodotExecutables.NamesGodot(Program, OperatingSystem.IsWindows());

    /// <summary>Whether it runs its project's own program among these projects: it names none, or Godot for a Godot project's game.</summary>
    public bool NamesNoOtherProgram(IEnumerable<ProjectModel> projects) => Program.Length == 0 || RunsGodot(projects);

    /// <summary>Whether it runs a Godot project of GDScript alone, its project.godot its project (<see cref="ProjectModel.IsScriptGame"/>):
    /// no C# project is built or debugged for it.</summary>
    [JsonIgnore]
    public bool RunsScriptGame => Kind == RunKind.Project && Path.GetFileName(Project) == GodotProject.FileName;

    /// <summary>Why it cannot launch here among these projects: its launch profile's kind, or, when it runs its project's
    /// own program, a project that runs only on Windows (WPF, Windows Forms) elsewhere; null when it can.</summary>
    public string? WhyNotHere(IEnumerable<ProjectModel> projects) => Unsupported
        ?? (IsMultiple ? WhyNotStarted(projects) : null)
        ?? (IsScript && Script.Length == 0 ? "it names no script of its package.json to run" : null)
        ?? (!IsContainer && Aspire && Path.GetExtension(Project).Equals(".cs", StringComparison.OrdinalIgnoreCase)
            ? AspireAppHosts.SingleFileWhyNot(Project, projects.Select(project => project.Path)) : null)
        ?? (!IsContainer && Program.Length == 0 && projects.FirstOrDefault(p => Core.PathNames.Same(p.Path, Project)) is { } project ? project.RunsOnlyOnWindows(Framework) : null);

    /// <summary>Why a multi-project configuration cannot start here among these projects: it starts none, or one it starts is
    /// not loaded, runs only on Windows, or takes an action Passenger does not know; null when it can.</summary>
    private string? WhyNotStarted(IEnumerable<ProjectModel> projects)
    {
        List<ProjectStart> starting = [.. Starts.Where(s => s.Action != ProjectLaunchAction.None)];
        if (starting.Count == 0)
        {
            return "it starts no project: each is set to None";
        }
        List<ProjectModel> loaded = [.. projects];
        foreach (ProjectStart start in starting)
        {
            string name = Path.GetFileNameWithoutExtension(start.Project);
            if (start.Action is null)
            {
                return $"its action for {name} is one Passenger does not know";
            }
            if (loaded.FirstOrDefault(p => p.Exists && Core.PathNames.Same(p.Path, start.Project)) is not { } project)
            {
                return $"{name} is not loaded";
            }
            if (project.RunsOnlyOnWindows(string.Empty) is { } windows)
            {
                return $"{name}: {windows}";
            }
        }
        return null;
    }

    /// <summary>The configurations the folder's executable projects make: one per launch profile, named "Project: profile",
    /// and the project itself when none of its profiles runs it here (a Godot project's game), with a Godot project's editor
    /// beside it ("Project: Godot Editor"), the workspace's Godot projects of GDScript alone among them (<see cref="WorkspaceModel.ScriptGames"/>);
    /// then single-file AppHosts, the container ones (<see cref="Containers.ContainerConfigurations"/>), and each Node
    /// package's scripts, "package: script" (<see cref="OfScript"/>).</summary>
    public static IReadOnlyList<RunConfiguration> Generate(IEnumerable<ProjectModel> projects, WorkspaceModel? workspace = null,
        IEnumerable<string>? singleFileAppHosts = null, IEnumerable<NodePackage>? packages = null)
    {
        List<RunConfiguration> generated = [];
        List<ProjectModel> all = [.. projects];
        foreach (ProjectModel project in all.Where(p => p.Exists).Concat(workspace?.ScriptGames(all).Select(ProjectModel.OfScriptGame) ?? []))
        {
            bool godot = project.Godot() is not null;
            if (!project.IsRunnable && !godot)
            {
                continue;
            }
            string name = NameOf(project);
            // A profile tied to a platform its apps run on makes none: the project's own takes it where that platform is the target.
            IReadOnlyList<LaunchProfile> profiles = project.IsScriptGame ? [] : Devices.DeviceTargets.OwnProfiles(project.TargetFrameworks, Run.LaunchProfile.Read(project));
            if (!profiles.Any(p => p.Unsupported is null && !p.RunsInContainer))
            {
                generated.Add(new RunConfiguration
                {
                    Name = name,
                    Project = project.Path,
                    Aspire = project.IsAspireHost,
                    Generated = true,
                    // A program runs beside its C# file, as dotnet run there runs it: what it builds lies out of sight.
                    WorkingDirectory = project.IsProgram ? DotnetRunFolder : string.Empty,
                });
            }
            generated.AddRange(profiles.Select(p => From(project, p) with { Name = NameOf(project, p.Name), Generated = true }));
            if (godot)
            {
                generated.Add(new RunConfiguration { Name = NameOf(project, GodotEditorName), Project = project.Path, GodotEditor = true, Generated = true });
            }
        }
        foreach (string path in singleFileAppHosts ?? [])
        {
            generated.Add(new RunConfiguration
            {
                Name = $"Aspire: {Path.GetFileNameWithoutExtension(path)}",
                Project = path,
                Aspire = true,
                Generated = true,
            });
        }
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < generated.Count; i++)
        {
            string name = UniqueName(generated[i].Name, names);
            generated[i] = generated[i] with { Name = name };
            names.Add(name);
        }
        if (workspace is not null)
        {
            HashSet<string> taken = new(generated.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
            foreach (RunConfiguration container in Containers.ContainerConfigurations.Generate(workspace, all))
            {
                if (taken.Add(container.Name))
                {
                    generated.Add(container with { Generated = true });
                }
            }
        }
        HashSet<string> used = new(generated.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        foreach (NodePackage package in packages ?? [])
        {
            foreach (NodeScript script in package.Runnable)
            {
                string name = UniqueName(OfScript(package, script.Name).Name, used);
                used.Add(name);
                generated.Add(OfScript(package, script.Name) with { Name = name, Generated = true });
            }
        }
        return generated;
    }

    /// <summary>The configuration running a Node package's script, named "package: script".</summary>
    public static RunConfiguration OfScript(NodePackage package, string script) => new()
    {
        Name = $"{package.Name}: {script}",
        Kind = RunKind.Script,
        Project = package.Manifest,
        Script = script,
        BuildBeforeLaunch = false,
        Origin = $"Generated from the script {script} in {package.Name}'s package.json, run with {NodeProjects.ProgramOf(package.Manager)}",
    };

    /// <summary>It with a launch profile's values where it gives none of its own, as it runs on the platform the profile is
    /// tied to: the profile's arguments, working folder and browser where it names none, the profile's variables under its
    /// own, and the profile's name, which dotnet run names it by.</summary>
    public RunConfiguration TakingProfile(ProjectModel project, LaunchProfile profile)
    {
        RunConfiguration made = From(project, profile);
        Dictionary<string, string> environment = new(made.Environment);
        foreach ((string name, string value) in Environment)
        {
            environment[name] = value;
        }
        return this with
        {
            LaunchProfile = profile.Name,
            Arguments = Arguments.Length > 0 ? Arguments : made.Arguments,
            WorkingDirectory = WorkingDirectory.Length > 0 ? WorkingDirectory : made.WorkingDirectory,
            Environment = environment,
            LaunchBrowser = LaunchBrowser || made.LaunchBrowser,
            BrowserUrl = BrowserUrl.Length > 0 ? BrowserUrl : made.BrowserUrl,
        };
    }

    /// <summary>What a Godot project's configuration that starts Godot's editor on it is called after the project's name.</summary>
    public const string GodotEditorName = "Godot Editor";

    /// <summary>The name of the configuration generated for a project itself, which those of its launch profiles begin with
    /// ("Hello", "Aspire: Host").</summary>
    public static string NameOf(ProjectModel project) => project.IsAspireHost ? $"Aspire: {project.Name}" : project.Name;

    /// <summary>The name of the configuration generated for a project's launch profile ("Hello: https"), unless another
    /// generated one took it first.</summary>
    public static string NameOf(ProjectModel project, string profile) => $"{NameOf(project)}: {profile}";

    /// <summary>A configuration with a launch profile's values: what generating one makes, and what choosing a profile in
    /// Edit Configurations fills in. A profile's own folder, else where <c>dotnet run</c> runs it (<see cref="DotnetRunFolder"/>);
    /// a container profile's (Docker, SdkContainer) a container configuration of its project.</summary>
    public static RunConfiguration From(ProjectModel project, LaunchProfile profile) => profile.RunsInContainer
        ? Containers.ContainerConfigurations.From(project, profile)
        : new()
        {
            Name = profile.Name,
            Project = project.Path,
            Aspire = project.IsAspireHost,
            LaunchProfile = profile.Name,
            Program = profile.RunsProject ? string.Empty : profile.ExecutablePath ?? string.Empty,
            Arguments = profile.CommandLineArgs ?? string.Empty,
            // A Godot project's profile running Godot on it with --editor opens its editor, as the editor's configuration does.
            GodotEditor = project.GodotFolder is not null && !profile.RunsProject && profile.ExecutablePath is { } program
                && GodotExecutables.NamesGodot(program, OperatingSystem.IsWindows()) && GodotExecutables.OpensEditor(CommandLine.Split(profile.CommandLineArgs)),
            Environment = profile.Environment(),
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory) ? DotnetRunFolder : profile.WorkingDirectory,
            LaunchBrowser = profile.LaunchBrowser,
            BrowserUrl = profile.BrowserUrl(),
            Unsupported = profile.Unsupported,
        };

    /// <summary>The name a new or copied one takes: the one wanted, or with the first number that makes it unique.</summary>
    public static string UniqueName(string wanted, IEnumerable<string> taken)
    {
        HashSet<string> names = new(taken, StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(wanted))
        {
            return wanted;
        }
        int n = 1;
        while (names.Contains($"{wanted} ({n})"))
        {
            n++;
        }
        return $"{wanted} ({n})";
    }

    /// <summary>Its values brought into range, as read from a file this build did not write: a missing value its default,
    /// a kind, console, mode or service action this build does not know the default, empty names and paths left out.</summary>
    public RunConfiguration Clamped() => this with
    {
        Kind = Enum.IsDefined(Kind) ? Kind : RunKind.Project,
        Project = Project ?? string.Empty,
        LinkedAppHosts = [.. (LinkedAppHosts ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(Core.PathNames.Comparer)],
        Framework = Framework ?? string.Empty,
        LaunchProfile = LaunchProfile ?? string.Empty,
        Program = Program ?? string.Empty,
        Script = Script ?? string.Empty,
        Arguments = Arguments ?? string.Empty,
        GodotCopies = Math.Clamp(GodotCopies, 1, GameCopies.Most),
        CopyArguments = [.. (CopyArguments ?? []).Select(a => a ?? string.Empty).Take(GameCopies.Most)],
        Environment = (Environment ?? []).Where(v => !string.IsNullOrEmpty(v.Key)).ToDictionary(v => v.Key, v => v.Value ?? string.Empty),
        WorkingDirectory = WorkingDirectory ?? string.Empty,
        BuildConfiguration = BuildConfiguration ?? string.Empty,
        BrowserUrl = BrowserUrl ?? string.Empty,
        Console = Enum.IsDefined(Console) ? Console : ConsoleKind.Internal,
        ComposeFiles = [.. (ComposeFiles ?? []).Where(f => !string.IsNullOrWhiteSpace(f))],
        EnvFiles = [.. (EnvFiles ?? []).Where(f => !string.IsNullOrWhiteSpace(f))],
        Profiles = [.. (Profiles ?? []).Where(f => !string.IsNullOrWhiteSpace(f))],
        Services = [.. (Services ?? []).Where(f => !string.IsNullOrWhiteSpace(f))],
        ServiceActions = (ServiceActions ?? []).Where(a => !string.IsNullOrEmpty(a.Key))
            .ToDictionary(a => a.Key, a => Enum.IsDefined(a.Value) ? a.Value : ServiceStart.Run),
        ServiceProjects = (ServiceProjects ?? []).Where(a => !string.IsNullOrEmpty(a.Key) && !string.IsNullOrEmpty(a.Value))
            .ToDictionary(a => a.Key, a => a.Value),
        Mode = Enum.IsDefined(Mode) ? Mode : ContainerMode.Fast,
        LaunchService = LaunchService ?? string.Empty,
        Dockerfile = Dockerfile ?? string.Empty,
        Context = Context ?? string.Empty,
        BuildArguments = BuildArguments ?? string.Empty,
        RunArguments = RunArguments ?? string.Empty,
        Starts = [.. (Starts ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Project)).Select(s => s with { LaunchProfile = s.LaunchProfile ?? string.Empty })],
    };

    /// <summary>A deep enough copy to edit in Edit Configurations without touching the one it came from.</summary>
    public RunConfiguration Copied() => this with
    {
        Starts = [.. Starts],
        Environment = new Dictionary<string, string>(Environment),
        LinkedAppHosts = [.. LinkedAppHosts],
        ComposeFiles = [.. ComposeFiles],
        EnvFiles = [.. EnvFiles],
        Profiles = [.. Profiles],
        Services = [.. Services],
        ServiceActions = new Dictionary<string, ServiceStart>(ServiceActions),
        ServiceProjects = new Dictionary<string, string>(ServiceProjects),
    };
}

/// <summary>A folder's own run configurations, the person's changes to the generated ones and the one chosen, kept in its
/// place in the store. The generated ones are not kept: they are made again from the projects each time, each one's
/// changes (<see cref="Edits"/>) put over it.</summary>
public sealed record RunConfigurationsState : IStateDocument<RunConfigurationsState>
{
    public List<RunConfiguration> Configurations { get; init; } = [];

    /// <summary>The person's changes to generated configurations, by the generated one's name (<see cref="ConfigurationEdit"/>),
    /// its paths relative to the folder: kept while its source is gone too, for a launch profile a branch takes away and
    /// brings back.</summary>
    public Dictionary<string, JsonNode?> Edits { get; init; } = [];

    /// <summary>The name of the configuration chosen in the toolbar, the person's or a generated one.</summary>
    public string? Selected { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned, Kinded, Edited];

    /// <summary>Version 2, when a configuration gained a kind: every one before it ran a project.</summary>
    private static void Kinded(JsonObject document)
    {
        if (document["configurations"] is JsonArray configurations)
        {
            foreach (JsonObject configuration in configurations.OfType<JsonObject>())
            {
                configuration["kind"] ??= nameof(RunKind.Project);
            }
        }
    }

    /// <summary>Version 3, when a generated configuration could be changed in place: none had been, and the person's own
    /// (their copies among them) stay as they are.</summary>
    private static void Edited(JsonObject document) => document["edits"] ??= new JsonObject();

    /// <summary>One without a name is left out, as is a second of the same name, a project, Dockerfile or SDK container
    /// configuration without its project and a Compose one without its files; a kind, console, mode or service action this
    /// build does not know is the default. A change to a generated one that is no object of fields is left out.</summary>
    public RunConfigurationsState Clamped()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        List<RunConfiguration> kept = [];
        foreach (RunConfiguration configuration in Configurations ?? [])
        {
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.Name))
            {
                continue;
            }
            RunConfiguration clamped = configuration.Clamped();
            if ((clamped.Kind == RunKind.Compose ? clamped.ComposeFiles.Count == 0 : string.IsNullOrWhiteSpace(clamped.Project)) || !names.Add(clamped.Name))
            {
                continue;
            }
            kept.Add(clamped);
        }
        Dictionary<string, JsonNode?> edits = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, JsonNode? edit) in Edits ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name) && edit is JsonObject)
            {
                edits.TryAdd(name, edit);
            }
        }
        return this with { Configurations = kept, Edits = edits };
    }
}
