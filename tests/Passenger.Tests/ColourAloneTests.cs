using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Passenger.App.Editors;
using Passenger.App.Testing;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Every state the editor tells apart by colour is told apart by shape as well: drawn one at a time in one place
/// with each family's colours made one grey, an error's, a warning's and a suggestion's lines, the gutter's and the
/// scrollbar stripe's change marks, a diff's signs, a test's marks and a line's coverage marks (and those of the tests
/// running it) still differ in the pixels they ink, so a person who cannot tell their colours apart can; and every state of
/// each family, a changed file's kind and a run's state too, has a shape, sign or letter of its own, a state added later
/// held to one as well.</summary>
public sealed class ColourAloneTests
{
    private const string Grey = "#808080";
    /// <summary>Lines of spaces, so nothing but the marks inks them.</summary>
    private static readonly string Text = string.Concat(Enumerable.Range(0, 16).Select(_ => new string(' ', 26) + "\n"));

    [AvaloniaFact]
    public void Each_state_inks_its_own_line_shape_or_sign_in_one_colour_in_the_gutter_the_stripe_a_diff_and_the_tests() => Cases.All(
        ("An error a warning and a suggestion ink different lines in one colour", An_error_a_warning_and_a_suggestion_ink_different_lines_in_one_colour),
        ("The gutters added changed conflicted and deleted marks ink different shapes in one colour", The_gutters_added_changed_conflicted_and_deleted_marks_ink_different_shapes_in_one_colour),
        ("A diffs added removed changed and conflicted lines ink different signs in one colour", A_diffs_added_removed_changed_and_conflicted_lines_ink_different_signs_in_one_colour),
        ("A tests marks before its run passed failed and running ink different shapes in one colour", A_tests_marks_before_its_run_passed_failed_and_running_ink_different_shapes_in_one_colour),
        ("A lines coverage marks and those of the tests running it ink different shapes in one colour", A_lines_coverage_marks_and_those_of_the_tests_running_it_ink_different_shapes_in_one_colour),
        ("The stripes added changed conflicted and deleted marks ink different shapes in one colour", The_stripes_added_changed_conflicted_and_deleted_marks_ink_different_shapes_in_one_colour));

    private void An_error_a_warning_and_a_suggestion_ink_different_lines_in_one_colour()
    {
        EditorRig rig = OneColour(TokenKeys.Editor_Error, TokenKeys.Editor_Warning, TokenKeys.Editor_Info);
        CodeEditor editor = rig.Editor;
        Point start = editor.PointOf(0);
        Rect under = new(start.X, start.Y + editor.LineHeight - 6, editor.PointOf(20).X - start.X, 6);
        AllApart(((MarkerKind[])[MarkerKind.Error, MarkerKind.Warning, MarkerKind.Info]).Select(kind =>
        {
            editor.SetMarkers([new Marker(new TextRange(0, 20), kind, "said")]);
            return (kind.ToString(), Inked(rig, () => under));
        }), "the lines under code");
    }

    private void The_gutters_added_changed_conflicted_and_deleted_marks_ink_different_shapes_in_one_colour()
    {
        EditorRig rig = OneColour(TokenKeys.Vcs_Added, TokenKeys.Vcs_Modified, TokenKeys.Vcs_Conflict, TokenKeys.Vcs_Deleted);
        CodeEditor editor = rig.Editor;
        Strip changes = new(new ChangeColumn(editor));
        editor.SetGutterColumns([], [changes]);
        AllApart(Enum.GetValues<ChangeMarkKind>().Select(kind =>
        {
            // Three lines from the second, or a deletion between the second and the third.
            editor.SetChangeMarks([new ChangeMark(kind == ChangeMarkKind.Deleted ? 2 : 1, kind == ChangeMarkKind.Deleted ? 0 : 3, kind)]);
            return (kind.ToString(), Inked(rig, () => changes.Beside(editor, 0, 5)));
        }), "the gutter's change marks");
    }

