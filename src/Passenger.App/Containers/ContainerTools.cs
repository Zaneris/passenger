using System.Collections.Concurrent;
using System.Text.Json;
using Passenger.App.Updates;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>A container's system as the tools need it: its processor (docker's name, amd64 or arm64) and C library.</summary>
public sealed record ContainerSystem(string Architecture, bool Musl)
{
    /// <summary>The tools' runtime identifier for it (linux-musl-arm64), or null for a processor none are made for.</summary>
    public string? Rid => Architecture switch
    {
        "amd64" => Musl ? "linux-musl-x64" : "linux-x64",
        "arm64" => Musl ? "linux-musl-arm64" : "linux-arm64",
        _ => null,
    };
}

/// <summary>Where Passenger's container tools come from for this window: folders that may hold a copy already (beside the
/// app, a dev build's artifacts/container-tools, the state's cache), the release of this version to download the rest
/// from, and the cache it downloads into.</summary>
public sealed record ContainerToolsSource(IReadOnlyList<string> Local, ReleaseFeed Releases, string Cache)
{
    /// <summary>Beside the app (container-tools/RID), a dev build's artifacts/container-tools/RID (found up from the app's
    /// folder), and the state's cache/container-tools/VERSION/RID, downloaded from <paramref name="releases"/>.</summary>
    public static ContainerToolsSource Default(ReleaseFeed releases) => new(
        [Path.Combine(AppContext.BaseDirectory, "container-tools"), .. DevBuild() is { } dev ? [dev] : Array.Empty<string>()],
        releases, Path.Combine(State.StateStore.DefaultRoot, "cache", "container-tools"));

    /// <summary>artifacts/container-tools of the build the app runs from (artifacts/bin/Passenger.App/debug), where
    /// <c>just container-tools</c> puts them.</summary>
    private static string? DevBuild()
    {
        for (DirectoryInfo? at = new(AppContext.BaseDirectory); at is not null; at = at.Parent)
        {
            if (at.Name == "artifacts")
            {
                return Path.Combine(at.FullName, "container-tools");
            }
        }
        return null;
    }
}

/// <summary>A container whose .NET programs Passenger's tools reach (<see cref="ContainerTools.ReachAsync"/>): the tools'
/// executable there, and why a debugger there may not attach (its image run under emulation), null where nothing stands in
/// its way. What it runs there goes over docker exec: the tools' list of programs, the debug adapter, the profiler.</summary>
public sealed record ContainerReach(ContainerEngine Engine, string Container, string Tools, string? Emulated)
{
    /// <summary>Its .NET programs, by their ids there (<see cref="ContainerTools.ProcessesAsync"/>).</summary>
    public Task<IReadOnlyList<DotnetProcess>> ProcessesAsync(CancellationToken cancel) => ContainerTools.ProcessesAsync(Engine, Container, Tools, cancel);

    /// <summary>The debug adapter's place there for a program (its name as the container's processes give it), paths
    /// crossing through <paramref name="paths"/>, the addresses it names as its own reached where the container publishes
    /// them; the container stopped (its own end, the Containers window's Stop) is its program's end, never the debugger's.</summary>
    public Debugging.AdapterPlace Adapter(string? program, ContainerPaths paths) =>
        new(arguments => Engine.Command(ContainerTools.AdapterCommand(Container, Tools, arguments)), ContainerTools.SymbolCache, paths, Container)
        {
            Running = Debugging.AdapterPlace.Runs(Engine, Container),
            Program = program,
            Reaching = address => ContainerSnapshot.ReachingAsync(Engine, address, c => c.Name == Container || ContainerRow.SameId(c.Id, Container)),
        };

