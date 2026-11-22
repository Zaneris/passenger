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
/// the hover card naming a step's definition with its summary, Go to Declaration from a step to it, Find Usages from it to its steps, and
/// completion offering the steps of a step's kind, and a missing step's definition written by Show Context Actions.</summary>
public sealed class BddTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    /// <summary>Where each step of a flow takes its own folder from.</summary>
    private readonly RestoredFolders folders;

    public BddTests(RestoredFolders folders)
    {
        this.folders = folders;
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

    /// <summary>Bindings read from code and a referenced library with no build; an unbound step marked, a bound one's
    /// card and declaration leading to its definition; Find Usages and completion of steps; a context action writing an
    /// unbound step's definition; Rename of a definition and its steps, an outline's left as written; a feature in
    /// another language than its project's marked.</summary>
    [AvaloniaFact]
    public void A_projects_step_definitions_are_read_from_code_and_bind_mark_find_complete_and_rename_its_steps()
    {
        Flow.StepApart<BddTests>("a project's bindings are read from its code and the library it references with no build", () => new BddTests(folders), t =>
        {
            t.A_projects_bindings_are_read_from_its_code_and_the_library_it_references_with_no_build();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<BddTests>("a step no definition binds is marked and a bound step's card and declaration lead to its definition", () => new BddTests(folders), t =>
        {
            t.A_step_no_definition_binds_is_marked_and_a_bound_steps_card_and_declaration_lead_to_its_definition();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<BddTests>("find usages of a step definition lists its steps and completion offers the steps of a step's kind", () => new BddTests(folders), t =>
        {
            t.Find_usages_of_a_step_definition_lists_its_steps_and_completion_offers_the_steps_of_a_steps_kind();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<BddTests>("show context actions on a step none binds writes its definition into the class binding the feature's steps", () => new BddTests(folders), t =>
        {
            t.Show_context_actions_on_a_step_none_binds_writes_its_definition_into_the_class_binding_the_features_steps();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<BddTests>("rename on a step renames its definition's expression and every step it binds leaving an outline's as written", () => new BddTests(folders), t =>
        {
            t.Rename_on_a_step_renames_its_definitions_expression_and_every_step_it_binds_leaving_an_outlines_as_written();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<BddTests>("a feature written in another language than its project's is marked saying so and the project's load problem names it", () => new BddTests(folders), t =>
        {
            t.A_feature_written_in_another_language_than_its_projects_is_marked_saying_so_and_the_projects_load_problem_names_it();
            OpenWindows.CloseAll();
        });
    }

    private void A_projects_bindings_are_read_from_its_code_and_the_library_it_references_with_no_build()
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
            Assert.Equal(new BindingPlace(PathNames.Canonical(StepsFile), 11, 16), first.MethodPlace);
            Assert.Equal(new BindingPlace(PathNames.Canonical(StepsFile), 10, 11), first.ExpressionPlace);
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

    private void A_step_no_definition_binds_is_marked_and_a_bound_steps_card_and_declaration_lead_to_its_definition()
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
            string card = Hover(window, editor, "first number is 50");
            Assert.Contains("GivenTheFirstNumberIs", card, StringComparison.Ordinal);
            Assert.Contains("Keys the first number into the CalculatorStepDefinitions.", card, StringComparison.Ordinal);

            Ui.PlaceAfter(editor, "Given the first num");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Path == PathNames.Canonical(StepsFile) && window.EditorArea.CodeEditor.CaretPosition.Line == 11,
                () => $"Go to Declaration to GivenTheFirstNumberIs ({window.EditorArea.Active?.Path}:{window.EditorArea.CodeEditor.CaretPosition.Line})");
        }
        finally
        {
            window.Close();
        }
    }

    private void Find_usages_of_a_step_definition_lists_its_steps_and_completion_offers_the_steps_of_a_steps_kind()
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

    private void Show_context_actions_on_a_step_none_binds_writes_its_definition_into_the_class_binding_the_features_steps()
    {
        MainWindow window = Open(out _);
        try
        {
            OpenDocument feature = window.EditorArea.Active!;
            Ui.WaitFor(() => feature.Markers.Any(m => m.Code == Passenger.App.Bdd.FeatureAnalysis.Undefined), "the step nobody wrote marked");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "Given a step no");
            Ui.Run(window, AppActions.QuickFix);
            Choose(window, "Define step 'a step nobody wrote'");
            Ui.WaitFor(() => window.EditorArea.Active?.Path == PathNames.Canonical(StepsFile), "the step definitions shown");
            string written = window.EditorArea.Active!.Text.Snapshot.ToString();
            Assert.Contains(
                "    [Then(\"nothing is ever said\")]\n    public void ThenNothingIsEverSaid()\n    {\n    }\n\n" +
                "    [Given(\"a step nobody wrote\")]\n    public void GivenAStepNobodyWrote()\n    {\n        throw new PendingStepException();\n    }\n}\n",
                written, StringComparison.Ordinal);
            Ui.WaitFor(() => feature.Markers.Count == 0, () => $"the step defined, its mark gone: {string.Join("; ", feature.Markers.Select(m => m.Message))}",
                TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
        }
    }

    private void Rename_on_a_step_renames_its_definitions_expression_and_every_step_it_binds_leaving_an_outlines_as_written()
    {
        MainWindow window = Open(out _);
        try
        {
            OpenDocument feature = window.EditorArea.Active!;
            Ui.WaitFor(() => feature.Markers.Any(m => m.Code == Passenger.App.Bdd.FeatureAnalysis.Undefined), "the feature read and matched");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "Given the first num");
            Ui.Run(window, AppActions.Rename);
            Ui.Settle();
            window.KeyTextInput("the initial number is {int}");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => feature.Text.Snapshot.ToString().Contains("  Given the initial number is 50\n", StringComparison.Ordinal),
                () => $"the step renamed ({window.Status}): {feature.Text.Snapshot}");
            Assert.Contains("  Given the first number is <a>\n", feature.Text.Snapshot.ToString(), StringComparison.Ordinal);
            Ui.WaitFor(() => window.EditorArea.TextOf(StepsFile)?.ToString().Contains("[Given(\"the initial number is {int}\")]", StringComparison.Ordinal) == true
                || File.ReadAllText(StepsFile).Contains("[Given(\"the initial number is {int}\")]", StringComparison.Ordinal),
                () => $"the definition's expression renamed ({window.Status})");
            Assert.Contains("1 step with placeholders or no arguments to keep left as written", window.Status ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A project configured to read its features in Austrian German, whose feature is written in English with no
    /// <c># language:</c> line: Reqnroll's generator reads it in German, finds no feature and fails the project's load, so the
    /// load's problem names the file and says why, in the status bar and Problems as the load ends, and the feature is marked
    /// where it was to begin, saying the language it is read in, whence, and how a file is read in its own.</summary>
    private void A_feature_written_in_another_language_than_its_projects_is_marked_saying_so_and_the_projects_load_problem_names_it()
    {
        TestFolders.Write(folder, "Calc.Specs/reqnroll.json", "{\n  \"language\": { \"feature\": \"de-AT\" },\n  \"bindingAssemblies\": [ { \"assembly\": \"Calc.Steps\" } ]\n}\n");
        const string Why = "Reqnroll reads this file in de-AT, the language.feature of its project's reqnroll.json, where a feature begins with Funktionalität: or "
            + "Funktion:, and finds none: name the language it is written in on its first line, as # language: en names English and # language: de German, "
            + "or the language the project's features are written in as language.feature in its reqnroll.json";
        string load = $"{Path.Combine("Features", "Calculator.feature")}(2,1): {Why}";
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Passenger.App.ToolWindows.ProblemsModel problems = window.BottomPanel.Problems;
            Ui.WaitFor(() => problems.ToolsetProblems.Count > 0 && window.Status is { Length: > 0 },
                () => $"the load's problem said ({window.Status}; {window.CSharpService?.State})", TimeSpan.FromMinutes(3));
            Assert.True(window.Status == $"Error while loading {Path.Combine("Calc.Specs", "Calc.Specs.csproj")}: {load}" && window.StatusSeverity == Passenger.App.Run.ProblemSeverity.Error,
                $"The status bar says \"{window.Status}\" ({window.StatusSeverity}) as the load ends, not that Calc.Specs did not load as its feature reads as no feature in de-AT");
            Passenger.App.Run.Problem failed = Assert.Single(problems.ToolsetProblems);
            Assert.True(failed is { Severity: Passenger.App.Run.ProblemSeverity.Error, Project: "Calc.Specs" } && failed.Message == load,
                $"Problems lists the load's failure as {failed.Severity} \"{failed.Message}\" under {failed.Project}, not once naming the feature and why");

            Assert.Null(window.OpenFile(FeatureFile));
            OpenDocument feature = window.EditorArea.Active!;
            Ui.WaitFor(() => feature.Markers.Any(m => m.Code == FeatureAnalysis.Language),
                () => $"the English feature marked as read in German: {string.Join("; ", feature.Markers.Select(m => m.Message))}");
            Marker marked = Assert.Single(feature.Markers);
            Assert.True(marked.Kind == MarkerKind.Error && marked.Said == Why,
                $"The feature is marked {marked.Kind} \"{marked.Said}\", not saying it is read in de-AT, finds no feature and how its own language is named");
            Assert.Equal("Feature: Calculator", feature.Text.Snapshot.GetText(marked.Range.Start, marked.Range.Length));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Choose(MainWindow window, string label)
    {
        Ui.WaitFor(() => window.Assist.Offered.Contains(label), () => $"'{label}' offered among {string.Join(", ", window.Assist.Offered)}");
        int index = window.Assist.Offered.ToList().IndexOf(label);
        for (int i = 0; i < index; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
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
