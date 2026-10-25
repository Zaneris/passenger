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
public sealed record BuildStep(string Project, string? Framework, string? Output = null);

/// <summary>Why a configuration cannot be launched, in words for the person.</summary>
public class LaunchException(string message) : Exception(message);

/// <summary>What a run configuration launches, worked out once: the program (the project's built assembly under the
/// <c>dotnet</c> that builds it, or another program), its arguments, working folder and environment, and the build that
/// comes first. Run, Debug, Profile and a debugged test host each start a plan, so they launch the same thing.</summary>
public sealed partial record LaunchPlan
{
    /// <summary>The configuration's name, as the windows and the status bar say it.</summary>
    public required string Name { get; init; }

    /// <summary>The program started: the <c>dotnet</c> host for an assembly, or an executable.</summary>
    public required string Program { get; init; }

    /// <summary>The program's arguments, the assembly first when it runs under <c>dotnet</c>.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    public required string WorkingDirectory { get; init; }

    /// <summary>Set over the IDE's own environment, which the program inherits.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>The project, and the framework of it that runs; null for a test host VSTest describes.</summary>
    public string? Project { get; init; }

    public string? Framework { get; init; }

    /// <summary>The build before launch, or null when the configuration launches what was built last.</summary>
    public BuildStep? Build { get; init; }

    /// <summary>The file the build makes and the launch needs (the project's assembly), checked once the build is done.</summary>
    public string? Built { get; init; }

    public ConsoleKind Console { get; init; }

    /// <summary>Where the browser opens once the program listens, or null when the configuration opens none.</summary>
    public string? BrowserUrl { get; init; }

    /// <summary>The program as its messages name it: the project's assembly, else the executable, without its extension.</summary>
    public string ProgramName => System.IO.Path.GetFileNameWithoutExtension(Built ?? Program);

    /// <summary>Null when the plan can launch now, its build done: else that the build made no program (unless what starts
    /// it builds it), or that the working folder is gone ("Could not run Hello: its working folder ... does not exist"),
    /// unless what starts it builds it there (<see cref="WorksWhereBuilt"/>).</summary>
    public string? WhyNot(string verb, bool built = true)
    {
        if (built && Built is { } file && !File.Exists(file))
        {
            return $"Could not {verb} {ProgramName}: {System.IO.Path.GetFileName(file)} has not been built";
        }
        return Directory.Exists(WorkingDirectory) || (!built && WorksWhereBuilt) ? null
            : $"Could not {verb} {ProgramName}: its working folder {WorkingDirectory} does not exist";
    }

    /// <summary>Whether the program runs in the folder its build puts it in, which a project never built lacks until its
    /// first build makes it (dotnet watch's, for a run with Hot Reload).</summary>
    public bool WorksWhereBuilt => Built is { } file && System.IO.Path.GetDirectoryName(file) is { } output && PathNames.Same(output, WorkingDirectory);

    /// <summary>Works out what a configuration launches among the open folder's projects (of <paramref name="workspace"/>,
    /// whose solution's properties MSBuild is given), asking MSBuild where the project builds (and any property its values
    /// name), which needs no build. A Passenger it launches keeps its state in the launching one's state folder's sandbox/
    /// (<see cref="SandboxedState"/>). Throws a <see cref="LaunchException"/> saying why it cannot: a kind of profile
    /// Passenger does not run, a project gone or unloaded, MSBuild's refusal.</summary>
    public static async Task<LaunchPlan> ResolveAsync(RunConfiguration configuration, IReadOnlyList<ProjectModel> projects, WorkspaceModel? workspace, string stateFolder,
        CancellationToken cancel = default)
    {
        if (configuration.Unsupported is { } why)
        {
            throw new LaunchException($"{configuration.Name} cannot run here: {why}");
        }
        if (configuration.IsContainer)
        {
            // Run, Debug and Profile bring containers up themselves (BuildRunController.Containers.cs): no plan of this machine's runs them.
            throw new LaunchException(InContainers(configuration.Name));
        }
        ProjectModel project = projects.FirstOrDefault(p => PathNames.Same(p.Path, configuration.Project))
            ?? throw new LaunchException($"{configuration.Name} cannot run: its project {System.IO.Path.GetFileName(configuration.Project)} is not loaded in the open folder");
        string? framework = configuration.Framework is { Length: > 0 } chosen ? chosen
            : project.TargetFrameworks.Count > 1 ? project.TargetFrameworks[0] : null;
        // Before its build: the program would build here (EnableWindowsTargeting) and then fail to start for want of WindowsDesktop.
        if (configuration.Program.Length == 0 && project.RunsOnlyOnWindows(framework) is { } windows)
        {
            throw new LaunchException($"{configuration.Name} cannot run here: {windows}");
        }
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase) { "TargetPath", "ProjectDir" };
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

