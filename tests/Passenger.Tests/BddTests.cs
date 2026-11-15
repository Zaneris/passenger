using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.App.Bdd;
using Passenger.Core;
using Passenger.Workspace.Gherkin;

namespace Passenger.Tests;

/// <summary>A Reqnroll solution's steps and their definitions as one thing (<see cref="ReqnrollProjects"/>): the bindings read
/// from the code with no build, a project's own and those of the library it references; a step no definition binds marked,
/// the hover card naming a step's definition, Go to Declaration from a step to it, Find Usages from it to its steps, and
/// completion offering the steps of a step's kind.</summary>
public sealed class BddTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public BddTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("bdd-", made => ReqnrollProjects.Write(made)));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private string Specs => Path.Combine(folder, "Calc.Specs", "Calc.Specs.csproj");

    private string FeatureFile => Path.Combine(folder, "Calc.Specs", "Features", "Calculator.feature");

    private string StepsFile => Path.Combine(folder, "Calc.Specs", "StepDefinitions", "CalculatorStepDefinitions.cs");

    /// <summary>The window on the solution, a feature open, its bindings read.</summary>
    private MainWindow Open(out ProjectSteps steps)
    {
        MainWindow window = Ui.OpenFolder(folder);
        Assert.Null(window.OpenFile(FeatureFile));
        Ui.WaitFor(() => window.Session.Steps.For(Specs) is { Bindings.Count: > 0 }, () => $"the specs' bindings read ({window.Status})", TimeSpan.FromMinutes(3));
        steps = window.Session.Steps.For(Specs)!;
        return window;
    }

    [AvaloniaFact]
    public void A_projects_bindings_are_read_from_its_code_and_the_library_it_references_with_no_build()
    {
        MainWindow window = Open(out ProjectSteps steps);
        try
        {
            Assert.False(Directory.EnumerateFiles(folder, "Calc.Specs.dll", SearchOption.AllDirectories).Any(), "The specs were built: bindings are to be read with no build");
            Assert.Equal(
                ["CalculatorStepDefinitions.GivenTheFirstNumberIs(Int32)", "CalculatorStepDefinitions.GivenTheSecondNumberIs(Int32)",
                    "CalculatorStepDefinitions.ThenNothingIsEverSaid()", "CalculatorStepDefinitions.ThenTheResultShouldBe(Int32)",
                    "CalculatorStepDefinitions.WhenTheTwoNumbersAreAdded()", "SharedSteps.Clear()"],
                steps.Bindings.Select(b => b.Shown).Order(StringComparer.Ordinal));
            StepBinding first = steps.Bindings.Single(b => b.Method == "GivenTheFirstNumberIs");
            Assert.Equal((BindingKind.Given, "the first number is {int}"), (first.Kind, first.Expression));
            Assert.Equal(new BindingPlace(PathNames.Canonical(StepsFile), 10, 16), first.MethodPlace);
            Assert.Equal(new BindingPlace(PathNames.Canonical(StepsFile), 9, 11), first.ExpressionPlace);
            StepBinding clear = steps.Bindings.Single(b => b.Method == "Clear");
            Assert.Equal((BindingKind.StepDefinition, PathNames.Canonical(Path.Combine(folder, "Calc.Steps", "Calc.Steps.csproj"))), (clear.Kind, clear.Project));
            StepHook hook = Assert.Single(steps.Hooks);
            Assert.Equal(("BeforeScenario", 5, "calc"), (hook.Kind, hook.Order, hook.Scopes.Single().Tag?.TrimStart('@')));
            Assert.Equal(("System.Double", "(\\d+) percent"), (steps.Transformations.Single().ReturnType, steps.Transformations.Single().Regex));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_step_no_definition_binds_is_marked_and_a_bound_steps_card_and_declaration_lead_to_its_definition()
    {
        MainWindow window = Open(out _);
        try
        {
            OpenDocument feature = window.EditorArea.Active!;
            Ui.WaitFor(() => feature.Markers.Any(m => m.Code == Passenger.App.Bdd.FeatureAnalysis.Undefined),
                () => $"the step nobody wrote marked: {string.Join("; ", feature.Markers.Select(m => m.Message))}");
            Marker undefined = Assert.Single(feature.Markers);
            Assert.Equal("Given a step nobody wrote", feature.Text.Snapshot.GetText(undefined.Range.Start, undefined.Range.Length));
            Assert.Equal(MarkerKind.Warning, undefined.Kind);

            CodeEditor editor = window.EditorArea.CodeEditor;
            Assert.Contains("GivenTheFirstNumberIs", Hover(window, editor, "first number is 50"), StringComparison.Ordinal);

            Ui.PlaceAfter(editor, "Given the first num");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Path == PathNames.Canonical(StepsFile) && window.EditorArea.CodeEditor.CaretPosition.Line == 10,
                () => $"Go to Declaration to GivenTheFirstNumberIs ({window.EditorArea.Active?.Path}:{window.EditorArea.CodeEditor.CaretPosition.Line})");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Find_usages_of_a_step_definition_lists_its_steps_and_completion_offers_the_steps_of_a_steps_kind()
    {
        MainWindow window = Open(out _);
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "  Given a step nobody wrote");
            editor.Focus();
            Ui.Press(window, "ENTER");
            window.KeyTextInput("And ");
            Ui.WaitFor(() => window.Assist.IsCompletionOpen && window.Assist.CompletionLabels.Contains("the first number is [int]"),
                () => $"the Given steps offered after And: {string.Join(", ", window.Assist.CompletionLabels)}");
            Assert.Contains("the calculator is cleared", window.Assist.CompletionLabels);
            Assert.DoesNotContain("the result should be [int]", window.Assist.CompletionLabels);
            window.KeyTextInput("first");
            Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "the first number is [int]",
                () => $"the list narrowed to the first number: {string.Join(", ", window.Assist.CompletionLabels)}");
            Ui.Press(window, "ENTER");
            string written = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
            Assert.True(written == "  And the first number is [int]", $"The step chosen written as \"{written}\"");
            Assert.Equal("[int]", editor.Document.Snapshot.GetText(editor.Selections.Primary.Start, editor.Selections.Primary.Length));

            Assert.Null(window.OpenFile(StepsFile));
            Ui.Settle();
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "public void GivenTheFirst");
            Ui.Run(window, AppActions.FindUsages);
            Ui.WaitFor(() => window.Find.Rows.Count(r => r.Usage is not null) >= 2,
                () => $"the steps GivenTheFirstNumberIs binds in the Find window ({window.Status})");
            Passenger.App.ToolWindows.Usage[] usages = [.. window.Find.Rows.Select(r => r.Usage).OfType<Passenger.App.ToolWindows.Usage>()];
            Assert.All(usages, u => Assert.Equal(PathNames.Canonical(FeatureFile), u.Path));
            Assert.Equal(2, usages.Length);
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
        window.MouseMove(new Point(0, 0), RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the last card to go");
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
        Point point = editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, $"the card on {text}");
        return window.Assist.HoverText!;
    }
}
