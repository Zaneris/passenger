using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Testing;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Reqnroll feature's scenarios run and debugged from the feature (<see cref="ReqnrollProjects"/>): each scenario
/// and example row marked in the gutter before anything is built, Run Tests at the caret building the project and running the scenario's
/// test, its mark taking the result; a breakpoint on a step's line stopping there, Step Into going into the step's
/// definition and Step Over from a step to the next, an outline's placeholder hovered showing its example's value, and a
/// Scenario with examples bound and run once for each row. Debugged through the window, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class BddRunTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    /// <summary>Where each step of a flow takes its own folder from.</summary>
    private readonly RestoredFolders folders;

    public BddRunTests(RestoredFolders folders)
    {
        this.folders = folders;
        folder = PathNames.Canonical(folders.Take("bddrun-", made => ReqnrollProjects.Write(made)));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private string FeatureFile => Path.Combine(folder, "Calc.Specs", "Features", "Calculator.feature");

    private string StepsFile => Path.Combine(folder, "Calc.Specs", "StepDefinitions", "CalculatorStepDefinitions.cs");

    private string Specs => Path.Combine(folder, "Calc.Specs", "Calc.Specs.csproj");

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900);
        Assert.Null(window.OpenFile(FeatureFile));
        Ui.Settle();
        return window;
    }

    /// <summary>A scenario is marked before a build and Run Tests at its caret runs it and marks its result; a
    /// scenario with examples binds its steps for each row and its run mark runs a row for each example; a breakpoint
    /// on a step stops there, Step Into goes into its definition and Step Over to the next step; after a run each step
    /// shows its outcome; a code-behind under obj still finds its test.</summary>
    [AvaloniaFact]
    public void A_scenario_runs_and_debugs_from_its_caret_each_example_a_row_and_each_step_a_place_to_stop()
    {
        Flow.StepApart<BddRunTests>("a scenario is marked before a build and run tests at its caret runs it and marks its result", () => new BddRunTests(folders), t =>
        {
            t.A_scenario_is_marked_before_a_build_and_run_tests_at_its_caret_runs_it_and_marks_its_result();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<BddRunTests>("a scenario with examples binds its steps for each row and its run mark runs a row for each example", () => new BddRunTests(folders), t =>
        {
            t.A_scenario_with_examples_binds_its_steps_for_each_row_and_its_run_mark_runs_a_row_for_each_example();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<BddRunTests>("a breakpoint on a step stops there step into goes into its definition and step over goes to the next step", () => new BddRunTests(folders), t =>
        {
            t.A_breakpoint_on_a_step_stops_there_step_into_goes_into_its_definition_and_step_over_goes_to_the_next_step();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<BddStepRunTests>("after a run each step shows its outcome, the failure its words and stack, and an example row runs alone", () => new BddStepRunTests(folders), t =>
        {
            t.After_a_run_each_step_shows_its_outcome_the_failure_its_words_and_stack_and_an_example_row_runs_alone();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<BddIntermediateRunTests>("a scenario whose code-behind its build writes under obj runs from its caret", () => new BddIntermediateRunTests(folders), t =>
        {
            t.A_scenario_whose_code_behind_is_under_obj_runs_from_its_caret();
            OpenWindows.CloseAll();
        });
    }

    private void A_scenario_is_marked_before_a_build_and_run_tests_at_its_caret_runs_it_and_marks_its_result()
    {
        MainWindow window = Open();
        try
        {
            OpenDocument feature = window.EditorArea.Active!;
            Ui.WaitFor(() => feature.TestMarks.Select(m => m.Line).Order().SequenceEqual([1, 7, 13, 21, 22, 24]),
                () => $"the feature, its scenarios and its example rows marked: {string.Join(", ", feature.TestMarks.Select(m => $"{m.Line} {m.Kind}"))}");
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "Given the first number is 5");
            Ui.Run(window, AppActions.RunTests);
            Ui.WaitFor(() => feature.TestMarks.Any(m => m.Line == 7 && m.Kind == TestMarkKind.Passed),
                () => $"the scenario's mark to show it passed ({window.Status}): {string.Join(", ", feature.TestMarks.Select(m => $"{m.Line} {m.Kind}"))}; tests: {Listed(window)}",
                TimeSpan.FromMinutes(4));
            Assert.DoesNotContain(feature.TestMarks, m => m.Line == 13 && m.Kind != TestMarkKind.NotRun);

            editor.GoTo(new TextPosition(1, 3));
            Ui.Run(window, AppActions.RunTests);
            Ui.WaitFor(() => !window.Tests.IsBusy && feature.TestMarks.Any(m => m.Line == 24 && m.Kind == TestMarkKind.Failed),
                () => $"the scenario of a step none binds to fail ({window.Status}): {string.Join(", ", feature.TestMarks.Select(m => $"{m.Line} {m.Kind}"))}",
                TimeSpan.FromMinutes(4));
            Assert.Contains(feature.TestMarks, m => m.Line == 13 && m.Kind == TestMarkKind.Passed);
        }
        finally
        {
            window.Close();
        }
    }

    private void A_scenario_with_examples_binds_its_steps_for_each_row_and_its_run_mark_runs_a_row_for_each_example()
    {
        TestFolders.Write(folder, "Calc.Specs/Features/Calculator.feature",
            ReqnrollProjects.Feature.Replace("Scenario Outline: Add numbers", "Scenario: Add numbers", StringComparison.Ordinal));
        MainWindow window = Open();
        try
        {
            OpenDocument feature = window.EditorArea.Active!;
            Ui.WaitFor(() => window.Session.Steps.For(Specs) is { Bindings.Count: > 0 }, () => $"the specs' bindings read ({window.Status})", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => feature.Markers.Any(m => m.Code == Passenger.App.Bdd.FeatureAnalysis.Undefined),
                () => $"the step nobody wrote marked: {string.Join("; ", feature.Markers.Select(m => m.Message))}");
            string[] marked = [.. feature.Markers.Select(m => feature.Text.Snapshot.GetText(m.Range.Start, m.Range.Length))];
            Assert.True(marked.SequenceEqual(["Given a step nobody wrote"]),
                $"The scenario's steps with placeholders are said unbound though each of its examples binds them: {string.Join("; ", marked)}");

            Ui.PlaceAfter(window.EditorArea.CodeEditor, "Given the first number is <");
            Ui.Run(window, AppActions.RunTests);
            Ui.WaitFor(() => !window.Tests.IsBusy && feature.TestMarks.Any(m => m.Line == 13 && m.Kind == TestMarkKind.Passed),
                () => $"the scenario's mark to show it passed ({window.Status}): {string.Join(", ", feature.TestMarks.Select(m => $"{m.Line} {m.Kind}"))}; tests: {Listed(window)}",
                TimeSpan.FromMinutes(4));
            TestNode scenario = Assert.Single(window.Tests.Tree.Methods, m => m.FullName?.EndsWith(".AddNumbers", StringComparison.Ordinal) == true);
            string[] rows = [.. scenario.Children.Where(c => c.Kind == TestNodeKind.Row && c.State == TestState.Passed).Select(c => c.Name)];
            Assert.True(rows.Length == 2 && rows.Any(r => r.Contains("a: \"1\", b: \"2\", sum: \"3\"", StringComparison.Ordinal))
                && rows.Any(r => r.Contains("a: \"5\", b: \"5\", sum: \"10\"", StringComparison.Ordinal)),
                $"The scenario's run is to pass a row for each example: {string.Join("; ", scenario.Children.Select(c => $"{c.Name} {c.State}"))}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The tests the Unit Tests window knows, each with the file and line its items name.</summary>
    private static string Listed(MainWindow window) => string.Join("; ", window.Tests.Tree.Methods.Select(m =>
        $"{m.FullName} [code {m.FromCode?.Path}:{m.FromCode?.Line}; {string.Join(", ", m.Items.Select(i => $"{i.Path}:{i.Line}"))}]"));

    private void A_breakpoint_on_a_step_stops_there_step_into_goes_into_its_definition_and_step_over_goes_to_the_next_step()
    {
        MainWindow window = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(8, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.DebugTests);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop?.Top is not null,
                () => $"the stop on the step's line ({window.Status})", TimeSpan.FromMinutes(4));
            Assert.Equal(PathNames.Canonical(FeatureFile), window.EditorArea.Active?.Path);
            Assert.Contains(new LineMark(8, LineMarkKind.ExecutionPoint), window.EditorArea.CodeEditor.LineMarks);

            Ui.Run(window, AppActions.StepOver);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused
                && window.EditorArea.CodeEditor.LineMarks.Contains(new LineMark(9, LineMarkKind.ExecutionPoint)),
                () => $"Step Over to the next step ({window.Debugger.Stop?.Top?.Source?.Path}:{window.Debugger.Stop?.Top?.Line})", TimeSpan.FromMinutes(1));

            Ui.Run(window, AppActions.StepInto);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.EditorArea.Active?.Path == PathNames.Canonical(StepsFile),
                () => $"Step Into the step's definition ({window.Debugger.Stop?.Top?.Name} at {window.Debugger.Stop?.Top?.Source?.Path}:{window.Debugger.Stop?.Top?.Line})",
                TimeSpan.FromMinutes(1));
            Assert.Contains("GivenTheSecondNumberIs", window.Debugger.Stop!.Top!.Name, StringComparison.Ordinal);

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Tests.IsBusy, "the run to end", TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The card shown with the pointer resting on some text of the editor's.</summary>
    private static string Hover(MainWindow window, CodeEditor editor, string text)
    {
        Ui.Press(window, "ESCAPE");
        window.MouseMove(new Avalonia.Point(0, 0), Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the last card to go");
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
        Avalonia.Point point = editor.TranslatePoint(editor.PointOf(at) + new Avalonia.Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, Avalonia.Input.RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, $"the card on {text}");
        return window.Assist.HoverText!;
    }
}

/// <summary>A Reqnroll project whose code-behind its build writes under obj (ReqnrollUseIntermediateOutputPathForCodeBehind),
/// where the folder's walk never reads: a scenario run from its caret still finds its test by its feature line. A step of
/// <see cref="BddRunTests"/>' flow, which runs alone.</summary>
public sealed class BddIntermediateRunTests : IDisposable
{
    private readonly string folder;

    public BddIntermediateRunTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("bddobj-", made => ReqnrollProjects.Write(made, intermediate: true)));
    }

    public void Dispose() => TestFolders.Delete(folder);

    internal void A_scenario_whose_code_behind_is_under_obj_runs_from_its_caret()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900);
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "Calc.Specs", "Features", "Calculator.feature")));
            Ui.Settle();
            OpenDocument feature = window.EditorArea.Active!;
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "Given the first number is 5");
            Ui.Run(window, AppActions.RunTests);
            Ui.WaitFor(() => feature.TestMarks.Any(m => m.Line == 7 && m.Kind == TestMarkKind.Passed),
                () => $"the scenario's mark to show it passed ({window.Status}): {string.Join(", ", feature.TestMarks.Select(m => $"{m.Line} {m.Kind}"))}",
                TimeSpan.FromMinutes(4));
            Assert.False(File.Exists(Path.Combine(folder, "Calc.Specs", "Features", "Calculator.feature.cs")), "Reqnroll wrote the code-behind beside the feature, not under obj");
        }
        finally
        {
            window.Close();
        }
    }
}
