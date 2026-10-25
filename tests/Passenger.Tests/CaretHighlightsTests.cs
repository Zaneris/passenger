using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.App;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>What the caret lights up in a C# file, through the whole window on a real project: the uses of the symbol it
/// rests on from the language server, writes apart from reads and marked on the scrollbar, gone the moment the caret
/// leaves them or the text changes; Highlight Usages in File keeping them; and the bracket at the caret with its pair,
/// which Move Caret to Matching Brace goes between, from the file's own parse, in windows with no language server.</summary>
public sealed class CaretHighlightsTests : IClassFixture<CaretHighlightsTests.Project>
{
    private const string Totals = "static class Totals\n{\n    public static int Sum(int[] values)\n    {\n        int total = 0;\n"
        + "        foreach (int value in values)\n        {\n            total += value * (value + 1);\n        }\n        return total;\n    }\n}\n";

    private const string Braces = "static class Braces\n{\n    public static int Deep(int a)\n    {\n        return ((a + 1) * (a + 2));\n    }\n}\n";

    private readonly Project project;

    public CaretHighlightsTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    [AvaloniaFact]
    public void Resting_the_caret_on_a_local_highlights_its_writes_and_reads_and_marks_them_on_the_scrollbar()
    {
        MainWindow window = project.Open("Totals.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Click(editor, editor.PointOf(Totals.IndexOf("total = 0", StringComparison.Ordinal) + 2) + new Point(1, editor.LineHeight / 2));
        Ui.Settle();

        Assert.Equal(["total", "total", "total"], editor.UsageHighlights.Select(u => TextOf(editor, u.Range)));
        Assert.True(editor.UsageHighlights.Select(u => u.IsWrite).SequenceEqual([true, true, false]),
            $"The declaration with its value, the += and the return should read write, write, read: {string.Join(", ", editor.UsageHighlights.Select(u => u.IsWrite ? "write" : "read"))}");
        Assert.Equal([4, 7, 9], editor.UsageHighlights.Select(u => editor.Document.Snapshot.GetLineFromOffset(u.Range.Start)));

        UsageHighlight write = editor.UsageHighlights[1];
        UsageHighlight read = editor.UsageHighlights[2];
        Assert.Equal(Token(window, TokenKeys.Editor_UsageWrite), Ui.Pixel(window, InBox(window, editor, write.Range.Start)));
        Assert.Equal(Token(window, TokenKeys.Editor_UsageRead), Ui.Pixel(window, InBox(window, editor, read.Range.Start)));

        CodeEditor.StripeMark writeMark = Assert.Single(editor.StripeMarks, m => m.Kind == StripeMarkKind.WriteUsage && m.Row == 7);
        CodeEditor.StripeMark readMark = Assert.Single(editor.StripeMarks, m => m.Kind == StripeMarkKind.Usage);
        Assert.Equal(9, readMark.Row);
        Assert.Equal(Token(window, TokenKeys.Editor_UsageWriteMark), Ui.Pixel(window, editor.TranslatePoint(editor.StripePoint(writeMark), window)!.Value));
        Assert.Equal(Token(window, TokenKeys.Editor_UsageReadMark), Ui.Pixel(window, editor.TranslatePoint(editor.StripePoint(readMark), window)!.Value));
        window.Close();
    }

    [AvaloniaFact]
    public void Moving_the_caret_off_a_symbol_takes_its_highlights_away_at_once_and_another_symbol_brings_its_own()
    {
        MainWindow window = project.Open("Totals.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        Ui.Click(editor, editor.PointOf(Totals.IndexOf("return total", StringComparison.Ordinal) + "return to".Length) + new Point(1, editor.LineHeight / 2));
        Ui.Settle();
        Assert.Equal(3, editor.UsageHighlights.Count);

        Ui.Press(window, "RIGHT");
        Ui.Press(window, "RIGHT");
        Assert.Equal(3, editor.UsageHighlights.Count);
        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
        window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.None, null);
        Assert.Empty(editor.UsageHighlights);
        Assert.DoesNotContain(editor.StripeMarks, m => m.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage);
        Ui.Settle();
        Assert.Empty(editor.UsageHighlights);

        Ui.Click(editor, editor.PointOf(Totals.IndexOf("value * (", StringComparison.Ordinal) + 1) + new Point(1, editor.LineHeight / 2));
        Ui.Settle();
        Assert.Equal(["value", "value", "value"], editor.UsageHighlights.Select(u => TextOf(editor, u.Range)));
        Assert.Equal([true, false, false], editor.UsageHighlights.Select(u => u.IsWrite));
        window.Close();
    }

    [AvaloniaFact]
    public void Typing_never_leaves_a_highlight_on_text_it_changed_and_the_uses_come_back_where_they_now_are()
    {
        MainWindow window = project.Open("Totals.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        Ui.Click(editor, editor.PointOf(Totals.IndexOf("return total", StringComparison.Ordinal) + "return to".Length) + new Point(1, editor.LineHeight / 2));
        Ui.Settle();
        Assert.Equal(3, editor.UsageHighlights.Count);

        Ui.Press(window, "END");
        Ui.Press(window, "LEFT");
        Assert.Equal(3, editor.UsageHighlights.Count);
        window.KeyTextInput("s");
        Assert.Empty(editor.UsageHighlights);
        Ui.Settle();
        Assert.All(editor.UsageHighlights, u => Assert.Equal("totals", TextOf(editor, u.Range)));
        Ui.Press(window, "BACK_SPACE");
        Ui.Settle();
        Assert.Equal(["total", "total", "total"], editor.UsageHighlights.Select(u => TextOf(editor, u.Range)));

        editor.ApplyEdits([new TextEdit(0, 0, "// Totals of squares.\n")]);
        Assert.Empty(editor.UsageHighlights);
        Ui.Settle();
        Assert.Equal(["total", "total", "total"], editor.UsageHighlights.Select(u => TextOf(editor, u.Range)));
        Assert.Equal([5, 8, 10], editor.UsageHighlights.Select(u => editor.Document.Snapshot.GetLineFromOffset(u.Range.Start)));
        window.Close();
    }

    [AvaloniaFact]
    public void Highlight_usages_in_file_keeps_the_uses_as_the_caret_moves_and_the_text_changes_until_escape()
    {
        MainWindow window = project.Open("Totals.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        Ui.Click(editor, editor.PointOf(Totals.IndexOf("total +=", StringComparison.Ordinal) + 1) + new Point(1, editor.LineHeight / 2));
        Ui.Run(window, AppActions.HighlightUsages);
        Ui.Settle();
        Assert.True(editor.UsagesKept, "Highlight Usages in File did not keep the uses");
        Assert.Equal(3, editor.UsageHighlights.Count);
        Assert.StartsWith("3 usages of total found (press ", window.Status, StringComparison.Ordinal);

        Ui.Run(window, AppActions.HighlightUsages);
        Ui.Settle();
        Assert.Empty(editor.UsageHighlights);
        Ui.Run(window, AppActions.HighlightUsages);
        Ui.Settle();
        Assert.Equal(3, editor.UsageHighlights.Count);

        Ui.Press(window, "DOWN");
        Ui.Press(window, "DOWN");
        Ui.Settle();
        editor.ApplyEdits([new TextEdit(0, 0, "// Totals of squares.\n")]);
        Ui.Settle();
        Assert.True(editor.UsagesKept);
        Assert.Equal(["total", "total", "total"], editor.UsageHighlights.Select(u => TextOf(editor, u.Range)));
        Assert.Equal([5, 8, 10], editor.UsageHighlights.Select(u => editor.Document.Snapshot.GetLineFromOffset(u.Range.Start)));

        Ui.Press(window, "ESCAPE");
        Assert.Empty(editor.UsageHighlights);
        Assert.False(editor.UsagesKept);
        Assert.DoesNotContain(editor.StripeMarks, m => m.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage);
        window.Close();
    }

    [AvaloniaFact]
    public void A_bracket_at_the_caret_and_its_pair_are_highlighted_nested_and_across_lines()
    {
        MainWindow window = project.Open("Braces.cs", languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        int outer = Braces.IndexOf("((", StringComparison.Ordinal);
        int closing = Braces.IndexOf("));", StringComparison.Ordinal);

        Assert.Equal((outer, closing + 1), PairAt(window, editor, outer));
        Assert.Equal((outer + 1, Braces.IndexOf(") *", StringComparison.Ordinal)), PairAt(window, editor, outer + 1));
        Assert.Equal((closing, Braces.IndexOf("(a + 2", StringComparison.Ordinal)), PairAt(window, editor, closing + 1));
        Assert.Equal((closing + 1, outer), PairAt(window, editor, closing + 2));
        Assert.Null(PairAt(window, editor, Braces.IndexOf("return", StringComparison.Ordinal) + 2));

        int body = Braces.IndexOf("    {\n        return", StringComparison.Ordinal) + 4;
        int end = Braces.IndexOf("    }\n}", StringComparison.Ordinal) + 4;
        Assert.Equal((body, end), PairAt(window, editor, body));
        Color brace = Token(window, TokenKeys.Editor_MatchedBrace);
        Assert.Equal(brace, Ui.Pixel(window, InBox(window, editor, body)));
        Assert.Equal(brace, Ui.Pixel(window, InBox(window, editor, end)));
        Assert.NotEqual(brace, Ui.Pixel(window, InBox(window, editor, Braces.LastIndexOf('}'))));

        int statement = Braces.IndexOf("return", StringComparison.Ordinal);
        editor.ApplyEdits([new TextEdit(statement, 0, "(")]);
        editor.Selections = SelectionSet.Single(statement + 1);
        Ui.Settle();
        Assert.Null(editor.MatchedBraces);
        window.Close();
    }

    [AvaloniaFact]
    public void Move_caret_to_matching_brace_goes_to_the_pair_and_back()
    {
        MainWindow window = project.Open("Braces.cs", languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        int outer = Braces.IndexOf("((", StringComparison.Ordinal);
        int closing = Braces.IndexOf("));", StringComparison.Ordinal) + 1;
        editor.Selections = SelectionSet.Single(outer);
        Ui.Run(window, EditorActions.MatchBrace);
        Assert.Equal(closing, editor.Selections.Primary.Caret);
        Ui.Run(window, EditorActions.MatchBrace);
        Assert.Equal(outer, editor.Selections.Primary.Caret);

        int body = Braces.IndexOf("    {\n        return", StringComparison.Ordinal) + 4;
        editor.Selections = SelectionSet.Single(body + 1);
        Ui.Run(window, EditorActions.MatchBrace);
        Assert.Equal(new TextPosition(5, 4), editor.CaretPosition);
        window.Close();
    }

    /// <summary>The pair the editor highlights with the caret at an offset, as (the bracket the caret touches, its pair).</summary>
    private static (int Touched, int Matching)? PairAt(MainWindow window, CodeEditor editor, int caret)
    {
        editor.Selections = SelectionSet.Single(caret);
        Ui.Settle();
        return editor.MatchedBraces is { } pair ? (pair.Touched.Start, pair.Matching.Start) : null;
    }

    private static string TextOf(CodeEditor editor, TextRange range) => editor.Document.Snapshot.GetText(range);

    /// <summary>A point inside the box drawn behind the character at an offset: at its top, above the glyph, and at its
    /// right, clear of a caret before it.</summary>
    private static Point InBox(MainWindow window, CodeEditor editor, int offset) =>
        editor.TranslatePoint(new Point(editor.PointOf(offset + 1).X - 2, editor.PointOf(offset).Y + 1), window)!.Value;

    private static Color Token(MainWindow window, string key) =>
        Avalonia.Application.Current!.TryFindResource(key, window.ActualThemeVariant, out object? brush) && brush is ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException($"The {window.ActualThemeVariant} theme has no {key}");

    /// <summary>A small project on disk, restored once for every test, and the warm server they share on it.</summary>
    public sealed class Project : SharedProject
    {
        public Project()
            : base("caret-highlights-")
        {
            File.WriteAllText(PathOf("Sample.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Library</OutputType>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(PathOf("Totals.cs"), Totals);
            File.WriteAllText(PathOf("Braces.cs"), Braces);
            Prepare();
        }
    }
}
