using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.App;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>What the caret lights up in a C# file, through the whole window on a real project: the uses of the symbol it
/// rests on from the language server, writes apart from reads and marked on the scrollbar, gone the moment the caret
/// leaves them or the text changes; a word chosen that the server lights nothing for (a keyword, a word in a comment)
/// lighting its other places by their text; Highlight Usages in File keeping them; and the bracket at the caret with its
/// pair, which Move Caret to Matching Brace goes between, from the file's own parse, in windows with no language server.</summary>
[SameOnEverySystem]
public sealed class CaretHighlightsTests : IClassFixture<CaretHighlightsTests.Project>
{
    private const string Totals = "static class Totals\n{\n    public static int Sum(int[] values)\n    {\n        int total = 0;\n"
        + "        foreach (int value in values)\n        {\n            total += value * (value + 1);\n        }\n        return total;\n    }\n}\n";

    private const string Shapes = """
        sealed class Shapes
        {
            #region Sizes
            private int count;

            public int Area(int side)
            {
                int total = side * side;
                if (total > 10)
                {
                    total -= count;
                }
                return total;
            }
            #endregion

            public string Name => "shapes";
            public string Describe() => Name;
        }

        """;

    private const string Braces = "static class Braces\n{\n    public static int Deep(int a)\n    {\n        return ((a + 1) * (a + 2));\n    }\n}\n";

    private const string MediatorField = "_mediator";
    private static readonly string PricesController = """
        sealed partial class PricesControllerV2
        {
            private readonly IMediator _mediator;

            public PricesControllerV2(IMediator mediator)
            {
                _mediator = mediator;
            }

            // An unrelated _mediator in a different scope.
            public string Preview(IMediator _mediator) => _mediator.Send("_mediator");
        """ + new string('\n', 80) + """
            public string ReadPrice() => this._mediator.Send("price");
        }

        interface IMediator
        {
            string Send(string request);
        }

        """;

    private const string Words = "static class Words\n{\n    // The public count of a text's words, publicly said: Public in a title.\n"
        + "    public static int Count(string text)\n    {\n        int count = 0;\n        foreach (string word in text.Split(' '))\n        {\n"
        + "            count += 1;\n        }\n        return count;\n    }\n\n    public static readonly string Said = \"count every public word\";\n}\n";

    private readonly Project project;

    public CaretHighlightsTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>What the caret lights: a symbol's writes and reads with their marks on the scrollbar, gone as it moves
    /// off and never left on text typing changed, kept by Highlight Usages in File until Escape, and a bracket's pair,
    /// which Move Caret to Matching Brace goes to; an answer before the file's first parse waits without holding the
    /// window's thread; and the moves by the code's structure, the uses lit among them.</summary>
    [AvaloniaFact]
    public void The_caret_lights_a_symbols_uses_and_a_brackets_pair_follows_moves_and_typing_and_never_holds_the_window()
    {
        Flow.StepApart("resting the caret on a local highlights its writes and reads and marks them on the scrollbar", () => new CaretHighlightsTests(project), t => t.Resting_the_caret_on_a_local_highlights_its_writes_and_reads_and_marks_them_on_the_scrollbar());
        Flow.StepApart("selecting _mediator in either direction marks its occurrences and lets the scrollbar reveal the off-screen read (#2387)", () => new CaretHighlightsTests(project), t => t.Selecting_the_mediator_field_marks_its_occurrences_in_this_file());
        Flow.StepApart("a selected C# identifier is never highlighted by spelling when no server can resolve it", () => new CaretHighlightsTests(project), t => t.A_selected_identifier_is_never_highlighted_by_spelling());
        Flow.StepApart("highlights answered before a file's first parse wait for it without holding the window's thread", () => new CaretHighlightsTests(project), t => t.Highlights_answered_before_a_files_first_parse_wait_for_it_without_holding_the_windows_thread());
        Flow.StepApart("moving the caret off a symbol takes its highlights away at once, and another symbol brings its own", () => new CaretHighlightsTests(project), t => t.Moving_the_caret_off_a_symbol_takes_its_highlights_away_at_once_and_another_symbol_brings_its_own());
        Flow.StepApart("typing never leaves a highlight on text it changed, and the uses come back where they now are", () => new CaretHighlightsTests(project), t => t.Typing_never_leaves_a_highlight_on_text_it_changed_and_the_uses_come_back_where_they_now_are());
        Flow.StepApart("Highlight Usages in File keeps the uses as the caret moves and the text changes, until Escape", () => new CaretHighlightsTests(project), t => t.Highlight_usages_in_file_keeps_the_uses_as_the_caret_moves_and_the_text_changes_until_escape());
        Flow.StepApart("a bracket at the caret and its pair are highlighted, nested and across lines", () => new CaretHighlightsTests(project), t => t.A_bracket_at_the_caret_and_its_pair_are_highlighted_nested_and_across_lines());
        Flow.StepApart("Move Caret to Matching Brace goes to the pair and back", () => new CaretHighlightsTests(project), t => t.Move_caret_to_matching_brace_goes_to_the_pair_and_back());
        Flow.StepApart("the moves by the code's structure go member to member, use to use, out to the declaration and the block around the caret, and to a region", () => new CaretHighlightsTests(project), t => t.The_moves_by_the_codes_structure_go_member_to_member_use_to_use_out_to_the_declaration_and_the_block_around_the_caret_and_to_a_region());
    }

