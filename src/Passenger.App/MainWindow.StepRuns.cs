using Passenger.App.Bdd;
using Passenger.App.Testing;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace.Gherkin;

namespace Passenger.App;

/// <summary>How each step of a feature went in its last run (<see cref="StepRuns"/>): read after each run from the trace its
/// scenarios' tests wrote, each scenario found at its feature line by its code-behind's <c>#line</c> and an outline's row by
/// the arguments its test ran with; shown on the steps' lines of each open file, following its text as it is edited, a press on
/// one choosing the test it ran in in the Unit Tests window, its failure's message and stack there.</summary>
public partial class MainWindow
{
    private bool stepRunsWired;

    /// <summary>A session's step outcomes in the open files, while it is the window's: each run's read once it has ended.</summary>
    private void BindStepRuns(ProjectSession next)
    {
        next.Tests.Ran += (_, summary) =>
        {
            if (session == next && !next.IsEnded)
            {
                IdeErrors.Observe(ReadStepRunsAsync(next, summary.Outcomes));
            }
        };
        next.StepRuns.Changed += (_, path) =>
        {
            if (session == next)
            {
                foreach (OpenDocument document in Editors.Documents.Where(d => PathNames.Same(d.Path, path)))
                {
                    document.StepMarks = StepMarksOf(next, path);
                }
            }
        };
        foreach (OpenDocument document in Editors.Documents.Where(d => d.HoldsText))
        {
            next.StepRuns.Attach(document.Path, document.Text);
            document.StepMarks = StepMarksOf(next, document.Path);
        }
        if (!stepRunsWired)
        {
            stepRunsWired = true;
            WireStepRuns();
        }
    }

    /// <summary>The open files' step outcomes follow whichever session is the window's: wired once, in a method of its own, so
    /// the handlers it leaves on the window's editors hold no session.</summary>
    private void WireStepRuns()
    {
        Editors.Opened += (_, document) =>
        {
            if (!document.HoldsText)
            {
                return;
            }
            session.StepRuns.Attach(document.Path, document.Text);
            document.StepMarks = StepMarksOf(session, document.Path);
            document.Text.Changed += (_, change) => session.StepRuns.Edited(document.Path, change);
        };
        Editors.Closed += (_, document) => session.StepRuns.Detach(document.Path);
    }

    private static IReadOnlyList<StepMark> StepMarksOf(ProjectSession of, string path) =>
        [.. of.StepRuns.In(path).Select(s => new StepMark(s.Line, s.Result.Outcome,
            s.Result.Outcome is StepOutcome.Failed or StepOutcome.Ambiguous ? FirstLine(s.Result.Message ?? s.Test.Message) : null))];

    private static string? FirstLine(string? said) =>
        said?.Trim() is { Length: > 0 } words ? words.Split('\n', 2)[0].TrimEnd('\r') : null;

    /// <summary>A run's scenarios' steps, taken by the session feature by feature.</summary>
    private async Task ReadStepRunsAsync(ProjectSession ran, IReadOnlyList<TestOutcome> outcomes)
    {
        foreach (FeatureRun feature in await FeatureRunsAsync(ran, outcomes))
        {
            if (session != ran || ran.IsEnded)
            {
                return;
            }
            // A file opened since is held to the text the results were read from, line by line.
            TextDocument? now = Editors.Find(feature.Path) is { HoldsText: true } opened ? opened.Text : null;
            ran.StepRuns.Take(feature.Path, feature.Steps, feature.Text, now);
        }
    }

