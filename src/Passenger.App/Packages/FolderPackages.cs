using System.Collections.Concurrent;
using Passenger.App.Editors;
using Passenger.App.MSBuild;
using Passenger.Core;
using Passenger.Packages;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Packages;

/// <summary>The folder's NuGet, whoever asks of it (the NuGet window, MSBuild files' code help, Migrations, an agent): the
/// package sources its nuget.config files give, read when first asked for and again after one changes, their feeds, the
/// audit's known vulnerabilities, and the one way a package goes in, moves or comes out of a project, through the .NET
/// command line, one change at a time, each one command of Undo. A session's own, ended with it; a UI-thread object.</summary>
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

    private (Task Until, TaskCompletionSource Reached)? holdNext;

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

    /// <summary>Raised after every package change begun, done, failed or refused as the open files would not save, with the
    /// files it wrote, before whoever asked for it is answered: the window takes them into its editors and reads the
    /// solution again.</summary>
    public event EventHandler<PackageOutcome>? Changed;

    /// <summary>Raised as nuget.config files the Sources tab wrote have landed, done or not: the window takes those open
    /// into its editors.</summary>
    public event EventHandler<IReadOnlyList<string>>? ConfigsWritten;

    /// <summary>Where a package change is recorded as one command of Undo, unless its caller records it; null records none.</summary>
    public CommandHistory? Commands { get; set; }

    /// <summary>Saves the open files and waits for the saves still landing before a change rewrites a project or a
    /// nuget.config, so what is rewritten, and what the restore then reads, holds their edits. Null once all are on disk,
    /// else why not, and nothing is changed.</summary>
    public Func<Working?, CancellationToken, Task<string?>>? SaveFirst { get; set; }

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

    /// <summary>The packages the projects reference: by package, each project's reference for each of its frameworks.</summary>
    public Dictionary<string, Dictionary<string, IReadOnlyList<InstalledVersion>>> Installed()
    {
        Dictionary<string, Dictionary<string, IReadOnlyList<InstalledVersion>>> installed = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectModel project in Projects)
        {
            foreach (IGrouping<string, InstalledVersion> package in References(project).GroupBy(r => r.Id, r => r.Version, StringComparer.OrdinalIgnoreCase))
            {
                if (!installed.TryGetValue(package.Key, out Dictionary<string, IReadOnlyList<InstalledVersion>>? byProject))
                {
                    installed[package.Key] = byProject = [];
                }
                byProject[project.Path] = [.. package];
            }
        }
        return installed;
    }

    /// <summary>The packages the projects reference (<see cref="Installed"/>), a version given as a range or floating
    /// taken as its project's restore resolved it, read off the window's thread from that project's project.assets.json.</summary>
    public async Task<Dictionary<string, Dictionary<string, IReadOnlyList<InstalledVersion>>>> InstalledAsync()
    {
        Dictionary<string, Dictionary<string, IReadOnlyList<InstalledVersion>>> installed = Installed();
        ProjectModel[] ranged = [.. Projects.Where(p => installed.Values.Any(byProject => byProject.TryGetValue(p.Path, out IReadOnlyList<InstalledVersion>? at)
            && at.Any(v => v.IsRanged)))];
        if (ranged.Length == 0)
        {
            return installed;
        }
        Dictionary<string, RestoredVersions> restored = await BackgroundWork.Run(() => ranged.ToDictionary(p => p.Path, Restored, PathNames.Comparer));
        foreach ((string id, Dictionary<string, IReadOnlyList<InstalledVersion>> byProject) in installed)
        {
            foreach ((string path, RestoredVersions versions) in restored)
            {
                if (byProject.TryGetValue(path, out IReadOnlyList<InstalledVersion>? at))
                {
                    byProject[path] = [.. at.Select(v => v.IsRanged ? v with { Resolved = versions.Of(v.Framework, id) } : v)];
                }
            }
        }
        return installed;
    }

    /// <summary>What a project's restore resolved: its project.assets.json, where its evaluation says, else in its obj
    /// folder or an artifacts folder above it; none before it is restored, or while its file does not read.</summary>
    private static RestoredVersions Restored(ProjectModel project)
    {
        if ((project.Property(Passenger.Host.CarriedProperties.ProjectAssetsFile) ?? Passenger.Xaml.Projects.ProjectInputs.FindAssets(project.Path)) is not { } assets)
        {
            return RestoredVersions.None;
        }
        try
        {
            using FileStream stream = new(assets, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return RestoredVersions.Read(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return RestoredVersions.None;
        }
    }

    /// <summary>A project's package references, each framework's as evaluated (but those the SDK adds of itself).</summary>
    private static IEnumerable<(string Id, InstalledVersion Version)> References(ProjectModel project) =>
        project.Evaluated is { Dependencies.Count: > 0 } evaluated
            ? evaluated.Dependencies.SelectMany(d => d.Packages.Where(p => !p.Implicit).Select(p => (p.Name, new InstalledVersion(d.Framework, p.Version))))
            : project.Packages.Select(p => (p.Name, new InstalledVersion(null, p.Version)));

    /// <summary>The version a change of a package in a project writes (<see cref="InstalledVersion.Written"/>): a pin kept a pin.</summary>
    private static string Written(ProjectModel project, string id, string version) =>
        InstalledVersion.Written(version, [.. References(project).Where(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Select(r => r.Version)]);

    /// <summary>Adds a package to a project at a version, or moves it to that version, a pin kept a pin: the one way a
    /// package goes in (<see cref="ChangeAsync"/>).</summary>
    public Task<PackageOutcome> InstallAsync(ProjectModel project, string id, string version, CommandHistory.Recording? within = null) =>
        ChangeAsync($"Installing {id} {version} in {project.Name}…", $"Install {id} {version} in {project.Name}", project, within,
            () => manager.InstallAsync(project.Path, id, Written(project, id, version)));

    /// <summary>Moves several packages of a project to their versions together, as one change (<see cref="ChangeAsync"/>):
    /// EF Core's packages with the provider that supports them, pins kept pins. One that fails is restored again once put back.</summary>
    public Task<PackageOutcome> InstallSetAsync(ProjectModel project, IReadOnlyList<(string Id, string Version)> packages, CommandHistory.Recording? within = null) =>
        ChangeAsync($"Moving {Moves(packages)} in {project.Name}…", $"Move {Moves(packages)} in {project.Name}", project, within,
            () => manager.InstallSetAsync(project.Path, [.. packages.Select(p => (p.Id, Written(project, p.Id, p.Version)))]), () => manager.RestoreAsync(project.Path));

    /// <summary>Takes a package out of a project.</summary>
    public Task<PackageOutcome> RemoveAsync(ProjectModel project, string id, CommandHistory.Recording? within = null) =>
        ChangeAsync($"Removing {id} from {project.Name}…", $"Remove {id} from {project.Name}", project, within, () => manager.RemoveAsync(project.Path, id));

    private static string Moves(IReadOnlyList<(string Id, string Version)> packages) => string.Join(", ", packages.Select(p => $"{p.Id} to {p.Version}"));

    /// <summary>Tells of nuget.config files the Sources tab wrote (<see cref="ConfigsWritten"/>), before their sources are
    /// read again.</summary>
    public void Wrote(IReadOnlyList<string> configs) => ConfigsWritten?.Invoke(this, configs);

    /// <summary>Ends a package change still under way, as the folder is left.</summary>
    public void Stop() => manager.Stop();

    /// <summary>The next package change waits until <paramref name="until"/> has ended before its command runs, as a slow
    /// restore keeps it, for a test of what goes on meanwhile: done once that change is waiting.</summary>
    public Task HoldNextChange(Task until)
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        holdNext = (until, reached);
        return reached.Task;
    }

    /// <summary>A package change, whoever asks for it: one at a time (another asked meanwhile is refused, saying what is under
    /// way), the open files saved first (refused, saying why, when one is not), shown as the window's work under <see
    /// cref="Changing"/>; its files (<see cref="PackageFiles"/>) recorded <paramref name="within"/> the caller's command, else
    /// as its own named <paramref name="name"/>. One that fails puts back what it wrote but a file saved meanwhile, then,
    /// unless stopped, runs <paramref name="again"/> (a restore of the project as it was). Its end is told (<see cref="Changed"/>).</summary>
    private async Task<PackageOutcome> ChangeAsync(string stage, string name, ProjectModel project, CommandHistory.Recording? within, Func<Task<bool>> change,
        Func<Task<bool>>? again = null)
    {
        if (IsBusy)
        {
            return new PackageOutcome(false, [], []) { Name = name, Refused = Work?.Of(Changing) is { } under ? ActionWork.Busy(under) : $"Already under way: {changing}" };
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
        CommandHistory.Recording? own = null;
        try
        {
            PackageOutcome outcome;
            try
            {
                if (SaveFirst is { } save && await save(working, CancellationToken.None) is { } unsaved)
                {
                    outcome = new PackageOutcome(false, [], []) { Name = name, Refused = unsaved };
                }
                else
                {
                    // Read once the open files are saved: what the save wrote is not the change's.
                    byte[]?[] before = [.. files.Select(Bytes)];
                    // The change writes through dotnet alone, so a save of one of its files from here is an edit of the person's.
                    ConcurrentDictionary<string, bool> saved = new(PathNames.Comparer);
                    Action<string, byte[]?, byte[]> saving = (path, _, _) =>
                    {
                        if (files.FirstOrDefault(f => PathNames.Same(f, path)) is { } file)
                        {
                            saved[file] = true;
                        }
                    };
                    FileText.Wrote += saving;
                    CommandHistory.Recording? recording = within ?? (own = Commands?.RecordWrites(name));
                    foreach (string file in files)
                    {
                        recording?.Watch(file);
                    }
                    if (holdNext is { } hold)
                    {
                        holdNext = null;
                        hold.Reached.TrySetResult();
                        await hold.Until;
                    }
                    bool done;
                    try
                    {
                        done = await change();
                    }
                    finally
                    {
                        FileText.Wrote -= saving;
                    }
                    // What the put back's restore says is the log's alone: the change's own words say why it failed.
                    manager.Line -= heard;
                    int[] wrote = Unlike(files, before);
                    int[] back = [.. wrote.Where(i => !saved.ContainsKey(files[i]))];
                    if (!done && back.Length > 0 && await PutBackAsync([.. back.Select(i => (files[i], before[i]))]) && again is not null && !manager.Stopped)
                    {
                        await again();
                    }
                    IReadOnlyList<string> written = [.. wrote.Select(i => files[i])];
                    IReadOnlyList<string> meanwhile = done ? [] : [.. wrote.Except(back).Select(i => files[i])];
                    if (meanwhile.Count > 0)
                    {
                        Line?.Invoke(this, $"{PackageOutcome.Named(meanwhile)} saved meanwhile, left as saved");
                    }
                    outcome = new(done, done ? written : [.. Unlike(files, before).Select(i => files[i])], said) { Name = name, Written = written, Meanwhile = meanwhile };
                }
            }
            finally
            {
                IsBusy = false;
                changing = null;
                manager.Line -= heard;
            }
            Changed?.Invoke(this, outcome);
            return outcome;
        }
        finally
        {
            // Ended once the window has taken the files into its editors, so the command holds their reload.
            own?.Dispose();
        }
    }

    /// <summary>Where the files' bytes are not those read before.</summary>
    private static int[] Unlike(IReadOnlyList<string> files, byte[]?[] before) =>
        [.. Enumerable.Range(0, files.Count).Where(i => !Same(Bytes(files[i]), before[i]))];

    private static bool Same(byte[]? now, byte[]? was) => now is null ? was is null : was is not null && now.AsSpan().SequenceEqual(was);

    /// <summary>A failed change's files written back as they were before it, one it made taken away, off the window's
    /// thread, its saves waited for: whether all landed, the log told which, or why one did not.</summary>
    private async Task<bool> PutBackAsync(IReadOnlyList<(string Path, byte[]? Bytes)> back)
    {
        string? refused = await BackgroundWork.Run(() =>
        {
            foreach ((string path, byte[]? bytes) in back)
            {
                try
                {
                    if (bytes is null)
                    {
                        File.Delete(path);
                    }
                    else
                    {
                        FileText.Restore(path, bytes);
                    }
                }
                catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
                {
                    return $"{Path.GetFileName(path)} could not be put back: {failed.Message}";
                }
            }
            return null;
        });
        if (refused is null && await EditorArea.LandedAsync(back.Select(b => b.Path), null) is [(string path, Exception unlanded), ..])
        {
            refused = $"{Path.GetFileName(path)} could not be put back: {unlanded.Message}";
        }
        Line?.Invoke(this, refused ?? $"The change failed: {PackageOutcome.Named(back.Select(b => b.Path).ToList())} put back as before");
        return refused is null;
    }

    private static byte[]? Bytes(string file)
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

    /// <summary>The files a package change can write: the project, its lock file (<see cref="ProjectModel.LockFile"/>),
    /// which its restore writes when it restores with one, and under central package management its
    /// Directory.Packages.props (<see cref="CentralPackages.PropsOf(ProjectModel)"/>), where dotnet add package puts the
    /// version.</summary>
    private static IReadOnlyList<string> PackageFiles(ProjectModel project) =>
        [project.Path, PathNames.Canonical(project.LockFile), .. CentralPackages.PropsOf(project) is { } props ? [props] : (string[])[]];
}