    /// <summary>Where the container's paths are files of <paramref name="folder"/>, as its image was built: for a container
    /// Compose started from the folder's own file (its labels name its files, its project and its service), every folder its
    /// service's Dockerfile copies from its context (<see cref="ContainerPaths.For"/>); for any other, or one Compose's files
    /// no longer say, <see cref="ContainerPaths.None"/>.</summary>
    public async Task<ContainerPaths> PathsAsync(string folder, CancellationToken cancel)
    {
        try
        {
            JsonElement inspected = await Engine.JsonAsync(["inspect", "--type", "container", Container], cancel).ConfigureAwait(false);
            JsonElement? labels = inspected.ValueKind == JsonValueKind.Array && inspected.GetArrayLength() > 0
                ? JsonMembers.Object(JsonMembers.Object(inspected[0], "Config"), "Labels") : null;
            if (labels is not { } named || JsonMembers.Scalar(named, ComposeLabels.Project) is not { Length: > 0 } project
                || JsonMembers.Scalar(named, ComposeLabels.Service) is not { Length: > 0 } service
                || JsonMembers.Scalar(named, ComposeLabels.WorkingDir) is not { Length: > 0 } workingDir || !Directory.Exists(workingDir))
            {
                return ContainerPaths.None;
            }
            string[] files = (JsonMembers.Scalar(named, ComposeLabels.ConfigFiles) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // Every file Compose read, here as it read it (a run's override too, in Passenger's state), the folder's among them.
            if (files.Length == 0 || !files.All(File.Exists) || !files.Any(file => PathNames.IsUnder(PathNames.Canonical(file), folder)))
            {
                return ContainerPaths.None;
            }
            ComposeModel model = await ComposeModel.ReadAsync(Engine, ["compose", "-p", project, .. files.SelectMany(file => (string[])["-f", file]), "--project-directory", workingDir],
                workingDir, cancel).ConfigureAwait(false);
            return model[service] is { DockerfilePath: { } dockerfile, Context: { } context } && File.Exists(dockerfile)
                ? ContainerPaths.For(DockerfileStages.Read(await File.ReadAllTextAsync(dockerfile, cancel).ConfigureAwait(false)), PathNames.Canonical(context))
                : ContainerPaths.None;
        }
        catch (Exception unread) when (unread is ContainerCommandException or JsonException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            IdeLog.Write(IdeLog.Debugger, $"The paths of {Container}'s image were not read from its Compose files: {unread.Message}");
            return ContainerPaths.None;
        }
    }

    /// <summary>Where the profiler records a program there, the paths its PDBs name made this machine's through <paramref name="paths"/>.</summary>
    public Profiler.ProfilePlace Recorder(ContainerPaths paths) =>
        new(words => Engine.Command(ContainerTools.ToolsCommand(Container, Tools, words)), Container, path => paths.ToHost(path) ?? path);

    /// <summary>A program's note there with the container's caveat beside it, once.</summary>
    public string? Noted(string? note) => ContainerTools.Noted(note, Emulated);
}

/// <summary>Passenger's container tools on this side (src/Passenger.ContainerTools runs inside): a copy for a container's
/// system found on this machine or downloaded once into the state's cache (<see cref="ReleaseAssets"/>), a container's
/// system read once per image, and the tools put into a container: mounted at <see cref="Mounted"/> where Passenger starts
/// it (<see cref="MountFor"/>), else copied to <see cref="Copied"/>. Any thread: nothing here touches the window.</summary>
public static class ContainerTools
{
    public const string Executable = "passenger-tools";

    /// <summary>Where the tools are mounted, read-only, into a container Passenger starts for debugging or profiling.</summary>
    public const string Mounted = "/passenger/tools";

    /// <summary>Where they are copied into a container Passenger did not start (or whose engine cannot see this machine's files).</summary>
    public const string Copied = "/tmp/passenger-tools";

    /// <summary>Where the adapter in a container keeps the symbols it decompiles.</summary>
    public const string SymbolCache = "/tmp/passenger-symbols";

    /// <summary>The release's archive of the tools for a system, by the fixed name the release workflow gives it.</summary>
    public static string Asset(string rid) => $"Passenger-container-tools-{rid}.tar.gz";

    /// <summary>A <c>-v</c> value (or a Compose service's <c>volumes:</c> entry) that mounts the tools' folder on this machine
    /// read-only at <see cref="Mounted"/>: what a container run configuration adds to a container it starts for Debug.</summary>
    public static string MountFor(string folder) => $"{folder}:{Mounted}:ro";

