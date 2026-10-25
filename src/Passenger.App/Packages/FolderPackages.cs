using Passenger.Core;
using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.App.Packages;

/// <summary>The folder's NuGet, whoever asks of it (the NuGet window, MSBuild files' code help, Migrations, an agent): the
/// package sources its nuget.config files give, read when first asked for and again after one changes, their feeds, the
/// audit's known vulnerabilities, and the one way a package goes in, moves or comes out of a project, through the .NET
/// command line, one change at a time. A session's own, ended with it; a UI-thread object.</summary>
public sealed class FolderPackages
{
    /// <summary>What a package change's work is held under in the window, which no action names.</summary>
    public const string Changing = "nuget.change";

    private readonly Func<IReadOnlyList<ProjectModel>> projects;
    private readonly Func<string?> root;
    private readonly PackageManager manager = new();
    private Dictionary<string, IPackageFeed> feeds = new(StringComparer.OrdinalIgnoreCase);
    private Task? reading;

    /// <summary>The audit's known vulnerabilities, read once the sources are, the first time they are asked for.</summary>
    private Task<VulnerabilityData>? audit;

    /// <summary>The package change under way, as its work says it.</summary>
    private string? changing;

    /// <param name="projects">The folder's projects, whose packages are listed and changed.</param>
    /// <param name="root">The folder NuGet's configuration is read from: the solution's, else the one open.</param>
    public FolderPackages(Func<IReadOnlyList<ProjectModel>> projects, Func<string?> root)
    {
        this.projects = projects;
        this.root = root;
        manager.Line += (_, line) => Line?.Invoke(this, line);
    }

    /// <summary>A line a package change's command printed, for the NuGet window's log.</summary>
    public event EventHandler<string>? Line;

    /// <summary>Raised as the sources read are taken, for whatever shows their feeds.</summary>
    public event EventHandler? SourcesChanged;

    /// <summary>Raised as a package change begins, with what it is.</summary>
    public event EventHandler<string>? ChangeBegun;

    /// <summary>Raised after a package change, done or not, with the files it changed, before whoever asked for it is
    /// answered: the window takes them into its editors and reads the solution again.</summary>
    public event EventHandler<PackageOutcome>? Changed;

    /// <summary>Saves the open files before a change rewrites a project or a nuget.config, so what is rewritten holds their
    /// edits.</summary>
    public Action? SaveFirst { get; set; }

    /// <summary>The window's work under way, which a package change shows itself in until it ends.</summary>
    public ActionWork? Work { get; set; }

    /// <summary>A feed searched in place of the folder's (a test's).</summary>
    public IPackageFeed? Given { get; set; }

    /// <summary>The folder's package sources as last read.</summary>
    public PackageSourceSet Sources { get; private set; } = PackageSourceSet.None;

    /// <summary>Whether the sources read are those of the folder NuGet's configuration is read from now.</summary>
    public bool IsCurrent => Sources != PackageSourceSet.None && Sources.Root == (root() ?? string.Empty);

    /// <summary>The folder's projects, whose packages are listed and changed.</summary>
    public IReadOnlyList<ProjectModel> Projects => projects();

    /// <summary>Whether a package change is under way.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Reads the folder's nuget.config files again (after one changed, here or on disk); false when a later read
    /// took its place.</summary>
    public async Task<bool> ReloadSourcesAsync()
    {
        string folder = root() ?? string.Empty;
        using IDisposable held = BackgroundWork.Scope();
        Task<PackageSourceSet> read = BackgroundWork.Run(() => folder.Length == 0 ? PackageSourceSet.None : PackageSourceSet.Load(folder));
        reading = read;
        PackageSourceSet loaded = await read;
        if (reading != read)
        {
            return false;
        }
        Dictionary<string, IPackageFeed> made = new(StringComparer.OrdinalIgnoreCase);
        foreach (FeedSource source in loaded.Feeds)
        {
            // A feed kept when nothing about it changed, so its service index is not asked for again.
            made[source.Name] = feeds.TryGetValue(source.Name, out IPackageFeed? kept) && Sources.Feeds.Contains(source) ? kept : PackageFeeds.For(source);
        }
        Sources = loaded;
        feeds = made;
        audit = null;
        SourcesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>A feed by its name, or All Feeds when none is given, the folder's sources read first when they are not.</summary>
    public async Task<IPackageFeed> FeedAsync(string? name)
    {
        if (Given is { } given)
        {
            return given;
        }
        if (!IsCurrent)
        {
            await ReloadSourcesAsync();
        }
        return Chosen(name ?? FeedSet.All);
    }

    /// <summary>A feed by its name in the folder's configuration, or All Feeds.</summary>
    public IPackageFeed Chosen(string name)
    {
        IPackageFeed[] enabled = [.. Sources.Enabled.Select(f => feeds[f.Name])];
        if (name.Equals(FeedSet.All, StringComparison.OrdinalIgnoreCase))
        {
            return new FeedSet(FeedSet.All, enabled, Sources.Allows);
        }
        return enabled.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } one
            ? new FeedSet(one.Name, [one], Sources.Allows)
            : throw new ArgumentException($"No enabled feed is named {name}; the folder's nuget.config files enable {string.Join(", ", enabled.Select(f => f.Name))}");
    }