    private void A_diffs_added_removed_changed_and_conflicted_lines_ink_different_signs_in_one_colour()
    {
        EditorRig rig = OneColour(TokenKeys.Vcs_Added, TokenKeys.Vcs_Modified, TokenKeys.Vcs_Conflict, TokenKeys.Vcs_Deleted);
        CodeEditor editor = rig.Editor;
        Strip signs = new(new DiffSigns(editor));
        editor.SetGutterColumns([], [signs]);
        AllApart(Enum.GetValues<ChangeMarkKind>().Select(kind =>
        {
            editor.SetChangeMarks([new ChangeMark(1, 1, kind)]);
            return (kind.ToString(), Inked(rig, () => signs.Beside(editor, 1, 1)));
        }), "a diff's signs");
    }

    private void A_tests_marks_before_its_run_passed_failed_and_running_ink_different_shapes_in_one_colour()
    {
        EditorRig rig = OneColour(TokenKeys.Test_Passed, TokenKeys.Run, TokenKeys.Test_Failed, TokenKeys.Editor_ExecutionPoint);
        CodeEditor editor = rig.Editor;
        Marks marks = new();
        Strip strip = new(marks);
        editor.SetGutterColumns([], [strip]);
        AllApart(Enum.GetValues<TestMarkKind>().Select(kind =>
        {
            marks.Kind = kind;
            return (kind.ToString(), Inked(rig, () => strip.Beside(editor, 1, 1)));
        }), "a test's gutter marks");
    }

    private void A_lines_coverage_marks_and_those_of_the_tests_running_it_ink_different_shapes_in_one_colour()
    {
        EditorRig rig = OneColour(TokenKeys.Test_Passed, TokenKeys.Editor_Warning, TokenKeys.Test_Failed);
        CodeEditor editor = rig.Editor;
        CoverageMarks marks = new();
        Strip strip = new(marks);
        editor.SetGutterColumns([], [strip]);
        AllApart(Enum.GetValues<CoverageShape>().Select(shape =>
        {
            marks.Shape = shape;
            return (shape.ToString(), Inked(rig, () => strip.Beside(editor, 1, 1)));
        }), "a line's coverage marks and those of the tests running it");
    }

    private void The_stripes_added_changed_conflicted_and_deleted_marks_ink_different_shapes_in_one_colour()
    {
        EditorRig rig = new(string.Concat(Enumerable.Range(0, 300).Select(i => $"line {i}\n")));
        foreach (string key in (string[])[TokenKeys.Vcs_Added, TokenKeys.Vcs_Modified, TokenKeys.Vcs_Conflict, TokenKeys.Vcs_Deleted])
        {
            rig.Window.Resources[key] = new SolidColorBrush(Color.Parse(Grey));
        }
        CodeEditor editor = rig.Editor;
        rig.PlaceCaret(rig.Document.Snapshot.Length);
        // The left of the track down its whole height, where a change's strip is drawn and nothing else of these.
        Rect Strip() => editor.StripeTrack.WithWidth(6);
        Color[] bare = Seen(rig, Strip());
        AllApart(Enum.GetValues<ChangeMarkKind>().Select(kind =>
        {
            editor.SetChangeMarks([new ChangeMark(100, kind == ChangeMarkKind.Deleted ? 0 : 12, kind)]);
            return (kind.ToString(), Inked(rig, Strip, bare));
        }), "the scrollbar stripe's change marks");
    }

    [Fact]
    public void Every_state_problem_change_test_mark_and_changed_file_has_a_mark_of_its_own() => Cases.All(
        ("Every run state has a mark of its own", Every_run_state_has_a_mark_of_its_own),
        ("Every kind of problem has a line of its own", Every_kind_of_problem_has_a_line_of_its_own),
        ("Every kind of change has a gutter shape and a diff sign of its own", Every_kind_of_change_has_a_gutter_shape_and_a_diff_sign_of_its_own),
        ("Every test mark has a shape of its own", Every_test_mark_has_a_shape_of_its_own),
        ("Every coverage mark has a shape of its own", Every_coverage_mark_has_a_shape_of_its_own),
        ("Every changed files kind has a letter and a word of its own", Every_changed_files_kind_has_a_letter_and_a_word_of_its_own));

