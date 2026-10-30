using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
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
    /// crossing through <paramref name="paths"/>; the container stopped (its own end, the Containers window's Stop) is its
    /// program's end, never the debugger's.</summary>
    public Debugging.AdapterPlace Adapter(string? program, ContainerPaths paths) =>
        new(arguments => Engine.Command(ContainerTools.AdapterCommand(Container, Tools, arguments)), ContainerTools.SymbolCache, paths, Container)
        {
            Running = Debugging.AdapterPlace.Runs(Engine, Container),
            Program = program,
        };

    /// <summary>Where the profiler records a program there, the paths its PDBs name made this machine's through <paramref name="paths"/>.</summary>
    public Profiler.ProfilePlace Recorder(ContainerPaths paths) =>
        new(words => Engine.Command(ContainerTools.ToolsCommand(Container, Tools, words)), Container, path => paths.ToHost(path) ?? path);

    /// <summary>A program's note there with the container's caveat beside it, once.</summary>
    public string? Noted(string? note) => ContainerTools.Noted(note, Emulated);
}

/// <summary>Passenger's container tools on this side (src/Passenger.ContainerTools runs inside): a copy for a container's
/// system found on this machine or downloaded once (its own version's release, checked against that release's
/// SHA256SUMS, into the state's cache through a temporary folder renamed), a container's system read once per image,
/// and the tools put into a container: mounted at <see cref="Mounted"/> where Passenger starts it (<see cref="MountFor"/>),
/// else copied to <see cref="Copied"/>. Any thread: nothing here touches the window.</summary>
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
        string cached = Path.Combine(source.Cache, version, rid);
        if (File.Exists(Path.Combine(cached, Executable)))
        {
            return cached;
        }
        // One download a system at a time in this process; another process's is kept as it lands (DownloadAsync).
        SemaphoreSlim one = Downloads.GetOrAdd(cached, _ => new SemaphoreSlim(1, 1));
        await one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (File.Exists(Path.Combine(cached, Executable)))
            {
                return cached;
            }
            return await DownloadAsync(source, version, rid, cached, say, cancel).ConfigureAwait(false);
        }
        catch (ContainerToolsException failed) when (stale is not null)
        {
            throw new ContainerToolsException($"{failed.Message} ({stale}, not this Passenger's {version}: just container-tools {rid} makes this version's)");
        }
        finally
        {
            one.Release();
        }
    }

    /// <summary>The downloads under way, one a system's folder.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Downloads = new(StringComparer.Ordinal);

    /// <summary>The version a folder's tools say they are (their assembly's informational version, less its commit), read
    /// from their files without running them; null when it cannot be read.</summary>
    public static string? VersionOf(string folder) =>
        AssemblyFacts.Read(Path.Combine(folder, $"{Executable}.dll"))?.InformationalVersion?.Split('+')[0];

    /// <summary>The tools of a release downloaded, checked and unpacked into <paramref name="cached"/>.</summary>
    private static async Task<string> DownloadAsync(ContainerToolsSource source, string version, string rid, string cached, Action<string>? say, CancellationToken cancel)
    {
        string tag = $"v{version}";
        Uri release = new(source.Releases.Downloads, $"{tag}/");
        string asset = Asset(rid);
        string parent = Path.GetDirectoryName(cached)!;
        Directory.CreateDirectory(parent);
        string archive = Path.Combine(parent, $".{asset}.{Guid.NewGuid():N}");
        string unpacking = Path.Combine(parent, $".{rid}.{Guid.NewGuid():N}");
        try
        {
            say?.Invoke($"Downloading Passenger's container tools for {rid}");
            string sums = await Releases.ReadTextAsync(new ReleaseFile("SHA256SUMS", new Uri(release, "SHA256SUMS"), 0), cancel).ConfigureAwait(false);
            string? expected = sums.Split('\n').Select(line => line.Trim().Split(' ', 2, StringSplitOptions.TrimEntries))
                .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*') == asset)?[0].ToLowerInvariant();
            if (expected is null)
            {
                throw new ContainerToolsException($"The {tag} release's SHA256SUMS lists no {asset}");
            }
            string got = await Releases.DownloadAsync(new ReleaseFile(asset, new Uri(release, asset), 0), archive,
                percent => say?.Invoke($"Downloading Passenger's container tools for {rid}: {percent}%"), cancel).ConfigureAwait(false);
            if (got != expected)
            {
                IdeLog.Write(IdeLog.Debugger, $"{asset} of {tag} has SHA-256 {got}, where SHA256SUMS says {expected}: refused");
                throw new ContainerToolsException($"The container tools downloaded for {rid} are not what the {tag} release's SHA256SUMS says they are, and were not used");
            }
            Directory.CreateDirectory(unpacking);
            await using (FileStream file = File.OpenRead(archive))
            await using (GZipStream unzipped = new(file, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(unzipped, unpacking, overwriteFiles: false, cancel).ConfigureAwait(false);
            }
            if (!File.Exists(Path.Combine(unpacking, Executable)))
            {
                throw new ContainerToolsException($"{asset} of {tag} holds no {Executable}");
            }
            if (Directory.Exists(cached) && !File.Exists(Path.Combine(cached, Executable)))
            {
                // Something left with no tools in it, never a copy another Passenger put there whole.
                Directory.Delete(cached, recursive: true);
            }
            try
            {
                Directory.Move(unpacking, cached);
            }
            catch (IOException) when (File.Exists(Path.Combine(cached, Executable)))
            {
                // Another Passenger's download landed while this one unpacked: theirs stays, and this one goes below.
            }
            return cached;
        }
        catch (Exception failed) when (failed is UpdateFailedException or UpdateRefusedException)
        {
            throw new ContainerToolsException($"Passenger's container tools for {rid} could not be downloaded ({failed.Message})"
                + (Product.Version.StartsWith("0.1.", StringComparison.Ordinal) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "LICENSE.txt"))
                    ? $"; a build of your own makes them with just container-tools {rid}" : string.Empty));
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ContainerToolsException($"Passenger's container tools for {rid} could not be unpacked: {failed.Message}");
        }
        finally
        {
            File.Delete(archive);
            if (Directory.Exists(unpacking))
            {
                Directory.Delete(unpacking, recursive: true);
            }
        }
    }

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
        // Podman's own docker has no server apart, nor Arch: the state reads its system from what it has.
        if ((await engine.StateAsync(cancel: cancel).ConfigureAwait(false)) is { PodmanCli: true, ServerArch: { } arch })
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
        string image = ContainerEngine.Text(First(inspected), "Image") ?? throw new ContainerToolsException($"docker inspect named no image of {container}");
        if (Systems.TryGetValue(image, out ContainerSystem? known))
        {
            return known;
        }
        JsonElement described = await engine.JsonAsync(["image", "inspect", image], cancel).ConfigureAwait(false);
        string architecture = ContainerEngine.Text(First(described), "Architecture") ?? "amd64";
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
        catch (Exception failed) when (failed is FileNotFoundException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ContainerToolsException($"Could not read the system of container {container}: {failed.Message}");
        }
        using (process)
        {
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None).ContinueWith(read => read.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(ContainerEngine.AskLimit);
            try
            {
                // An archive's first header is 512 bytes: their coming says the file is there.
                byte[] header = new byte[512];
                int read = await process.StandardOutput.BaseStream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, limit.Token).ConfigureAwait(false);
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
                throw new ContainerToolsException($"Container {container} did not say within {ContainerEngine.AskLimit.TotalSeconds:0} s whether it has {path}");
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
        if (ContainerEngine.Text(described[0], "Id") is { } id && Systems.TryGetValue(id, out ContainerSystem? known))
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
            await engine.RunAsync(["rm", "-f", container], cancel: CancellationToken.None).ConfigureAwait(false);
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
            throw new ContainerToolsException($"Passenger's tools in {container} answered more than the {MostProcesses / (1024 * 1024)} MB a list of programs takes, "
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
                .Where(p => p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out _))
                .Select(p => new DotnetProcess(p.GetProperty("id").GetInt32(), ContainerEngine.Text(p, "name") ?? string.Empty,
                    ContainerEngine.Text(p, "commandLine") ?? string.Empty, ContainerEngine.Text(p, "user") ?? string.Empty, true)
                {
                    Container = container,
                    HotReload = p.TryGetProperty("hotReload", out JsonElement hot) && hot.ValueKind == JsonValueKind.True,
                    Note = p.TryGetProperty("optimized", out JsonElement optimized) && optimized.ValueKind == JsonValueKind.True ? ReleaseBuild : null,
                    WhyNot = ContainerEngine.Text(p, "whyNot"),
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
    public static IReadOnlyList<string> AdapterCommand(string container, string executable, IReadOnlyList<string> adapterArguments)
    {
        List<string> words = ["exec", "-i", .. Unheld];
        if (Environment.GetEnvironmentVariable(Dap.AdapterEnvironment.EvaluationTimeoutVariable) is { Length: > 0 } limit)
        {
            words.AddRange(["-e", $"{Dap.AdapterEnvironment.EvaluationTimeoutVariable}={limit}"]);
        }
        return [.. words, container, executable, Profiler.ContainerToolsWords.Dap, .. adapterArguments];
    }

    /// <summary>A mode of the tools in a container, over docker exec's standard input and output: the profiler's
    /// <c>trace</c>, <c>heap</c> and <c>sources</c>.</summary>
    public static IReadOnlyList<string> ToolsCommand(string container, string executable, IReadOnlyList<string> words) =>
        ["exec", "-i", .. Unheld, container, executable, .. words];
}

/// <summary>Passenger's container tools could not be had or used: no copy and no download, a checksum that did not match, a
/// container they could not be copied into. Its message says so in a line.</summary>
public sealed class ContainerToolsException(string message) : Exception(message);
