using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>The project a launch builds first, for one framework when it has several, into the folder given where its own
/// would not do (a single-file AppHost's, kept in the state).</summary>
public sealed record BuildStep(string Project, string? Framework, string? Output = null)
{
    /// <summary>Global properties the build is given beyond the build configuration's, which make it a build of its own
    /// (a MAUI project's plain one for its preview), never checked as up to date; null for none.</summary>
    public IReadOnlyList<string>? Properties { get; init; }
}

/// <summary>A web app's build as its browser refresh needs it (<see cref="Debugging.BrowserRefresh"/>): the folder of the SDK
/// it builds with, its intermediate output folder, the global properties its build is given, and the projects it references
/// (a Razor class library, a Blazor Web App's client), whose scoped CSS its pages take in, each with the framework its build
/// of them is for where they have several.</summary>
public sealed record WebBuild(string Sdk, string Intermediate, IReadOnlyDictionary<string, string> Properties)
{
    public IReadOnlyList<(string Project, string? Framework)> Referenced { get; init; } = [];
}

/// <summary>Why a configuration cannot be launched, in words for the person.</summary>
public class LaunchException(string message) : Exception(message);

/// <summary>What a run configuration launches, worked out once: the program (the project's apphost, its built assembly under
/// the <c>dotnet</c> that builds it, or another program), its arguments, working folder and environment, and the build that
/// comes first. Run, Debug, Profile and a debugged test host each start a plan, so they launch the same thing.</summary>
public sealed partial record LaunchPlan
{
    /// <summary>The configuration's name, as the windows and the status bar say it.</summary>
    public required string Name { get; init; }

    /// <summary>The copy of its configuration's game it starts, where it starts one of several (<see cref="GameCopies"/>), or its
    /// place among a multi-project configuration's projects.</summary>
    public GameCopy? Copy { get; init; }

    /// <summary>The program started: the project's apphost (<see cref="ByAppHost"/>), the <c>dotnet</c> host for an assembly,
    /// or an executable.</summary>
    public required string Program { get; init; }

    /// <summary>Whether <see cref="Program"/> is the apphost the SDK made beside <see cref="Built"/>, which runs it
    /// (<see cref="AppHosts"/>).</summary>
    public bool ByAppHost { get; init; }

    /// <summary>The program's arguments, the assembly first when it runs under <c>dotnet</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>How many of <see cref="Arguments"/> come first to start the program, rather than being its own: the
    /// assembly under <c>dotnet</c>, or the RunArguments of the program the project's RunCommand names.</summary>
    public int Leading { get; init; }

    /// <summary>The program's own arguments, after those that start it (<see cref="Leading"/>).</summary>
    public IEnumerable<string> OwnArguments => Arguments.Skip(Leading);

    /// <summary>Whether the program's arguments are the project's own (its RunArguments, the configuration naming none),
    /// which dotnet watch gives it itself.</summary>
    public bool ArgumentsOfProject { get; init; }

    public required string WorkingDirectory { get; init; }

    /// <summary>Set over the IDE's own environment, which the program inherits.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether it runs an Azure Functions app by Azure Functions Core Tools' func (<see cref="Aspire.FunctionsHost"/>),
    /// which starts the app's own program, its worker, the one a debugger attaches to.</summary>
    public bool Functions { get; init; }

    /// <summary>The project, and the framework of it that runs; null for a test host VSTest describes.</summary>
    public string? Project { get; init; }

    public string? Framework { get; init; }

    /// <summary>The framework the program runs on ("net10.0"): the one chosen, else the project's own; null where unknown.</summary>
    public string? RunsOn { get; init; }

    /// <summary>The build before launch, or null when the configuration launches what was built last.</summary>
    public BuildStep? Build { get; init; }

    /// <summary>The workspace it was worked out in, whose build configuration its build, dotnet watch and its install on a
    /// device are given (<see cref="ProjectSession.WorkspaceOfAsync"/>); null for none.</summary>
    public WorkspaceModel? Workspace { get; init; }

    /// <summary>The file the build makes and the launch needs (the project's assembly), checked once the build is done.</summary>
    public string? Built { get; init; }

    /// <summary>The file its project builds, whichever program it names: what a <c>dotnet run</c> it names starts.</summary>
    public string? Target { get; init; }

    /// <summary>What desktop app its project says it is (<see cref="DesktopPrograms.OfProject"/>), for a program no build has
    /// made yet; null for any other project. A project named in its place says for it.</summary>
    public string? DesktopProject { get; init; }

    /// <summary>The project a named <c>dotnet run</c> runs in place of the plan's (<see cref="DesktopPrograms.NamedProject"/>); null for none.</summary>
    public string? NamedProject { get; init; }

