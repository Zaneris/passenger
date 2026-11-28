using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Bdd;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace.Gherkin;

namespace Passenger.Tests;

/// <summary>The Step Library over a Reqnroll solution (<see cref="ReqnrollProjects"/>, with a definition scoped to a tag and a
/// feature using it): every definition listed under its kind with its phrase, slots, tags and how many feature steps use
/// it, the unused said so, the hooks with their tags; narrowed by words and @tags; a phrase written into the open feature
/// at the caret with its first slot chosen; and a definition written meanwhile listed, its uses counted as a feature's step
/// is typed.</summary>
public sealed class StepLibraryTests : IClassFixture<RestoredFolders>, IDisposable
{
    private const string Scoped =
        "namespace Calc.Specs.StepDefinitions;\n" +
        "\n" +
        "[Binding]\n" +
        "[Scope(Tag = \"display\")]\n" +
        "public sealed class DisplaySteps\n" +
        "{\n" +
        "    [Then(\"the display shows {string}\")]\n" +
        "    public void ThenTheDisplayShows(string shown)\n" +
        "    {\n" +
        "    }\n" +
        "}\n";

    private const string Display =
        "@display\n" +
        "Feature: Display\n" +
        "\n" +
        "Scenario: The sum is shown\n" +
        "  Given the first number is 1\n" +
        "  Then the display shows \"1\"\n";

    private readonly string folder;

    /// <summary>Where each step of a flow takes its own folder from.</summary>
    private readonly RestoredFolders folders;

    public StepLibraryTests(RestoredFolders folders)
    {
        this.folders = folders;
        folder = PathNames.Canonical(folders.Take("steplib-", made =>
        {
            string solution = ReqnrollProjects.Write(made);
            TestFolders.Write(made, "Calc.Specs/StepDefinitions/DisplaySteps.cs", Scoped);
            TestFolders.Write(made, "Calc.Specs/Features/Display.feature", Display);
            return solution;
        }));
    }

    public void Dispose() => TestFolders.Delete(folder);

    private string FeatureFile => Path.Combine(folder, "Calc.Specs", "Features", "Calculator.feature");

    private string StepsFile => Path.Combine(folder, "Calc.Specs", "StepDefinitions", "CalculatorStepDefinitions.cs");

    /// <summary>The window on the solution with the Step Library shown, every definition listed and counted.</summary>
    private static StepLibraryView Shown(MainWindow window)
    {
        Assert.True(window.Perform(AppActions.ShowStepLibrary), "Show Step Library was refused");
        StepLibraryView library = window.StepLibraryWindow;
        Ui.WaitFor(() => library.Steps.Count >= 7 && library.Steps.All(s => s.Uses is not null),
            () => $"every step definition listed and counted ({window.Status}): {library.Said} {string.Join(", ", library.Steps.Select(s => $"{s.Sample.Text} {s.Uses}"))}",
            TimeSpan.FromMinutes(3));
        return library;
    }

