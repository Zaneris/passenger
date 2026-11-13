using System.Text.Json.Nodes;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A folder's run configurations: those its projects generate, each with the person's changes put over it, then
/// its solution's multi-project launch profiles (read from the files beside it, <see cref="SolutionLaunch"/>, the files
/// their store), then the person's own (kept with the folder's state, their projects relative to the folder there), and the
/// one chosen in the toolbar, which Run, Debug and Profile launch. A UI-thread object of its <see cref="ProjectSession"/>.</summary>
public sealed class RunConfigurations(string? folder, Func<WorkspaceModel?>? workspace = null)
{
    private List<RunConfiguration> own = [];
    private StateDocument<RunConfigurationsState>? kept;

    /// <summary>The person's changes to generated configurations by their names, paths full (<see cref="ConfigurationEdit"/>);
    /// those whose configuration is not made now are kept as they are.</summary>
    private Dictionary<string, JsonObject> edits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The projects the list was last made from.</summary>
    private IReadOnlyList<ProjectModel> projects = [];

    /// <summary>Whether the one chosen was the person's choice (in the toolbar, Edit Configurations or the store), which
    /// stays chosen, rather than the first there was, which follows the projects as their evaluations land.</summary>
    private bool picked;
    private string? preferred;

    /// <summary>Every configuration: the generated ones, then the folder's own.</summary>
    public IReadOnlyList<RunConfiguration> All { get; private set; } = [];

    /// <summary>The person's own, in their order.</summary>
    public IReadOnlyList<RunConfiguration> Own => own;

    /// <summary>The solution's multi-project launch profiles, the team's file's then the person's, as last read.</summary>
    public IReadOnlyList<RunConfiguration> Profiles { get; private set; } = [];

    /// <summary>Why a file of the solution's launch profiles could not be read as it was last, each; none when each read.</summary>
    public IReadOnlyList<string> ProfileProblems { get; private set; } = [];

    /// <summary>The solution whose launch profiles are read beside it (a solution filter's own solution), or null with none open.</summary>
    public string? Solution => workspace?.Invoke()?.Solution?.Path;

    /// <summary>The configuration chosen in the toolbar.</summary>
    public RunConfiguration? Chosen { get; private set; }

    /// <summary>Single-file AppHosts discovered independently of MSBuild project evaluation.</summary>
    public IReadOnlyList<string> SingleFileAppHosts { get; private set; } = [];

    public void ConfigureAppHosts(IReadOnlyList<string> paths)
    {
        UiThread.Check();
        SingleFileAppHosts = paths;
        Configure(projects, picked ? preferred : null);
    }

    /// <summary>The list or the one chosen changed, for the toolbar's widget.</summary>
    public event EventHandler? Changed;

    /// <summary>Takes the folder's own, the changes to generated ones and the one chosen from the store, which keeps each
    /// change from now; the one chosen before stays chosen when the store names none.</summary>
    public void Keep(StateDocument<RunConfigurationsState> document, IReadOnlyList<ProjectModel> projects)
    {
        RunConfigurationsState read = document.Value;
        own = [.. read.Configurations.Select(FromStore)];
        edits = new(read.Edits.Where(e => e.Value is JsonObject).Select(e => KeyValuePair.Create(e.Key, ConfigurationEdit.Mapped((JsonObject)e.Value!, Full))),
            StringComparer.OrdinalIgnoreCase);
        kept = document;
        picked |= read.Selected is not null;
        preferred = read.Selected ?? preferred ?? Chosen?.Name;
        Configure(projects, preferred);
    }

    /// <summary>Makes the list again from the projects loaded now and the folder's own, the one the person chose kept while
    /// it is there.</summary>
    public void Configure(IReadOnlyList<ProjectModel> projects) => Configure(projects, picked ? preferred : null);

    /// <summary>Why a configuration cannot launch here among the projects loaded now, as the toolbar's menu greys it; null
    /// when it can.</summary>
    public string? WhyNotHere(RunConfiguration configuration) => configuration.WhyNotHere(projects);