    /// <summary>Each image's system, read once (by the image's id).</summary>
    private static readonly ConcurrentDictionary<string, ContainerSystem> Systems = new(StringComparer.Ordinal);

    /// <summary>The containers the tools were copied into in this process, by id: where they are.</summary>
    private static readonly ConcurrentDictionary<string, string> CopiedInto = new(StringComparer.Ordinal);

    /// <summary>The tools' folder on this machine for a system, its executable inside: a copy found, else downloaded.
    /// <paramref name="say"/> tells of a download's progress. Throws <see cref="ContainerToolsException"/> saying why there
    /// are none.</summary>
    public static async Task<string> FolderAsync(ContainerToolsSource source, string rid, Action<string>? say, CancellationToken cancel)
    {
        string version = Product.Version;
        string? stale = null;
        foreach (string local in source.Local)
        {
            string folder = Path.Combine(local, rid);
            if (File.Exists(Path.Combine(folder, Executable)))
            {
                // A copy of another version (a dev build's made before the version moved) is passed over, never used.
                if (VersionOf(folder) is { } other && other != version)
                {
                    stale = $"the copy at {folder} is version {other}";
                    IdeLog.Write(IdeLog.Debugger, $"Passenger's container tools at {folder} are version {other}, not {version}: passed over");
                    continue;
                }
                return folder;
            }
        }
        try
        {
            return await ReleaseAssets.FolderAsync(source.Releases, Asset(rid), source.Cache, rid, Executable,
                $"Passenger's container tools for {rid}", say, cancel,
                unreached: Product.Version.StartsWith("0.1.", StringComparison.Ordinal) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt"))
                    ? $"a build of your own makes them with just container-tools {rid}" : null).ConfigureAwait(false);
        }
        catch (ReleaseAssetException failed)
        {
            throw new ContainerToolsException(stale is null ? failed.Message
                : $"{failed.Message} ({stale}, not this Passenger's {version}: just container-tools {rid} makes this version's)");
        }
    }

    /// <summary>The version a folder's tools say they are (their assembly's informational version, less its commit), read
    /// from their files without running them; null when it cannot be read.</summary>
    public static string? VersionOf(string folder) =>
        AssemblyFacts.Read(Path.Combine(folder, $"{Executable}.dll"))?.InformationalVersion?.Split('+')[0];

    /// <summary>A container's programs reached through Passenger's tools: the tools there, else copied in for its system (a
    /// copy found here or downloaded from <paramref name="source"/>, <paramref name="say"/> telling each stage and a download's
    /// progress), and whether its image runs under emulation. Throws <see cref="ContainerToolsException"/> saying why not.</summary>
    public static async Task<ContainerReach> ReachAsync(ContainerEngine engine, string container, ContainerToolsSource source, Action<string>? say,
        CancellationToken cancel)
    {
        ContainerSystem system = await SystemOfContainerAsync(engine, container, cancel).ConfigureAwait(false);
        string architecture = await EngineArchitectureAsync(engine, cancel).ConfigureAwait(false);
        string? tools = await InContainerAsync(engine, container, cancel).ConfigureAwait(false);
        if (tools is null)
        {
            string rid = system.Rid ?? throw new ContainerToolsException($"{container}'s image is for {system.Architecture}, for which Passenger has no container tools");
            string folder = await FolderAsync(source, rid, say, cancel).ConfigureAwait(false);
            say?.Invoke($"Copying Passenger's container tools into {container}");
            tools = await CopyIntoAsync(engine, container, folder, cancel).ConfigureAwait(false);
        }
        return new ContainerReach(engine, container, tools, system.Architecture == architecture ? null : Emulation(system.Architecture, architecture));
    }

    /// <summary>Why a debugger may not attach in a container whose image is for another processor than its engine's.</summary>
    public static string Emulation(string image, string engine) =>
        $"its image is for {image}, which this {engine} engine runs under emulation, where the debugger may not attach";