    /// <summary>The features a run's scenarios are of, each read off the window's thread from its text as it stands (an open
    /// file's, else the disk's), with each step's result and the test it ran in.</summary>
    internal async Task<IReadOnlyList<FeatureRun>> FeatureRunsAsync(ProjectSession ran, IReadOnlyList<TestOutcome> outcomes)
    {
        Dictionary<string, List<(TestOutcome Outcome, int Line)>> features = new(PathNames.Comparer);
        foreach (TestOutcome outcome in outcomes)
        {
            if (outcome.Output is null || outcome.Earlier is not null || ran.Tests.Tree.Node(outcome.Test) is not { } node)
            {
                continue;
            }
            TestNode method = node.Kind == TestNodeKind.Row ? node.Parent! : node;
            if (method.Places.FirstOrDefault(p => FeatureChecks.IsFeature(p.Path)) is ({ } path, > 0) place)
            {
                if (!features.TryGetValue(path, out List<(TestOutcome, int)>? its))
                {
                    features[path] = its = [];
                }
                its.Add((outcome, place.Line));
            }
        }
        List<FeatureRun> runs = [];
        foreach ((string path, List<(TestOutcome Outcome, int Line)> scenarios) in features)
        {
            TextSnapshot? shown = Editors.Find(path) is { HoldsText: true } document ? document.Text.Snapshot : null;
            string project = scenarios[0].Outcome.Test.Project;
            FeatureRun? read = await BackgroundWork.Run(() =>
                (shown ?? Passenger.App.Editors.EditorArea.Read(path)) is { } text ? new FeatureRun(path, project, text, StepResultsOf(path, text, Path.GetDirectoryName(project), scenarios)) : null);
            if (read is not null)
            {
                runs.Add(read);
            }
        }
        return runs;
    }

    /// <summary>Each step's result of a feature's scenarios as their tests' output says, the scenario at the line its
    /// code-behind gives it (from one, as the compiler counts), an outline's row by the arguments its test ran with.</summary>
    private static List<(StepResult Result, TestOutcome Test)> StepResultsOf(string path, TextSnapshot text, string? projectFolder, IReadOnlyList<(TestOutcome Outcome, int Line)> ran)
    {
        FeatureReading reading = FeatureReading.Read(text.ToString(), FeatureLanguage.Of(projectFolder));
        IReadOnlyList<ExampleRow> rows = ExampleRows.Of(reading);
        Passenger.Lsp.ServerLines? compiled = CSharp.ServerPlaces.Compiled(path, text);
        List<(StepResult Result, TestOutcome Test)> results = [];
        foreach ((TestOutcome outcome, int line) in ran)
        {
            int at = (compiled is null ? line : TestGutter.EditorLine(compiled, line)) - 1;
            if (reading.Scenarios.FirstOrDefault(s => s.Line == at && s.Kind != ScenarioKind.Background) is not { } scenario)
            {
                continue;
            }
            IReadOnlyDictionary<string, string>? values = null;
            if (scenario.Kind == ScenarioKind.Outline)
            {
                if (ExampleRows.RowOf(rows, scenario, outcome.DisplayName ?? outcome.Test.DisplayName) is not { } row)
                {
                    continue;
                }
                values = row.Values;
            }
            foreach (StepResult result in StepTrace.Read(outcome.Output!, StepTrace.StepsOf(reading, scenario, values), outcome.State == TestState.Passed))
            {
                results.Add((result, outcome));
            }
        }
        return results;
    }

    /// <summary>A press on a step's outcome: the test it ran in chosen in the Unit Tests window, which shows its failure's
    /// message, stack and output; false where the line has none.</summary>
    private bool ShowStepRun(OpenDocument document, int line)
    {
        if (session.StepRuns.In(document.Path).FirstOrDefault(s => s.Line == line) is not { } step || session.Tests.Tree.Node(step.Test.Test) is not { } node)
        {
            return false;
        }
        TestNode chosen = node.Kind != TestNodeKind.Row && step.Test.Row is { } row && node.Children.FirstOrDefault(c => c.Kind == TestNodeKind.Row && c.Name == row) is { } made ? made : node;
        ShowToolWindow(ToolWindowIds.Tests);
        Bottom.Tests.Choose(chosen);
        return true;
    }
}