    private void Configure(IReadOnlyList<ProjectModel> projects, string? keep)
    {
        this.projects = projects;
        IReadOnlyList<RunConfiguration> generated = [.. RunConfiguration.Generate(projects, workspace?.Invoke(), SingleFileAppHosts).Select(Edited)];
        (Profiles, ProfileProblems) = Solution is { } solution ? Read(solution, [.. generated.Select(g => g.Name), .. own.Select(o => o.Name)]) : ([], []);
        All = [.. generated, .. Profiles, .. own];
        // With none picked, an AppHost's before its services', which it runs as the person means.
        Chosen = All.FirstOrDefault(c => string.Equals(c.Name, keep, StringComparison.OrdinalIgnoreCase)) ?? All.FirstOrDefault(c => c.Aspire) ?? All.FirstOrDefault();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A solution's launch profiles as configurations, each named as its file names it unless another configuration
    /// is (<paramref name="taken"/>), which its file's name for it stays; with the problems of files that do not read.</summary>
    private static (IReadOnlyList<RunConfiguration> Profiles, IReadOnlyList<string> Problems) Read(string solution, IEnumerable<string> taken)
    {
        (IReadOnlyList<SolutionLaunchProfile> read, IReadOnlyList<string> problems) = SolutionLaunch.Read(solution);
        HashSet<string> names = new(taken, StringComparer.OrdinalIgnoreCase);
        List<RunConfiguration> profiles = [];
        foreach (SolutionLaunchProfile profile in read)
        {
            string name = RunConfiguration.UniqueName(profile.Name, names);
            names.Add(name);
            profiles.Add(OfProfile(profile) with { Name = name });
        }
        return (profiles, problems);
    }

    /// <summary>A launch profile as the configuration that starts its projects.</summary>
    public static RunConfiguration OfProfile(SolutionLaunchProfile profile) => new()
    {
        Name = profile.Name,
        Kind = RunKind.Projects,
        LaunchProfile = profile.Name,
        Starts = [.. profile.Projects.Select(p => new ProjectStart(p.Project, p.Action, p.DebugTarget ?? string.Empty) { Launch = p })],
        Shared = profile.Shared,
        Launch = profile,
    };

    /// <summary>A multi-project configuration as its file is to hold it: under its file's name for it unless renamed since it
    /// was read (<paramref name="shown"/>, the name it was listed by then), each project with what its file held of it, and
    /// its place in the file it was read from where it stays in that file.</summary>
    public static SolutionLaunchProfile ToProfile(RunConfiguration configuration, string? shown) => new(
        configuration.Launch is { } read && configuration.Name == shown ? read.Name : configuration.Name,
        [.. configuration.Starts.Select(s => new SolutionLaunchProject(s.Project, s.Action, s.LaunchProfile.Length > 0 ? s.LaunchProfile : null)
        {
            Read = s.Launch?.Read,
            ActionWritten = s.Launch?.ActionWritten,
        })],
        configuration.Shared)
    {
        Read = configuration.Launch?.Read,
        Index = configuration.Launch is { } was && was.Shared == configuration.Shared ? was.Index : -1,
    };

    /// <summary>The configuration a multi-project configuration starts one of its projects as: its launch profile's (the
    /// person's changes to it put over it), or for none named the first of the project's own that runs here, as dotnet run
    /// takes its first; null with why where there is none.</summary>
    public (RunConfiguration? Configuration, string? WhyNot) ProjectOf(ProjectStart start)
    {
        string name = Path.GetFileNameWithoutExtension(start.Project);
        if (projects.FirstOrDefault(p => p.Exists && PathNames.Same(p.Path, start.Project)) is not { } project)
        {
            return (null, $"{name} is not loaded");
        }
        List<RunConfiguration> made = [.. All.Where(c => c is { Generated: true, Kind: RunKind.Project, GodotEditor: false } && PathNames.Same(c.Project, project.Path))];
        if (start.LaunchProfile.Length == 0)
        {
            return made.FirstOrDefault(c => c.Unsupported is null) is { } first ? (first, null)
                : (null, project.IsRunnable ? $"none of {name}'s launch profiles runs here" : $"{name} is no executable project");
        }
        if (made.FirstOrDefault(c => c.LaunchProfile == start.LaunchProfile) is { } named)
        {
            return named.Unsupported is { } unsupported ? (null, $"its launch profile {start.LaunchProfile} cannot run here: {unsupported}") : (named, null);
        }
        LaunchProfile? profile = LaunchProfile.Read(project).FirstOrDefault(p => p.Name == start.LaunchProfile);
        if (profile is { Unsupported: null, RunsInContainer: false })
        {
            // A profile tied to a platform makes no configuration of its own (DeviceTargets.OwnProfiles), but runs as named.
            return (RunConfiguration.From(project, profile) with { Name = RunConfiguration.NameOf(project, profile.Name), Generated = true }, null);
        }
        return profile is not null
            ? (null, profile.RunsInContainer ? $"its launch profile {start.LaunchProfile} runs a container, which a configuration of its own runs"
                : $"its launch profile {start.LaunchProfile} cannot run here: {profile.Unsupported}")
            : (null, $"{name}'s {LaunchProfile.SettingsName(project.Path)} has no launch profile named {start.LaunchProfile}");
    }

    /// <summary>A generated configuration with the person's changes to it put over what its source makes.</summary>
    private RunConfiguration Edited(RunConfiguration generated) =>
        edits.TryGetValue(generated.Name, out JsonObject? edit) ? ConfigurationEdit.Over(generated, edit) : generated;

    /// <summary>Chooses a configuration by name, as the toolbar's menu does; false when there is none.</summary>
    public bool Select(string name)
    {
        if (All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } chosen)
        {
            return false;
        }
        Chosen = chosen;
        preferred = chosen.Name;
        picked = true;
        Changed?.Invoke(this, EventArgs.Empty);
        Write();
        return true;
    }