    public ConsoleKind Console { get; init; }

    /// <summary>Where the browser opens once the program listens, or null when the configuration opens none.</summary>
    public string? BrowserUrl { get; init; }

    /// <summary>The container's service whose HTTP answer at <see cref="BrowserUrl"/> opens the browser, since the engine
    /// takes connections on its published port before its program listens; null for a program whose port taking a connection
    /// opens it.</summary>
    public string? BrowserService { get; init; }

    /// <summary>A web app's build, which its browser refresh needs; null for any other program.</summary>
    public WebBuild? Web { get; init; }

    /// <summary>What its start says of itself in the program's console as it begins: where its logs and traces go, a variable
    /// of its own kept, why none are collected.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The dashboard its program's logs and traces go to (its configuration's Collect logs and traces), which its
    /// start ends with the program; null where none was brought up.</summary>
    public Telemetry.RunDashboard? Dashboard { get; init; }

    /// <summary>The projects of what it launches whose code runs in the browser (<see cref="ProjectModel.RunsInBrowser"/>), by
    /// their project files: its own project first when it does, then those it references.</summary>
    public IReadOnlyList<string> InBrowser { get; init; } = [];

    /// <summary>Where the app's page is, whether or not the browser opens there after launch: what a debugging of its
    /// WebAssembly half opens.</summary>
    public Uri? Page { get; init; }

    /// <summary>What debugging its code in the browser takes on this machine, once Debug has found it
    /// (<see cref="Debugging.BrowserDebugging"/>); null where none debugs it.</summary>
    public Debugging.BrowserDebugging? Browser { get; init; }

    /// <summary>Why no browser debugs its code there, where Debug looked and found none (no Chromium, no proxy restored).</summary>
    public string? BrowserWhyNot { get; init; }

    /// <summary>What a debugger of this plan cannot debug, in words for the person: code that runs in the browser where no
    /// browser debugs it, which no breakpoint stops, and why; null when it debugs all of it.</summary>
    public string? NotDebugged => InBrowser.Count == 0 || Project is null || Browser is not null ? null
        : $"{Plural.Listed([.. InBrowser.Select(NameOf)])}'s components run in the browser on .NET's WebAssembly runtime, and their breakpoints are not hit: "
            + (PathNames.Same(InBrowser[0], Project) ? "only the development server that serves them runs under the debugger" : $"{NameOf(Project)}'s are")
            + $". {BrowserWhyNot ?? "Passenger debugs them in a Chromium browser when Debug starts the app itself"}";

    /// <summary>Why a breakpoint in a WebAssembly app's own code never stops it where no browser debugs it, its development
    /// server being what runs under the debugger.</summary>
    public static string NeverHitInBrowser(string project) =>
        $"{NameOf(project)}'s components run in the browser on .NET's WebAssembly runtime, and no browser debugs them this time: only the development server "
        + "that serves them runs under the debugger, so this breakpoint is never hit";

    /// <summary>When a breakpoint in a Blazor Web App's client stops where no browser debugs it: only where its server runs
    /// the client's components itself.</summary>
    public static string HitOnlyOnServer(string client, string server) =>
        $"{NameOf(client)}'s components run in the browser on .NET's WebAssembly runtime, and no browser debugs them this time: this breakpoint stops only "
        + $"where {NameOf(server)} runs them itself, prerendering them or rendering them on the server";

    private static string NameOf(string project) => System.IO.Path.GetFileNameWithoutExtension(project);

    /// <summary>Where it runs its app other than on this machine (an Android device or emulator), which its start installs it
    /// on after its build (<see cref="Devices.DeviceLaunches"/>); null for a program of this machine.</summary>
    public Devices.DeviceLaunch? Device { get; init; }

    /// <summary>The folder of the Godot project whose game or editor the plan runs on Godot (<see cref="GodotLaunch"/>);
    /// null for any other plan.</summary>
    public string? Godot { get; init; }

    /// <summary>The version of the Godot a Godot game's plan runs on, where known.</summary>
    public string? GodotVersion { get; init; }

    /// <summary>The scene a Godot game's plan starts it in as its configuration names one (a res:// path, the scene open
    /// resolved as the plan was made); null where it starts in the project's main scene, or its arguments name one.</summary>
    public string? GodotScene { get; init; }

    /// <summary>Whether nobody sees the window that starts the plan's Godot (<see cref="MainWindow.Unseen"/>), which then runs
    /// it headless whatever its arguments say.</summary>
    public bool GodotUnseen { get; init; }

