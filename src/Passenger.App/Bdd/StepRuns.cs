using Passenger.App.Testing;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Bdd;

/// <summary>A feature's steps as a run's scenarios went: the text they were read from, and each step's result with the test
/// it ran in (a background's and an outline's steps once for each test that took them).</summary>
public sealed record FeatureRun(string Path, string Project, TextSnapshot Text, IReadOnlyList<(StepResult Result, TestOutcome Test)> Steps);

/// <summary>How a feature's step went in the last run that took it, on its line: the step's result, the test it ran in (the
/// scenario's or its example row's), and every other result the run gave the line (a background's step runs in each scenario,
/// an outline's in each row), the worst of which it shows. Its line is carried by the file's text while it is open.</summary>
public sealed class StepRun(string path, int line, string lineText, StepResult result, TestOutcome test)
{
    public string Path { get; } = path;

    /// <summary>The line (from zero), as its anchor last read it.</summary>
    public int Line => At.Line;

    /// <summary>The line's text as the run found it, which a file opened later is held to.</summary>
    public string LineText { get; } = lineText;

    public StepResult Result { get; private set; } = result;

    /// <summary>The outcome of the test the shown result ran in, its stack the failure's.</summary>
    public TestOutcome Test { get; private set; } = test;

    internal AnchoredLine At { get; } = new(line);

    /// <summary>Takes another result of the same run for the line, shown where it is worse.</summary>
    internal void Add(StepResult other, TestOutcome ran)
    {
        if (Rank(other.Outcome) > Rank(Result.Outcome))
        {
            (Result, Test) = (other, ran);
        }
    }

    /// <summary>How bad an outcome is beside another on one line.</summary>
    private static int Rank(StepOutcome outcome) => outcome switch
    {
        StepOutcome.Passed => 0,
        StepOutcome.Skipped => 1,
        StepOutcome.Pending => 2,
        StepOutcome.Undefined => 3,
        StepOutcome.Ambiguous => 4,
        _ => 5,
    };
}

/// <summary>How each step of the features last run went, on its line among the open and closed .feature files, for as long as
/// that line stands: a step's result comes off when an edit changes its line, a later run's result for the line takes its
/// place, and a file opened after its line changed on disk shows none there. A UI-thread object of the session.</summary>
public sealed class StepRuns
{
    private readonly List<StepRun> steps = [];

    /// <summary>A file's steps changed: came, moved or went.</summary>
    public event EventHandler<string>? Changed;

    public IReadOnlyList<StepRun> In(string path) => [.. steps.Where(s => PathNames.Same(s.Path, path))];

    /// <summary>A run's results for one feature, read from its text as the run found it: each line's results taken together,
    /// the worst shown, in place of any an earlier run left there.</summary>
    public void Take(string path, IReadOnlyList<(StepResult Result, TestOutcome Test)> results, TextSnapshot text, TextDocument? open)
    {
        UiThread.Check();
        Dictionary<int, StepRun> lines = [];
        foreach ((StepResult result, TestOutcome test) in results)
        {
            if (result.Line >= text.LineCount)
            {
                continue;
            }
            if (lines.TryGetValue(result.Line, out StepRun? run))
            {
                run.Add(result, test);
                continue;
            }
            LineSpan span = text.GetLine(result.Line);
            lines[result.Line] = new StepRun(path, result.Line, text.GetText(span.Start, span.Length), result, test);
        }
        if (lines.Count == 0)
        {
            return;
        }
        foreach (StepRun replaced in steps.Where(s => PathNames.Same(s.Path, path) && lines.ContainsKey(s.Line)).ToList())
        {
            replaced.At.Detach();
            steps.Remove(replaced);
        }
        foreach (StepRun run in lines.Values)
        {
            // Read from the text as it stood: an edit made since is followed once the anchor is in.
            if (open is not null && ReferenceEquals(open.Snapshot, text))
            {
                run.At.Attach(open);
            }
            steps.Add(run);
        }
        if (open is not null && !ReferenceEquals(open.Snapshot, text))
        {
            Attach(path, open);
        }
        Changed?.Invoke(this, path);
    }

    /// <summary>A file opened: its steps carried by its text from now on, those whose line no longer reads as it did let go.</summary>
    public void Attach(string path, TextDocument text)
    {
        bool went = false;
        foreach (StepRun step in In(path))
        {
            if (!AnchoredLine.Reads(text.Snapshot, step.Line, step.LineText))
            {
                step.At.Detach();
                steps.Remove(step);
                went = true;
                continue;
            }
            step.At.Attach(text);
        }
        if (went)
        {
            Changed?.Invoke(this, path);
        }
    }

    /// <summary>A file closed: its steps stay where they were last read.</summary>
    public void Detach(string path)
    {
        foreach (StepRun step in In(path))
        {
            step.At.Detach();
        }
    }

    /// <summary>An edit of an open file: the steps on a line whose text it changed come off, the rest follow their lines.
    /// A file with none costs a look at each step's path, and a line still read is compared where it stands.</summary>
    public void Edited(string path, DocumentChangedEventArgs change)
    {
        bool moved = false;
        for (int i = steps.Count - 1; i >= 0; i--)
        {
            StepRun step = steps[i];
            if (!PathNames.Same(step.Path, path))
            {
                continue;
            }
            bool followed = step.At.Follow();
            if (!AnchoredLine.Reads(change.After, step.Line, step.LineText))
            {
                steps.RemoveAt(i);
                step.At.Detach();
                moved = true;
                continue;
            }
            moved |= followed;
        }
        if (moved)
        {
            Changed?.Invoke(this, path);
        }
    }
}