    private void Every_run_state_has_a_mark_of_its_own() => Apart(Enum.GetValues<RunState>().Where(s => s != RunState.None), RunRows.ShapeOf);

    private void Every_kind_of_problem_has_a_line_of_its_own() =>
        Apart(Enum.GetValues<MarkerKind>().Where(k => k != MarkerKind.Unnecessary), Underlines.Of);

    private void Every_kind_of_change_has_a_gutter_shape_and_a_diff_sign_of_its_own()
    {
        Apart(Enum.GetValues<ChangeMarkKind>(), ChangeShapes.Of);
        Apart(Enum.GetValues<ChangeMarkKind>(), DiffSigns.SignOf);
    }

    private void Every_test_mark_has_a_shape_of_its_own() => Apart(Enum.GetValues<TestMarkKind>(), TestMarks.ShapeOf);

    private void Every_coverage_mark_has_a_shape_of_its_own()
    {
        Apart(Enum.GetValues<CoverageState>(), CoverageColumn.ShapeOf);
        Apart(Enum.GetValues<ReachState>(), CoverageColumn.ShapeOf);
    }

    private void Every_changed_files_kind_has_a_letter_and_a_word_of_its_own()
    {
        Apart(Enum.GetValues<ChangeKind>(), ChangedFileRow.LetterOf);
        Apart(Enum.GetValues<ChangeKind>(), ChangedFileRow.WordOf);
    }

    /// <summary>Fails where states of one family share a shape, a later state added to an enum held to one of its own.</summary>
    private static void Apart<TState, TShape>(IEnumerable<TState> states, Func<TState, TShape> shape)
        where TState : struct, Enum
    {
        string[] alike = [.. states.GroupBy(shape).Where(g => g.Count() > 1).Select(g => $"{string.Join(" and ", g)} are all {g.Key}")];
        Assert.True(alike.Length == 0, $"States drawn in colours of their own share a shape, so only their colours tell them apart:\n{string.Join("\n", alike)}");
    }

    /// <summary>An editor whose colours of a family are one grey, its caret on its last line, clear of the marks.</summary>
    private static EditorRig OneColour(params string[] keys)
    {
        EditorRig rig = new(Text);
        rig.PlaceCaret(Text.Length);
        foreach (string key in keys)
        {
            rig.Window.Resources[key] = new SolidColorBrush(Color.Parse(Grey));
        }
        Ui.Settle();
        return rig;
    }

    /// <summary>Which pixels of an area of the editor hold ink, the area found once what was just set is drawn: '#' for each that does,
    /// a row to a line, so two states drawn in one place in one colour ink alike only where their shapes are one.</summary>
    private static string Inked(EditorRig rig, Func<Rect> place)
    {
        Ui.Settle();
        CodeEditor editor = rig.Editor;
        // The editor's ground, past the end of a line of spaces, where nothing is drawn.
        Color ground = Ui.Pixel(rig.Window, editor.TranslatePoint(editor.PointOf(30) + new Vector(editor.LineHeight * 4, editor.LineHeight / 2), rig.Window)!.Value);
        Rect area = place();
        return Mask(Seen(rig, area), _ => ground, Columns(rig, area));
    }

    /// <summary>Which pixels of an area hold ink where it was bare before (<paramref name="bare"/>), as <see cref="Inked(EditorRig, Func{Rect})"/> says them.</summary>
    private static string Inked(EditorRig rig, Func<Rect> place, Color[] bare)
    {
        Ui.Settle();
        Rect area = place();
        return Mask(Seen(rig, area), i => bare[i], Columns(rig, area));
    }

