using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Testing;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace.Gherkin;

namespace Passenger.Tests;

/// <summary>A feature's steps after a run (<see cref="ReqnrollProjects"/> with a feature of every outcome): each step's
/// outcome beside its line, the failure's words after the failing step and its stack a click away in the Unit Tests window, an
/// outline's example rows each marked by its own run and run alone from its mark, the outcomes following the text and gone
/// from a line once it changes. Runs tests through the window, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class BddStepRunTests : IClassFixture<RestoredFolders>, IDisposable
{
    private const string Outcomes =
        "Feature: Outcomes\n" +
        "\n" +
        "Scenario: Mixed\n" +
        "  Given the first number is 50\n" +
        "  And the second number is 70\n" +
        "  When the two numbers are added\n" +
        "  Then the result should be 121\n" +
        "  And nothing is ever said\n" +
        "\n" +
        "Scenario: Waiting\n" +
        "  Given the first number is 1\n" +
        "  When a step is still to write\n" +
        "  Then nothing is ever said\n" +
        "\n" +
        "Scenario Outline: Sums\n" +
        "  Given the first number is <a>\n" +
        "  And the second number is <b>\n" +
        "  When the two numbers are added\n" +
        "  Then the result should be <sum>\n" +
        "\n" +
        "Examples:\n" +
        "  | a | b | sum |\n" +
        "  | 1 | 2 | 3   |\n" +
        "  | 5 | 5 | 11  |\n";

    private const string Waiting =
        "namespace Calc.Specs.StepDefinitions;\n" +
        "\n" +
        "[Binding]\n" +
        "public sealed class WaitingSteps\n" +
        "{\n" +
        "    [When(\"a step is still to write\")]\n" +
        "    public void StillToWrite() => throw new PendingStepException();\n" +
        "}\n";

    private readonly string folder;

    public BddStepRunTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("bddsteps-", made =>
        {
            string solution = ReqnrollProjects.Write(made);
            TestFolders.Write(made, "Calc.Specs/Features/Outcomes.feature", Outcomes);
            TestFolders.Write(made, "Calc.Specs/StepDefinitions/WaitingSteps.cs", Waiting);
            return solution;
        }));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private string FeatureFile => Path.Combine(folder, "Calc.Specs", "Features", "Outcomes.feature");

    [AvaloniaFact]
    public void After_a_run_each_step_shows_its_outcome_the_failure_its_words_and_stack_and_an_example_row_runs_alone()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900);
        try
        {
            Assert.Null(window.OpenFile(FeatureFile));
            Ui.Settle();
            OpenDocument feature = window.EditorArea.Active!;
            Ui.WaitFor(() => feature.TestMarks.Any(m => m.Line == 22) && feature.TestMarks.Any(m => m.Line == 23),
                () => $"the example rows marked before a build: {Marks(feature)}");

            window.EditorArea.CodeEditor.GoTo(new TextPosition(0, 3));
            Ui.Run(window, AppActions.RunTests);
            Ui.WaitFor(() => !window.Tests.IsBusy && feature.StepMarks.Count >= 12,
                () => $"each step's outcome after the feature's run ({window.Status}): {Steps(feature)}", TimeSpan.FromMinutes(4));

            (int Line, StepOutcome Outcome)[] expected =
            [
                (3, StepOutcome.Passed), (4, StepOutcome.Passed), (5, StepOutcome.Passed), (6, StepOutcome.Failed), (7, StepOutcome.Skipped),
                (10, StepOutcome.Passed), (11, StepOutcome.Pending), (12, StepOutcome.Skipped),
                (15, StepOutcome.Passed), (16, StepOutcome.Passed), (17, StepOutcome.Passed), (18, StepOutcome.Failed),
            ];
            Assert.True(expected.All(e => feature.StepMarks.Any(m => m.Line == e.Line && m.Outcome == e.Outcome)),
                $"Each step is to show how it went on its own line; they show {Steps(feature)}");
            Assert.Contains(feature.Decorations.LineAnnotations, a => a.Line == 6 && a.Look == LineAnnotationLook.Error
                && a.Text.StartsWith("Assert.Equal() Failure", StringComparison.Ordinal));
            Assert.True(feature.TestMarks.Contains(new TestMark(22, TestMarkKind.Passed)) && feature.TestMarks.Contains(new TestMark(23, TestMarkKind.Failed)),
                $"Each example row is to show its own run, the first passed and the second failed: {Marks(feature)}");

            // The failure's stack is a click on its step away, in the Unit Tests window.
            ClickMark(window, feature, 6);
            Ui.WaitFor(() => window.BottomPanel.Tests.Selected is [{ } chosen] && chosen.Name.Contains("Mixed", StringComparison.Ordinal),
                () => $"the failing scenario chosen in the Unit Tests window: {string.Join(", ", window.BottomPanel.Tests.Selected.Select(n => n.Name))}");
            Ui.WaitFor(() => window.BottomPanel.Tests.Details.Document.Snapshot.ToString().Contains("ThenTheResultShouldBe", StringComparison.Ordinal),
                () => $"the failure's stack in the details: {window.BottomPanel.Tests.Details.Document.Snapshot}");

            // An example row runs alone from its mark.
            MenuItem[] items = MarkMenu(window, feature, 22);
            Assert.Contains("Debug 'Sums [1 | 2 | 3]'", items.Select(i => i.Header as string));
            Ui.Choose(window.TestMarkMenu, "Run 'Sums [1 | 2 | 3]'");
            Ui.WaitFor(() => !window.Tests.IsBusy && window.Tests.RunOutcomes.Count > 0, () => $"the row's run to end ({window.Status})", TimeSpan.FromMinutes(4));
            Assert.True(window.Tests.RunOutcomes is [{ State: TestState.Passed } only] && (only.DisplayName ?? only.Test.DisplayName).Contains("a: \"1\"", StringComparison.Ordinal),
                $"The row's run is to run that row alone: {string.Join("; ", window.Tests.RunOutcomes.Select(o => $"{o.DisplayName ?? o.Test.DisplayName} {o.State}"))}");

            // The outcomes follow the text, and a line changed loses its own.
            window.EditorArea.CodeEditor.GoTo(new TextPosition(1, 0));
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => feature.StepMarks.Any(m => m.Line == 7 && m.Outcome == StepOutcome.Failed) && !feature.StepMarks.Any(m => m.Line == 6 && m.Outcome == StepOutcome.Failed),
                () => $"the outcomes a line down: {Steps(feature)}");
            window.EditorArea.CodeEditor.GoTo(new TextPosition(7, feature.Text.Snapshot.GetLine(7).Length));
            Ui.Type(window, "X", "x");
            Ui.WaitFor(() => !feature.StepMarks.Any(m => m.Line == 7) && !feature.Decorations.LineAnnotations.Any(a => a.Line == 7),
                () => $"the changed step's outcome and words gone: {Steps(feature)}");
            Assert.Contains(feature.StepMarks, m => m.Line == 8 && m.Outcome == StepOutcome.Skipped);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Run Tests at the caret on an outline's example row runs that row alone, as its mark's menu does, its outcome
    /// on its line; at the caret on the outline's title, every row.</summary>
    [AvaloniaFact]
    public void Run_Tests_at_the_caret_on_an_example_row_runs_that_row_alone_and_on_the_outline_every_row()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900);
        try
        {
            Assert.Null(window.OpenFile(FeatureFile));
            Ui.Settle();
            OpenDocument feature = window.EditorArea.Active!;
            Ui.WaitFor(() => feature.TestMarks.Any(m => m.Line == 22) && feature.TestMarks.Any(m => m.Line == 23),
                () => $"the example rows marked before a build: {Marks(feature)}");

            window.EditorArea.CodeEditor.GoTo(new TextPosition(23, 4));
            Ui.Run(window, AppActions.RunTests);
            Ui.WaitFor(() => !window.Tests.IsBusy && window.Tests.RunOutcomes.Count > 0, () => $"the row's run to end ({window.Status})", TimeSpan.FromMinutes(4));
            Assert.True(window.Tests.RunOutcomes is [{ State: TestState.Failed } only] && (only.DisplayName ?? only.Test.DisplayName).Contains("a: \"5\"", StringComparison.Ordinal),
                $"Run Tests on the second example row is to run that row alone: {Outcomes(window)}");
            Ui.WaitFor(() => feature.TestMarks.Contains(new TestMark(23, TestMarkKind.Failed)), () => $"the second row's failure on its line: {Marks(feature)}");
            Assert.False(feature.TestMarks.Any(m => m.Line == 22 && m.Kind is TestMarkKind.Passed or TestMarkKind.Failed),
                $"Run Tests on the second example row ran the first too: {Marks(feature)}");

            window.EditorArea.CodeEditor.GoTo(new TextPosition(14, 3));
            Ui.Run(window, AppActions.RunTests);
            Ui.WaitFor(() => !window.Tests.IsBusy && window.Tests.RunOutcomes.Count >= 2, () => $"the outline's run to end ({window.Status}): {Outcomes(window)}", TimeSpan.FromMinutes(4));
            Assert.True(window.Tests.RunOutcomes.Count == 2 && feature.TestMarks.Contains(new TestMark(22, TestMarkKind.Passed)) && feature.TestMarks.Contains(new TestMark(23, TestMarkKind.Failed)),
                $"Run Tests on the outline's title is to run every row: {Outcomes(window)}; marks {Marks(feature)}");
        }
        finally
        {
            window.Close();
        }

        static string Outcomes(MainWindow window) => string.Join("; ", window.Tests.RunOutcomes.Select(o => $"{o.DisplayName ?? o.Test.DisplayName} {o.State}"));
    }

    private static string Steps(OpenDocument feature) => string.Join(", ", feature.StepMarks.Select(m => $"{m.Line} {m.Outcome}{(m.Failure is null ? "" : $" ({m.Failure})")}"));

    private static string Marks(OpenDocument feature) => string.Join(", ", feature.TestMarks.Select(m => $"{m.Line} {m.Kind}"));

    /// <summary>A click on the gutter's mark beside a line.</summary>
    private static void ClickMark(MainWindow window, OpenDocument document, int line)
    {
        CodeEditor shown = window.EditorArea.CodeEditor;
        double x = Enumerable.Range(0, 400).First(x => shown.IsOnGutterColumn(shown.Gutter!.Marks, new Point(x, 0))) + 3;
        Ui.Click(shown, new Point(x, shown.PointOf(document.Text.Snapshot.GetLineStart(line)).Y + (shown.LineHeight / 2)));
    }

    /// <summary>The menu a click on a line's run icon opens.</summary>
    private static MenuItem[] MarkMenu(MainWindow window, OpenDocument document, int line)
    {
        ClickMark(window, document, line);
        return [.. Assert.IsType<MenuFlyout>(window.TestMarkMenu).Items.OfType<MenuItem>()];
    }
}