    /// <summary>Takes the folder's own as Edit Configurations leaves them, and the one chosen there.</summary>
    public void Set(IReadOnlyList<RunConfiguration> mine, string? chosen, IReadOnlyList<ProjectModel> projects) => Set(mine, [], chosen, projects);

    /// <summary>Takes the folder's own and the generated ones as Edit Configurations leaves them, each generated one's
    /// changes from its <see cref="RunConfiguration.Source"/> kept as its edit (none when it has no source: as made), and the
    /// one chosen there.</summary>
    public void Set(IReadOnlyList<RunConfiguration> mine, IReadOnlyList<RunConfiguration> generated, string? chosen, IReadOnlyList<ProjectModel> projects)
    {
        UiThread.Check();
        own = [.. mine.Select(c => c with { Generated = false, Unsupported = null, Origin = null, Source = null })];
        foreach (RunConfiguration each in generated)
        {
            Change(each.Name, each.Source is { } source ? ConfigurationEdit.Between(source, each) : null);
        }
        picked |= chosen is not null;
        preferred = chosen ?? preferred ?? Chosen?.Name;
        Configure(projects, preferred);
        Write();
    }

    /// <summary>Turns a configuration's Collect logs and traces on or off, kept as Edit Configurations keeps its box (a
    /// generated one's as its change, the folder's own in place), the choice of configuration left as it was; false where
    /// none has the name.</summary>
    public bool Collect(string name, bool on)
    {
        UiThread.Check();
        if (All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } found)
        {
            return false;
        }
        if (found.Telemetry != on)
        {
            // A generated one changed for the first time has no source yet: what it is now is what its project makes.
            RunConfiguration changed = found with { Telemetry = on, Source = found.Generated ? found.Source ?? found : null };
            Set(found.Generated ? own : [.. own.Select(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) ? changed : c)],
                found.Generated ? [changed] : [], null, projects);
        }
        return true;
    }

    /// <summary>The person's changes to a generated configuration as kept now (a copy), or null for none.</summary>
    public JsonObject? EditOf(string name) => edits.TryGetValue(name, out JsonObject? edit) ? (JsonObject)edit.DeepClone() : null;

    /// <summary>Puts a generated configuration's changes as they were, as Undo of writing them to a launch profile does.</summary>
    public void SetEdit(string name, JsonObject? edit)
    {
        UiThread.Check();
        Change(name, edit is null ? null : (JsonObject)edit.DeepClone());
        Configure(projects);
        Write();
    }

    /// <summary>A generated configuration made again from its source as the disk has it now (a launch profile just written),
    /// with what of <paramref name="edited"/> still differs from it kept as its change; null when it is no longer made.</summary>
    public RunConfiguration? Rebase(string name, RunConfiguration edited)
    {
        UiThread.Check();
        Configure(projects);
        if (All.FirstOrDefault(c => c.Generated && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } made)
        {
            return null;
        }
        Change(name, ConfigurationEdit.Between(made.Source ?? made, edited));
        Configure(projects);
        Write();
        return All.FirstOrDefault(c => c.Generated && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One of the folder's own given way to the configuration a launch profile just written for it makes (Save as
    /// Launch Profile): the own one gone, what of it the profile does not hold kept as the generated one's change, and the
    /// toolbar's choice of it the generated one's; null, the own one kept, when the profile makes none.</summary>
    public RunConfiguration? Share(string name, string project, string profile)
    {
        UiThread.Check();
        if (own.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) is not (>= 0 and var at))
        {
            return null;
        }
        RunConfiguration mine = own[at];
        own.RemoveAt(at);
        Configure(projects);
        if (All.FirstOrDefault(c => c.Generated && c.Kind == RunKind.Project && c.LaunchProfile == profile && PathNames.Same(c.Project, project)) is not { } made)
        {
            own.Insert(at, mine);
            Configure(projects);
            return null;
        }
        Change(made.Name, ConfigurationEdit.Between(made.Source ?? made, mine with { Name = made.Name }));
        if (string.Equals(preferred, name, StringComparison.OrdinalIgnoreCase))
        {
            preferred = made.Name;
        }
        Configure(projects, picked ? preferred : null);
        Write();
        return All.FirstOrDefault(c => c.Generated && string.Equals(c.Name, made.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Undo of <see cref="Share"/>: the folder's own back in its place, the generated one's change as it was before,
    /// and the own one chosen again if it was.</summary>
    public void Unshare(RunConfiguration mine, int at, string generated, JsonObject? edit, bool chosen)
    {
        UiThread.Check();
        own.Insert(Math.Clamp(at, 0, own.Count), mine);
        Change(generated, edit is null ? null : (JsonObject)edit.DeepClone());
        if (chosen)
        {
            preferred = mine.Name;
            picked = true;
        }
        Configure(projects, picked ? preferred : null);
        Write();
    }

    private void Change(string name, JsonObject? edit)
    {
        if (edit is null)
        {
            edits.Remove(name);
        }
        else
        {
            edits[name] = edit;
        }
    }

    private void Write() => kept?.Set(new RunConfigurationsState
    {
        Configurations = [.. own.Select(ForStore)],
        Edits = new(edits.Select(e => KeyValuePair.Create(e.Key, (JsonNode?)ConfigurationEdit.Mapped(e.Value, Relative))), StringComparer.OrdinalIgnoreCase),
        Selected = preferred ?? Chosen?.Name,
    });

    /// <summary>A configuration as the store keeps it: its project and every file it names (compose files, env files, a
    /// Dockerfile, a context, the projects chosen for services) relative to the folder, so a hand edit reads plainly.</summary>
    private RunConfiguration ForStore(RunConfiguration configuration) => folder is null ? configuration : configuration with
    {
        Project = Relative(configuration.Project),
        LinkedAppHosts = [.. configuration.LinkedAppHosts.Select(Relative)],
        ComposeFiles = [.. configuration.ComposeFiles.Select(Relative)],
        EnvFiles = [.. configuration.EnvFiles.Select(Relative)],
        Dockerfile = Relative(configuration.Dockerfile),
        Context = Relative(configuration.Context),
        ServiceProjects = configuration.ServiceProjects.ToDictionary(p => p.Key, p => Relative(p.Value)),
    };

    private RunConfiguration FromStore(RunConfiguration configuration) => folder is null ? configuration : configuration with
    {
        Project = Full(configuration.Project),
        LinkedAppHosts = [.. configuration.LinkedAppHosts.Select(Full)],
        ComposeFiles = [.. configuration.ComposeFiles.Select(Full)],
        EnvFiles = [.. configuration.EnvFiles.Select(Full)],
        Dockerfile = Full(configuration.Dockerfile),
        Context = Full(configuration.Context),
        ServiceProjects = configuration.ServiceProjects.ToDictionary(p => p.Key, p => Full(p.Value)),
    };

    private string Relative(string path) => folder is not null && path.Length > 0 && (PathNames.IsUnder(path, folder) || PathNames.Same(path, folder))
        ? Path.GetRelativePath(folder, path).Replace('\\', '/')
        : path;

    private string Full(string path) => folder is null || path.Length == 0 ? path : PathNames.Canonical(Path.Combine(folder, path));
}
