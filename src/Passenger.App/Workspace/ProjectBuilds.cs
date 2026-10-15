using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Host;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;

namespace Passenger.App.Workspace;

/// <summary>A folder's projects as Roslyn's server loads them, built at design time by the project host
/// (<see cref="ProjectHost"/>) rather than by the server's own build host, and kept from one opening to the next
/// (<see cref="ProjectBuildCache"/>): the server is given each project's builds that still stand at once, and asks for the
/// rest (<see cref="IProjectFeed"/>), which the host builds, those the server will want first first, while the server is
/// still starting. A UI-thread object; <see cref="Feed"/> is what the server's client asks on its own threads.</summary>
public sealed class ProjectBuilds
{
    private readonly Dictionary<string, KeptBuild> known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource<BuiltProject>> waiting = new(StringComparer.Ordinal);
    private readonly List<string> queued = [];
    private readonly Func<Task<(IReadOnlyList<string> First, IReadOnlyList<string> All)>> projects;
    private readonly Func<IReadOnlyDictionary<string, string>> solution;
    private readonly string intermediateRoot;
    private StateDocument<ProjectBuildCache>? kept;
    private string? folder;
    private string? sdk;

    /// <summary>The solution the builds known were made under (its global properties), which a build under another does
    /// not stand for.</summary>
    private string? builtUnder;
    private int generation;
    private bool building;

    /// <summary>The cache's read under way, off the UI thread, which what the server asks first waits for.</summary>
    private Task<ProjectBuildCache>? reading;

    /// <summary>Ends the folder's round in the host as the folder is let go, so the next folder's builds need not wait.</summary>
    private CancellationTokenSource ending = new();

    /// <param name="projects">The open workspace's projects to load, those of the file shown (and those it references)
    /// apart, asked on the UI thread as the server is ready for them.</param>
    /// <param name="solution">The global properties the open solution gives its projects, as a build of it would.</param>
    public ProjectBuilds(Func<Task<(IReadOnlyList<string> First, IReadOnlyList<string> All)>> projects, Func<IReadOnlyDictionary<string, string>> solution, string stateFolder)
    {
        this.projects = projects;
        this.solution = solution;
        intermediateRoot = PathNames.Canonical(Path.Combine(stateFolder, "project-intermediates", "obj"));
        Feed = new Crossing(this);
    }

    /// <summary>The builds as the server's client asks for them, from any thread.</summary>
    public IProjectFeed Feed { get; }

    /// <summary>How many projects the host has built at design time for this window: a folder whose builds all stand
    /// builds none as it opens.</summary>
    public int Builds { get; private set; }

    /// <summary>Takes up a folder, with the store's cache of its builds when the window keeps one, read off the UI thread
    /// as the folder opens.</summary>
    public void Open(string opened, StateDocument<ProjectBuildCache>? cache)
    {
        UiThread.Check();
        Close();
        folder = opened;
        kept = cache;
        reading = cache?.ValueAsync();
    }

    /// <summary>The cache, once read, in what the builds know.</summary>
    private async Task TakeCacheAsync()
    {
        if (reading is not { } pending)
        {
            return;
        }
        int mine = generation;
        ProjectBuildCache value = await pending;
        if (mine != generation || reading != pending)
        {
            return;
        }
        reading = null;
        sdk = value.Sdk;
        builtUnder = value.Solution;
        foreach ((string path, KeptBuild build) in value.Projects)
        {
            known[path] = build;
        }
    }

    /// <summary>Lets the folder go: what is still being built for it is dropped as it lands, and what waits for it fails.</summary>
    public void Close()
    {
        UiThread.Check();
        ending.Cancel();
        ending = new CancellationTokenSource();
        reading = null;
        generation++;
        folder = null;
        kept = null;
        sdk = null;
        builtUnder = null;
        known.Clear();
        queued.Clear();
        building = false;
        foreach (TaskCompletionSource<BuiltProject> waiter in waiting.Values)
        {
            waiter.TrySetResult(new BuiltProject(string.Empty, "[]", [], "the folder was closed"));
        }
        waiting.Clear();
    }