    /// <summary>Whether the program runs on Windows on a desktop of its own, which nobody switches to (an agent's own Godot
    /// game, drawn there for its frames): its start through ChildProcesses, and the debugger's, says so
    /// (<see cref="Passenger.Core.ChildProcesses.OnDesktopOfItsOwn"/>).</summary>
    public bool OwnDesktop { get; init; }

    /// <summary>Whether nobody sees the window that launches it (<see cref="MainWindow.Unseen"/>), so no window its program
    /// makes may reach the person's screen (<see cref="UnseenApps"/>).</summary>
    public bool Unseen { get; init; }

    /// <summary>Whether its program starts on Windows on a desktop of its own: a Godot game's where its launch says so
    /// (<see cref="OwnDesktop"/>), else a desktop app nobody is to see (<see cref="UnseenApps.TakesOwnDesktop"/>).</summary>
    public bool OnOwnDesktop => OwnDesktop || UnseenApps.TakesOwnDesktop(this);

    /// <summary>The start of the plan's program through ChildProcesses, on a desktop of its own where the plan says so
    /// (<see cref="OnOwnDesktop"/>).</summary>
    public System.Diagnostics.ProcessStartInfo Start() => Placed(Passenger.Core.ChildProcesses.Command(Program, Arguments, WorkingDirectory));

    /// <summary>A start of the plan's program (its own, or what runs it, as dotnet watch) on a desktop of its own where the
    /// plan says so (<see cref="OnOwnDesktop"/>), else as it was.</summary>
    public System.Diagnostics.ProcessStartInfo Placed(System.Diagnostics.ProcessStartInfo start) =>
        OnOwnDesktop ? Passenger.Core.ChildProcesses.OnDesktopOfItsOwn(start, OwnDesktopWhy) : start;

    /// <summary>Why the program runs on a desktop of its own, for the log of its start.</summary>
    public string OwnDesktopWhy => OwnDesktop ? $"{Name} is an agent's game, drawn where no one sees it for its frames"
        : $"{Name} is a desktop app a Passenger nobody sees runs, whose windows reach no one's screen";

    /// <summary>Whether its Godot game is to draw in the window's Game window (<see cref="RunConfiguration.GodotInPane"/>).</summary>
    public bool GodotInPane { get; init; }

    /// <summary>The size its Godot game's window opens at, by its project (<see cref="Passenger.Workspace.GodotProject.WindowSize"/>).</summary>
    public (int Width, int Height) GodotSize { get; init; }

    /// <summary>Whether its Godot game scales what it draws to its window (<see cref="Passenger.Workspace.GodotProject.Stretches"/>).</summary>
    public bool GodotStretches { get; init; }

    /// <summary>Whether it runs a Godot game of GDScript alone (<see cref="Passenger.Workspace.ProjectModel.IsScriptGame"/>): no
    /// C# of its own to build, debug or sample, its GDScript debugged by its GDScript session alone.</summary>
    public bool ScriptsAlone { get; init; }

    /// <summary>The game's name where the plan runs a Godot game of GDScript alone, which names it as an assembly would.</summary>
    public string? GameName { get; init; }

    /// <summary>Whether the plan runs Godot's editor on its project (<c>--editor</c> among the arguments that start it).</summary>
    public bool GodotEditor => Godot is not null && Arguments.Take(Leading).Contains("--editor");

    /// <summary>The program as its messages name it: a game of GDScript alone by its name, else the project's assembly, else the
    /// executable, without its extension.</summary>
    public string ProgramName => GameName ?? System.IO.Path.GetFileNameWithoutExtension(Built ?? Program);

    /// <summary>Null when the plan can launch now, its build done: else that the build made no program or apphost (unless
    /// what starts it builds it), that the working folder is gone ("Could not run Hello: its working folder ... does not exist"),
    /// unless what starts it builds it there (<see cref="WorksWhereBuilt"/>), or that its windows would reach the person's
    /// screen from a Passenger nobody sees (<see cref="UnseenApps.WhyNot"/>).</summary>
    public string? WhyNot(string verb, bool built = true)
    {
        if (built && (Built is { } file && !File.Exists(file) ? file : ByAppHost && !File.Exists(Program) ? Program : null) is { } missing)
        {
            return $"Could not {verb} {ProgramName}: {System.IO.Path.GetFileName(missing)} has not been built";
        }
        return !Directory.Exists(WorkingDirectory) && (built || !WorksWhereBuilt)
            ? $"Could not {verb} {ProgramName}: its working folder {WorkingDirectory} does not exist"
            : UnseenApps.WhyNot(this, verb, watched: !built);
    }

