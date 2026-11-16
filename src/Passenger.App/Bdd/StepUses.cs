using Passenger.Core;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Bdd;

/// <summary>The step definitions the feature steps of a folder's Reqnroll and SpecFlow projects bind, by feature file, for
/// the Step Library's counts: every feature read and matched again as the bindings are read again, and one feature alone as
/// its reading changes (an open one's as it lands, another's as it changes on disk). Of the window's thread; reading is off
/// it.</summary>
internal sealed class StepUses(BddSource bdd)
{
    private Dictionary<string, IReadOnlyList<StepBinding>> byFeature = new(PathNames.Comparer);

    /// <summary>When each feature was last taken alone, by <see cref="taken"/>, which a whole count begun before keeps.</summary>
    private readonly Dictionary<string, int> takenAt = new(PathNames.Comparer);
    private int taken;
    private int counting;

    /// <summary>The read of the bindings (<see cref="StepCatalog.Version"/>) the last whole count matched against; -1 before one.</summary>
    public int CountedAs { get; private set; } = -1;

    /// <summary>Every definition each feature's steps bind, a step's once for each definition it binds.</summary>
    public IEnumerable<StepBinding> Bound => byFeature.Values.SelectMany(b => b);

    /// <summary>Raised on the window's thread as a count lands.</summary>
    public event EventHandler? Changed;

    /// <summary>Every feature of the projects read and matched against the bindings as they stand; a later count, or a read of
    /// the bindings meanwhile, leaves this one's answer unused.</summary>
    public async Task CountAllAsync()
    {
        UiThread.Check();
        StepCatalog catalog = bdd.Session.Steps;
        int version = catalog.Version;
        int asked = ++counting;
        int since = taken;
        IReadOnlyList<(string Path, FeatureAnalysis Analysis)> features = await bdd.FeaturesAsync(catalog.Projects);
        if (asked != counting || catalog.Version != version || bdd.Session.Steps != catalog)
        {
            return;
        }
        Dictionary<string, IReadOnlyList<StepBinding>> read = new(PathNames.Comparer);
        foreach ((string path, FeatureAnalysis analysis) in features)
        {
            read[path] = StepLibrary.BoundBy(analysis.Matches.Values);
        }
        foreach ((string path, int at) in takenAt.Where(t => t.Value > since))
        {
            if (byFeature.TryGetValue(path, out IReadOnlyList<StepBinding>? newer))
            {
                read[path] = newer;
            }
            else
            {
                read.Remove(path);
            }
        }
        byFeature = read;
        CountedAs = version;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>An open feature's reading as it landed: taken where it is of the bindings the last count matched against.</summary>
    public void Took(string path, FeatureAnalysis analysis)
    {
        UiThread.Check();
        if (CountedAs >= 0 && analysis.StepsRead == CountedAs)
        {
            Take(path, analysis.Steps is null ? null : StepLibrary.BoundBy(analysis.Matches.Values));
        }
    }

    /// <summary>Feature files changed on disk: each read again (an open one as the editor holds it), one gone dropped.</summary>
    public async Task ChangedOnDiskAsync(IReadOnlyList<string> paths)
    {
        UiThread.Check();
        int version = bdd.Session.Steps.Version;
        foreach (string path in paths)
        {
            bool exists = await BackgroundWork.Run(() => File.Exists(path));
            FeatureAnalysis? analysis = exists ? await bdd.AnalysisOfAsync(path) : null;
            if (CountedAs < 0 || bdd.Session.Steps.Version != version)
            {
                return;
            }
            Take(path, analysis?.Steps is null ? null : StepLibrary.BoundBy(analysis.Matches.Values));
        }
    }

    private void Take(string path, IReadOnlyList<StepBinding>? bound)
    {
        if (bound is null)
        {
            byFeature.Remove(path);
        }
        else
        {
            byFeature[path] = bound;
        }
        takenAt[path] = ++taken;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
