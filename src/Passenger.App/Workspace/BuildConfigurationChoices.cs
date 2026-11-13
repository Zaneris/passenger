using Passenger.App.State;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.App.Workspace;

/// <summary>The build configuration and platform chosen for the folder, one choice for every build, run, test, publish and
/// evaluation of it: of those its solution declares (as MSBuild reads the solution), or with none, those its projects
/// declare (<see cref="BuildConfigurations.Declared"/>). The one MSBuild takes given none is chosen until another is, and a
/// choice is kept with the folder (<see cref="WorkspaceState.Build"/>), with what the solution maps each project to under
/// it, so the folder opens building as it last did. A UI-thread object.</summary>
public sealed class BuildConfigurationChoices
{
    private StateDocument<WorkspaceState>? kept;
    private BuildChoice? choice;
    private int reads;

    /// <summary>The choice changed: what every build is given from now on (<see cref="Choice"/>).</summary>
    public event EventHandler? Changed;

    /// <summary>The choices offered changed, as the solution or the projects were read again.</summary>
    public event EventHandler? Listed;

    /// <summary>The configurations offered, in the solution's or the projects' order; none until read.</summary>
    public IReadOnlyList<BuildConfiguration> Offered { get; private set; } = [];

    /// <summary>The one MSBuild takes given none; null until read.</summary>
    public BuildConfiguration? Default { get; private set; }

    /// <summary>Whether those offered are the solution's configurations, else its projects'.</summary>
    public bool OfSolution { get; private set; }

    /// <summary>Why the solution's configurations could not be read, the last time they were asked; null when they were.</summary>
    public string? Problem { get; private set; }

    /// <summary>What builds are given: null for the one MSBuild takes given none.</summary>
    public BuildChoice? Choice => choice;

    /// <summary>The one chosen: <see cref="Choice"/>'s, else the default.</summary>
    public BuildConfiguration? Chosen => choice?.Chosen ?? Default;

    /// <summary>The choice as the folder keeps it: null for the one MSBuild takes given none.</summary>
    public KeptBuildChoice? Kept => choice is { } chosen
        ? new KeptBuildChoice(chosen.Chosen.Configuration, chosen.Chosen.Platform, chosen.OfSolution) { Mapped = [.. chosen.Mapped] }
        : null;

    /// <summary>The choice the folder kept, taken as it opens, before anything is read.</summary>
    public void Take(WorkspaceState state)
    {
        UiThread.Check();
        choice = state.Build is { Configuration.Length: > 0, Platform.Length: > 0 } build
            ? new BuildChoice(new BuildConfiguration(build.Configuration, build.Platform), build.OfSolution) { Mapped = build.Mapped }
            : null;
    }

    /// <summary>The folder's document from now on, which a choice is kept in.</summary>
    public void Keep(StateDocument<WorkspaceState> document)
    {
        UiThread.Check();
        kept = document;
    }

    /// <summary>Reads what the workspace offers again (the solution's configurations through the folder's project host, else
    /// the projects' as evaluated), and the chosen one's mapping: a choice no longer offered falls back to the default, but a
    /// project's only once every project's evaluation is in (<paramref name="evaluated"/>).</summary>
    public async Task ReadAsync(string folder, WorkspaceModel workspace, bool evaluated)
    {
        UiThread.Check();
        int mine = ++reads;
        BuildConfiguration? wanted = Chosen;
        if (workspace.Solution is not { } solution)
        {
            IReadOnlyList<BuildConfiguration> declared = BuildConfigurations.Declared(workspace.Projects.Where(p => p.IsEvaluated)
                .Select(p => (p.Property(CarriedProperties.Configurations), p.Property(CarriedProperties.Platforms))));
            Take(declared, BuildConfigurations.DefaultOf(declared), ofSolution: false, problem: null);
            if (evaluated || wanted is null || declared.Any(d => d.Is(wanted)))
            {
                Apply(BuildConfigurations.Chosen(Offered, Default, wanted?.Key), []);
            }
            return;
        }
        (SolutionBuild? read, string? problem) = await AskAsync(folder, solution.Path, wanted);
        if (mine != reads)
        {
            return;
        }
        if (read is null)
        {
            Problem = problem;
            Listed?.Invoke(this, EventArgs.Empty);
            return;
        }
        Take(read.Declared.Select(c => new BuildConfiguration(c.Configuration, c.Platform)).ToList(),
            read.Default is { } fallback ? new BuildConfiguration(fallback.Configuration, fallback.Platform) : null, ofSolution: true, problem: null);
        BuildConfiguration? chosen = BuildConfigurations.Chosen(Offered, Default, wanted?.Key);
        if (chosen is not null && read.Taken is { } taken && !chosen.Is(new BuildConfiguration(taken.Configuration, taken.Platform)))
        {
            // The one kept is gone: the default's mapping is the one to give.
            (read, _) = await AskAsync(folder, solution.Path, chosen);
            if (mine != reads || read is null)
            {
                return;
            }
        }
        Apply(chosen, read.Mapped);
    }