    /// <summary>Whether the program runs in the folder its build puts it in, which a project never built lacks until its
    /// first build makes it (dotnet watch's, for a run with Hot Reload).</summary>
    public bool WorksWhereBuilt => Built is { } file && System.IO.Path.GetDirectoryName(file) is { } output && PathNames.Same(output, WorkingDirectory);

    /// <summary>Works out what a configuration launches among the open folder's projects (of <paramref name="workspace"/>,
    /// whose solution's properties MSBuild is given), asking MSBuild, with no build, where the project builds, where and how
    /// dotnet run runs it, and any property its values name; a Passenger it launches keeps its state in its sandbox/, a Godot
    /// project runs on the Godot <paramref name="godot"/> names or found, its editor on the scene at hand, in the user folders
    /// <paramref name="godotFolders"/> gives, headless where <paramref name="unseen"/> (the process's by default). Throws a <see cref="LaunchException"/> saying why not.</summary>
    public static async Task<LaunchPlan> ResolveAsync(RunConfiguration configuration, IReadOnlyList<ProjectModel> projects, WorkspaceModel? workspace, string stateFolder,
        CancellationToken cancel = default, string? godot = null, string? atHand = null, IReadOnlyDictionary<string, string>? godotFolders = null, bool? unseen = null,
        Func<ProjectModel, Devices.KeptTarget?>? targets = null) =>
        await ResolveWithinAsync(configuration, projects, workspace, stateFolder, cancel, godot, atHand, godotFolders, unseen, targets) with { Workspace = workspace };