    /// <summary>The projects the server is to load: each with its builds when they stand, the rest built now, those the
    /// server opens first first; none to give when the host will not start for the folder.</summary>
    private async Task<(IReadOnlyList<ProjectToOpen> First, IReadOnlyList<ProjectToOpen> Later)?> ProjectsAsync()
    {
        int mine = generation;
        await TakeCacheAsync();
        (IReadOnlyList<string> first, IReadOnlyList<string> all) = await projects();
        List<string> rest = [.. all.Where(p => !first.Contains(p, StringComparer.Ordinal))];
        string? host = await HostSdkAsync();
        if (mine != generation)
        {
            return ([], []);
        }
        if (host is null)
        {
            return null;
        }
        string? under = Under();
        if (sdk is null || !PathNames.Same(sdk, host) || under != builtUnder)
        {
            // Built with another SDK's MSBuild, or under another solution's properties: none of it stands.
            known.Clear();
            sdk = host;
            builtUnder = under;
        }
        Dictionary<string, KeptBuild> held = new(known, StringComparer.Ordinal);
        // Read off the window's thread: a large solution's builds are megabytes.
        Dictionary<string, ProjectToOpen> standing = await Task.Run(() => all
            .Where(p => held.TryGetValue(p, out KeptBuild? build) && build.Inputs.All(Stamps.Holds))
            .ToDictionary(p => p, p => new ProjectToOpen(p, held[p].Read(), Said(held[p].Diagnostics)), StringComparer.Ordinal));
        if (mine != generation)
        {
            return ([], []);
        }
        foreach (string path in first.Where(p => !standing.ContainsKey(p)))
        {
            Queue(path);
        }
        // The file shown's projects in a round of their own, so the server has them before the rest are built.
        Pump();
        foreach (string path in rest.Where(p => !standing.ContainsKey(p)))
        {
            Queue(path);
        }
        Pump();
        if (first.Count > 0)
        {
            IdeErrors.Write($"C# language server loads {string.Join(", ", first.Select(Path.GetFileNameWithoutExtension))} first, for the file shown ({first.Count(standing.ContainsKey)} of them as built before)");
        }
        return ([.. first.Select(Open)], [.. rest.Select(Open)]);

        ProjectToOpen Open(string path) => standing.GetValueOrDefault(path) ?? new(path, null);
    }

    private static List<ProjectDiagnostic> Said(IReadOnlyList<BuildDiagnostic>? logged) =>
        [.. (logged ?? []).Select(d => new ProjectDiagnostic(d.Error, d.Message, d.Project))];

    /// <summary>The SDK the folder's host loads, which the builds kept were made with or not; null when it will not start.</summary>
    private async Task<string?> HostSdkAsync()
    {
        if (folder is not { } following)
        {
            return null;
        }
        try
        {
            return (await Task.Run(() => ProjectHosts.ForAsync(following))).Sdk;
        }
        catch (ProjectHostFailed)
        {
            return null;
        }
    }

    /// <summary>One project's builds as the server asks for them: those kept when they stand, else built now.</summary>
    private async Task<ProjectInfoResult> InfoAsync(string path)
    {
        await TakeCacheAsync();
        if (!waiting.ContainsKey(path) && known.TryGetValue(path, out KeptBuild? held))
        {
            Stamp? changed = await Task.Run(() => held.Inputs.FirstOrDefault(stamp => !Stamps.Holds(stamp)));
            if (changed is null)
            {
                return new ProjectInfoResult(held.Read(), null, Said(held.Diagnostics));
            }
            IdeErrors.Write($"Design-time project refresh of {path}: {(changed.Folder ? "folder names" : "file")} changed: {changed.Path}");
        }
        BuiltProject built = await Built(path);
        return built.Problem is { } problem ? new ProjectInfoResult(null, problem) : new ProjectInfoResult(built.Infos, null, Said(built.Diagnostics));
    }

    private Task<BuiltProject> Built(string path)
    {
        Queue(path);
        Pump();
        return waiting[path].Task;
    }

    private void Queue(string path)
    {
        if (waiting.ContainsKey(path))
        {
            return;
        }
        waiting[path] = new TaskCompletionSource<BuiltProject>(TaskCreationOptions.RunContinuationsAsynchronously);
        queued.Add(path);
    }

    /// <summary>Has the host build what is queued, all of it at once, the next round once this one ends.</summary>
    private void Pump()
    {
        if (building || queued.Count == 0 || folder is null)
        {
            return;
        }
        building = true;
        List<string> round = [.. queued];
        queued.Clear();
        Dictionary<string, string> properties = new(solution(), StringComparer.OrdinalIgnoreCase)
        {
            // What Roslyn's server gives its own build host, so Razor's generated C# is in the builds as it would be there.
            ["RazorDesignTimeTargets"] = Path.Combine(AppContext.BaseDirectory, "roslyn", "Targets", "Microsoft.NET.Sdk.Razor.DesignTime.targets"),
            ["PassengerDesignTimeRoot"] = intermediateRoot,
        };
        // Not background work: nothing drawn waits on it but the language server's load, which a screenshot does not wait
        // for either, and a large solution's round takes minutes.
        _ = RoundAsync(generation, folder, round, properties, ending.Token);
    }

    /// <summary>The solution the projects are built under now, by the path its global properties give.</summary>
    private string? Under() => solution().GetValueOrDefault("SolutionPath");