    /// <summary>Chooses one of those offered, its mapping asked of the solution first: nothing changes where it is chosen
    /// already; the folder keeps it. Why not, where the solution could not say.</summary>
    public async Task<string?> ChooseAsync(string folder, WorkspaceModel workspace, BuildConfiguration configuration)
    {
        UiThread.Check();
        if (Offered.FirstOrDefault(o => o.Is(configuration)) is not { } offered)
        {
            return $"{configuration.Key} is not one of the configurations offered";
        }
        if (Chosen is { } now && now.Is(offered))
        {
            return null;
        }
        int mine = ++reads;
        IReadOnlyList<SolutionBuilt> mapped = [];
        if (OfSolution && workspace.Solution is { } solution)
        {
            (SolutionBuild? read, string? problem) = await AskAsync(folder, solution.Path, offered);
            if (read is null)
            {
                return problem;
            }
            if (mine != reads)
            {
                return null;
            }
            mapped = read.Mapped;
        }
        Apply(offered, mapped);
        return null;
    }

    private static async Task<(SolutionBuild? Read, string? Problem)> AskAsync(string folder, string solution, BuildConfiguration? chosen)
    {
        try
        {
            SolutionBuild read = await BackgroundWork.Run(async () => await (await ProjectHosts.ForAsync(folder)).SolutionBuildAsync(solution, CancellationToken.None,
                chosen is null ? null : new SolutionConfiguration(chosen.Configuration, chosen.Platform)));
            return read.Problem is { } problem ? (null, $"MSBuild could not read {Path.GetFileName(solution)}: {problem}") : (read, null);
        }
        catch (ProjectHostFailed failed)
        {
            return (null, $"MSBuild could not read {Path.GetFileName(solution)}: {failed.Reason}");
        }
    }

    private void Take(IReadOnlyList<BuildConfiguration> offered, BuildConfiguration? fallback, bool ofSolution, string? problem)
    {
        bool same = offered.Count == Offered.Count && offered.Zip(Offered).All(p => p.First.Is(p.Second) && p.First.Key == p.Second.Key)
            && (fallback?.Key == Default?.Key) && ofSolution == OfSolution;
        Offered = offered;
        Default = fallback;
        OfSolution = ofSolution;
        Problem = problem;
        if (!same)
        {
            Listed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The one chosen and its mapping given from now on, kept with the folder: nothing for the default.</summary>
    private void Apply(BuildConfiguration? chosen, IReadOnlyList<SolutionBuilt> mapped)
    {
        BuildChoice? next = chosen is null || Default is null || chosen.Is(Default) ? null : new BuildChoice(chosen, OfSolution) { Mapped = mapped };
        bool same = next is null ? choice is null
            : choice is not null && choice.Chosen.Key == next.Chosen.Key && choice.OfSolution == next.OfSolution
              && choice.Mapped.SequenceEqual(next.Mapped);
        choice = next;
        if (kept is { } document)
        {
            KeptBuildChoice? keeping = Kept;
            if (document.Value.Build != keeping && !(document.Value.Build is { } was && keeping is not null && was.Same(keeping)))
            {
                document.Set(document.Value with { Build = keeping });
            }
        }
        if (!same)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>A folder's build configuration chosen, as kept: the configuration and platform, whether they are its solution's,
/// and what the solution mapped each project to under them as last read.</summary>
public sealed record KeptBuildChoice(string Configuration, string Platform, bool OfSolution)
{
    public List<SolutionBuilt> Mapped { get; init; } = [];

    /// <summary>Whether another is the same choice with the same mapping.</summary>
    public bool Same(KeptBuildChoice other) =>
        Configuration == other.Configuration && Platform == other.Platform && OfSolution == other.OfSolution && Mapped.SequenceEqual(other.Mapped);
}
