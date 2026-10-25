using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.Debugging;
using Passenger.App.Run;
using Passenger.App.Telemetry;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Containers;

/// <summary>A .NET service of a container run: its Compose service, the project it runs and that project's assembly,
/// where Passenger's build puts it (mounted into the container in fast mode), what the configuration does with it, the
/// stage fast mode stops its image at, and the folder its image's paths map back to (<see cref="Paths"/>).</summary>
public sealed record DotnetService(string Service, string Project, string Assembly, string Output, ServiceStart Action, string? Stage, ContainerPaths Paths)
    : IRunService
{
    /// <summary>Whether this run starts it.</summary>
    public bool Starts => Action != ServiceStart.DontStart;

    /// <summary>Why fast mode cannot run it (built for one runtime, a Dockerfile with no runtime stage), kept for a service
    /// marked Don't start, which its run refuses only when a start of it is asked later; null when it can run.</summary>
    public string? Unfit { get; init; }

    string IRunService.Name => Service;
}

/// <summary>What a container configuration runs, worked out once from Compose's own model of its files: the Compose
/// words every command takes (<c>compose -p NAME -f … -f override --env-file … --profile …</c>, the override Passenger
/// wrote into the folder's state last), the services it starts, the .NET ones Passenger builds on the host, those Compose
/// watches, the browser's address once known, and the paths of its images. Data; <see cref="ContainerRun"/> runs it.</summary>
public sealed record ContainerPlan(string Name, string Folder, string ProjectName, IReadOnlyList<string> Compose, IReadOnlyList<string> Start,
    IReadOnlyList<DotnetService> Dotnet, IReadOnlyList<string> Backing, IReadOnlyList<string> Watched, ContainerMode Mode, bool KeepBacking,
    string? BrowserUrl, string? LaunchService, int? LaunchPort, ContainerPaths Paths)
{
    /// <summary>The projects Run builds first: each .NET service's that starts, once, in fast mode (images mode builds its
    /// images as Compose says, from their Dockerfiles).</summary>
    public IReadOnlyList<string> Builds => Mode == ContainerMode.Fast
        ? [.. Dotnet.Where(d => d.Starts).Select(d => d.Project).Distinct(StringComparer.Ordinal)]
        : [];

    /// <summary>The services Rerun may restart, those Passenger builds.</summary>
    public IEnumerable<DotnetService> Restartable => Dotnet.Where(d => d.Starts);

    /// <summary>Writes Passenger's override into the state, which <see cref="Compose"/> names: after the host's build, with the
    /// services held at their start (for the debugger or the profiler), and those of them whose code the debugger may change
    /// (DOTNET_MODIFIABLE_ASSEMBLIES=debug, which a profiled service goes without, since it would measure code built for
    /// changing); each .NET service as the plan has it now (one started later run from Passenger's build).</summary>
    public Task WriteOverride(IReadOnlyCollection<string> held, IReadOnlyCollection<string> modifiable, DevelopmentCertificate.Exported? certificate, CancellationToken cancel) =>
        Writer(Dotnet, held, modifiable, certificate, cancel);

    /// <summary>What <see cref="WriteOverride"/> writes with, given the plan's .NET services.</summary>
    public Func<IReadOnlyList<DotnetService>, IReadOnlyCollection<string>, IReadOnlyCollection<string>, DevelopmentCertificate.Exported?, CancellationToken, Task> Writer { get; init; } =
        (_, _, _, _, _) => Task.CompletedTask;

    /// <summary>The plan with .NET services it left unstarted (marked Don't start) started from now, run from Passenger's build
    /// as the rest are: the override gives them fast mode's stage and mounts, Rerun builds and restarts them, Stop takes them.</summary>
    public ContainerPlan StartingLater(IReadOnlyCollection<string> later) => this with
    {
        Dotnet = [.. Dotnet.Select(d => d.Action == ServiceStart.DontStart && later.Contains(d.Service) ? d with { Action = ServiceStart.Run } : d)],
        Start = Start.Count == 0 ? Start : [.. Start.Concat(later).Distinct(StringComparer.Ordinal)],
        Touched = [.. Touched.Concat(later).Distinct(StringComparer.Ordinal)],
        Notes = [.. Notes.Where(note => !later.Any(service => note == DependedOn(service)))],
    };

    /// <summary>What the run says of a .NET service marked Don't start that a service it starts depends on.</summary>
    public static string DependedOn(string service) =>
        $"{service} is marked Don't start, but a service it starts depends on it: Compose starts it from its own image, as its files build it";

    /// <summary>The .NET services Debug debugs: those the configuration marks Debug, else every .NET service it starts (Run
    /// with Hot Reload: every one it starts).</summary>
    public IReadOnlyList<DotnetService> Debugged(bool marked) =>
        [.. Dotnet.Where(d => d.Starts && (Start.Count == 0 || Start.Contains(d.Service)))
            .Where(d => !marked || d.Action == ServiceStart.Debug || !Dotnet.Any(m => m.Action == ServiceStart.Debug && (Start.Count == 0 || Start.Contains(m.Service))))];

    /// <summary>The one .NET service Profile records, the profiler window following one program: of those marked Debug (else
    /// every .NET service it starts), the launched service, else the first; null when it starts none.</summary>
    public DotnetService? Profiled() => Debugged(marked: true) is { } candidates ? candidates.FirstOrDefault(d => d.Service == LaunchService) ?? candidates.FirstOrDefault() : null;

    /// <summary>Whether this run starts a .NET service (as itself, not as another's dependency alone).</summary>
    public bool Starts(string service) => Dotnet.Any(d => d.Service == service && d.Starts) && (Start.Count == 0 || Start.Contains(service));

    /// <summary>The <c>dotnet publish -t:PublishContainer</c> words that make an SDK container's image as deployed, or null.</summary>
    public IReadOnlyList<string>? Publish { get; init; }

    /// <summary>The folder's place in the state for its container runs, where the development certificate is exported.</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>The .NET services that serve HTTPS (Kestrel told an https address or port, or a port of HTTPS's), which take
    /// the development certificate, or are told to serve HTTP instead when there is none.</summary>
    public IReadOnlyList<string> Https { get; init; } = [];

    /// <summary>The services <c>up</c> makes: those it starts and what they depend on (every service when it names none),
    /// whose containers it would make again over any of the project's it finds.</summary>
    public IReadOnlyList<string> Touched { get; init; } = [];

    /// <summary>What the run says of itself as it comes up (a service marked Don't start that another depends on, one run as
    /// the person on this Linux engine), after its status.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The dashboard it runs beside its services when its configuration collects logs and traces; null when not.</summary>
    public ContainerTelemetry? Telemetry { get; init; }
}

/// <summary>Works out a container configuration's <see cref="ContainerPlan"/>: Compose's model of its files (a Dockerfile
/// or SDK container configuration's is a compose file Passenger writes for it, one service), the project each service
/// runs (its Dockerfile's <c>ENTRYPOINT ["dotnet", "X.dll"]</c> by assembly, else its <c>dotnet publish X.csproj</c>, else
/// the Docker Compose project's, else the person's choice), each project's build output and Visual Studio's container
/// properties through MSBuild, and the override file of Passenger's own, which every command takes as one more <c>-f</c>.
/// Throws a <see cref="LaunchException"/> saying why it cannot run.</summary>
public static class ContainerPlans
{
    /// <summary>Where fast mode mounts Passenger's build of a service's project inside its container.</summary>
    public const string AppFolder = "/passenger/app";