    private static async Task<LaunchPlan> ResolveWithinAsync(RunConfiguration configuration, IReadOnlyList<ProjectModel> projects, WorkspaceModel? workspace, string stateFolder,
        CancellationToken cancel, string? godot, string? atHand, IReadOnlyDictionary<string, string>? godotFolders, bool? unseen,
        Func<ProjectModel, Devices.KeptTarget?>? targets)
    {
        bool headless = unseen ?? PassengerApp.ShowsNoWindow;
        if (configuration.Unsupported is { } why)
        {
            throw new LaunchException($"{configuration.Name} cannot run here: {why}");
        }
        if (configuration.IsContainer)
        {
            // Run, Debug and Profile bring containers up themselves (BuildRunController.Containers.cs): no plan of this machine's runs them.
            throw new LaunchException(InContainers(configuration.Name));
        }
        if (configuration.IsMultiple)
        {
            // Run and Debug start each of its projects as its own configuration does (BuildRunController.Projects.cs).
            throw new LaunchException(SeveralProjects(configuration.Name));
        }
        if (configuration.IsScript)
        {
            // Run starts it with its package manager (BuildRunController.Scripts.cs): no .NET program to plan.
            throw new LaunchException(Refusals.ScriptOnlyRuns(configuration.Name));
        }
        if (configuration.RunsScriptGame)
        {
            // Godot runs a game of GDScript alone as it is: no MSBuild to ask, nothing to build.
            ProjectModel scripts = ProjectModel.OfScriptGame(System.IO.Path.GetDirectoryName(configuration.Project)!);
            Dictionary<string, string> variables = Unnamed(configuration.Environment.ToDictionary(v => v.Key, v => System.Environment.ExpandEnvironmentVariables(v.Value)), headless);
            if (configuration.Program.Length == 0 || configuration.NamesGodot)
            {
                return await GodotLaunch.PlanAsync(configuration, scripts, scripts.Folder, null, null, System.Environment.ExpandEnvironmentVariables,
                    Passenger.App.Godot.GodotUserFolders.Into(variables, godotFolders), godot, null, headless, cancel, atHand);
            }
            return new LaunchPlan
            {
                Name = configuration.Name,
                Program = Executable(System.Environment.ExpandEnvironmentVariables(configuration.Program), scripts.Folder),
                Arguments = CommandLine.Split(System.Environment.ExpandEnvironmentVariables(configuration.Arguments)),
                WorkingDirectory = configuration.WorkingDirectory is { Length: > 0 } given
                    ? System.IO.Path.TrimEndingDirectorySeparator(PathNames.Canonical(System.IO.Path.Combine(scripts.Folder, System.Environment.ExpandEnvironmentVariables(given))))
                    : scripts.Folder,
                Environment = variables,
                Project = scripts.Path,
                Console = configuration.Console,
                Unseen = headless,
            };
        }
        ProjectModel project = projects.FirstOrDefault(p => PathNames.Same(p.Path, configuration.Project))
            ?? throw new LaunchException($"{configuration.Name} cannot run: its project {System.IO.Path.GetFileName(configuration.Project)} is not loaded in the open folder");
        // The target chosen to run on decides the framework built: its platform's, unless the configuration names one of it.
        Devices.KeptTarget? device = Devices.DeviceTargets.PlatformsOf(project.TargetFrameworks).Count > 0 ? targets?.Invoke(project) : null;
        if (device is not null && Devices.DeviceTargets.FrameworkFor(project.TargetFrameworks, device.Platform) is null)
        {
            device = null;
        }
        string? framework = device is not null && Devices.DeviceTargets.PlatformOf(configuration.Framework) != device.Platform && project.TargetFrameworks.Count > 1
            ? Devices.DeviceTargets.FrameworkFor(project.TargetFrameworks, device.Platform)
            : FrameworkOf(configuration.Framework, project);
        Devices.TargetPlatform? platform = device?.Platform ?? Devices.DeviceTargets.PlatformOf(framework ?? project.TargetFrameworks.FirstOrDefault() ?? string.Empty);
        if (device is null && configuration.Program.Length == 0 && platform is Devices.TargetPlatform.Android or Devices.TargetPlatform.Ios)
        {
            throw new LaunchException(Refusals.NoRunTarget(project.Name));
        }
        // The project's own configuration, named for no platform, runs its platform's profile where the target is of that platform.
        if (platform is { } on && configuration is { LaunchProfile.Length: 0, Program.Length: 0 } && Devices.DeviceTargets.ProfileFor(LaunchProfile.Read(project), on) is { } tied)
        {
            configuration = configuration.TakingProfile(project, tied);
        }
        // Before its build: the program would build here (EnableWindowsTargeting) and then fail to start for want of WindowsDesktop.
        if (configuration.Program.Length == 0 && project.RunsOnlyOnWindows(framework) is { } windows)
        {
            throw new LaunchException($"{configuration.Name} cannot run here: {windows}");
        }
        HashSet<string> names = new([CarriedProperties.TargetPath, MSBuildNames.ProjectDir, MSBuildNames.RunWorkingDirectory, MSBuildNames.RunArguments, .. AppHosts.Properties],
            StringComparer.OrdinalIgnoreCase);
        if (project.GodotFolder is not null)
        {
            names.UnionWith([MSBuildNames.GodotSdkVersion, MSBuildNames.Configuration]);
        }
        if (project.IsWeb)
        {
            names.UnionWith([MSBuildNames.ExtensionsPath, MSBuildNames.IntermediateOutputPath]);
        }
        foreach (string text in (string[])[configuration.Program, configuration.Arguments, configuration.WorkingDirectory, .. configuration.Environment.Values])
        {
            foreach (Match macro in Macro().Matches(text))
            {
                names.Add(macro.Groups[1].Value);
            }
        }
        IReadOnlyDictionary<string, string> properties = await EvaluateAsync(project, framework, names, workspace, cancel);
        string Expand(string text) => System.Environment.ExpandEnvironmentVariables(
            Macro().Replace(text, macro => properties.GetValueOrDefault(macro.Groups[1].Value, string.Empty)));
        // The configuration's own, its launch profile named as dotnet run names it.
        Dictionary<string, string> Variables()
        {
            Dictionary<string, string> set = configuration.Environment.ToDictionary(v => v.Key, v => Expand(v.Value));
            if (configuration.LaunchProfile.Length > 0 && !set.Keys.Any(k => string.Equals(k, LaunchProfile.NameVariable, StringComparison.OrdinalIgnoreCase)))
            {
                set[LaunchProfile.NameVariable] = configuration.LaunchProfile;
            }
            return Unnamed(set, headless);
        }

        string target = PathNames.Canonical(properties.GetValueOrDefault(CarriedProperties.TargetPath) is { Length: > 0 } path
            ? path
            : throw new LaunchException($"MSBuild could not say what {project.Name} builds"));
        if ((configuration.Program.Length == 0 || configuration.NamesGodot) && project.GodotFolder is { } game)
        {
            if (properties.GetValueOrDefault(MSBuildNames.Configuration) is { Length: > 0 } builtAs && !builtAs.Equals(GodotLaunch.RunsConfiguration, StringComparison.OrdinalIgnoreCase))
            {
                throw new LaunchException($"{configuration.Name} cannot run: Godot loads a game's {GodotLaunch.RunsConfiguration} build alone, where the build configuration "
                    + $"chosen builds {project.Name} as {builtAs} (as an export does): choose {GodotLaunch.RunsConfiguration} to run it");
            }
            return await GodotLaunch.PlanAsync(configuration, project, game, target, framework, Expand, Passenger.App.Godot.GodotUserFolders.Into(Variables(), godotFolders), godot,
                properties.GetValueOrDefault(MSBuildNames.GodotSdkVersion), headless, cancel, atHand);
        }
        List<string> arguments = [.. CommandLine.Split(Expand(configuration.Arguments))];
        IReadOnlyList<string> ofProject = [];
        string program;
        string? built = null;
        int leading = 0;
        AppHost? host = null;
        string? underDotnet = null;
        if (configuration.Program is { Length: > 0 } named)
        {
            program = Executable(Expand(named), project.Folder);
        }
        else if (OtherProgram(properties, target) is ({ } command, { } given))
        {
            program = Executable(command, project.Folder);
            arguments.InsertRange(0, given);
            leading = given.Count;
            built = target;
        }
        else
        {
            ofProject = arguments.Count == 0 ? ProjectArguments(properties, target) : [];
            arguments.AddRange(ofProject);
            bool assembly = target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            (host, underDotnet) = assembly ? AppHosts.Of(properties, target) : (null, null);
            if (host is not null)
            {
                program = host.Program;
                built = target;
            }
            else if (assembly)
            {
                program = Dotnet.Host(project.Folder);
                arguments.Insert(0, target);
                built = target;
                leading = 1;
            }
            else
            {
                program = built = target;
            }
        }
        string? folder = configuration.WorkingDirectory is { Length: > 0 } own ? Expand(own) : properties.GetValueOrDefault(MSBuildNames.RunWorkingDirectory);
        string working = folder is not null
            ? System.IO.Path.TrimEndingDirectorySeparator(PathNames.Canonical(System.IO.Path.Combine(project.Folder, folder)))
            : System.IO.Path.GetDirectoryName(target)!;
        Dictionary<string, string> environment = Variables();
        // The apphost takes its runtime from the .NET that builds it, as dotnet run's does, or the one the configuration names, over any the IDE inherited.
        if (host is not null && !environment.Keys.Any(k => k.Equals(host.RootVariable, StringComparison.OrdinalIgnoreCase)))
        {
            environment[host.RootVariable] = environment.Where(v => v.Key.Equals(RuntimeVariables.Root, StringComparison.OrdinalIgnoreCase)).Select(v => v.Value).FirstOrDefault()
                ?? Dotnet.Root(project.Folder);
        }
        string? other = DesktopPrograms.NamedProject(program, arguments, working) is { } runs && !PathNames.Same(runs, project.Path) && !PathNames.Same(runs, project.Folder) ? runs : null;
        if (SandboxedState(built ?? program, environment, stateFolder) is { } sandbox)
        {
            environment[StateStore.Variable] = sandbox;
        }
        LaunchPlan plan = new()
        {
            Name = configuration.Name,
            Program = program,
            ByAppHost = host is not null,
            Arguments = arguments,
            Leading = leading,
            ArgumentsOfProject = ofProject.Count > 0,
            WorkingDirectory = working,
            Environment = environment,
            Project = project.Path,
            InBrowser = [.. project.WithReferenced(projects).Where(p => p.RunsInBrowser).Select(p => p.Path)],
            Framework = framework,
            RunsOn = framework ?? project.TargetFrameworks.FirstOrDefault(),
            Build = configuration.BuildBeforeLaunch ? new BuildStep(project.Path, framework) : null,
            Built = built,
            Target = target,
            DesktopProject = other is null ? DesktopPrograms.OfProject(project, projects) : DesktopPrograms.OfNamed(other, projects),
            NamedProject = other,
            Console = configuration.Console,
            BrowserUrl = configuration.LaunchBrowser && configuration.BrowserUrl.Length > 0 ? Expand(configuration.BrowserUrl) : null,
            Page = configuration.BrowserUrl.Length > 0 ? BrowserLaunch.Address(Expand(configuration.BrowserUrl)) : null,
            Web = WebOf(project, framework, properties, workspace, projects),
            Unseen = headless,
            Notes = underDotnet is null ? [] : [$"{configuration.Name} runs as dotnet {System.IO.Path.GetFileName(target)}, not by its apphost: {underDotnet}"],
            Device = device is { Kind: not Devices.TargetKind.ThisMachine } && configuration.Program.Length == 0 ? new Devices.DeviceLaunch(device, framework ?? project.TargetFrameworks[0]) : null,
        };
        if (configuration.Program.Length > 0 || !project.IsFunctionsApp)
        {
            return plan;
        }
        // A Functions app runs as dotnet run runs it, by func, whose SDK names it only in a target no evaluation runs.
        string func = await BackgroundWork.Run(Aspire.FunctionsHost.Func, cancel) ?? throw new LaunchException(Refusals.NoFunc(project.Name));
        return Aspire.FunctionsHost.Plan(plan, func, [.. plan.OwnArguments]);
    }

