using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Passenger.App.Testing;
using Passenger.App.Vcs;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>Every state the editor tells apart by colour is told apart by shape as well: drawn one at a time in one place
/// with each family's colours made one grey, an error's, a warning's and a suggestion's lines, the gutter's change marks,
/// a diff's signs and a test's marks still differ in the pixels they ink, so a person who cannot tell their colours apart
/// can; and
/// every state of each family, a changed file's kind too, has a shape, sign or letter of its own, a state added later held
/// to one as well.</summary>
public sealed class ColourAloneTests
{
    private const string Grey = "#808080";
    /// <summary>Lines of spaces, so nothing but the marks inks them.</summary>
    private static readonly string Text = string.Concat(Enumerable.Range(0, 16).Select(_ => new string(' ', 26) + "\n"));

    [AvaloniaFact]
    public void An_error_a_warning_and_a_suggestion_ink_different_lines_in_one_colour()
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

    [AvaloniaFact]
    public void The_gutters_added_changed_conflicted_and_deleted_marks_ink_different_shapes_in_one_colour()
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

    [AvaloniaFact]
    public void A_diffs_added_removed_changed_and_conflicted_lines_ink_different_signs_in_one_colour()
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

    [AvaloniaFact]
    public void A_tests_marks_before_its_run_passed_failed_and_running_ink_different_shapes_in_one_colour()
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

    [Fact]
    public void Every_kind_of_problem_has_a_line_of_its_own() =>
        Apart(Enum.GetValues<MarkerKind>().Where(k => k != MarkerKind.Unnecessary), Underlines.Of);

    [Fact]
    public void Every_kind_of_change_has_a_gutter_shape_and_a_diff_sign_of_its_own()
    {
        Apart(Enum.GetValues<ChangeMarkKind>(), ChangeColumn.ShapeOf);
        Apart(Enum.GetValues<ChangeMarkKind>(), DiffSigns.SignOf);
    }

    [Fact]
    public void Every_test_mark_has_a_shape_of_its_own() => Apart(Enum.GetValues<TestMarkKind>(), TestMarks.ShapeOf);

    [Fact]
    public void Every_changed_files_kind_has_a_letter_and_a_word_of_its_own()
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
        Rect area = place();
        Window window = rig.Window;
        CodeEditor editor = rig.Editor;
        // The editor's ground, past the end of a line of spaces, where nothing is drawn.
        Color ground = Ui.Pixel(window, editor.TranslatePoint(editor.PointOf(30) + new Vector(editor.LineHeight * 4, editor.LineHeight / 2), window)!.Value);
        Point origin = editor.TranslatePoint(area.TopLeft, window)!.Value;
        double step = 1 / window.RenderScaling;
        int columns = (int)Math.Floor(area.Width / step);
        int rows = (int)Math.Floor(area.Height / step);
        List<Point> points = [];
        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                points.Add(origin + new Vector((column + 0.5) * step, (row + 0.5) * step));
            }
        }
        Color[] seen = Ui.Pixels(window, points);
        return string.Join("/", Enumerable.Range(0, rows).Select(row => new string([.. Enumerable.Range(0, columns).Select(column =>
        {
            Color c = seen[(row * columns) + column];
            return Math.Abs(c.R - ground.R) + Math.Abs(c.G - ground.G) + Math.Abs(c.B - ground.B) > 90 ? '#' : '.';
        })])));
    }

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