    private async Task RoundAsync(int mine, string following, List<string> round, Dictionary<string, string> properties, CancellationToken cancel)
    {
        long began = Stopwatch.GetTimestamp();
        try
        {
            ProjectHost host = await Task.Run(() => ProjectHosts.ForAsync(following));
            BuildDone done = await host.BuildAsync(round, properties, built => UiThread.Post(() => Land(mine, built)), cancel);
            if (mine == generation)
            {
                IdeErrors.Write($"Project host (process {host.ProcessId}) built {done.Built} projects of {following} at design time in {done.Seconds:0.00} s, {Stopwatch.GetElapsedTime(began).TotalSeconds:0.00} s after they were asked for");
                Keep();
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The folder was let go: the host was told to end its round.
        }
        catch (Exception error) when (error is ProjectHostFailed or IOException or UnauthorizedAccessException)
        {
            if (mine == generation)
            {
                string why = error is ProjectHostFailed failed ? failed.Reason : error.Message;
                IdeErrors.Write($"The project host could not build the projects of {following}: {why}");
                foreach (string path in round)
                {
                    Land(mine, new BuiltProject(path, "[]", [], why));
                }
            }
        }
        finally
        {
            if (mine == generation)
            {
                building = false;
                Pump();
            }
        }
    }

    private void Land(int mine, BuiltProject built)
    {
        if (mine != generation)
        {
            return;
        }
        if (built.Problem is null)
        {
            known[built.Path] = KeptBuild.Of(built);
            Builds++;
        }
        else
        {
            known.Remove(built.Path);
        }
        if (waiting.Remove(built.Path, out TaskCompletionSource<BuiltProject>? waiter))
        {
            waiter.TrySetResult(built);
        }
    }

    /// <summary>Every build that stands, kept for the folder's next opening.</summary>
    private void Keep()
    {
        if (kept is not null && sdk is not null)
        {
            kept.Set(new ProjectBuildCache { Sdk = sdk, Solution = builtUnder, Projects = new(known, StringComparer.Ordinal) });
        }
    }

    /// <summary>The server's client's way in: each question crosses to the UI thread, where the builds are.</summary>
    private sealed class Crossing(ProjectBuilds builds) : IProjectFeed
    {
        public Task<(IReadOnlyList<ProjectToOpen> First, IReadOnlyList<ProjectToOpen> Later)?> ProjectsAsync(CancellationToken cancel) =>
            UiThread.InvokeAsync(builds.ProjectsAsync);

        public Task<ProjectInfoResult> InfoAsync(string path, CancellationToken cancel) => UiThread.InvokeAsync(() => builds.InfoAsync(path));
    }
}

/// <summary>A folder's projects' design-time builds as the project host last made them, kept in the folder's place in the
/// store so the language server loads them from here as the folder opens again: each with what its builds read, so one
/// whose inputs changed since is built again, and the SDK they were built with.</summary>
public sealed record ProjectBuildCache : IStateDocument<ProjectBuildCache>
{
    public string? Sdk { get; init; }

    /// <summary>The solution they were built under, whose global properties a project's build may read.</summary>
    public string? Solution { get; init; }

    public Dictionary<string, KeptBuild> Projects { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned, ForgetWindowsTargeting, ForgetSharedIntermediates, ForgetIsolatedAssemblyIdentity, ForgetAliasedGeneratedPaths, ForgetUnnormalizedAssemblyPaths];

    private static void ForgetUnnormalizedAssemblyPaths(JsonObject document) => document.Remove("projects");

    private static void ForgetAliasedGeneratedPaths(JsonObject document) => document.Remove("projects");

    /// <summary>Roslyn's compilation output must name the real assembly, not the non-emitting design-time build's.</summary>
    private static void ForgetIsolatedAssemblyIdentity(JsonObject document) => document.Remove("projects");

    /// <summary>Generated documents from earlier builds shared obj with ordinary builds; rebuild them in the IDE's directory.</summary>
    private static void ForgetSharedIntermediates(JsonObject document) => document.Remove("projects");

    /// <summary>The builds that failed off Windows for want of EnableWindowsTargeting (NETSDK1100), kept before the host
    /// gave it, are built again.</summary>
    private static void ForgetWindowsTargeting(JsonObject document)
    {
        if (document["projects"] is not JsonObject projects)
        {
            return;
        }
        foreach (string path in projects.Where(p => p.Value?["diagnostics"] is JsonArray said
            && said.Any(d => d?["message"] is JsonValue message && message.GetValueKind() == System.Text.Json.JsonValueKind.String
                && message.GetValue<string>().Contains("EnableWindowsTargeting", StringComparison.Ordinal)))
            .Select(p => p.Key).ToList())
        {
            projects.Remove(path);
        }
    }

    public ProjectBuildCache Clamped() => this with
    {
        Projects = (Projects ?? []).Where(p => p.Value is { Builds.Length: > 0 }).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
    };
}

/// <summary>One project's builds, compressed (Brotli, as Base64: a large solution's command lines repeat themselves many
/// times over), and what they read.</summary>
public sealed record KeptBuild
{
    public string Builds { get; init; } = string.Empty;

    public List<Stamp> Inputs { get; init; } = [];

    /// <summary>The errors and warnings the builds logged, which the server is given again with them.</summary>
    public List<BuildDiagnostic> Diagnostics { get; init; } = [];

    public static KeptBuild Of(BuiltProject built)
    {
        byte[] text = Encoding.UTF8.GetBytes(built.Infos);
        using MemoryStream packed = new();
        using (BrotliStream brotli = new(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(text);
        }
        return new KeptBuild { Builds = Convert.ToBase64String(packed.ToArray()), Inputs = [.. built.Inputs], Diagnostics = [.. built.Diagnostics ?? []] };
    }

    public string Read()
    {
        using BrotliStream brotli = new(new MemoryStream(Convert.FromBase64String(Builds)), CompressionMode.Decompress);
        using StreamReader reader = new(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