    /// <summary>The window's pixels over an area of the editor, a row at a time, once what was just set is drawn.</summary>
    private static Color[] Seen(EditorRig rig, Rect area)
    {
        Ui.Settle();
        Window window = rig.Window;
        Point origin = rig.Editor.TranslatePoint(area.TopLeft, window)!.Value;
        double step = 1 / window.RenderScaling;
        int columns = Columns(rig, area);
        int rows = (int)Math.Floor(area.Height / step);
        List<Point> points = [];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                points.Add(origin + new Vector((column + 0.5) * step, (row + 0.5) * step));
            }
        }
        return Ui.Pixels(window, points);
    }

    private static int Columns(EditorRig rig, Rect area) => (int)Math.Floor(area.Width * rig.Window.RenderScaling);

    private static string Mask(Color[] seen, Func<int, Color> ground, int columns) =>
        string.Join("/", seen.Chunk(Math.Max(1, columns)).Select((row, r) => new string([.. row.Select((c, column) =>
        {
            Color under = ground((r * columns) + column);
            return Math.Abs(c.R - under.R) + Math.Abs(c.G - under.G) + Math.Abs(c.B - under.B) > 90 ? '#' : '.';
        })])));

    private static void AllApart(IEnumerable<(string State, string Inked)> shapes, string what)
    {
        (string State, string Inked)[] all = [.. shapes];
        Assert.All(all, s => Assert.True(s.Inked.Contains('#'), $"{s.State} inks nothing among {what}, so nothing shows it"));
        string[] alike = [.. all.SelectMany((a, i) => all.Skip(i + 1).Where(b => b.Inked == a.Inked).Select(b => $"{a.State} and {b.State} both ink {a.Inked}"))];
        Assert.True(alike.Length == 0, $"Among {what}, states told apart by colour alone, which a person who cannot tell those colours apart cannot read:\n{string.Join("\n", alike)}");
    }

    /// <summary>A column of the gutter drawn by another, which keeps where its strip stands.</summary>
    private sealed class Strip(IGutterColumn inner) : IGutterColumn
    {
        public double Left { get; private set; }

        public double Width { get; private set; }

        /// <summary>Its strip beside some lines, in the editor's coordinates.</summary>
        public Rect Beside(CodeEditor editor, int line, int lines) =>
            new(Left, editor.PointOf(line * (Text.IndexOf('\n') + 1)).Y, Width, editor.LineHeight * lines);

        public event EventHandler? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        double IGutterColumn.Width(GutterFont font) => inner.Width(font);

        public void Draw(GutterFrame frame)
        {
            Left = frame.Left;
            Width = frame.Width;
            inner.Draw(frame);
        }

        public GutterAnswer Press(GutterPress press) => inner.Press(press);
    }

    /// <summary>A line's coverage mark of one shape on the second line.</summary>
    private sealed class CoverageMarks : IGutterColumn
    {
        private CoverageShape shape;

        public CoverageShape Shape
        {
            get => shape;
            set
            {
                shape = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? Changed;

        public double Width(GutterFont font) => CoverageColumn.StripWidth;

        public void Draw(GutterFrame frame)
        {
            if (frame.First <= 1 && frame.Last >= 1)
            {
                CoverageColumn.DrawMark(frame, Shape, TokenKeys.Test_Passed, frame.Left, 1);
            }
        }

        public GutterAnswer Press(GutterPress press) => GutterAnswer.Passed;
    }

    /// <summary>A test's mark of one kind on the second line.</summary>
    private sealed class Marks : IGutterColumn
    {
        private TestMarkKind kind;

        public TestMarkKind Kind
        {
            get => kind;
            set
            {
                kind = value;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? Changed;

        public double Width(GutterFont font) => 20;

        public void Draw(GutterFrame frame)
        {
            foreach (GutterLine line in frame.Lines)
            {
                if (line.Line == 1)
                {
                    TestMarks.Draw(frame, Kind, frame.Left, line.Top);
                }
            }
        }

        public GutterAnswer Press(GutterPress press) => GutterAnswer.Passed;
    }
}