    private void Resting_the_caret_on_a_local_highlights_its_writes_and_reads_and_marks_them_on_the_scrollbar()
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

        // Each use's mark stands at the row its use is drawn on, below any signposts' rows above the lines.
        CodeEditor.StripeMark writeMark = Assert.Single(editor.StripeMarks, m => m.Kind == StripeMarkKind.WriteUsage && m.Item == 1);
        CodeEditor.StripeMark readMark = Assert.Single(editor.StripeMarks, m => m.Kind == StripeMarkKind.Usage);
        Assert.Equal((Ui.RowOf(editor, write.Range.Start), Ui.RowOf(editor, read.Range.Start)), (writeMark.Row, readMark.Row));
        Assert.Equal(Token(window, TokenKeys.Editor_UsageWriteMark), Ui.Pixel(window, editor.TranslatePoint(editor.StripePoint(writeMark), window)!.Value));
        Assert.Equal(Token(window, TokenKeys.Editor_UsageReadMark), Ui.Pixel(window, editor.TranslatePoint(editor.StripePoint(readMark), window)!.Value));
        window.Close();
    }

    private void A_selected_identifier_is_never_highlighted_by_spelling()
    {
        MainWindow window = project.Open("PricesControllerV2.cs", languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        int comment = PricesController.IndexOf("_mediator in a different scope", StringComparison.Ordinal);
        int write = PricesController.IndexOf("_mediator = mediator", StringComparison.Ordinal);
        TextRange field = new(write, MediatorField.Length);

        editor.Selections = SelectionSet.Single(new Selection(comment, comment + field.Length));
        Ui.WaitFor(() => editor.UsageHighlights.Any(use => use.ByText && use.Range == field), "the comment's word matches the field by spelling");

        editor.Selections = SelectionSet.Single(new Selection(field.Start, field.End));
        Assert.Empty(editor.UsageHighlights);
        Ui.Settle();
        Assert.Empty(editor.UsageHighlights);
        Assert.DoesNotContain(editor.StripeMarks, m => m.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage);
        window.Close();
    }

    private void Selecting_the_mediator_field_marks_its_occurrences_in_this_file()
    {
        MainWindow window = project.Open("PricesControllerV2.cs", height: 600);
        CodeEditor editor = window.EditorArea.CodeEditor;
        int declaration = PricesController.IndexOf("_mediator;", StringComparison.Ordinal);
        int write = PricesController.IndexOf("_mediator = mediator", StringComparison.Ordinal);
        int read = PricesController.IndexOf("_mediator.Send(\"price\")", StringComparison.Ordinal);
        int length = MediatorField.Length;
        TextRange[] occurrences = [new(declaration, length), new(write, length), new(read, length)];
        int readLine = editor.Document.Snapshot.GetLineFromOffset(read);
        Assert.True(readLine > editor.LinesShown.Last);

        Ui.DoubleClick(editor, editor.PointOf(write + 2) + new Point(1, editor.LineHeight / 2));
        Assert.Equal(occurrences[1], editor.Selections.Primary.Range);
        Ui.WaitFor(() => editor.UsageHighlights.Count > 0, () => "the selected field's symbol highlights");
        Assert.Equal(occurrences, editor.UsageHighlights.Select(use => use.Range));
        Assert.All(editor.UsageHighlights, use => Assert.False(use.ByText));
        Assert.Equal(Token(window, TokenKeys.Editor_UsageRead), Ui.Pixel(window, InBox(window, editor, declaration)));

        CodeEditor.StripeMark[] marks = [.. editor.StripeMarks.Where(m => m.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage)];
        Assert.Equal(occurrences.Select(range => range.Start), marks.Select(m => m.Offset));
        Assert.Equal([StripeMarkKind.Usage, StripeMarkKind.WriteUsage, StripeMarkKind.Usage], marks.Select(m => m.Kind));
        Color readMark = Token(window, TokenKeys.Editor_UsageReadMark);
        Color writeMark = Token(window, TokenKeys.Editor_UsageWriteMark);
        Point[] points = [.. marks.Select(mark => editor.TranslatePoint(editor.StripePoint(mark), window)!.Value)];
        Assert.Equal([readMark, writeMark, readMark], Ui.Pixels(window, points));

        Ui.Click(editor, editor.StripePoint(marks[2]));
        Assert.Equal(read, editor.Selections.Primary.Caret);
        Assert.InRange(readLine, editor.LinesShown.First, editor.LinesShown.Last);
        Assert.Equal(occurrences, editor.UsageHighlights.Select(use => use.Range));
        Assert.Equal(Token(window, TokenKeys.Editor_UsageRead), Ui.Pixel(window, InBox(window, editor, read)));

        // Extending the selection while its caret stays at the field's end clears the old highlights.
        editor.Selections = SelectionSet.Single(new Selection(read, read + length));
        editor.Selections = SelectionSet.Single(new Selection(read - "this.".Length, read + length));
        Assert.Empty(editor.UsageHighlights);
        Ui.Settle();
        Assert.Empty(editor.UsageHighlights);
        Assert.DoesNotContain(editor.StripeMarks, m => m.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage);

        editor.Selections = SelectionSet.Single(new Selection(read + length, read));
        Ui.Settle();
        Assert.Equal(occurrences, editor.UsageHighlights.Select(use => use.Range));
        window.Close();
    }

    /// <summary>The server's highlights for the caret in a file whose first parse has not landed wait for that parse
    /// without holding the window's thread, which goes on running meanwhile: with the file's parse held, its highlights
    /// come once the parse lands, and the window's thread never waited for it, where it used to stand still until then.</summary>
    private void Highlights_answered_before_a_files_first_parse_wait_for_it_without_holding_the_windows_thread()
    {
        MainWindow window = project.Open("Totals.cs");
        TaskCompletionSource parse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int waits = CSharpHighlighter.FirstParseWaits;
        try
        {
            // Let go of from apart, so a wait on the window's thread ends and is counted rather than waiting for good.
            Task landed = Waiting.Apart(async () =>
            {
                await Waiting.ForAsync(TimeSpan.FromSeconds(3), "the server answering the caret's highlights while the file's first parse is held");
                parse.TrySetResult();
            });
            CSharpHighlighter.HeldParses = parse.Task;
            window.OpenFile(project.PathOf("Braces.cs"));
            CSharpHighlighter.HeldParses = null;
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.Click(editor, editor.PointOf(Braces.IndexOf("int a", StringComparison.Ordinal) + 4) + new Point(1, editor.LineHeight / 2), settle: false);
            Ui.PumpUntil(() => editor.UsageHighlights.Count == 3 && landed.IsCompleted, "the parameter's three highlights, once the file's first parse landed", TimeSpan.FromMinutes(1));
            Assert.True(CSharpHighlighter.FirstParseWaits == waits,
                $"The window's thread stood still {CSharpHighlighter.FirstParseWaits - waits} times waiting for a file's first parse, while the server's answer for its caret waited for it");
        }
        finally
        {
            CSharpHighlighter.HeldParses = null;
            parse.TrySetResult();
        }
        window.Close();
    }

    private void Moving_the_caret_off_a_symbol_takes_its_highlights_away_at_once_and_another_symbol_brings_its_own()
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

    private void Typing_never_leaves_a_highlight_on_text_it_changed_and_the_uses_come_back_where_they_now_are()
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

    private void Highlight_usages_in_file_keeps_the_uses_as_the_caret_moves_and_the_text_changes_until_escape()
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
        editor.ApplyEdits([new TextEdit(editor.UsageHighlights[1].Range.Start, 0, "x")]);
        Ui.Settle();
        Assert.True(editor.UsagesKept);
        Assert.True(editor.UsageHighlights.All(u => TextOf(editor, u.Range) == "total"),
            $"Text typed at a kept use's start was taken into it: {string.Join(", ", editor.UsageHighlights.Select(u => TextOf(editor, u.Range)))}");

        Ui.Press(window, "ESCAPE");
        Assert.Empty(editor.UsageHighlights);
        Assert.False(editor.UsagesKept);
        Assert.DoesNotContain(editor.StripeMarks, m => m.Kind is StripeMarkKind.Usage or StripeMarkKind.WriteUsage);
        window.Close();
    }

    private void A_bracket_at_the_caret_and_its_pair_are_highlighted_nested_and_across_lines()
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

    private void Move_caret_to_matching_brace_goes_to_the_pair_and_back()
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

    /// <summary>Method Up and Down, Next and Previous Usage, Go to and Select Containing Declaration, Code Block Start and End
    /// (the selection drawn out with them) and Go to Region, each from its keys in the keymap in force where it has some.</summary>
    private void The_moves_by_the_codes_structure_go_member_to_member_use_to_use_out_to_the_declaration_and_the_block_around_the_caret_and_to_a_region()
    {
        MainWindow window = project.Open("Shapes.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        int At(string text, int past = 0) => Shapes.IndexOf(text, StringComparison.Ordinal) + past;
        int Moved(string action, int from)
        {
            editor.Selections = SelectionSet.Single(from);
            Run(action);
            return editor.Selections.Primary.Caret;
        }
        void Run(string action)
        {
            if (window.Actions.Keymap.ShortcutsFor(action) is [var key, ..])
            {
                Ui.Press(window, key.Notation());
            }
            else
            {
                Assert.Null(window.Actions.Run(action)?.Shown);
                Ui.Settle();
            }
        }

        int inner = At("total -= count");
        Assert.Equal(At("public int Area"), Moved(AppActions.MethodUp, inner));
        Run(AppActions.MethodUp);
        Assert.Equal(At("private int count"), editor.Selections.Primary.Caret);
        Assert.Equal(At("public string Name"), Moved(AppActions.MethodDown, inner));

        editor.Selections = SelectionSet.Single(inner + 1);
        Ui.WaitFor(() => editor.UsageHighlights.Count == 4, () => $"the uses of total highlighted, not {editor.UsageHighlights.Count}");
        Run(AppActions.NextUsage);
        Assert.Equal(At("total;"), editor.Selections.Primary.Caret);
        Run(AppActions.PreviousUsage);
        Assert.Equal(inner, editor.Selections.Primary.Caret);

        Assert.Equal(At("Area"), Moved(AppActions.GoToContainingDeclaration, inner + "total -= ".Length));
        Run(AppActions.GoToContainingDeclaration);
        Assert.Equal(At("Shapes"), editor.Selections.Primary.Caret);
        editor.Selections = SelectionSet.Single(inner);
        Run(AppActions.SelectContainingDeclaration);
        Assert.Equal((At("public int Area"), At("    #endregion") - 1), (editor.Selections.Primary.Start, editor.Selections.Primary.End));

        int ifBlock = At("{\n            total");
        Assert.Equal(ifBlock, Moved(AppActions.CodeBlockStart, inner));
        Run(AppActions.CodeBlockStart);
        Assert.Equal(At("{\n        int total"), editor.Selections.Primary.Caret);
        Assert.Equal(At("}\n        return") + 1, Moved(AppActions.CodeBlockEnd, inner));
        editor.Selections = SelectionSet.Single(inner);
        Run(AppActions.CodeBlockStartWithSelection);
        Assert.Equal((inner, ifBlock), (editor.Selections.Primary.Anchor, editor.Selections.Primary.Caret));

        Assert.Equal(At("#region"), Moved(AppActions.GoToRegion, inner));

        int nameRead = At("Name;");
        editor.Selections = SelectionSet.Single(new Selection(nameRead, nameRead + "Name".Length));
        Ui.Settle();
        Assert.Equal([At("Name =>"), nameRead], editor.UsageHighlights.Select(use => use.Range.Start));
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
            File.WriteAllText(PathOf("PricesControllerV2.cs"), PricesController);
            File.WriteAllText(PathOf("PricesControllerV2.Other.cs"), """
                sealed partial class PricesControllerV2
                {
                    public string ReadFromAnotherFile() => _mediator.Send("other");
                }
                """);
            File.WriteAllText(PathOf("Braces.cs"), Braces);
            File.WriteAllText(PathOf("Words.cs"), Words);
            File.WriteAllText(PathOf("Shapes.cs"), Shapes);
        }
    }
}