    /// <summary>Every definition is listed under its kind with its uses and tags, and the filter narrows them by words
    /// and tags; a chosen definition's phrase is written at the caret with its first slot chosen to type over; a
    /// definition written meanwhile is listed unused and counted as a feature's step is typed.</summary>
    [AvaloniaFact]
    public void The_Step_Library_lists_each_definition_with_its_uses_and_writes_a_chosen_phrase_into_the_feature()
    {
        Flow.StepApart<StepLibraryTests>("every definition is listed under its kind with its uses and tags and the filter narrows them by words and tags", () => new StepLibraryTests(folders), t =>
        {
            t.Every_definition_is_listed_under_its_kind_with_its_uses_and_tags_and_the_filter_narrows_them_by_words_and_tags();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<StepLibraryTests>("a chosen definition's phrase is written into the feature at the caret with its first slot chosen to type over", () => new StepLibraryTests(folders), t =>
        {
            t.A_chosen_definitions_phrase_is_written_into_the_feature_at_the_caret_with_its_first_slot_chosen_to_type_over();
            OpenWindows.CloseAll();
        });
        Flow.StepApart<StepLibraryTests>("a definition written meanwhile is listed unused and counted as a feature's step is typed", () => new StepLibraryTests(folders), t =>
        {
            t.A_definition_written_meanwhile_is_listed_unused_and_counted_as_a_features_step_is_typed();
            OpenWindows.CloseAll();
        });
    }

    private void Every_definition_is_listed_under_its_kind_with_its_uses_and_tags_and_the_filter_narrows_them_by_words_and_tags()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            StepLibraryView library = Shown(window);
            Assert.Equal(
                [
                    (BindingKind.Given, "the first number is [int]", 3, ""),
                    (BindingKind.Given, "the second number is [int]", 2, ""),
                    (BindingKind.When, "the two numbers are added", 2, ""),
                    (BindingKind.Then, "nothing is ever said", 0, ""),
                    (BindingKind.Then, "the display shows \"[string]\"", 1, "@display"),
                    (BindingKind.Then, "the result should be [int]", 2, ""),
                    (BindingKind.StepDefinition, "the calculator is cleared", 1, ""),
                ],
                library.Steps.Select(s => (s.Binding.Kind, s.Sample.Text, s.Uses ?? -1, string.Join(' ', s.Tags))));
            Assert.Equal(["Given", "When", "Then", "Any step", "Hooks"], library.Rows.OfType<StepLibraryView.Heading>().Select(h => h.Title.English));
            StepHook hook = Assert.Single(library.Hooks);
            Assert.Equal(("BeforeScenario", "@calc"), (hook.Kind, string.Join(' ', StepLibrary.TagsOf(hook.Scopes))));
            Ui.Settle();
            string[] shown = [.. library.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty)];
            Assert.Contains("Unused", shown);
            Assert.Contains("@display", shown);
            Assert.Contains("Before each scenario", shown);
            Assert.Contains(library.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && b.Content as string == "3 uses");

            library.Filter = "second";
            Ui.Settle();
            Assert.Equal(["the second number is [int]"], library.Steps.Select(s => s.Sample.Text));
            Assert.Empty(library.Hooks);
            library.Filter = "number";
            Ui.Settle();
            Assert.Equal(["the first number is [int]", "the second number is [int]", "the two numbers are added"], library.Steps.Select(s => s.Sample.Text));
            library.Filter = "@disp";
            Ui.Settle();
            Assert.Equal(["the display shows \"[string]\""], library.Steps.Select(s => s.Sample.Text));
            Assert.True(library.Filters.Narrowed, "The filter's button is not pressed while a tag narrows the list");
            library.Filter = "@calc";
            Ui.Settle();
            Assert.Empty(library.Steps);
            Assert.Equal("Ready", Assert.Single(library.Hooks).Method);
            library.Filter = "";
            library.UnusedOnly = true;
            Ui.Settle();
            Assert.Equal(["nothing is ever said"], library.Steps.Select(s => s.Sample.Text));
            library.UnusedOnly = false;
            library.Filter = "nobody";
            Ui.Settle();
            Assert.Equal("No step definition fits the filter", library.Said);
        }
        finally
        {
            window.Close();
        }
    }

    private void A_chosen_definitions_phrase_is_written_into_the_feature_at_the_caret_with_its_first_slot_chosen_to_type_over()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Assert.Null(window.OpenFile(FeatureFile));
            StepLibraryView library = Shown(window);
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "  Given a step nobody wrote");
            Ui.Click(InsertOf(library, "the second number is [int]"));
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("  Given a step nobody wrote\n  And the second number is [int]\n", StringComparison.Ordinal),
                () => $"the step written under the caret's, after And ({window.Status}): {editor.Document.Snapshot}");
            Assert.Equal("[int]", editor.Document.Snapshot.GetText(editor.Selections.Primary.Start, editor.Selections.Primary.Length));
            Assert.True(editor.IsKeyboardFocusWithin, "The editor was not given the keys to fill the slot");
            window.KeyTextInput("7");
            Assert.Contains("  And the second number is 7\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

            Ui.Press(window, "ENTER");
            window.KeyTextInput("Then ");
            Ui.Press(window, "ESCAPE");
            ListBoxItem result = RowOf(library, "the result should be [int]");
            Ui.Click(result, new Avalonia.Point(40, result.Bounds.Height / 2));
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("  And the second number is 7\n  Then the result should be [int]", StringComparison.Ordinal),
                () => $"the phrase written after the keyword alone on the caret's line ({window.Status}): {editor.Document.Snapshot}");
            Assert.Equal("[int]", editor.Document.Snapshot.GetText(editor.Selections.Primary.Start, editor.Selections.Primary.Length));

            Assert.Null(window.OpenFile(StepsFile));
            Ui.Settle();
            Assert.Equal(Refusals.NoFeature.Shown, window.Actions.WhyNot(AppActions.InsertStep, window.Actions.Context())?.Shown);
        }
        finally
        {
            window.Close();
        }
    }

    private void A_definition_written_meanwhile_is_listed_unused_and_counted_as_a_features_step_is_typed()
    {
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Assert.Null(window.OpenFile(FeatureFile));
            StepLibraryView library = Shown(window);
            Assert.Null(window.OpenFile(StepsFile));
            Ui.Settle();
            Typing.Replace(window, "    [Then(\"nothing is ever said\")]",
                "    [Then(\"the memory is empty\")]\npublic void ThenTheMemoryIsEmpty() { }\n\n[Then(\"nothing is ever said\")]");
            Ui.WaitFor(() => library.Steps.FirstOrDefault(s => s.Sample.Text == "the memory is empty") is { Uses: 0 },
                () => $"the definition typed listed unused ({window.Status}): {string.Join(", ", library.Steps.Select(s => $"{s.Sample.Text} {s.Uses}"))}",
                TimeSpan.FromMinutes(1));

            Assert.Null(window.OpenFile(FeatureFile));
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "  Given a step nobody wrote");
            editor.Focus();
            Ui.Press(window, "ENTER");
            window.KeyTextInput("Then the memory is empty");
            Ui.Press(window, "ESCAPE");
            Ui.WaitFor(() => library.Steps.FirstOrDefault(s => s.Sample.Text == "the memory is empty") is { Uses: 1 },
                () => $"the step typed counted ({window.Status}): {string.Join(", ", library.Steps.Select(s => $"{s.Sample.Text} {s.Uses}"))}");
            Assert.Equal(3, library.Steps.Single(s => s.Sample.Text == "the first number is [int]").Uses);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A definition's row as the list shows it.</summary>
    private static ListBoxItem RowOf(StepLibraryView library, string phrase)
    {
        ListBoxItem? row = null;
        Ui.WaitFor(() => (row = library.GetVisualDescendants().OfType<ListBoxItem>()
            .FirstOrDefault(r => r.IsEffectivelyVisible && r.DataContext is LibraryStep step && step.Sample.Text == phrase)) is not null,
            $"the row of '{phrase}' shown");
        return row!;
    }

    /// <summary>The Insert button of a definition's row as the list shows it.</summary>
    private static Button InsertOf(StepLibraryView library, string phrase)
    {
        Button? insert = null;
        Ui.WaitFor(() => (insert = library.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name == "stepInsert" && b.IsEffectivelyVisible && b.DataContext is LibraryStep step && step.Sample.Text == phrase)) is not null,
            $"the row of '{phrase}' shown with its Insert");
        return insert!;
    }
}
