using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Languages;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A .feature file is Gherkin's: coloured by its grammar (keywords, tags, comments, its tables as data) and by
/// Gherkin's reading of it (descriptions, placeholders, an examples table's header),
/// folded by Gherkin's own reading of it (the feature, each scenario, its examples and doc strings), outlined in Structure
/// and commented with a <c>#</c>, laid out by Reformat Code and its tables aligned as a bar is typed, all with no project
/// or language server.</summary>
public sealed class GherkinEditingTests : FileColoursFolder
{
    private const string Feature =
        "@calc\n" +
        "Feature: Calculator\n" +
        "  Adds numbers\n" +
        "\n" +
        "  # The plain case\n" +
        "  Scenario: Add two numbers\n" +
        "    Given the first number is 50\n" +
        "    And the second number is 70\n" +
        "    When the two numbers are added\n" +
        "    Then the result should be 120\n" +
        "\n" +
        "  Scenario Outline: Add numbers\n" +
        "    Given the first number is <a>\n" +
        "    Then the result should be <sum>\n" +
        "      \"\"\"\n" +
        "      sum\n" +
        "      \"\"\"\n" +
        "\n" +
        "    Examples:\n" +
        "      | a | sum |\n" +
        "      | 1 | 1   |\n";

    [AvaloniaFact]
    public void A_feature_file_is_coloured_folded_outlined_and_commented_as_gherkin()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "Calculator.feature", Feature);
        OpenDocument document = window.EditorArea.Active!;

        Assert.Equal(Language.Gherkin, document.Language);
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "Feature"));
        Assert.Equal(TokenKind.Text, KindOf(editor, "Calculator"));
        Assert.Equal(TokenKind.Class, KindOf(editor, "@calc"));
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "Given "));
        Assert.Equal(TokenKind.String, KindOf(editor, "| 1 | 1   |"));
        Ui.WaitFor(() => KindOf(editor, "<a>") == TokenKind.Number, "the outline's placeholder in a number's colour");
        Assert.Equal(TokenKind.DocComment, KindOf(editor, "Adds numbers"));
        int header = Feature.IndexOf("| a | sum |", StringComparison.Ordinal) + "| a | ".Length;
        Assert.Contains(editor.Highlighter!.GetSpans(editor.Document.Snapshot, header, 3), s => s.Start == header && s.Length == 3 && s.Kind == TokenKind.Bold);
        Assert.Equal(TokenKind.Comment, KindOf(editor, "# The plain case"));

        Ui.WaitFor(() => document.FoldsFound, "the feature's folds");
        TextSnapshot text = document.Text.Snapshot;
        (int, int)[] folds = [.. document.FoldRegions.Select(r => (text.GetLineFromOffset(r.Start), text.GetLineFromOffset(r.End)))];
        Assert.Equal([(1, 20), (5, 9), (11, 20), (14, 16), (18, 20)], folds);

        Ui.Run(window, AppActions.ShowStructure);
        StructureView structure = window.StructureView;
        (string, string, int)[] expected =
        [
            ("Calculator", "Feature", 0), ("Add two numbers", "Scenario", 1), ("Add numbers", "Scenario Outline", 1), ("Examples", "", 2),
        ];
        Ui.WaitFor(() => structure.Rows.Select(r => (r.Name, r.Detail ?? "", r.Depth)).SequenceEqual(expected),
            () => $"The feature's parts in Structure: {string.Join(", ", structure.Rows.Select(r => $"{r.Name} ({r.Detail}) {r.Depth}"))}");

        editor.Focus();
        editor.Selections = SelectionSet.Single(Feature.IndexOf("When the", StringComparison.Ordinal));
        Ui.Run(window, EditorActions.ToggleLineComment);
        Assert.Contains("    # When the two numbers are added\n", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Reformat_code_lays_out_a_feature_and_a_bar_typed_in_a_row_aligns_its_table()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "Layout.feature", "Feature: F\n\n  Scenario: S\nGiven the users\n|name|age|\n| Ann | 7 |\n    And   more\n");
        editor.Focus();
        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => editor.Document.Snapshot.ToString() == "Feature: F\n\nScenario: S\n  Given the users\n    | name | age |\n    | Ann  |   7 |\n  And more\n",
            () => $"the feature laid out: {editor.Document.Snapshot}");

        Ui.PlaceAfter(editor, "| Ann  |   7 |");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("|Bartholomew");
        window.KeyTextInput("|");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("    | name        | age |\n    | Ann         |   7 |\n    | Bartholomew |", StringComparison.Ordinal),
            () => $"the table aligned as the bar was typed: {editor.Document.Snapshot}");
        window.KeyTextInput("42");
        window.KeyTextInput("|");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("    | Bartholomew |  42 |\n", StringComparison.Ordinal),
            () => $"the row's number aligned as its bar was typed: {editor.Document.Snapshot}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_feature_in_another_language_completes_its_own_keywords_and_one_of_no_project_marks_no_step_undefined()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "Rechner.feature", "# language: de\nFunktionalität: Rechner\n\nSzenario: Addieren\n  Angenommen eine Zahl 5\n");
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => KindOf(editor, "Angenommen") == TokenKind.Keyword, "the German keywords coloured from Gherkin's reading");
        Assert.Empty(document.Markers);

        Ui.PlaceAfter(editor, "eine Zahl 5");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("W");
        Ui.WaitFor(() => window.Assist.IsCompletionOpen && window.Assist.CompletionLabels.Contains("Wenn"),
            () => $"German's keywords offered: {string.Join(", ", window.Assist.CompletionLabels)}");
        Assert.DoesNotContain("When", window.Assist.CompletionLabels);
        window.Close();
    }
}