    /// <summary>A program's note with a container's caveat beside it, once; either alone where the other is null.</summary>
    public static string? Noted(string? note, string? caveat) =>
        caveat is null || note?.Contains(caveat, StringComparison.Ordinal) == true ? note : note is null ? caveat : $"{note}; {caveat}";

    /// <summary>The engine's own processor, docker's name for it (amd64, arm64).</summary>
    public static async Task<string> EngineArchitectureAsync(ContainerEngine engine, CancellationToken cancel)
    {
        // Podman's own docker has no server apart, nor Arch, WSL Containers' wslc no template: the state reads their systems.
        EngineState read = await engine.StateAsync(cancel: cancel).ConfigureAwait(false);
        if ((read.PodmanCli || read.IsWslc) && read.ServerArch is { } arch)
        {
            return arch;
        }
        ChildResult result = await engine.RunAsync(["version", "--format", "{{.Server.Arch}}"], cancel: cancel).ConfigureAwait(false);
        return result.Succeeded ? result.Output.Trim() : throw new ContainerToolsException($"The container engine did not say its processor: {ContainerEngine.Said(result)}");
    }

    /// <summary>A container's system: its image's processor, and musl when its /lib holds musl's loader (read by docker cp,
    /// which needs nothing in the image and works on a container created and never started). Once per image.</summary>
    public static async Task<ContainerSystem> SystemOfContainerAsync(ContainerEngine engine, string container, CancellationToken cancel)
    {
        JsonElement inspected = await engine.JsonAsync(["inspect", "--type", "container", container], cancel).ConfigureAwait(false);
        string image = JsonMembers.Scalar(First(inspected), "Image") ?? throw new ContainerToolsException($"docker inspect named no image of {container}");
        if (Systems.TryGetValue(image, out ContainerSystem? known))
        {
            return known;
        }
        JsonElement described = await engine.JsonAsync(["image", "inspect", image], cancel).ConfigureAwait(false);
        string architecture = JsonMembers.Scalar(First(described), "Architecture") ?? "amd64";
        string loader = architecture == "arm64" ? "aarch64" : architecture == "amd64" ? "x86_64" : architecture;
        ContainerSystem system = new(architecture, await HasAsync(engine, container, $"/lib/ld-musl-{loader}.so.1", cancel).ConfigureAwait(false));
        Systems[image] = system;
        return system;

        static JsonElement First(JsonElement array) => array.ValueKind == JsonValueKind.Array && array.GetArrayLength() > 0 ? array[0] : default;
    }

    /// <summary>Whether a container has a file, asked of <c>docker cp … -</c>, whose archive is only begun: nothing is
    /// written here, and no more of a file the image made huge is read than its header.</summary>
    private static async Task<bool> HasAsync(ContainerEngine engine, string container, string path, CancellationToken cancel)
    {
        System.Diagnostics.Process process;
        try
        {
            System.Diagnostics.ProcessStartInfo start = engine.Command(["cp", $"{container}:{path}", "-"]);
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            process = await ChildProcesses.StartAsync(start);
        }
        catch (Exception failed) when (ChildFailures.Of(failed))
        {
            throw new ContainerToolsException($"Could not read the system of container {container}: {failed.Message}");
        }
        using (process)
        {
            UnawaitedWork.Dismiss(ChildProcesses.Hear(process, errors: _ => { }));
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(ContainerEngine.AskLimit);
            try
            {
                // An archive's first header is 512 bytes: their coming says the file is there.
                byte[] header = new byte[512];
                int read = await ChildProcesses.OutputStream(process).ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, limit.Token).ConfigureAwait(false);
                if (read == header.Length)
                {
                    return true;
                }
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return false;
            }
            catch (Exception failed) when (failed is IOException or ObjectDisposedException)
            {
                return false;
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new ContainerToolsException($"Container {container} did not say within {Durations.Said(ContainerEngine.AskLimit)} whether it has {path}");
            }
            finally
            {
                ChildProcesses.Kill(process);
            }
        }
    }