    public static async Task<ContainerPlan> ResolveAsync(RunConfiguration configuration, IReadOnlyList<ProjectModel> projects, WorkspaceModel? workspace,
        string stateFolder, ContainerEngine engine, IReadOnlyList<string> labels, CancellationToken cancel)
    {
        string folder = workspace?.Folder ?? throw new LaunchException("Nothing to run: no folder is open");
        string state = StateOf(stateFolder, folder);
        Directory.CreateDirectory(state);
        string file = Slug(configuration.Name);
        List<string> files;
        string runsIn;
        Dictionary<string, string> fixedProjects = new(configuration.ServiceProjects, StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> properties = new Dictionary<string, string>();
        ProjectModel? single = null;
        ContainerMode mode = configuration.Mode;
        IReadOnlyList<string>? publish = null;
        if (configuration.Kind == RunKind.Compose)
        {
            files = [.. configuration.ComposeFiles];
            if (files.Count == 0)
            {
                throw new LaunchException($"{configuration.Name} cannot run: it names no compose file");
            }
            if (files.FirstOrDefault(f => !File.Exists(f)) is { } missing)
            {
                throw new LaunchException($"{configuration.Name} cannot run: its compose file {missing} is not there");
            }
            runsIn = Path.GetDirectoryName(files[0])!;
        }
        else
        {
            single = projects.FirstOrDefault(p => PathNames.Same(p.Path, configuration.Project))
                ?? throw new LaunchException($"{configuration.Name} cannot run: its project {Path.GetFileName(configuration.Project)} is not loaded in the open folder");
            properties = await LaunchPlan.EvaluateAsync(single, configuration.Framework.Length > 0 ? configuration.Framework : null,
                ["TargetPath", "TargetDir", "AssemblyName", "DockerfileContext", "DockerfileFile", "DockerfileFastModeStage", "ContainerDevelopmentMode",
                    "DockerfileBuildArguments", "DockerfileRunArguments", "DockerfileTag", "ContainerBaseImage", "ContainerPort", "RuntimeIdentifier", "SelfContained"],
                workspace, cancel, configuration.Kind == RunKind.SdkContainer ? "ComputeContainerBaseImage" : null);
            if (configuration.Kind == RunKind.Dockerfile && string.Equals(properties.GetValueOrDefault("ContainerDevelopmentMode"), "Regular", StringComparison.OrdinalIgnoreCase))
            {
                mode = ContainerMode.Images;
            }
            // As deployed, the SDK makes the image itself (PublishContainer, into the engine), named for the run alone.
            if (configuration.Kind == RunKind.SdkContainer && mode == ContainerMode.Images)
            {
                string repository = $"passenger-{Slug(configuration.Name)}-{ContainerLabels.FolderValue(workspace.Folder)[..8]}";
                properties = new Dictionary<string, string>(properties, StringComparer.OrdinalIgnoreCase) { ["PassengerImage"] = $"{repository}:{SdkImageTag}" };
                publish = ["publish", single.Path, "-t:PublishContainer", "-c", "Debug", $"-p:ContainerRepository={repository}", $"-p:ContainerImageTag={SdkImageTag}",
                    "-nologo"];
            }
            string service = DockerFiles.ServiceName(single.Name.Split(' ')[0]);
            fixedProjects[service] = single.Path;
            string composed = Path.Combine(state, $"{file}.compose.yaml");
            WriteJson(composed, SingleService(configuration, single, properties, service, workspace, mode));
            files = [composed];
            runsIn = single.Folder;
        }
        List<string> compose = Words(files, configuration, runsIn);
        ComposeModel model;
        try
        {
            model = await ComposeModel.ReadAsync(engine, compose, runsIn, cancel);
        }
        catch (ContainerCommandException said)
        {
            throw new ComposeModelException(configuration.Name, said.Message);
        }
        string projectName = model.Name.Length > 0 ? model.Name : Slug(Path.GetFileName(runsIn));
        compose.InsertRange(1, ["-p", projectName]);

        List<DotnetService> dotnet = [];
        foreach (ComposeService service in model.Services)
        {
            if (DotnetProject(service, projects, workspace, configuration, fixedProjects) is not { } project)
            {
                continue;
            }
            IReadOnlyDictionary<string, string> its = single is not null && PathNames.Same(project.Path, single.Path) ? properties
                : await LaunchPlan.EvaluateAsync(project, null, ["TargetPath", "TargetDir", "AssemblyName", "DockerfileFastModeStage", "RuntimeIdentifier", "SelfContained"], workspace, cancel);
            string output = its.GetValueOrDefault("TargetDir") is { Length: > 0 } dir ? Path.TrimEndingDirectorySeparator(dir)
                : Path.GetDirectoryName(its.GetValueOrDefault("TargetPath") ?? string.Empty) ?? project.Folder;
            string assembly = its.GetValueOrDefault("AssemblyName") is { Length: > 0 } named ? named : Path.GetFileNameWithoutExtension(project.Path);
            ServiceStart action = configuration.ServiceActions.GetValueOrDefault(service.Name, ServiceStart.Run);
            DockerfileStages? stages = service.DockerfilePath is { } dockerfile && File.Exists(dockerfile) ? DockerfileStages.Read(File.ReadAllText(dockerfile)) : null;
            string? stage = null;
            string? unfit = null;
            if (mode == ContainerMode.Fast)
            {
                if (its.GetValueOrDefault("RuntimeIdentifier") is { Length: > 0 } || string.Equals(its.GetValueOrDefault("SelfContained"), "true", StringComparison.OrdinalIgnoreCase))
                {
                    unfit = $"{configuration.Name} cannot run {service.Name} in fast mode: {project.Name} is built for one runtime"
                        + $"{(its.GetValueOrDefault("RuntimeIdentifier") is { Length: > 0 } r ? $" ({r})" : string.Empty)}, and fast mode runs a framework-dependent build with dotnet; "
                        + "choose Build images, as deployed in Run > Edit Configurations";
                }
                else if (stages is not null && (stage = stages.FastStage(its.GetValueOrDefault("DockerfileFastModeStage"))?.Name) is null)
                {
                    unfit = $"{configuration.Name} cannot run {service.Name} in fast mode: its Dockerfile has no runtime stage to run Passenger's build in "
                        + "(a stage named base, or one its final stage is FROM); choose Build images, as deployed in Run > Edit Configurations";
                }
                // One marked Don't start is refused only when a start of it is asked, later in the run.
                if (unfit is not null && action != ServiceStart.DontStart)
                {
                    throw new LaunchException(unfit);
                }
            }
            ContainerPaths paths = stages is not null && service.Context is { } context ? ContainerPaths.For(stages, context) : ContainerPaths.None;
            dotnet.Add(new DotnetService(service.Name, project.Path, assembly, output, action, stage, paths) { Unfit = unfit });
        }
        HashSet<string> dotnetNames = [.. dotnet.Select(d => d.Service)];
        bool dontStarts = configuration.ServiceActions.Values.Any(a => a == ServiceStart.DontStart);
        List<string> start = configuration.Services.Count > 0 ? [.. configuration.Services.Where(s => configuration.ServiceActions.GetValueOrDefault(s) != ServiceStart.DontStart)]
            : dontStarts ? [.. model.Services.Select(s => s.Name).Where(s => configuration.ServiceActions.GetValueOrDefault(s) != ServiceStart.DontStart)]
            : [];
        // up given no service would start them all, those marked Don't start among them.
        if (start.Count == 0 && (configuration.Services.Count > 0 || dontStarts))
        {
            throw new LaunchException($"{configuration.Name} starts nothing: every service it would start is marked Don't start (Run > Edit Configurations)");
        }
        List<string> touched = start.Count == 0 ? [.. model.Services.Select(s => s.Name)] : Needed(model, start);
        List<string> backing = [.. model.Services.Select(s => s.Name).Where(s => !dotnetNames.Contains(s))];
        List<string> watched = [.. model.Services.Where(s => s.Watches && !dotnetNames.Contains(s.Name) && (start.Count == 0 || start.Contains(s.Name))).Select(s => s.Name)];
        List<string> notes = [.. dotnet.Where(d => !d.Starts && touched.Contains(d.Service)).Select(d => ContainerPlan.DependedOn(d.Service))];
        // As deployed, the image's own user could not read what is kept for the person alone (OverrideAsync's owner).
        if (mode == ContainerMode.Images && OperatingSystem.IsLinux() && DotnetProcesses.UserAndGroup is { } owner)
        {
            foreach (DotnetService runs in dotnet.Where(d => d.Starts && touched.Contains(d.Service)))
            {
                string[] kept = [.. Serves(model[runs.Service]!) ? ["development certificate"] : Array.Empty<string>(),
                    .. !Development(model[runs.Service]!) || UserSecrets(projects.FirstOrDefault(p => PathNames.Same(p.Path, runs.Project))) is null ? Array.Empty<string>() : ["user secrets"]];
                if (kept.Length > 0)
                {
                    notes.Add($"{runs.Service} runs as you ({owner}) on this Linux engine, as fast mode does, so it can read the {string.Join(" and ", kept)} kept for you alone");
                }
            }
        }

        ContainerTelemetry? telemetry = configuration.Telemetry ? ContainerTelemetry.New() : null;
        if (telemetry is not null && model[ContainerTelemetry.Service] is not null)
        {
            throw new LaunchException($"{configuration.Name} cannot collect logs and traces: a service of its own is named {ContainerTelemetry.Service}, Passenger's dashboard's name");
        }
        string overrideFile = Path.Combine(state, $"{file}.override.yaml");
        compose.InsertRange(compose.IndexOf("--project-directory"), ["-f", overrideFile]);
        // Written once the host's build is done: what fast mode mounts (its static web assets' folders) is that build's.
        EngineState engineState = await engine.StateAsync(cancel: cancel);
        bool pin = Overrides(engineState.ComposeVersion);
        // Rootless Podman maps a container's users onto the person's subordinate ids: keep-id makes the person's own theirs.
        bool keepId = engineState is { IsPodman: true, Rootless: true };
        // Each port Passenger pins is chosen once for the plan: every override it writes after (a hold, a restart) keeps it.
        Dictionary<(string Service, int Port), int> pinned = [];
        async Task WriteOverride(IReadOnlyList<DotnetService> now, IReadOnlyCollection<string> held, IReadOnlyCollection<string> modifiable, DevelopmentCertificate.Exported? certificate,
            CancellationToken stop)
        {
            Dictionary<string, IReadOnlyDictionary<string, string>> images = [];
            WriteJson(overrideFile, await OverrideAsync(model, projectName, now, mode, labels, projects, state, pin ? pinned : null, held, modifiable, certificate, keepId, telemetry,
                images, stop), overriding: true);
            // A hold is its image's to refuse too, read as the override just written builds it: one its image turns off goes unheld.
            List<ComposeService> holding = [.. model.Services.Where(s => held.Contains(s.Name) && now.Any(d => d.Service == s.Name && d.Starts))];
            if (holding.Count == 0)
            {
                return;
            }
            images = await ImageEnvironmentsAsync(engine, compose, runsIn, holding, projectName, mode, stop);
            if (holding.Any(s => HeldStart.DebuggerOff(Started(s, images)) is not null))
            {
                WriteJson(overrideFile, await OverrideAsync(model, projectName, now, mode, labels, projects, state, pin ? pinned : null, held, modifiable, certificate, keepId,
                    telemetry, images, stop), overriding: true);
            }
        }
        if (mode == ContainerMode.Images || dotnet.Count == 0)
        {
            await WriteOverride(dotnet, [], [], null, cancel);
        }

        DotnetService? launched = dotnet.FirstOrDefault(d => d.Service == configuration.LaunchService) ?? dotnet.FirstOrDefault(d => d.Starts && (start.Count == 0 || start.Contains(d.Service)));
        string? launchService = configuration.LaunchService.Length > 0 ? configuration.LaunchService : launched?.Service;
        int? launchPort = launchService is null ? null : model[launchService]?.Ports.FirstOrDefault(p => p.Protocol == "tcp")?.Target;
        // Every image built from a folder here maps its paths back, a .NET service's or not.
        ContainerPaths all = model.Services
            .Where(s => s.DockerfilePath is { } dockerfile && File.Exists(dockerfile) && dotnet.All(d => d.Service != s.Name))
            .Aggregate(dotnet.Aggregate(ContainerPaths.None, (map, d) => map.With(d.Paths)),
                (map, s) => map.With(ContainerPaths.For(DockerfileStages.Read(File.ReadAllText(s.DockerfilePath!)), s.Context!)));
        return new ContainerPlan(configuration.Name, runsIn, projectName, compose, start, dotnet, backing, watched, mode, configuration.KeepBackingServices,
            configuration.LaunchBrowser && configuration.BrowserUrl.Length > 0 ? configuration.BrowserUrl : null, launchService, launchPort, all)
        {
            Writer = WriteOverride,
            Publish = publish,
            State = state,
            Https = [.. model.Services.Where(s => dotnet.Any(d => d.Service == s.Name) && Serves(s)).Select(s => s.Name)],
            Touched = touched,
            Notes = notes,
            Telemetry = telemetry,
        };
    }

    /// <summary>The services <c>up</c> of these makes: they and what they depend on, as Compose's model says.</summary>
    private static List<string> Needed(ComposeModel model, IReadOnlyList<string> start)
    {
        List<string> needed = [];
        Queue<string> next = new(start);
        while (next.TryDequeue(out string? service))
        {
            if (!needed.Contains(service))
            {
                needed.Add(service);
                foreach (string on in model[service]?.DependsOn ?? [])
                {
                    next.Enqueue(on);
                }
            }
        }
        return needed;
    }

    /// <summary>The tag of an SDK container's image Passenger has the SDK make, as deployed.</summary>
    public const string SdkImageTag = "passenger";

    /// <summary>The <c>compose -f … --env-file … --profile … --project-directory …</c> words of files and a configuration.</summary>
    private static List<string> Words(IReadOnlyList<string> files, RunConfiguration configuration, string runsIn)
    {
        List<string> compose = ["compose"];
        foreach (string each in files)
        {
            compose.AddRange(["-f", each]);
        }
        foreach (string env in configuration.EnvFiles)
        {
            compose.AddRange(["--env-file", env]);
        }
        foreach (string profile in configuration.Profiles)
        {
            compose.AddRange(["--profile", profile]);
        }
        compose.AddRange(["--project-directory", runsIn]);
        return compose;
    }

    /// <summary>A Compose configuration's services as Compose's own model has them (Edit Configurations' table), each with
    /// the project it runs as a run would find it, and whether it is built from a Dockerfile here (whose project a person
    /// may choose); a <see cref="ContainerCommandException"/> with what Compose said when it cannot read the files.</summary>
    public static async Task<IReadOnlyList<(ComposeService Service, string? Project, bool Built)>> ServicesAsync(RunConfiguration configuration,
        IReadOnlyList<ProjectModel> projects, ContainerEngine engine, CancellationToken cancel = default)
    {
        List<string> files = [.. configuration.ComposeFiles.Where(File.Exists)];
        if (files.Count == 0)
        {
            throw new ContainerCommandException(configuration.ComposeFiles.Count == 0 ? "It names no compose file" : $"{configuration.ComposeFiles[0]} is not there");
        }
        string runsIn = Path.GetDirectoryName(files[0])!;
        ComposeModel model = await ComposeModel.ReadAsync(engine, Words(files, configuration, runsIn), runsIn, cancel);
        Dictionary<string, string> chosen = new(configuration.ServiceProjects, StringComparer.Ordinal);
        return [.. model.Services.Select(s => (s, DotnetProject(s, projects, null, configuration, chosen)?.Path, s.DockerfilePath is { } file && File.Exists(file)))];
    }

    /// <summary>The project a service runs: its Dockerfile's (by ENTRYPOINT's assembly, else its dotnet publish), else the
    /// person's choice kept in the configuration, else a project of the Docker Compose project named as the service.</summary>
    private static ProjectModel? DotnetProject(ComposeService service, IReadOnlyList<ProjectModel> projects, WorkspaceModel? workspace, RunConfiguration configuration,
        IReadOnlyDictionary<string, string> chosen)
    {
        if (chosen.TryGetValue(service.Name, out string? kept) && projects.FirstOrDefault(p => PathNames.Same(p.Path, kept)) is { } named)
        {
            return named;
        }
        if (service.DockerfilePath is { } dockerfile && File.Exists(dockerfile)
            && Web.ComposeDeclarations.Built(TextSnapshot.From(File.ReadAllText(dockerfile)), service.Context!, projects) is { } built)
        {
            return projects.FirstOrDefault(p => PathNames.Same(p.Path, built));
        }
        if (configuration.Project.Length > 0 && ContainerConfigurations.IsComposeProject(configuration.Project))
        {
            string dcproj = Path.GetFileName(configuration.Project);
            return projects.FirstOrDefault(p => DockerFiles.ServiceName(Path.GetFileNameWithoutExtension(p.Path)) == service.Name
                && (ContainerConfigurations.Text(p.Path) ?? string.Empty).Contains(dcproj, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    /// <summary>A Dockerfile or SDK container configuration as a compose file of one service: its image built from the
    /// project's Dockerfile and context (the project's DockerfileFile and DockerfileContext, else the configuration's, else
    /// the Dockerfile beside it and the solution's folder), or the SDK's base image, with the configuration's environment
    /// and <c>docker run</c> words (ports, env files, volumes, variables).</summary>
    private static JsonObject SingleService(RunConfiguration configuration, ProjectModel project, IReadOnlyDictionary<string, string> properties, string name,
        WorkspaceModel workspace, ContainerMode mode)
    {
        JsonObject service = [];
        List<int> exposed = [];
        if (configuration.Kind == RunKind.Dockerfile)
        {
            string dockerfile = configuration.Dockerfile.Length > 0 ? configuration.Dockerfile
                : properties.GetValueOrDefault("DockerfileFile") is { Length: > 0 } file ? Path.GetFullPath(Path.Combine(project.Folder, file))
                : ContainerConfigurations.Dockerfile(project)
                    ?? throw new LaunchException($"{configuration.Name} cannot run: {project.Name} has no Dockerfile");
            string context = configuration.Context.Length > 0 ? configuration.Context
                : properties.GetValueOrDefault("DockerfileContext") is { Length: > 0 } given ? Path.GetFullPath(Path.Combine(project.Folder, given))
                : workspace.Solution is { } solution ? Path.GetDirectoryName(solution.Path)! : workspace.Folder;
            JsonObject build = new() { ["context"] = context, ["dockerfile"] = dockerfile };
            JsonObject args = [];
            foreach (string word in CommandLine.Split($"{properties.GetValueOrDefault("DockerfileBuildArguments")} {configuration.BuildArguments}"))
            {
                string pair = word.StartsWith("--build-arg", StringComparison.Ordinal) ? word[11..].TrimStart('=') : word;
                if (pair.Split('=', 2) is [{ Length: > 0 } key, var value])
                {
                    args[key] = value;
                }
            }
            if (args.Count > 0)
            {
                build["args"] = args;
            }
            service["build"] = build;
            if (properties.GetValueOrDefault("DockerfileTag") is { Length: > 0 } tag && mode == ContainerMode.Images)
            {
                service["image"] = tag;
            }
            DockerfileStages stages = DockerfileStages.Read(File.Exists(dockerfile) ? File.ReadAllText(dockerfile) : string.Empty);
            DockerfileStage? running = mode == ContainerMode.Fast ? stages.FastStage(properties.GetValueOrDefault("DockerfileFastModeStage")) : stages.Final;
            exposed.AddRange(running is null ? [] : stages.Lineage(running).SelectMany(s => s.Exposed));
        }
        else
        {
            string image = properties.GetValueOrDefault("PassengerImage") is { Length: > 0 } published ? published
                : properties.GetValueOrDefault("ContainerBaseImage") is { Length: > 0 } basis ? basis
                : $"mcr.microsoft.com/dotnet/{(ContainerConfigurations.IsWeb(project, workspace.Projects) ? "aspnet" : "runtime")}:{FrameworkTag(project)}";
            service["image"] = image;
            exposed.AddRange((properties.GetValueOrDefault("ContainerPort") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => int.TryParse(p.Split(':')[0], NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0).Where(n => n > 0));
            if (exposed.Count == 0 && ContainerConfigurations.IsWeb(project, workspace.Projects))
            {
                exposed.Add(8080);
            }
        }
        JsonArray ports = [];
        JsonArray envFiles = [];
        JsonArray volumes = [];
        JsonObject environment = [];
        foreach ((string key, string value) in configuration.Environment)
        {
            environment[key] = value;
        }
        List<string> run = [.. CommandLine.Split($"{properties.GetValueOrDefault("DockerfileRunArguments")} {configuration.RunArguments}")];
        for (int i = 0; i < run.Count; i++)
        {
            string word = run[i];
            string? Next() => i + 1 < run.Count ? run[++i] : null;
            switch (word)
            {
                case "-p" or "--publish":
                    if (Next() is { } port)
                    {
                        ports.Add(port);
                    }
                    break;
                case "-P" or "--publish-all":
                    foreach (int each in exposed.Distinct())
                    {
                        ports.Add(each.ToString(CultureInfo.InvariantCulture));
                    }
                    break;
                case "-e" or "--env":
                    if (Next()?.Split('=', 2) is [{ Length: > 0 } key, var value])
                    {
                        environment[key] = value;
                    }
                    break;
                case "--env-file":
                    if (Next() is { } env)
                    {
                        envFiles.Add(Path.GetFullPath(Path.Combine(project.Folder, env)));
                    }
                    break;
                case "-v" or "--volume":
                    if (Next() is { } volume)
                    {
                        volumes.Add(volume);
                    }
                    break;
            }
        }
        if (ports.Count > 0)
        {
            service["ports"] = ports;
        }
        if (envFiles.Count > 0)
        {
            service["env_file"] = envFiles;
        }
        if (volumes.Count > 0)
        {
            service["volumes"] = volumes;
        }
        if (environment.Count > 0)
        {
            service["environment"] = environment;
        }
        return new JsonObject { ["name"] = $"passenger-{Slug(configuration.Name)}-{ContainerLabels.FolderValue(workspace.Folder)[..8]}", ["services"] = new JsonObject { [name] = service } };
    }

    /// <summary>The image tag of a project's newest .NET (10.0 of net10.0), else 10.0.</summary>
    private static string FrameworkTag(ProjectModel project) =>
        project.TargetFrameworks.Select(f => f.Split('-')[0]).Where(f => f.StartsWith("net", StringComparison.OrdinalIgnoreCase) && Version.TryParse(f[3..], out _))
            .Select(f => Version.Parse(f[3..])).OrderByDescending(v => v).Select(v => $"{v.Major}.{Math.Max(0, v.Minor)}").FirstOrDefault() ?? "10.0";

    /// <summary>The override Passenger lays over the files: its labels on every service, image and network it makes (an image
    /// built as deployed tagged apart from the one its files name); for each .NET service it starts, in fast mode its image's
    /// runtime stage running <c>dotnet /passenger/app/X.dll</c> as PID 1 over Passenger's build mounted read-only, in images
    /// mode a Debug build where its Dockerfile takes BUILD_CONFIGURATION; the development certificate and user secrets where
    /// they serve; a service held for the debugger started held (its debugger on), its code open to Hot Reload. JSON, read as YAML.</summary>
    private static async Task<JsonObject> OverrideAsync(ComposeModel model, string project, IReadOnlyList<DotnetService> dotnet, ContainerMode mode, IReadOnlyList<string> labels,
        IReadOnlyList<ProjectModel> projects, string state, Dictionary<(string Service, int Port), int>? pinned, IReadOnlyCollection<string> held,
        IReadOnlyCollection<string> modifiable, DevelopmentCertificate.Exported? certificate, bool keepId, ContainerTelemetry? telemetry,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> images, CancellationToken cancel)
    {
        JsonObject services = [];
        HashSet<string> heard = new(StringComparer.Ordinal);
        string? owner = OperatingSystem.IsLinux() ? DotnetProcesses.UserAndGroup : null;
        foreach (ComposeService service in model.Services)
        {
            JsonObject entry = new() { ["labels"] = Labels(labels) };
            if (service.Context is not null)
            {
                entry["build"] = new JsonObject { ["labels"] = Labels(labels) };
                // Built again with Passenger's labels, never under the image name its files give, which stays the person's.
                if (mode == ContainerMode.Images)
                {
                    entry["image"] = ImageOf(service, project, mode);
                    entry["pull_policy"] = "build";
                }
            }
            // A .NET service marked Don't start that another depends on runs as its files say: Passenger built nothing for it.
            if (dotnet.FirstOrDefault(d => d.Service == service.Name) is { Starts: true } built)
            {
                JsonObject environment = [];
                bool development = Development(service);
                JsonArray volumes = [];
                if (mode == ContainerMode.Fast)
                {
                    // Its own image stops at the runtime stage, tagged apart; one it only pulls (an SDK's base) runs as it is.
                    if (entry["build"] is JsonObject build)
                    {
                        if (built.Stage is { } stage)
                        {
                            build["target"] = stage;
                        }
                        entry["image"] = ImageOf(service, project, mode);
                        entry["pull_policy"] = "build";
                    }
                    entry["entrypoint"] = new JsonArray("dotnet", $"{AppFolder}/{built.Assembly}.dll");
                    entry["working_dir"] = AppFolder;
                    entry["init"] = true;
                    // The engine gives a port of its own choosing a new one at each restart: chosen once, so a browser stays right.
                    if (pinned is not null && service.Ports.Any(p => ComposeModel.HostPort(p.Published) is null))
                    {
                        entry["ports"] = new JsonArray([.. service.Ports.Select((p, i) =>
                            (JsonNode)Port(p, ComposeModel.HostPort(p.Published) is null ? Pinned(pinned, service.Name, i, p.HostIp) : null))]);
                    }
                    volumes.Add(new JsonObject { ["type"] = "bind", ["source"] = built.Output, ["target"] = AppFolder, ["read_only"] = true });
                    if (!service.Environment.ContainsKey("ASPNETCORE_ENVIRONMENT") && !service.Environment.ContainsKey("DOTNET_ENVIRONMENT"))
                    {
                        environment["ASPNETCORE_ENVIRONMENT"] = "Development";
                        environment["DOTNET_ENVIRONMENT"] = "Development";
                    }
                    if (development && StaticWebAssets(built, state, $"{model.Name}-{service.Name}") is { } assets)
                    {
                        foreach ((string source, string target) in assets.Mounts)
                        {
                            volumes.Add(new JsonObject { ["type"] = "bind", ["source"] = source, ["target"] = target, ["read_only"] = true });
                        }
                        environment["ASPNETCORE_STATICWEBASSETS"] = assets.Manifest;
                    }
                }
                else if (service.DockerfilePath is { } dockerfile && File.Exists(dockerfile) && DockerfileStages.Read(File.ReadAllText(dockerfile)).Declares("BUILD_CONFIGURATION"))
                {
                    JsonObject build = (JsonObject)(entry["build"] ??= new JsonObject());
                    build["args"] = new JsonObject { ["BUILD_CONFIGURATION"] = "Debug" };
                }
                (string Folder, string Id)? secrets = development ? UserSecrets(projects.FirstOrDefault(p => PathNames.Same(p.Path, built.Project))) : null;
                // Linux keeps the host's owners on a mount: the app runs as the person, who alone can read what is kept for them.
                if (owner is not null && (mode == ContainerMode.Fast || Serves(service) || secrets is not null))
                {
                    entry["user"] = owner;
                    if (keepId)
                    {
                        entry["userns_mode"] = "keep-id";
                    }
                }
                // Held until what attaches lets it go, never where nothing can; the profiler's gets no changeable code to measure.
                if (held.Contains(service.Name) && HeldStart.DebuggerOff(Started(service, images)) is null)
                {
                    foreach ((string name, string value) in HeldStart.Variables(changeable: modifiable.Contains(service.Name)))
                    {
                        environment[name] = value;
                    }
                }
                if (Serves(service) && certificate is { } exported)
                {
                    volumes.Add(new JsonObject { ["type"] = "bind", ["source"] = exported.Folder, ["target"] = "/https", ["read_only"] = true });
                    // PEM, its key unencrypted in the folder only the person enters, mounted read-only: no password anywhere.
                    environment["ASPNETCORE_Kestrel__Certificates__Default__Path"] = $"/https/{Path.GetFileName(exported.Certificate)}";
                    environment["ASPNETCORE_Kestrel__Certificates__Default__KeyPath"] = $"/https/{Path.GetFileName(exported.Key)}";
                }
                else if (Serves(service))
                {
                    OverHttp(service, environment);
                }
                // .NET reads user secrets from APPDATA first, so whichever user the app runs as finds this project's own there.
                if (secrets is { } own)
                {
                    volumes.Add(new JsonObject { ["type"] = "bind", ["source"] = own.Folder, ["target"] = $"{AppData}/Microsoft/UserSecrets/{own.Id}", ["read_only"] = true });
                    environment["APPDATA"] = AppData;
                }
                if (telemetry is not null && telemetry.Tell(service, entry, environment))
                {
                    heard.UnionWith(service.Networks);
                }
                if (volumes.Count > 0)
                {
                    entry["volumes"] = volumes;
                }
                if (environment.Count > 0)
                {
                    entry["environment"] = environment;
                }
            }
            services[service.Name] = entry;
        }
        if (telemetry is not null)
        {
            services[ContainerTelemetry.Service] = telemetry.Entry(Labels(labels), heard);
        }
        JsonObject made = new() { ["services"] = services };
        JsonObject networks = [];
        foreach ((string network, bool external) in model.Networks)
        {
            if (!external)
            {
                networks[network] = new JsonObject { ["labels"] = Labels(labels) };
            }
        }
        if (networks.Count > 0)
        {
            made["networks"] = networks;
        }
        return made;
    }

    /// <summary>The image a service runs: one built from its folder under Passenger's own tag for the mode, else the one it names.</summary>
    private static string? ImageOf(ComposeService service, string project, ContainerMode mode) =>
        service.Context is null ? service.Image : mode == ContainerMode.Images ? $"{project}-{service.Name}:passenger" : $"{project}-{service.Name}:passenger-fast";

    /// <summary>A variable of a service's runtime as its container starts it: Compose's environment's, else its image's own.</summary>
    private static Func<string, string?> Started(ComposeService service, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> images) =>
        name => service.Environment.TryGetValue(name, out string? set) ? set : images.GetValueOrDefault(service.Name)?.GetValueOrDefault(name);

    /// <summary>The environment each service's image gives its runtime (<c>docker image inspect</c>'s Config.Env), one built
    /// from its folder built first as <c>up</c> builds it. A service whose image is not here (its build failed, which
    /// <c>up</c> then says, or one only pulled that <c>up</c> is yet to pull) is left out, its environment Compose's alone.</summary>
    private static async Task<Dictionary<string, IReadOnlyDictionary<string, string>>> ImageEnvironmentsAsync(ContainerEngine engine, IReadOnlyList<string> compose,
        string folder, IReadOnlyList<ComposeService> services, string project, ContainerMode mode, CancellationToken cancel)
    {
        Dictionary<string, IReadOnlyDictionary<string, string>> read = new(StringComparer.Ordinal);
        string[] built = [.. services.Where(s => s.Context is not null).Select(s => s.Name)];
        try
        {
            if (built.Length > 0)
            {
                await engine.RunAsync([.. compose, "build", "--", .. built], ImageLimit, cancel, folder);
            }
        }
        catch (TimeoutException)
        {
            // Still building after its limit: up builds it again, saying what it does, and the image there now is read.
        }
        foreach (ComposeService service in services)
        {
            if (ImageOf(service, project, mode) is { } image && await EnvironmentAsync(image) is { } variables)
            {
                read[service.Name] = variables;
            }
        }
        return read;

        async Task<Dictionary<string, string>?> EnvironmentAsync(string image)
        {
            JsonElement described;
            try
            {
                described = await engine.JsonAsync(["image", "inspect", image], cancel);
            }
            catch (Exception unread) when (unread is ContainerCommandException or JsonException or TimeoutException)
            {
                return null;
            }
            if (described.ValueKind != JsonValueKind.Array || described.GetArrayLength() == 0 || !described[0].TryGetProperty("Config", out JsonElement config)
                || config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("Env", out JsonElement environment) || environment.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            Dictionary<string, string> variables = new(StringComparer.Ordinal);
            foreach (string[] pair in environment.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()?.Split('=', 2) : null).OfType<string[]>()
                .Where(p => p.Length == 2))
            {
                variables[pair[0]] = pair[1];
            }
            return variables;
        }
    }

    /// <summary>How long a service's image may take to build before its hold is read without it.</summary>
    private static readonly TimeSpan ImageLimit = TimeSpan.FromMinutes(15);

    /// <summary>Where the container finds an ASP.NET Core app's static web assets in Development, which its build's manifest
    /// names by the host's folders (wwwroot, obj's, packages'): each folder mounted under /passenger/assets, and the manifest
    /// written again with those places into the state, mounted and named by ASPNETCORE_STATICWEBASSETS. Null for an app
    /// with none.</summary>
    private static (IReadOnlyList<(string Source, string Target)> Mounts, string Manifest)? StaticWebAssets(DotnetService built, string state, string name)
    {
        string manifest = Path.Combine(built.Output, $"{built.Assembly}.staticwebassets.runtime.json");
        if (!File.Exists(manifest))
        {
            return null;
        }
        try
        {
            JsonObject read = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject ?? throw new JsonException("not an object");
            List<(string, string)> mounts = [];
            if (read["ContentRoots"] is JsonArray roots)
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    string root = roots[i]?.GetValue<string>() ?? string.Empty;
                    string target = $"/passenger/assets/{i}/";
                    // A root the build named but never made would stop the app as it starts, as it would on this machine.
                    string source = Directory.Exists(root) ? root : Directory.CreateDirectory(Path.Combine(state, "assets", name, i.ToString(CultureInfo.InvariantCulture))).FullName;
                    mounts.Add((Path.TrimEndingDirectorySeparator(source), target.TrimEnd('/')));
                    roots[i] = target;
                }
            }
            string written = Path.Combine(state, $"{name}.staticwebassets.runtime.json");
            OwnFiles.Write(written, read.ToJsonString());
            mounts.Add((written, "/passenger/assets/manifest.json"));
            return (mounts, "/passenger/assets/manifest.json");
        }
        catch (Exception unreadable) when (unreadable is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A service that serves HTTPS told to serve HTTP for this run, there being no certificate to give it
    /// (<see cref="DevelopmentCertificate.OverHttp"/>).</summary>
    private static void OverHttp(ComposeService service, JsonObject environment)
    {
        foreach ((string name, string value) in DevelopmentCertificate.OverHttp(service.Environment))
        {
            environment[name] = value;
        }
    }

    /// <summary>Whether a service runs in Development (as its files say, or with nothing said, as Passenger runs it), where it
    /// reads its user secrets and static web assets.</summary>
    private static bool Development(ComposeService service) =>
        !service.Environment.ContainsKey("ASPNETCORE_ENVIRONMENT") && !service.Environment.ContainsKey("DOTNET_ENVIRONMENT")
        || service.Environment.GetValueOrDefault("ASPNETCORE_ENVIRONMENT") == "Development" || service.Environment.GetValueOrDefault("DOTNET_ENVIRONMENT") == "Development";

    /// <summary>Whether a service serves HTTPS: Kestrel told an https address or an HTTPS port, or a port of HTTPS's.</summary>
    private static bool Serves(ComposeService service) =>
        service.Environment.GetValueOrDefault("ASPNETCORE_URLS")?.Contains("https:", StringComparison.OrdinalIgnoreCase) == true
        || service.Environment.ContainsKey("ASPNETCORE_HTTPS_PORTS") || service.Ports.Any(p => p.Target is 443 or 8081);

    /// <summary>Where a .NET service in its container finds its user secrets: APPDATA, which Passenger sets there.</summary>
    public const string AppData = "/passenger/appdata";

    /// <summary>The folder user secrets are kept in on this machine in place of .NET's own (APPDATA's Microsoft/UserSecrets
    /// on Windows, ~/.microsoft/usersecrets elsewhere): read for that alone, so a test's run never mounts the person's own.</summary>
    public const string UserSecretsVariable = Workspace.UserSecrets.Variable;

    /// <summary>The folder of a project's own user secrets on this machine and its id (<see cref="Workspace.UserSecrets.Of"/>),
    /// when it has an id that names one folder (no separator, never a dot or two alone) and that folder is there.</summary>
    private static (string Folder, string Id)? UserSecrets(ProjectModel? project) =>
        project is not null && Workspace.UserSecrets.Of(project.Path, project.Property(Passenger.Host.CarriedProperties.UserSecretsId), Editors.EditorArea.ReadText) is { } id
            && Workspace.UserSecrets.FolderOf(id) is { } folder && Directory.Exists(folder) ? (folder, id) : null;

    private static JsonObject Labels(IReadOnlyList<string> words)
    {
        JsonObject labels = [];
        for (int i = 0; i + 1 < words.Count; i += 2)
        {
            if (words[i] == "--label" && words[i + 1].Split('=', 2) is [var key, var value])
            {
                labels[key] = value;
            }
        }
        return labels;
    }

    /// <summary>A port nothing on this machine listens on now (at the host's address it is published on), for a service's
    /// published port Passenger chooses: chosen once for the plan, the same one after.</summary>
    private static int Pinned(Dictionary<(string Service, int Port), int> pinned, string service, int index, string? hostIp)
    {
        if (!pinned.TryGetValue((service, index), out int port))
        {
            System.Net.IPAddress at = hostIp is { Length: > 0 } given && System.Net.IPAddress.TryParse(given, out System.Net.IPAddress? parsed) ? parsed : System.Net.IPAddress.Loopback;
            using System.Net.Sockets.TcpListener listener = new(at, 0);
            listener.Start();
            pinned[(service, index)] = port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        return port;
    }

    /// <summary>A port in Compose's long syntax as its model had it (the host's address, mode, name and application protocol
    /// kept), published on <paramref name="chosen"/> where Passenger chose its port.</summary>
    private static JsonObject Port(ComposePort port, int? chosen)
    {
        JsonObject written = new() { ["target"] = port.Target, ["protocol"] = port.Protocol };
        if ((chosen?.ToString(CultureInfo.InvariantCulture) ?? port.Published) is { Length: > 0 } published)
        {
            written["published"] = published;
        }
        foreach ((string key, string? value) in (ReadOnlySpan<(string, string?)>)[("host_ip", port.HostIp), ("mode", port.Mode), ("name", port.Name), ("app_protocol", port.AppProtocol)])
        {
            if (value is { Length: > 0 })
            {
                written[key] = value;
            }
        }
        return written;
    }

    /// <summary>Whether Compose takes YAML's <c>!override</c> tag (2.24 and later), which puts a list in place of the file's
    /// rather than beside it.</summary>
    private static bool Overrides(string? composeVersion) =>
        composeVersion is { } said && Version.TryParse(new string([.. said.TrimStart('v').TakeWhile(c => char.IsAsciiDigit(c) || c == '.')]), out Version? version)
        && version >= new Version(2, 24);

    private static void WriteJson(string path, JsonObject json, bool overriding = false)
    {
        // A service's ports Passenger chose replace the file's (YAML's tag, on JSON's flow, which YAML reads alike).
        string text = json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        text = overriding ? text.Replace("\"ports\": [", "\"ports\": !override [", StringComparison.Ordinal) : text;
        // It may hold the configuration's environment: the person's alone to read.
        OwnFiles.Write(path, text);
    }

    /// <summary>A name as Compose takes a project's: lowercase letters, digits, dashes and underscores.</summary>
    public static string Slug(string name)
    {
        System.Text.StringBuilder made = new();
        foreach (char c in name.ToLowerInvariant())
        {
            made.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-');
        }
        string slug = made.ToString().Trim('-', '_');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }
        return slug.Length > 0 ? slug : "app";
    }

    /// <summary>The folder's own place in the state, where its override files live.</summary>
    private static string StateFolderOf(string stateFolder, string folder) => State.ProjectState.FolderOf(stateFolder, folder);

    /// <summary>A folder's place in the state for its container runs (<see cref="ContainerPlan.State"/>), where the development
    /// certificate is exported, and which a certificate made since lets go of.</summary>
    public static string StateOf(string stateFolder, string folder) => Path.Combine(StateFolderOf(stateFolder, folder), "containers");
}

/// <summary>Compose would not read a configuration's files: what it said, for Problems' Containers tab.</summary>
public sealed class ComposeModelException(string name, string said) : LaunchException($"{name} cannot run: Compose could not read its files: {said}")
{
    public string Said { get; } = said;
}

/// <summary>The state's files that only the person may read on macOS and Linux (the development certificate's copy and its
/// password, a run's override and single service's compose file, which name them and the configuration's environment), so
/// from the moment each is made: a folder made 0700, a file 0600 as it is created, never set so after its text is in.</summary>
internal static class OwnFiles
{
    private const UnixFileMode OwnersFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnersFolder = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Makes a folder only the person enters (one an earlier Passenger made, put right), before anything is
    /// written in it.</summary>
    public static void Folder(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }
        Directory.CreateDirectory(path, OwnersFolder);
        File.SetUnixFileMode(path, OwnersFolder);
    }

    /// <summary>Writes a file only the person reads, whole (<see cref="AtomicFile"/>): one there before is replaced, never
    /// written into, since it would keep the mode it had.</summary>
    public static void Write(string path, string text) => AtomicFile.Write(path, System.Text.Encoding.UTF8.GetBytes(text), OwnersFile);

    /// <summary>A file another program wrote in such a folder, made only the person's to read too.</summary>
    public static void Keep(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, OwnersFile);
        }
    }
}

/// <summary>A .NET service of a container run in its container, as the debugger and the profiler reach it: its container
/// reached through Passenger's tools (with its caveat, an image run under emulation), its program there (by its id there),
/// and when the container started (its log followed from then).</summary>
public sealed record ServiceInContainer(ContainerReach Reach, DotnetProcess Program, string Started);

/// <summary>A service's container as the engine says it stands: its service, state, health, published ports, the
/// container's name and its last exit code.</summary>
public sealed record ServiceState(string Service, string State, string? Health, string Ports, string? Container, int? ExitCode);

/// <summary>A container configuration running, a run of services (<see cref="ServiceRun"/>) whose services run in containers:
/// its plan, Compose's watch of the services that ask for one, and each .NET service's build as it was when started, so
/// Rerun restarts only those whose build changed. Brought up by <see cref="UpAsync"/> (its words in the Run console), taken
/// down by <see cref="StopAsync"/> (<c>docker compose down</c>, its volumes kept, or only the .NET services when backing
/// services are kept). What is its own: a service restarts through Compose (<see cref="RestartAsync"/>), a hold chosen is
/// the override written again, and a service's program is in its container, reached through Passenger's tools there
/// (<see cref="ProgramAsync"/>). A UI-thread object of the session's.</summary>
public sealed class ContainerRun(ContainerPlan plan, ContainerEngine engine, ConsoleSink console) : ServiceRun
{
    private readonly Dictionary<string, string> stamps = new(StringComparer.Ordinal);
    private Process? watching;
    private Task? stopping;

    // Never disposed: a command that took its token may register on it after the run has ended.
    /// <summary>Canceled as the run is taken down (by whoever asks, whatever started it) or its folder ends: its own
    /// <c>up</c>, publish and restarts end with it, whichever start or action began them.</summary>
    private readonly CancellationTokenSource ending = new();

    /// <summary>Whether <c>up</c> was ended before it had made all it would: the engine may finish a container it was making.</summary>
    private bool cut;

    /// <summary>Done once <c>up</c> has ended, having made all it would make or been ended; never begun, done already.</summary>
    private TaskCompletionSource upEnded = Done();

    private static TaskCompletionSource Done()
    {
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    public ContainerPlan Plan { get; private set; } = plan;

    public override string Name => Plan.Name;

    public override IReadOnlyList<IRunService> Services => Plan.Dotnet;

    /// <summary>The address the browser was opened at, which a restart on an engine that chose another port changes.</summary>
    public string? Address { get; set; }

    public ContainerEngine Engine => engine;

    /// <summary>Where the container tools come from that are put in a service's container to reach its program: its session's.</summary>
    public required ContainerToolsSource Tools { get; init; }

    /// <summary>Whether it was taken down (or is being).</summary>
    public override bool IsStopped => stopping is not null;

    /// <summary>Done once <see cref="StopAsync"/> has had the engine take it down.</summary>
    public Task Stopped => stopping ?? Task.CompletedTask;

    /// <summary>The services whose container was made held (the override in force when it was made said so), which
    /// <c>docker compose restart</c> holds again whatever was chosen since: one chosen otherwise is made anew instead.</summary>
    private readonly HashSet<string> madeHeld = new(StringComparer.Ordinal);

    protected override void MadeHeld(string service) => madeHeld.Add(service);

    /// <summary>The .NET services it left unstarted (marked Don't start) and has started since, which a Rerun or a start of its
    /// configuration that takes its place starts again with the rest.</summary>
    public IReadOnlyCollection<string> StartedLater
    {
        get => startedLater;
        init => startedLater.UnionWith(value);
    }

    private readonly HashSet<string> startedLater = new(StringComparer.Ordinal);

    /// <summary>Services started later whose container no <c>up</c> has made yet: their next restart makes it.</summary>
    private readonly HashSet<string> unmade = new(StringComparer.Ordinal);

    /// <summary>Services held at their next start for the profiler alone (a later start's Profile), whose recording lets them go.</summary>
    private readonly HashSet<string> heldOnce = new(StringComparer.Ordinal);

    /// <summary>A .NET service marked Don't start, not started since.</summary>
    public override bool LeftUnstarted(IRunService service) => Dotnet(service).Action == ServiceStart.DontStart;

    /// <summary>A service of a container run left unstarted is started by the Containers window's Start, or its own Run, Debug
    /// or Profile.</summary>
    public const string StartsService = "its Start, Run, Debug or Profile starts it";

    public override string StartsIt => StartsService;

    /// <summary>The .NET services it left unstarted, by name, which the Containers window lists as not started.</summary>
    public IReadOnlyList<string> Unstarted => [.. Plan.Dotnet.Where(d => d.Action == ServiceStart.DontStart).Select(d => d.Service)];

    public override string? WhyNotStartedLater(IRunService service) => LeftUnstarted(service) ? Dotnet(service).Unfit : null;

    /// <summary>A service it left unstarted started from now by its next restart (<c>up --detach --no-deps</c>, its image built
    /// first as deployed): the plan runs it from Passenger's build, which in fast mode must be there to run.</summary>
    public override string? StartLater(IRunService service, bool profiled)
    {
        DotnetService dotnet = Dotnet(service);
        if (!LeftUnstarted(service))
        {
            return null;
        }
        if (dotnet.Unfit is { } unfit)
        {
            return unfit;
        }
        if (Plan.Mode == ContainerMode.Fast && !File.Exists(Path.Combine(dotnet.Output, dotnet.Assembly + ".dll")))
        {
            return $"Could not start {service.Name}: {dotnet.Assembly}.dll has not been built in {dotnet.Output}";
        }
        Plan = Plan.StartingLater([service.Name]);
        startedLater.Add(service.Name);
        unmade.Add(service.Name);
        if (profiled)
        {
            heldOnce.Add(service.Name);
        }
        return null;
    }

    /// <summary>A hold chosen takes effect as the override, written again, says it at the service's next restart.</summary>
    protected override Task HoldsChangedAsync(CancellationToken cancel) => WriteOverrideAsync(cancel);

    /// <summary>The override as the holds chosen say, each held service's code open to Hot Reload (one held for the profiler
    /// alone, not).</summary>
    private Task WriteOverrideAsync(CancellationToken cancel) => Plan.WriteOverride([.. HeldNames.Union(heldOnce)], [.. HeldNames], Certificate, cancel);

    /// <summary>In fast mode a service's container runs the build here, which an edit's restart builds first; as deployed,
    /// its restart builds its image.</summary>
    public override bool BuildsBeforeRestart => Plan.Mode == ContainerMode.Fast;

    /// <summary>In the run's console, beside the services' logs, named for the service.</summary>
    public override void Say(IRunService service, string line, bool error) =>
        console.Write($"{service.Name}: {line}\n", error ? ConsoleStream.Error : ConsoleStream.Output);

    public override bool Refused(Exception failure) => failure is IOException or UnauthorizedAccessException or ContainerToolsException or ContainerCommandException
        or TimeoutException;

    /// <summary>A run that starts one service (a gutter icon's configuration) is named for it already.</summary>
    public override string NameOf(IRunService service) => Plan.Start is [{ } only] && only == service.Name ? Name : base.NameOf(service);

    /// <summary>The run's .NET service by its name in the run.</summary>
    private DotnetService Dotnet(IRunService service) => Plan.Dotnet.First(d => d.Service == service.Name);

    /// <summary>A service's program in its container once Compose has started it, reached through Passenger's tools there:
    /// the adapter run there over docker exec, its console the container's log since its start, its paths mapped back in
    /// images mode; the profiler's tools there too.</summary>
    public override async Task<ServiceProgram?> ProgramAsync(IRunService service, Task up, CancellationToken stop)
    {
        DotnetService dotnet = Dotnet(service);
        ServiceInContainer? found;
        try
        {
            found = await ThereAsync(dotnet, up, stop);
        }
        catch (Exception failed) when (failed is ContainerToolsException or ContainerCommandException or FileNotFoundException or TimeoutException or JsonException)
        {
            throw new ServiceUnreachedException(failed.Message, failed);
        }
        if (found is not { } there)
        {
            return null;
        }
        if (there.Program.WhyNot is { } unreached)
        {
            // In the run's console, which still says it once a later word has taken the status bar's place.
            Say(service, unreached, error: false);
        }
        ContainerPaths paths = Plan.Mode == ContainerMode.Images ? dotnet.Paths : ContainerPaths.None;
        AdapterPlace place = there.Reach.Adapter(there.Program.Program, paths) with
        {
            Output = () => engine.Command(["logs", "--follow", "--since", there.Started, there.Reach.Container]),
        };
        return new ServiceProgram(there.Program.Id, place, ProfilePlaceOf(dotnet, there))
        {
            WhyNot = there.Program.WhyNot,
            Notes = [.. ((string?[])[there.Program.Note, there.Reach.Emulated]).OfType<string>()],
            Caveat = there.Reach.Emulated,
            HotReload = there.Program.HotReload,
        };
    }

    /// <summary>A service of the run in its container, once Compose has started it (null when Compose ended without it):
    /// reached through Passenger's tools there (<see cref="ContainerTools.ReachAsync"/>, each stage of putting them in said
    /// in the run's console), its program by its assembly as the tools list it there, and when its container started. Off
    /// the UI thread.</summary>
    public Task<ServiceInContainer?> ThereAsync(DotnetService service, Task up, CancellationToken stop)
    {
        ContainerToolsSource source = Tools;
        string? told = null;
        return BackgroundWork.Run(async () =>
        {
            string? container = await ContainerOfAsync(engine, Plan, service.Service, up, stop);
            if (container is null)
            {
                return null;
            }
            ContainerReach reach = await ContainerTools.ReachAsync(engine, container, source, Told, stop);
            DotnetProcess program = await HeldProgramAsync(reach, service, stop);
            // As JSON, RFC 3339 either way: Podman's own docker writes the time itself in Go's words, which its logs --since refuses.
            ChildResult started = await engine.RunAsync(["inspect", "-f", "{{json .State.StartedAt}}", container], cancel: stop);
            return (ServiceInContainer?)new ServiceInContainer(reach, program, started.Output.Trim().Trim('"'));
        }, stop);

        void Told(string said)
        {
            // A line a stage: a download's percentages are a status's, not the console's.
            string stage = said.Split(": ", 2)[0];
            if (stage != told)
            {
                told = stage;
                Say(service, stage, error: false);
            }
        }
    }

    /// <summary>Where the profiler records a service of the run: Passenger's tools in its container over docker exec, its
    /// PDBs' paths made this machine's through the service's map in images mode (in fast mode they are this machine's).</summary>
    public Profiler.ProfilePlace ProfilePlaceOf(DotnetService service, ServiceInContainer there) =>
        there.Reach.Recorder(Plan.Mode == ContainerMode.Images ? service.Paths : ContainerPaths.None);

    /// <summary>A service's container once Compose has started it (null when Compose ended without it).</summary>
    internal static async Task<string?> ContainerOfAsync(ContainerEngine engine, ContainerPlan plan, string service, Task up, CancellationToken stop)
    {
        while (true)
        {
            bool ended = up.IsCompleted;
            ChildResult found = await engine.RunAsync([.. plan.Compose, "ps", "-q", "--", service], cancel: stop);
            if (found.Succeeded && found.Output.Trim().Split('\n')[0].Trim() is { Length: > 0 } container)
            {
                return container;
            }
            if (ended)
            {
                return null;
            }
            await Task.WhenAny(up, Task.Delay(TimeSpan.FromMilliseconds(300), stop));
            stop.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The service's program in its container as the tools list it once its runtime has loaded: held at its start
    /// for the debugger (or, its debugger turned off, saying why), by its assembly; given up on after a minute.</summary>
    internal static async Task<DotnetProcess> HeldProgramAsync(ContainerReach reach, DotnetService service, CancellationToken stop)
    {
        DateTime until = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        string assembly = $"{service.Assembly}.dll";
        while (true)
        {
            IReadOnlyList<DotnetProcess> there = await reach.ProcessesAsync(stop);
            if ((there.FirstOrDefault(p => p.CommandLine.Contains(assembly, StringComparison.OrdinalIgnoreCase)) ?? (there is [{ } only] ? only : null)) is { } program)
            {
                return program;
            }
            if (DateTime.UtcNow > until)
            {
                throw new ContainerToolsException($"no .NET program running {assembly} started in its container {reach.Container} within a minute");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), stop);
        }
    }

    /// <summary>Brings it up as <c>docker compose up --detach --wait</c> (images built as Compose says in images mode), its
    /// words in the console as they come: null once every service is up (healthy where it has a check), else what went
    /// wrong. <paramref name="alongside"/> runs while it comes up (the debugger attaching to the services held at their start,
    /// which no wait would see healthy before it lets them go): its problem ends the command. Canceled, the command is ended.</summary>
    public async Task<string?> UpAsync(CancellationToken cancel, Func<Task, CancellationToken, Task<string?>>? alongside = null)
    {
        Stamp();
        List<string> words = [.. Plan.Compose, "up", "--detach", "--wait"];
        if (Plan.Mode == ContainerMode.Images)
        {
            words.Add("--build");
        }
        words.AddRange(Plan.Start.Count > 0 ? ["--", .. Plan.Start] : []);
        using CancellationTokenSource upEnding = CancellationTokenSource.CreateLinkedTokenSource(cancel, ending.Token);
        upEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ending.IsCancellationRequested)
        {
            upEnded.TrySetResult();
            throw new OperationCanceledException(ending.Token);
        }
        Task<ChildResult> coming = StreamAsync(words, upEnding.Token);
        ChildResult up;
        try
        {
            if (alongside is not null && await alongside(coming, upEnding.Token) is { } unattached)
            {
                await EndedAsync();
                return unattached;
            }
            up = await coming;
        }
        catch (OperationCanceledException)
        {
            // Stopped as it came up: Compose ends first, or it would go on making containers after Stop took the others.
            await EndedAsync();
            throw;
        }
        finally
        {
            upEnded.TrySetResult();
        }

        async Task EndedAsync()
        {
            cut = true;
            await upEnding.CancelAsync();
            try
            {
                await coming;
            }
            catch (OperationCanceledException)
            {
                // Ended for the attach that failed, or the Stop, which say why.
            }
        }
        if (!up.Succeeded)
        {
            // What the services said before they ended is why, which the down that follows would take with them.
            await StreamAsync([.. Plan.Compose, "logs", "--no-color", "--tail", "40"], cancel);
            string last = up.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !ContainerEngine.ProviderFailed(l)).ToArray()
                is { Length: > 0 } lines
                ? lines.LastOrDefault(l => l.Contains("rror", StringComparison.Ordinal)) ?? lines[^1] : $"exit code {up.ExitCode}";
            return $"{Plan.Name} did not come up: {ContainerSecrets.InSaid(last, Plan.Folder)}";
        }
        if (Plan.Watched.Count > 0)
        {
            await WatchAsync();
        }
        if (Plan.Telemetry is { } dashboard)
        {
            (telemetry, string? why) = await dashboard.ReadAsync(engine, Plan.Compose, Plan.Folder, cancel);
            if (why is not null)
            {
                Notes.Add($"no logs and traces: {why}");
            }
        }
        return null;
    }

    private TelemetrySource? telemetry;

    /// <summary>Its dashboard's telemetry API once up, while it runs (its configuration's Collect logs and traces).</summary>
    public override TelemetrySource? Telemetry => IsStopped ? null : telemetry;

    /// <summary>Compose's own watch of the services that declare <c>develop.watch</c>, while the run lasts.</summary>
    private async Task WatchAsync()
    {
        try
        {
            ProcessStartInfo start = engine.Command([.. Plan.Compose, "watch", "--no-up", "--", .. Plan.Watched], Plan.Folder);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            // Its fork off the window's thread (#441); a run stopped meanwhile, whose stop found no watch, ends it here.
            Process started = await ChildProcesses.StartAsync(start);
            if (IsStopped)
            {
                ChildProcesses.Kill(started);
                return;
            }
            watching = started;
            Task pumped = Task.WhenAll(console.PumpAsync(started.StandardOutput, ConsoleStream.Output), console.PumpAsync(started.StandardError, ConsoleStream.Output));
            IdeErrors.Observe(Ended());

            // A watch that ends while the run lasts says so, rather than leaving its files unsynced unsaid.
            async Task Ended()
            {
                await pumped;
                await started.WaitForExitAsync();
                if (watching == started && !IsStopped)
                {
                    console.Write($"Compose's watch of {string.Join(", ", Plan.Watched)} ended with exit code {started.ExitCode}\n", ConsoleStream.Error);
                }
            }
        }
        catch (Exception failed) when (failed is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            console.Write($"Compose's watch could not start: {failed.Message}\n", ConsoleStream.Error);
        }
    }

    /// <summary>The <c>docker compose logs -f</c> words that follow every service's output into the Run console.</summary>
    public IReadOnlyList<string> Logs => [.. Plan.Compose, "logs", "--follow", "--no-color", "--tail", "all", .. Logged];

    /// <summary>The services the Run console follows: every one but Passenger's dashboard, whose records are the Telemetry
    /// view's and whose own log names a login address of its container's, not this machine's.</summary>
    private IReadOnlyList<string> Logged => Plan.Telemetry is null ? [] : ["--", .. Plan.Dotnet.Select(d => d.Service), .. Plan.Backing];

    /// <summary>The workspace folder whose label is on what the run made (<see cref="FolderContainers"/>), by which Stop
    /// finds what is its own to take down.</summary>
    public string? LabelledFor { get; init; }

    /// <summary>The development certificate its HTTPS services take, which every override it writes keeps; null when they
    /// serve HTTP for this run, or it has none to serve.</summary>
    public DevelopmentCertificate.Exported? Certificate { get; init; }

    /// <summary>Takes it down: Compose's watch ended, then, once <paramref name="first"/> is done (its debugged services'
    /// sessions ended, so no debugger loses its container under it), the containers and networks it made, by this machine's,
    /// process's and folder's labels (volumes kept, and never the person's own of the same Compose project, which
    /// <c>docker compose down --remove-orphans</c> would take), or only its .NET services' when backing services are kept;
    /// once, whoever asks. Off the UI thread.</summary>
    public Task StopAsync(Task? first = null)
    {
        if (stopping is not null)
        {
            return stopping;
        }
        if (watching is { } watch)
        {
            ChildProcesses.Kill(watch);
            watching = null;
        }
        IReadOnlyList<string>? only = Plan.KeepBacking && Plan.Backing.Count > 0
            ? [.. Plan.Dotnet.Select(d => d.Service), .. Plan.Telemetry is null ? [] : (string[])[ContainerTelemetry.Service]] : null;
        Task up = upEnded.Task;
        bool coming = !up.IsCompleted || cut;
        stopping = BackgroundWork.Run(async () =>
        {
            // Compose still bringing it up (ended just below, whoever started it) makes no container after the take-down's look.
            await Task.WhenAny(Task.WhenAll(first ?? Task.CompletedTask, up), Task.Delay(TimeSpan.FromSeconds(10)));
            await FolderContainers.TakeDownAsync(engine, Plan.Compose, LabelledFor, $"as {Plan.Name} stopped", only);
            if (coming)
            {
                // Stopped as it came up: a container the engine was still making as Compose ended is taken too.
                await Task.Delay(TimeSpan.FromSeconds(2));
                await FolderContainers.TakeDownAsync(engine, Plan.Compose, LabelledFor, $"as {Plan.Name} stopped", only);
            }
        });
        // After stopping is set: a caller its end wakes at once asks again and is given the same take-down.
        ending.Cancel();
        return stopping;
    }

    /// <summary>Lets go of what the run started on this machine (Compose's watch, and its <c>up</c> or restart under way,
    /// ended) as the folder ends; its containers go with the folder's (<see cref="FolderContainers"/>), once the task given
    /// back is done: <c>up</c> has ended, making nothing after their take-down looks.</summary>
    public Task Abandon()
    {
        if (watching is { } watch)
        {
            ChildProcesses.Kill(watch);
            watching = null;
        }
        ending.Cancel();
        return upEnded.Task;
    }

    /// <summary>The .NET services whose build changed since they started (or were last restarted), Rerun's to restart.</summary>
    public IReadOnlyList<DotnetService> Changed() =>
        [.. Plan.Restartable.Where(d => !stamps.TryGetValue(d.Service, out string? was) || was != BuiltInputs.Stamp(d.Output))];

    /// <summary>Restarts services (their containers kept, the same mount reading the new build), saying so in the console
    /// (<paramref name="why"/>, else that their build changed). A service whose container was made held, or unheld, other
    /// than as it is now chosen is made anew from the override, as are all of them in images mode, from an image built again
    /// from its Dockerfile.</summary>
    public override Task<string?> RestartAsync(IReadOnlyList<IRunService> services, CancellationToken cancel, string? why = null, bool build = false) =>
        Restarting(() => RestartNowAsync(services, cancel, why));

    /// <summary>A restart, known to be under way until it ends (<see cref="StillUpAsync"/> waits it out).</summary>
    private async Task<string?> Restarting(Func<Task<string?>> restart)
    {
        TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        restarts.Add(ended.Task);
        try
        {
            return await restart();
        }
        finally
        {
            restarts.Remove(ended.Task);
            ended.TrySetResult();
        }
    }

    /// <summary>The restarts under way, on the UI thread.</summary>
    private readonly List<Task> restarts = [];

    /// <summary>Whether any of its containers is up once the restarts under way have ended (one made anew has none up for a
    /// moment): what tells a log that ended by itself from one that ended as its services did.</summary>
    public async Task<bool> StillUpAsync(CancellationToken cancel)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancel, ending.Token);
        while (restarts.Count > 0)
        {
            await Task.WhenAll(restarts.ToArray()).WaitAsync(stop.Token);
        }
        return (await StatesAsync(stop.Token)).Any(s => s.State is "running" or "restarting" or "created" or "paused");
    }

    /// <summary>The <c>docker compose logs -f</c> words that follow every service's output again from <paramref name="since"/>
    /// (RFC 3339), after the run's log ended while its services ran.</summary>
    public IReadOnlyList<string> LogsSince(string since) => [.. Plan.Compose, "logs", "--follow", "--no-color", "--since", since, .. Logged];

    private async Task<string?> RestartNowAsync(IReadOnlyList<IRunService> services, CancellationToken cancel, string? why)
    {
        if (services.Count == 0)
        {
            console.Write("No service's build changed: nothing restarted\n");
            return null;
        }
        console.Write($"{(services.All(s => unmade.Contains(s.Name)) ? "Starting" : "Restarting")} {string.Join(", ", services.Select(s => s.Name))}: "
            + $"{why ?? (services.Count == 1 ? "its build changed" : "their builds changed")}\n");
        // One started later has no container yet: made, as one whose hold changed since it was made is made anew.
        List<string> made = [.. services.Select(s => s.Name).Where(s => Plan.Mode == ContainerMode.Images || unmade.Contains(s)
            || madeHeld.Contains(s) != (HeldFor(s) is not null || heldOnce.Contains(s)))];
        List<string> kept = [.. services.Select(s => s.Name).Except(made)];
        if (made.Count > 0)
        {
            await WriteOverrideAsync(cancel);
        }
        // Stop of the run ends its restart too, whoever asked for it.
        using CancellationTokenSource restartEnding = CancellationTokenSource.CreateLinkedTokenSource(cancel, ending.Token);
        string? failed = await RestartEachAsync(restartEnding.Token);
        // Only those restarted run the build they have now: another's that changed meanwhile is still Rerun's to restart.
        Stamp(services.Select(Dotnet));
        return failed;

        async Task<string?> RestartEachAsync(CancellationToken stop)
        {
            foreach ((List<string> named, IReadOnlyList<string> words) in ((List<string>, IReadOnlyList<string>)[])[
                (kept, [.. Plan.Compose, "restart", "--", .. kept]),
                // As deployed, a service's image is built again from its Dockerfile and its container made anew from it.
                (made, [.. Plan.Compose, "up", "--detach", .. Plan.Mode == ContainerMode.Images ? (string[])["--build"] : [], "--no-deps", "--force-recreate", "--", .. made])])
            {
                if (named.Count == 0)
                {
                    continue;
                }
                ChildResult restarted = await StreamAsync(words, stop);
                if (!restarted.Succeeded)
                {
                    return $"{Plan.Name}'s services did not restart: docker compose {words[Plan.Compose.Count]} ended with exit code {restarted.ExitCode}";
                }
                foreach (string service in named)
                {
                    // Held for the profiler alone, once: its recording lets it go, and its next restart makes it anew unheld.
                    bool forProfiler = heldOnce.Remove(service);
                    if (forProfiler || HeldFor(service) is not null)
                    {
                        madeHeld.Add(service);
                    }
                    else
                    {
                        madeHeld.Remove(service);
                    }
                    unmade.Remove(service);
                }
            }
            return null;
        }
    }

    private void Stamp(IEnumerable<DotnetService>? services = null)
    {
        foreach (DotnetService service in services ?? Plan.Restartable)
        {
            stamps[service.Service] = BuiltInputs.Stamp(service.Output);
        }
    }

    /// <summary>Restarts services the run does not build (its backing services) as they are, saying so in the console;
    /// null once done, else why not. Stop of the run ends it.</summary>
    public Task<string?> RestartOthersAsync(IReadOnlyList<string> services, CancellationToken cancel) => services.Count == 0 ? Task.FromResult<string?>(null)
        : Restarting(async () =>
        {
            using CancellationTokenSource restartEnding = CancellationTokenSource.CreateLinkedTokenSource(cancel, ending.Token);
            ChildResult restarted = await StreamAsync([.. Plan.Compose, "restart", "--", .. services], restartEnding.Token);
            return restarted.Succeeded ? null : $"{Plan.Name}'s services did not restart: docker compose restart ended with exit code {restarted.ExitCode}";
        });

    /// <summary>The services' state as the engine says it now, for an agent.</summary>
    public async Task<IReadOnlyList<ServiceState>> StatesAsync(CancellationToken cancel)
    {
        IReadOnlyList<JsonElement> rows = await engine.JsonLinesAsync([.. Plan.Compose, "ps", "--all", "--format", "json"], cancel);
        return [.. rows.Select(r => new ServiceState(ContainerEngine.Text(r, "Service") ?? string.Empty, ContainerEngine.Text(r, "State") ?? string.Empty,
            ContainerEngine.Text(r, "Health") is { Length: > 0 } health ? health : null,
            ContainerEngine.Text(r, "Publishers") is null ? ContainerEngine.Text(r, "Ports") ?? string.Empty : Ports(r), ContainerEngine.Text(r, "Name"),
            int.TryParse(ContainerEngine.Text(r, "ExitCode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) ? code : null))];

        static string Ports(JsonElement row) => row.TryGetProperty("Publishers", out JsonElement publishers) && publishers.ValueKind == JsonValueKind.Array
            ? string.Join(", ", publishers.EnumerateArray().Where(p => ContainerEngine.Text(p, "PublishedPort") is not (null or "0"))
                .Select(p => $"{ContainerEngine.Text(p, "PublishedPort")}->{ContainerEngine.Text(p, "TargetPort")}/{ContainerEngine.Text(p, "Protocol")}").Distinct())
            : string.Empty;
    }

    /// <summary>Where a .NET service of the run answers HTTP from this machine: the port the engine published for its
    /// container's ASP.NET Core port (8080, else 80, else the first it published), on the engine's machine
    /// (<see cref="ContainerPort.LinkOn"/>); null when it is not started or publishes none.</summary>
    public override async Task<string?> AddressAsync(IRunService started, CancellationToken cancel)
    {
        if (Plan.Dotnet.FirstOrDefault(s => s.Starts && s.Service == started.Name) is not { } service)
        {
            return null;
        }
        IReadOnlyList<JsonElement> rows = await engine.JsonLinesAsync([.. Plan.Compose, "ps", "--format", "json", "--", service.Service], cancel);
        List<ContainerPort> published = [.. rows.SelectMany(r => r.TryGetProperty("Publishers", out JsonElement publishers) && publishers.ValueKind == JsonValueKind.Array
                ? publishers.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(p => ContainerEngine.Text(p, "PublishedPort") is not (null or "0"))
            .Select(p => new ContainerPort(ContainerEngine.Text(p, "PublishedPort")!, ContainerEngine.Text(p, "TargetPort") ?? string.Empty, ContainerEngine.Text(p, "Protocol") ?? "tcp"))];
        ContainerPort? chosen = published.FirstOrDefault(p => p.Inside == "8080") ?? published.FirstOrDefault(p => p.Inside == "80") ?? published.FirstOrDefault();
        string? endpoint = (await engine.StateAsync(cancel: cancel)).Endpoint;
        return chosen?.LinkOn(endpoint)?.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>The address the browser opens: the plan's, its <c>{Scheme}</c>, <c>{ServiceHost}</c> and
    /// <c>{ServicePort}</c> filled from the port the engine published for the launched service; null without one.</summary>
    public async Task<string?> BrowserUrlAsync(CancellationToken cancel)
    {
        if (Plan.BrowserUrl is not { } url)
        {
            return null;
        }
        if (!url.Contains('{', StringComparison.Ordinal))
        {
            return url;
        }
        if (Plan.LaunchService is not { } service || Plan.LaunchPort is not { } port)
        {
            return null;
        }
        ChildResult published = await engine.RunAsync([.. Plan.Compose, "port", "--", service, port.ToString(CultureInfo.InvariantCulture)], cancel: cancel);
        string answer = published.Output.Trim().Split('\n')[0].Trim();
        int colon = answer.LastIndexOf(':');
        if (!published.Succeeded || colon < 0)
        {
            return null;
        }
        string scheme = port is 443 or 8081 || url.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        return url.Replace("{Scheme}", scheme, StringComparison.OrdinalIgnoreCase)
            .Replace("{ServiceHost}", "localhost", StringComparison.OrdinalIgnoreCase)
            .Replace("{ServicePort}", answer[(colon + 1)..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Runs a docker command to its end, its output streamed into the console, its words first.</summary>
    private Task<ChildResult> StreamAsync(IReadOnlyList<string> words, CancellationToken cancel) => StreamAsync(engine.Command(words, Plan.Folder), cancel);

    /// <summary>The SDK's image made, as deployed: <c>dotnet publish -t:PublishContainer</c> into the engine, its words in the
    /// console; null once the image is there, else why not.</summary>
    public async Task<string?> PublishAsync(CancellationToken cancel)
    {
        if (Plan.Publish is not { } words)
        {
            return null;
        }
        ProcessStartInfo start = ChildProcesses.DotnetCommand(words, Plan.Folder);
        // The SDK loads the image through the engine's CLI, the one Passenger asks.
        if (engine.Cli is { } cli && Path.GetDirectoryName(cli) is { } bin)
        {
            start.Environment["PATH"] = start.Environment.TryGetValue("PATH", out string? path) ? $"{bin}{Path.PathSeparator}{path}" : bin;
        }
        using CancellationTokenSource publishEnding = CancellationTokenSource.CreateLinkedTokenSource(cancel, ending.Token);
        ChildResult published = await StreamAsync(start, publishEnding.Token);
        return published.Succeeded ? null : $"{Plan.Name} could not be made as deployed: dotnet publish ended with exit code {published.ExitCode}; the Run window has what it said";
    }

    /// <summary>A command run into the run's console (<see cref="ConsoleCommand"/>), its words first, its secrets masked as
    /// Compose echoes what it interpolated, Compose's progress on standard error its ordinary text; its exit code and its
    /// last lines, which say why it failed.</summary>
    private async Task<ChildResult> StreamAsync(ProcessStartInfo start, CancellationToken cancel)
    {
        console.Write($"{ChildProcesses.Shown(start)}\n");
        ConsoleOutcome ran = await ConsoleCommand.RunAsync(console, start, cancel, Line, ContainerSecrets.Masking(Plan.Folder), errors: ConsoleStream.Output);
        return new ChildResult(ran.ExitCode, string.Join('\n', ran.Last), string.Empty);

        void Line(string line)
        {
            // An image's build that fails says where in the workspace's files.
            if (Plan.Mode == ContainerMode.Images && BuildOutput.Parse(line, Plan.Paths) is { } problem)
            {
                UiThread.Post(() => ProblemFound?.Invoke(this, problem));
            }
        }
    }

    /// <summary>A compiler's message in an image's build, its file the workspace's; UI thread.</summary>
    public event EventHandler<Problem>? ProblemFound;
}
