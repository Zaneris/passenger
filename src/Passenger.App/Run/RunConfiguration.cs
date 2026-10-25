using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Passenger.App.Aspire;
using Passenger.App.State;
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
/// run by the person's engine from Passenger's own build of its .NET services (<see cref="Containers.ContainerRun"/>).</summary>
public enum RunKind
{
    Project,
    Compose,
    Dockerfile,
    SdkContainer,
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

    /// <summary>The project's file: a canonical path in the window, relative to the open folder in the store.</summary>
    public string Project { get; init; } = string.Empty;

    /// <summary>Whether Passenger coordinates this AppHost and its project resources.</summary>
    public bool Aspire { get; init; }

    /// <summary>Additional AppHosts started and stopped with this Aspire configuration.</summary>
    public IReadOnlyList<string> LinkedAppHosts { get; init; } = [];

    /// <summary>The target framework run, for a project with several; empty for the first.</summary>
    public string Framework { get; init; } = string.Empty;

    /// <summary>The launch profile its values came from, if one did.</summary>
    public string LaunchProfile { get; init; } = string.Empty;

    /// <summary>A program run in place of the project's own (an <c>Executable</c> profile's); empty for the project's.
    /// MSBuild's <c>$(Property)</c> and the environment's <c>%NAME%</c> are expanded, as below.</summary>
    public string Program { get; init; } = string.Empty;

    /// <summary>The program's arguments as one command line.</summary>
    public string Arguments { get; init; } = string.Empty;

    public Dictionary<string, string> Environment { get; init; } = [];

    /// <summary>Where the program runs, relative to the project's folder; empty for the project's output folder.</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    public bool BuildBeforeLaunch { get; init; } = true;

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

    /// <summary>Whether a container run collects its .NET services' logs and traces: Aspire's dashboard run beside them,
    /// which they are told to send OpenTelemetry to. Off unless chosen, since its image is pulled the first time.</summary>
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

    /// <summary>Whether it runs containers rather than a program on this machine.</summary>
    [JsonIgnore]
    public bool IsContainer => Kind != RunKind.Project;

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

    /// <summary>Why it cannot launch here among these projects: its launch profile's kind, or, when it runs its project's
    /// own program, a project that runs only on Windows (WPF, Windows Forms) elsewhere; null when it can.</summary>
    public string? WhyNotHere(IEnumerable<ProjectModel> projects) => Unsupported
        ?? (!IsContainer && Aspire && Path.GetExtension(Project).Equals(".cs", StringComparison.OrdinalIgnoreCase)
            ? AspireAppHosts.SingleFileWhyNot(Project, projects.Select(project => project.Path)) : null)
        ?? (!IsContainer && Program.Length == 0 && projects.FirstOrDefault(p => Core.PathNames.Same(p.Path, Project)) is { } project ? project.RunsOnlyOnWindows(Framework) : null);

    /// <summary>The configurations the folder's executable projects make: one per launch profile, named "Project: profile",
    /// and the project itself when none of its profiles runs it here; then single-file AppHosts and the container ones the
    /// workspace's compose files, Dockerfiles, SDK container projects and Docker Compose projects (.dcproj) make
    /// (<see cref="Containers.ContainerConfigurations"/>).</summary>
    public static IReadOnlyList<RunConfiguration> Generate(IEnumerable<ProjectModel> projects, WorkspaceModel? workspace = null,
        IEnumerable<string>? singleFileAppHosts = null)
    {
        List<RunConfiguration> generated = [];
        List<ProjectModel> all = [.. projects];
        foreach (ProjectModel project in all.Where(p => p.Exists && p.IsRunnable))
        {
            string name = project.IsAspireHost ? $"Aspire: {project.Name}" : project.Name;
            IReadOnlyList<LaunchProfile> profiles = Run.LaunchProfile.Read(project);
            if (!profiles.Any(p => p.Unsupported is null && !p.RunsInContainer))
            {
                generated.Add(new RunConfiguration { Name = name, Project = project.Path, Aspire = project.IsAspireHost, Generated = true });
            }
            generated.AddRange(profiles.Select(p => From(project, p) with { Name = $"{name}: {p.Name}", Generated = true }));
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
        return generated;
    }

    /// <summary>A configuration with a launch profile's values: what generating one makes, and what choosing a profile in
    /// Edit Configurations fills in. A profile's own folder, else the project's, as <c>dotnet run</c> runs it; a container
    /// profile's (Docker, SdkContainer) a container configuration of its project.</summary>
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
            Environment = profile.Environment(),
            WorkingDirectory = string.IsNullOrWhiteSpace(profile.WorkingDirectory) ? "$(ProjectDir)" : profile.WorkingDirectory,
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
        LinkedAppHosts = [.. (LinkedAppHosts ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(
            StringComparer.FromComparison(Core.PathNames.Comparison))],
        Framework = Framework ?? string.Empty,
        LaunchProfile = LaunchProfile ?? string.Empty,
        Program = Program ?? string.Empty,
        Arguments = Arguments ?? string.Empty,
        Environment = (Environment ?? []).Where(v => !string.IsNullOrEmpty(v.Key)).ToDictionary(v => v.Key, v => v.Value ?? string.Empty),
        WorkingDirectory = WorkingDirectory ?? string.Empty,
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
    };

    /// <summary>A deep enough copy to edit in Edit Configurations without touching the one it came from.</summary>
    public RunConfiguration Copied() => this with
    {
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