    /// <summary>The known vulnerabilities, read from the audit's feeds the first time they are asked for after the sources
    /// were read.</summary>
    public Task<VulnerabilityData> Audit()
    {
        IReadOnlyList<FeedSource> audited = Sources.Audited;
        return audit ??= BackgroundWork.Run(() => VulnerabilityData.ReadAsync(audited, CancellationToken.None));
    }

    /// <summary>The known vulnerabilities of the folder's audit feeds, its sources read first when they are not; none when
    /// no feed could say.</summary>
    public async Task<VulnerabilityData> VulnerabilitiesAsync()
    {
        if (!IsCurrent)
        {
            await ReloadSourcesAsync();
        }
        try
        {
            return await Audit();
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            return VulnerabilityData.None;
        }
    }

    /// <summary>The vulnerabilities as far as they are known: none until they are read, or when they could not be.</summary>
    public VulnerabilityData Known => audit is { IsCompletedSuccessfully: true } read ? read.Result : VulnerabilityData.None;

    /// <summary>The packages the projects reference: by package, each project's version (null where only central
    /// management would know and it does not say).</summary>
    public Dictionary<string, Dictionary<string, string?>> Installed()
    {
        Dictionary<string, Dictionary<string, string?>> installed = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectModel project in Projects)
        {
            foreach ((string name, string? version) in project.Packages)
            {
                if (!installed.TryGetValue(name, out Dictionary<string, string?>? byProject))
                {
                    installed[name] = byProject = [];
                }
                byProject[project.Path] = version;
            }
        }
        return installed;
    }

    /// <summary>Adds a package to a project at a version, or moves it to that version: the one way a package goes in
    /// (<see cref="ChangeAsync"/>).</summary>
    public Task<PackageOutcome> InstallAsync(ProjectModel project, string id, string version) =>
        ChangeAsync($"Installing {id} {version} in {project.Name}…", project, () => manager.InstallAsync(project.Path, id, version));

    /// <summary>Moves several packages of a project to their versions together, as one change (<see cref="ChangeAsync"/>):
    /// EF Core's packages with the provider that supports them.</summary>
    public Task<PackageOutcome> InstallSetAsync(ProjectModel project, IReadOnlyList<(string Id, string Version)> packages) =>
        ChangeAsync($"Moving {string.Join(", ", packages.Select(p => $"{p.Id} to {p.Version}"))} in {project.Name}…", project, () => manager.InstallSetAsync(project.Path, packages));

    /// <summary>Takes a package out of a project.</summary>
    public Task<PackageOutcome> RemoveAsync(ProjectModel project, string id) =>
        ChangeAsync($"Removing {id} from {project.Name}…", project, () => manager.RemoveAsync(project.Path, id));

    /// <summary>Ends a package change still under way, as the folder is left.</summary>
    public void Stop() => manager.Stop();

    /// <summary>A package change, whoever asks for it: one at a time (another asked meanwhile is refused, saying what is under
    /// way), the open files saved first, shown as the window's work under <see cref="Changing"/>, which <see cref="Stop"/>
    /// ends; then the files it changed told (<see cref="Changed"/>).</summary>
    private async Task<PackageOutcome> ChangeAsync(string stage, ProjectModel project, Func<Task<bool>> change)
    {
        if (IsBusy)
        {
            return new PackageOutcome(false, [], []) { Refused = Work?.Of(Changing) is { } under ? ActionWork.Busy(under) : $"Already under way: {changing}" };
        }
        IsBusy = true;
        changing = stage;
        ChangeBegun?.Invoke(this, stage);
        using IDisposable held = BackgroundWork.Scope();
        using Working? working = Work?.Begin(Changing, stage, stoppable: false);
        List<string> said = [];
        EventHandler<string> heard = (_, line) => said.Add(line);
        manager.Line += heard;
        IReadOnlyList<string> files = PackageFiles(project);
        byte[]?[] before;
        bool done;
        try
        {
            SaveFirst?.Invoke();
            // Read once the open files are saved: what the save wrote is not the change's.
            before = [.. files.Select(Bytes)];
            done = await change();
        }
        finally
        {
            IsBusy = false;
            changing = null;
            manager.Line -= heard;
        }
        PackageOutcome outcome = new(done, [.. files.Where((file, i) => !(Bytes(file) ?? []).AsSpan().SequenceEqual(before[i] ?? []))], said);
        Changed?.Invoke(this, outcome);
        return outcome;

        static byte[]? Bytes(string file)
        {
            try
            {
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>The files a package change can write: the project, and under central package management the nearest
    /// Directory.Packages.props, where dotnet add package puts the version.</summary>
    private static IReadOnlyList<string> PackageFiles(ProjectModel project)
    {
        List<string> files = [project.Path];
        for (string? at = Path.GetDirectoryName(project.Path); at is not null; at = Path.GetDirectoryName(at))
        {
            string props = Path.Combine(at, "Directory.Packages.props");
            if (File.Exists(props))
            {
                files.Add(PathNames.Canonical(props));
                break;
            }
        }
        return files;
    }
}
