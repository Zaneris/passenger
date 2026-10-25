using System.Text.Json.Nodes;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Run;

/// <summary>A folder's run configurations: those its projects generate, each with the person's changes put over it, then
/// the person's own (kept with the folder's state, their projects relative to the folder there), and the one chosen in the
/// toolbar, which Run, Debug and Profile launch. A UI-thread object of its <see cref="ProjectSession"/>.</summary>
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
        All = [.. RunConfiguration.Generate(projects, workspace?.Invoke(), SingleFileAppHosts).Select(Edited), .. own];
        Chosen = All.FirstOrDefault(c => string.Equals(c.Name, keep, StringComparison.OrdinalIgnoreCase)) ?? All.FirstOrDefault();
        Changed?.Invoke(this, EventArgs.Empty);
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