    /// <summary>A configuration's variables, on Linux with no display in them where nobody is to see what they start.</summary>
    private static Dictionary<string, string> Unnamed(Dictionary<string, string> variables, bool unseen)
    {
        if (unseen && OperatingSystem.IsLinux())
        {
            NoDisplay.Over(variables);
        }
        return variables;
    }

    /// <summary>A web project's build as its evaluation says it; null for another, or one whose evaluation says too little.</summary>
    private static WebBuild? WebOf(ProjectModel project, string? framework, IReadOnlyDictionary<string, string> properties, WorkspaceModel? workspace,
        IReadOnlyList<ProjectModel> projects)
    {
        if (!project.IsWeb || properties.GetValueOrDefault(MSBuildNames.ExtensionsPath) is not { Length: > 0 } sdk
            || properties.GetValueOrDefault(MSBuildNames.IntermediateOutputPath) is not { Length: > 0 } intermediate)
        {
            return null;
        }
        Dictionary<string, string> globals = ProjectModels.Properties(workspace, project);
        if (framework is not null)
        {
            globals[MSBuildNames.TargetFramework] = framework;
        }
        // MSBuild writes its default folders with backslashes, which only Windows takes for separators.
        string folder = System.IO.Path.DirectorySeparatorChar == '\\' ? intermediate : intermediate.Replace('\\', System.IO.Path.DirectorySeparatorChar);
        return new WebBuild(System.IO.Path.TrimEndingDirectorySeparator(sdk), PathNames.Canonical(System.IO.Path.Combine(project.Folder, folder)), globals)
        {
            Referenced = LaunchedFrameworks.Referenced(project, framework ?? project.TargetFrameworks.FirstOrDefault(), projects),
        };
    }