        string target = PathNames.Canonical(properties.GetValueOrDefault("TargetPath") is { Length: > 0 } path
            ? path
            : throw new LaunchException($"MSBuild could not say what {project.Name} builds"));
        List<string> arguments = [.. CommandLine.Split(Expand(configuration.Arguments))];
        string program;
        string? built = null;
        if (configuration.Program is { Length: > 0 } named)
        {
            program = Executable(Expand(named), project.Folder);
        }
        else if (target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            program = Dotnet.Host();
            arguments.Insert(0, target);
            built = target;
        }
        else
        {
            program = built = target;
        }
        string working = configuration.WorkingDirectory is { Length: > 0 } folder
            ? System.IO.Path.TrimEndingDirectorySeparator(PathNames.Canonical(System.IO.Path.Combine(project.Folder, Expand(folder))))
            : System.IO.Path.GetDirectoryName(target)!;
        Dictionary<string, string> environment = configuration.Environment.ToDictionary(v => v.Key, v => Expand(v.Value));
        if (SandboxedState(built ?? program, environment, stateFolder) is { } sandbox)
        {
            environment[StateStore.Variable] = sandbox;
        }
        return new LaunchPlan
        {
            Name = configuration.Name,
            Program = program,
            Arguments = arguments,
            WorkingDirectory = working,
            Environment = environment,
            Project = project.Path,
            Framework = framework,
            Build = configuration.BuildBeforeLaunch ? new BuildStep(project.Path, framework) : null,
            Built = built,
            Console = configuration.Console,
            BrowserUrl = configuration.LaunchBrowser && configuration.BrowserUrl.Length > 0 ? Expand(configuration.BrowserUrl) : null,
        };
    }

    /// <summary>Why a container configuration has no plan of this machine's: Run, Debug and Profile bring its containers up.</summary>
    public static string InContainers(string name) =>
        $"{name} runs in containers, which Run, Debug and Profile bring up: it starts no program on this computer";

    /// <summary>The state folder a Passenger launched from this one is given: sandbox/ in this one's, so the two never share
    /// settings, recent solutions, a folder's editors and breakpoints, the log or agents' sockets, and the one debugged
    /// starts on Passenger's defaults (no MCP server, no Discord). Null for any other program, or when the configuration names
    /// a state folder itself.</summary>
    private static string? SandboxedState(string launched, IReadOnlyDictionary<string, string> environment, string stateFolder) =>
        string.Equals(System.IO.Path.GetFileNameWithoutExtension(launched), typeof(LaunchPlan).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)
        && !environment.Keys.Any(k => string.Equals(k, StateStore.Variable, StringComparison.OrdinalIgnoreCase))
            ? System.IO.Path.Combine(stateFolder, "sandbox")
            : null;

    /// <summary>A program a configuration names: the .NET command line by name is the one the IDE builds with, a path is
    /// the project folder's when relative, and a bare name is looked for on the PATH as a shell would.</summary>
    private static string Executable(string named, string projectFolder)
    {
        if (named is "dotnet" or "dotnet.exe")
        {
            return Dotnet.Host();
        }
        if (named.IndexOfAny(['/', '\\']) >= 0 || System.IO.Path.IsPathRooted(named))
        {
            return PathNames.Canonical(System.IO.Path.Combine(projectFolder, named));
        }
        return Programs.OnPath(named) ?? Programs.OnPath(named + (OperatingSystem.IsWindows() ? ".exe" : string.Empty)) ?? named;
    }

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
            globals["TargetFramework"] = framework;
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
            return read.RootElement.GetProperty("Properties").EnumerateObject()
                .Where(p => p.Value.GetString() is { Length: > 0 })
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