    /// <summary>An image's system, read from a container created from it (never started, labelled as all Passenger makes,
    /// removed at once): for a container Passenger is about to start with the tools mounted.</summary>
    public static async Task<ContainerSystem> SystemOfImageAsync(ContainerEngine engine, string image, IReadOnlyList<string> labels, CancellationToken cancel)
    {
        JsonElement described = await engine.JsonAsync(["image", "inspect", image], cancel).ConfigureAwait(false);
        if (JsonMembers.Scalar(described[0], "Id") is { } id && Systems.TryGetValue(id, out ContainerSystem? known))
        {
            return known;
        }
        ChildResult created = await engine.RunAsync(["create", .. labels, image], cancel: cancel).ConfigureAwait(false);
        if (!created.Succeeded)
        {
            throw new ContainerToolsException($"Could not read the system of image {image}: {ContainerEngine.Said(created)}");
        }
        string container = created.Output.Trim();
        try
        {
            return await SystemOfContainerAsync(engine, container, cancel).ConfigureAwait(false);
        }
        finally
        {
            await engine.RunAsync(ContainerLabels.RemovingWhole([container]), cancel: CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Where the tools' executable is in a container now: mounted where Passenger started it, or copied in by this
    /// process; null when it has none.</summary>
    public static async Task<string?> InContainerAsync(ContainerEngine engine, string container, CancellationToken cancel)
    {
        foreach (string folder in (string[])[Mounted, CopiedInto.GetValueOrDefault(container) ?? Copied])
        {
            string executable = $"{folder}/{Executable}";
            ChildResult said = await engine.RunAsync(["exec", .. Unheld, container, executable, Profiler.ContainerToolsWords.Version], cancel: cancel).ConfigureAwait(false);
            if (said.Succeeded && said.Output.Trim() == Product.Version)
            {
                return executable;
            }
        }
        return null;
    }

    /// <summary>Copies the tools into a container (<see cref="Copied"/>), and gives where their executable is there, once
    /// they say they are this Passenger's version; refused, saying which they are, when they are another's.</summary>
    public static async Task<string> CopyIntoAsync(ContainerEngine engine, string container, string folder, CancellationToken cancel)
    {
        string version = Product.Version;
        if (VersionOf(folder) is { } known && known != version)
        {
            throw new ContainerToolsException(Mismatch(folder, known));
        }
        // docker cp makes the folder and keeps each file's mode, the executable's among them (Windows' CLI marks every file executable).
        ChildResult copied = await engine.RunAsync(["cp", Path.Combine(folder, "."), $"{container}:{Copied}"], TimeSpan.FromMinutes(2), cancel).ConfigureAwait(false);
        if (!copied.Succeeded)
        {
            throw new ContainerToolsException($"Could not copy Passenger's container tools into {container}: {ContainerEngine.Said(copied)}");
        }
        string executable = $"{Copied}/{Executable}";
        ChildResult said = await engine.RunAsync(["exec", .. Unheld, container, executable, Profiler.ContainerToolsWords.Version], cancel: cancel).ConfigureAwait(false);
        if (said.Output.Trim() != version)
        {
            throw new ContainerToolsException(said.Succeeded ? Mismatch(folder, said.Output.Trim())
                : $"Passenger's container tools copied into {container} did not run: {ContainerEngine.Said(said)}");
        }
        CopiedInto[container] = Copied;
        return executable;

        static string Mismatch(string folder, string other) =>
            $"Passenger's container tools at {folder} are version {other}, and this Passenger is {Product.Version}: they were not used "
            + $"(just container-tools {Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))} makes this version's)";
    }

    /// <summary>The most read of the tools' <c>processes</c> answer: a few hundred characters a program, so 4 MB is
    /// thousands of them, and a container that sends more is refused rather than read into the IDE's memory.</summary>
    private const int MostProcesses = 4 * 1024 * 1024;

    /// <summary>The tools' <c>processes</c> in a container: its .NET programs, by their ids there. What the container
    /// answers is read as it is: an entry with no id is passed over, an answer that is no list, or longer than
    /// <see cref="MostProcesses"/>, refused.</summary>
    public static async Task<IReadOnlyList<DotnetProcess>> ProcessesAsync(ContainerEngine engine, string container, string executable, CancellationToken cancel)
    {
        ChildResult listed;
        try
        {
            listed = await engine.RunAsync(["exec", .. Unheld, container, executable, Profiler.ContainerToolsWords.Processes], cancel: cancel, mostOutput: MostProcesses).ConfigureAwait(false);
        }
        catch (ChildOutputException overran)
        {
            throw new ContainerToolsException($"Passenger's tools in {container} answered more than the {Sizes.Said(MostProcesses)} a list of programs takes, "
                + $"which was not read: {overran.Message}");
        }
        if (!listed.Succeeded)
        {
            throw new ContainerToolsException($"Passenger's tools could not list the programs in {container}: {ContainerEngine.Said(listed)}");
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(listed.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new ContainerToolsException($"Passenger's tools in {container} listed no programs");
            }
            return [.. document.RootElement.EnumerateArray()
                .Where(p => JsonMembers.Number(p, "id") is not null)
                .Select(p => new DotnetProcess(JsonMembers.Number(p, "id") ?? 0, JsonMembers.Scalar(p, "name") ?? string.Empty,
                    JsonMembers.Scalar(p, "commandLine") ?? string.Empty, JsonMembers.Scalar(p, "user") ?? string.Empty, true)
                {
                    Container = container,
                    HotReload = JsonMembers.Bool(p, "hotReload") == true,
                    Note = JsonMembers.Bool(p, "optimized") == true ? ReleaseBuild : null,
                    WhyNot = JsonMembers.Scalar(p, "whyNot"),
                })];
        }
        catch (JsonException unread)
        {
            throw new ContainerToolsException($"Passenger's tools in {container} answered what is no list of programs: {unread.Message}");
        }
    }

    /// <summary>What a person should know of a Release build: Just My Code finds none of its code the user's.</summary>
    public const string ReleaseBuild = "A Release build: optimized, so under Just My Code none of its code is yours and no breakpoint in it stops";

    /// <summary>docker exec runs the tools in the container's environment, which a run held for the debugger sets for its
    /// program: the tools' own runtime would hold at its start too, waiting for a debugger never coming, or load the
    /// program's startup hooks. Cleared for every command of theirs.</summary>
    private static readonly string[] Unheld =
        ["-e", $"{RuntimeVariables.DefaultDiagnosticPortSuspend}=0", "-e", $"{RuntimeVariables.DiagnosticPorts}=", "-e", $"{RuntimeVariables.StartupHooks}=",
            "-e", $"{RuntimeVariables.ModifiableAssemblies}="];

    /// <summary>The adapter's command in a container, over docker exec's standard input and output, given the IDE's
    /// evaluation limit.</summary>
    public static IReadOnlyList<string> AdapterCommand(string container, string executable, IReadOnlyList<string> adapterArguments) =>
        ["exec", "-i", .. Unheld, .. Carried(Dap.AdapterEnvironment.EvaluationTimeoutVariable), container, executable, Profiler.ContainerToolsWords.Dap,
            .. adapterArguments];

    /// <summary>A mode of the tools in a container, over docker exec's standard input and output: the profiler's
    /// <c>trace</c>, <c>heap</c> and <c>sources</c>, given the IDE's limits on its requests of a runtime.</summary>
    public static IReadOnlyList<string> ToolsCommand(string container, string executable, IReadOnlyList<string> words) =>
        ["exec", "-i", .. Unheld, .. Carried(Waits.ScaleVariable, Profiler.SessionStarts.LimitVariable), container, executable, .. words];

    /// <summary>The IDE's own value of each variable set here, for the tools there: docker exec gives them the container's
    /// environment alone.</summary>
    private static IEnumerable<string> Carried(params string[] variables) =>
        variables.SelectMany(variable => Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? (string[])["-e", $"{variable}={value}"] : []);
}

/// <summary>Passenger's container tools could not be had or used: no copy and no download, a checksum that did not match, a
/// container they could not be copied into. Its message says so in a line.</summary>
public sealed class ContainerToolsException(string message) : Exception(message);