    /// <summary>Why a container configuration has no plan of this machine's: Run, Debug and Profile bring its containers up.</summary>
    public static string InContainers(string name) =>
        $"{name} runs in containers, which Run, Debug and Profile bring up: it starts no program on this computer";

    /// <summary>Why a multi-project configuration has no one plan: Run and Debug start each of its projects, and Profile records
    /// one program, which a project's own configuration starts.</summary>
    public static string SeveralProjects(string name) =>
        $"{name} starts several projects, each as its own configuration starts it: Run and Debug start them together, and Profile records one program, which one of their configurations starts";

    /// <summary>The state folder a Passenger launched from this one is given: sandbox/ in this one's, so the two never share
    /// settings, recent solutions, a folder's editors and breakpoints, the log or agents' sockets, and the one debugged
    /// starts on Passenger's defaults (no MCP server, no Discord). Null for any other program, or when the configuration names
    /// a state folder itself.</summary>
    private static string? SandboxedState(string launched, IReadOnlyDictionary<string, string> environment, string stateFolder) =>
        string.Equals(System.IO.Path.GetFileNameWithoutExtension(launched), typeof(LaunchPlan).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)
        && !environment.Keys.Any(k => string.Equals(k, StateStore.Variable, StringComparison.OrdinalIgnoreCase))
            ? System.IO.Path.Combine(stateFolder, "sandbox")
            : null;

    /// <summary>The program's arguments dotnet run gives it from the project's evaluation: its RunArguments, after the SDK's
    /// own <c>exec "assembly"</c> when the project runs under dotnet; none when its RunCommand starts another program
    /// (<see cref="OtherProgram"/>), whose arguments they are.</summary>
    private static IReadOnlyList<string> ProjectArguments(IReadOnlyDictionary<string, string> properties, string target)
    {
        IReadOnlyList<string> given = CommandLine.Split(properties.GetValueOrDefault(MSBuildNames.RunArguments));
        string command = properties.GetValueOrDefault(MSBuildNames.RunCommand) ?? string.Empty;
        if (DesktopPrograms.IsDotnet(command))
        {
            return given is ["exec", { } assembly, ..] && PathNames.Same(PathNames.Canonical(assembly), target) ? [.. given.Skip(2)] : [];
        }
        return AppHosts.Is(command, target) ? given : [];
    }

