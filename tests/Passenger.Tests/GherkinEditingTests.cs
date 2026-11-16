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
/// and commented with a <c>#</c>, laid out by Reformat Code and its tables aligned as a bar or Enter is typed, read in the
/// language its <c># language:</c> line names as each key of it is typed, all with no project or language server.</summary>
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

        Typing.Replace(window, "    | Bartholomew |  42 |", "    | Bartholomew |  42 |\n    |Zed|1|");
        Ui.PlaceAfter(editor, "|Zed|1|");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("    | Bartholomew |  42 |\n    | Zed         |   1 |\n", StringComparison.Ordinal),
            () => $"the table aligned as Enter ended its row: {editor.Document.Snapshot}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_CRLF_feature_is_laid_out_and_its_tables_aligned_with_each_line_ending_in_one_CRLF()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "Crlf.feature", "Feature: F\r\n\r\n  Scenario: S\r\nGiven the users\r\n|name|age|\r\n| Ann | 7 |\r\n    And   more\r\n");
        OpenDocument document = window.EditorArea.Active!;
        const string Laid = "Feature: F\r\n\r\nScenario: S\r\n  Given the users\r\n    | name | age |\r\n    | Ann  |   7 |\r\n  And more\r\n";
        editor.Focus();
        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => window.Status is "Reformatted" or "Already formatted", "the reformat");
        Assert.True(editor.Document.Snapshot.ToString() == Laid, $"The feature should be laid out in its own line breaks: {Shown(editor.Document.Snapshot.ToString())}");

        int version = document.Text.Version;
        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => window.Status == "Already formatted" || document.Text.Version != version,
            () => $"the second reformat: {window.Status}, {Shown(editor.Document.Snapshot.ToString())}");
        Assert.True(document.Text.Version == version, $"A feature laid out already should take no edit: {Shown(editor.Document.Snapshot.ToString())}");

        Ui.PlaceAfter(editor, "| Ann  |   7 |");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("|Bartholomew");
        window.KeyTextInput("|");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("    | name        | age |\r\n    | Ann         |   7 |\r\n    | Bartholomew |", StringComparison.Ordinal),
            () => $"the table aligned as the bar was typed: {Shown(editor.Document.Snapshot.ToString())}");
        Typing.Replace(window, "    | Bartholomew |", "    | Bartholomew |\n    |Zed|");
        Ui.PlaceAfter(editor, "|Zed|");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("    | Bartholomew |\r\n    | Zed         |\r\n", StringComparison.Ordinal),
            () => $"the table aligned as Enter ended its row: {Shown(editor.Document.Snapshot.ToString())}");
        string text = editor.Document.Snapshot.ToString();
        Assert.True(text.Replace("\r\n", "", StringComparison.Ordinal).IndexOfAny(['\r', '\n']) < 0,
            $"Every line of a CRLF feature should still end in one CRLF: {Shown(text)}");
        window.Close();

        static string Shown(string text) => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
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

    [AvaloniaFact]
    public void A_language_line_typed_a_key_at_a_time_marks_a_language_gherkin_lacks_and_reads_the_feature_in_the_one_named()
    {
        MainWindow window = Window();
        CodeEditor editor = Open(window, "Sprache.feature", "\nFunktionalität: Rechner\n\nSzenario: Addieren\n  Angenommen eine Zahl 5\n  Dann ist das Ergebnis 5\n");
        OpenDocument document = window.EditorArea.Active!;
        editor.Focus();
        editor.Selections = SelectionSet.Single(0);
        foreach (char key in "# language: de")
        {
            window.KeyTextInput(key.ToString());
            Ui.Settle();
            string typed = document.Text.Snapshot.GetLineText(0);
            Assert.True(IdeErrors.Errors.Count == 0, $"Reading the feature once \"{typed}\" was typed made internal errors: {string.Join("; ", IdeErrors.Errors.Select(e => e.Message))}");
            if (typed == "# language: d")
            {
                Assert.True(document.Markers.Any(m => m.Range.Start == 0 && m.Kind == MarkerKind.Error && m.Said == "Language not supported: d"),
                    $"A language Gherkin lacks should be marked where it is named: {string.Join("; ", document.Markers.Select(m => $"{m.Range.Start} {m.Said}"))}");
            }
        }

        Ui.WaitFor(() => KindOf(editor, "Angenommen") == TokenKind.Keyword, "the German keywords coloured once the language is named");
        Assert.True(document.Markers.Count == 0, $"The feature read in German should hold no mistake: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        Ui.WaitFor(() => Folds().SequenceEqual([(1, 5), (3, 5)]), () => $"the German feature and scenario folded: {string.Join(", ", Folds())}");
        window.Close();

        (int, int)[] Folds() => [.. document.FoldRegions.Select(r => (document.Text.Snapshot.GetLineFromOffset(r.Start), document.Text.Snapshot.GetLineFromOffset(r.End)))];
    }
}
