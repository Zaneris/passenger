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
/// marked in the gutter before anything is built, Run Tests at the caret building the project and running the scenario's
/// test, its mark taking the result; a breakpoint on a step's line stopping there, Step Into going into the step's
/// definition and Step Over from a step to the next, and an outline's placeholder hovered showing its example's value.
/// Debugged through the window, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class BddRunTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public BddRunTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("bddrun-", made => ReqnrollProjects.Write(made)));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private string FeatureFile => Path.Combine(folder, "Calc.Specs", "Features", "Calculator.feature");

    private string StepsFile => Path.Combine(folder, "Calc.Specs", "StepDefinitions", "CalculatorStepDefinitions.cs");

    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, width: 1400, height: 900);
        Assert.Null(window.OpenFile(FeatureFile));
        Ui.Settle();
        return window;
    }

    [AvaloniaFact]
    public void A_scenario_is_marked_before_a_build_and_run_tests_at_its_caret_runs_it_and_marks_its_result()
    {
        MainWindow window = Open();
        try
        {
            OpenDocument feature = window.EditorArea.Active!;
            Ui.WaitFor(() => feature.TestMarks.Select(m => m.Line).Order().SequenceEqual([1, 7, 13, 24]),
                () => $"the feature and its scenarios marked: {string.Join(", ", feature.TestMarks.Select(m => $"{m.Line} {m.Kind}"))}");
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

    /// <summary>The tests the Unit Tests window knows, each with the file and line its items name.</summary>
    private static string Listed(MainWindow window) => string.Join("; ", window.Tests.Tree.Methods.Select(m =>
        $"{m.FullName} [code {m.FromCode?.Path}:{m.FromCode?.Line}; {string.Join(", ", m.Items.Select(i => $"{i.Path}:{i.Line}"))}]"));

    [AvaloniaFact]
    public void A_breakpoint_on_a_step_stops_there_step_into_goes_into_its_definition_and_step_over_goes_to_the_next_step()
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

    [AvaloniaFact]
    public void Paused_in_an_outlines_example_the_hover_on_a_placeholder_shows_the_examples_value()
    {
        MainWindow window = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.GoTo(new TextPosition(14, 4));
            Ui.Run(window, AppActions.ToggleBreakpoint);
            Ui.Run(window, AppActions.DebugTests);
            Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.Debugger.Stop?.Top is not null,
                () => $"the stop on the outline's step ({window.Status})", TimeSpan.FromMinutes(4));
            string card = Hover(window, window.EditorArea.CodeEditor, "<a>");
            Assert.Matches("^a = \\{string\\} \"[15]\"", card);
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