    /// <summary>The program dotnet run starts in place of the project's own, with the RunArguments it is given: what the
    /// project's RunCommand names, or another assembly it has <c>dotnet</c> run (a Blazor WebAssembly app's development
    /// server, which serves the app to the browser); null when it runs the project's assembly or its apphost.</summary>
    private static (string Command, IReadOnlyList<string> Arguments)? OtherProgram(IReadOnlyDictionary<string, string> properties, string target)
    {
        string command = properties.GetValueOrDefault(MSBuildNames.RunCommand) ?? string.Empty;
        IReadOnlyList<string> given = CommandLine.Split(properties.GetValueOrDefault(MSBuildNames.RunArguments));
        if (DesktopPrograms.IsDotnet(command))
        {
            IReadOnlyList<string> run = given is ["exec", ..] ? [.. given.Skip(1)] : given;
            return run is [{ } assembly, ..] && assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !PathNames.Same(PathNames.Canonical(assembly), target)
                ? (command, given) : null;
        }
        return command.Length > 0 && !AppHosts.Is(command, target) ? (command, given) : null;
    }

    /// <summary>A program a configuration names: the .NET command line by name is the one the IDE builds with, a path is
    /// the project folder's when relative, and a bare name is looked for on the PATH as a shell would.</summary>
    private static string Executable(string named, string projectFolder)
    {
        if (named is "dotnet" or "dotnet.exe")
        {
            return Dotnet.Host(projectFolder);
        }
        if (named.IndexOfAny(['/', '\\']) >= 0 || System.IO.Path.IsPathRooted(named))
        {
            return PathNames.Canonical(System.IO.Path.Combine(projectFolder, named));
        }
        return Programs.OnPath(named) ?? Programs.OnPath(named + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)) ?? named;
    }

    /// <summary>The framework a project runs for: the one <paramref name="chosen"/> names, else the first of its several;
    /// null for a project of one.</summary>
    internal static string? FrameworkOf(string chosen, ProjectModel project) =>
        chosen.Length > 0 ? chosen : project.TargetFrameworks.Count > 1 ? project.TargetFrameworks[0] : null;

    /// <summary>The properties MSBuild evaluates for the project (with its framework when it has several), with the global
    /// properties a build of it is given (<see cref="ProjectModels.Properties(WorkspaceModel, ProjectModel)"/>), asked of
    /// the warm project host; given a target, as they stand once it has run (the SDK's ComputeContainerBaseImage for
    /// ContainerBaseImage), which the host does not run, so dotnet msbuild does, and as evaluated when it does not run (a
    /// project not restored yet). A property with no value is left out.</summary>
    internal static async Task<IReadOnlyDictionary<string, string>> EvaluateAsync(ProjectModel project, string? framework, IReadOnlyCollection<string> names,
        WorkspaceModel? workspace, CancellationToken cancel, string? target = null)
    {
        Dictionary<string, string> globals = ProjectModels.Properties(workspace, project);
        if (framework is not null)
        {
            globals[MSBuildNames.TargetFramework] = framework;
        }
        if (target is not null)
        {
            return await AfterTargetAsync(project, names, globals, target, cancel) ?? await EvaluateAsync(project, framework, names, workspace, cancel);
        }
        PropertiesAnswer answer;
        try
        {
            // The SDK the project's folder picks, as dotnet msbuild run there would load: the host for it, warm already.
            ProjectHost host = await ProjectHosts.ForAsync(project.Folder, cancel);
            answer = await host.PropertiesAsync(project.Path, globals, [.. names], cancel);
        }
        catch (ProjectHostFailed failed)
        {
            throw new LaunchException($"MSBuild could not say what {project.Name} builds: {failed.Reason}");
        }
        return answer.Problem is { } problem
            ? throw new LaunchException($"MSBuild could not say what {project.Name} builds: {problem}")
            : new Dictionary<string, string>(answer.Values, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The properties as they stand once a target has run, through dotnet msbuild; null when it did not run.</summary>
    private static async Task<IReadOnlyDictionary<string, string>?> AfterTargetAsync(ProjectModel project, IReadOnlyCollection<string> names,
        IReadOnlyDictionary<string, string> globals, string target, CancellationToken cancel)
    {
        List<string> args = ["msbuild", project.Path, "-nologo", $"-target:{target}", .. names.Select(n => $"-getProperty:{n}"), .. globals.Select(p => $"-property:{p.Key}={p.Value}")];
        ChildResult msbuild = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(args, project.Folder), cancel: cancel);
        string output = msbuild.Output.Trim();
        if (!msbuild.Succeeded || output.Length == 0)
        {
            return null;
        }
        try
        {
            using JsonDocument read = JsonDocument.Parse(output);
            if (JsonMembers.Object(read.RootElement, "Properties") is not { } properties)
            {
                return null;
            }
            return properties.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Length > 0)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>An MSBuild property named in a launch profile's value, <c>$(TargetDir)</c>.</summary>
    [GeneratedRegex(@"\$\(([A-Za-z_][A-Za-z0-9_.-]*)\)")]
    private static partial Regex Macro();
}
