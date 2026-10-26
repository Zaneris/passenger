using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The error stripe on the editor's scrollbar, its problems set as the language server sets them: what a mark
/// says under the pointer, how it is drawn, and that keeping the marks costs a change, not a frame.</summary>
public sealed class ErrorStripeTests
{
    private const string Unused = "CS0219: The variable is assigned but its value is never used";

    [AvaloniaFact]
    public void Hovering_the_stripe_of_a_file_with_thousands_of_warnings_works_its_marks_out_once_and_again_only_after_a_change()
    {
        const int Lines = 4000;
        EditorRig rig = new(string.Concat(Enumerable.Range(0, Lines).Select(i => $"        int value{i} = {i};\n")));
        CodeEditor editor = rig.Editor;
        TextSnapshot text = rig.Document.Snapshot;
        int built = editor.StripeMarksBuilt;
        editor.SetMarkers([.. Enumerable.Range(0, Lines).Select(i => new Marker(new TextRange(text.GetLineStart(i) + 12, 5), MarkerKind.Warning, Unused))]);
        Ui.Settle();
        Assert.Equal((0, Lines), editor.ProblemCount);
        List<CodeEditor.StripeMark> warnings = [.. editor.StripeMarks.Where(m => m.Kind == StripeMarkKind.Warning)];
        Assert.Equal(Lines, warnings.Count);

        Point middle = editor.TranslatePoint(editor.StripePoint(warnings[Lines / 2]), rig.Window)!.Value;
        Color colour = Ui.Pixel(rig.Window, middle);
        Assert.True(colour.R > 180 && colour.G is > 120 and < 200 && colour.B < 110, $"The warnings' marks are {colour}, not the warning colour");

        // Up the stripe, since its tooltip opens below the pointer.
        for (int i = Lines - 100; i > 0; i -= 100)
        {
            Point at = editor.TranslatePoint(editor.StripePoint(warnings[i]), rig.Window)!.Value;
            rig.Window.MouseMove(at);
            Ui.Settle();
            Assert.Equal(Unused, ToolTip.GetTip((Control)rig.Window.InputHitTest(at)!));
            editor.InvalidateVisual();
            Ui.Settle();
        }

        editor.Focus();
        for (int i = 0; i < 5; i++)
        {
            rig.Press("DOWN");
        }
        Assert.Equal(5, Assert.Single(editor.StripeMarks, m => m.Kind == StripeMarkKind.Caret).Row);
        Assert.True(editor.StripeMarksBuilt == built + 1,
            $"The stripe's marks were worked out {editor.StripeMarksBuilt - built} times for one set of warnings: every frame and pointer move pays for thousands of them");

        // A key that leaves every line where it was leaves every mark where it was; a line put in works them out once more.
        rig.Type("x");
        Assert.True(editor.StripeMarksBuilt == built + 1,
            $"The stripe's marks were worked out {editor.StripeMarksBuilt - built - 1} times after a key that moved no line, which left each where it was");
        rig.Press("ENTER");
        for (int i = 0; i < 10; i++)
        {
            rig.Window.MouseMove(middle + new Vector(0, i));
            Ui.Settle();
        }
        Assert.Equal(Lines, editor.StripeMarks.Count(m => m.Kind == StripeMarkKind.Warning));
        Assert.True(editor.StripeMarksBuilt == built + 2,
            $"The stripe's marks were worked out {editor.StripeMarksBuilt - built - 1} times after a line was put in, not once");
    }

    /// <summary>Keys typed above 4,000 warnings, the warnings set again moved through each as the language server's
    /// are, keep every mark and allocate on the UI thread under twice what they do above 40, and a click on a mark goes
    /// to its warning as the text stands.</summary>
    [AvaloniaFact]
    public void Keys_above_four_thousand_warnings_carried_through_them_cost_what_they_do_above_forty()
    {
        long few = BytesPerKey(40);
        long many = BytesPerKey(4000);
        TestContext.Current.TestOutputHelper?.WriteLine($"A key allocated {few:N0} bytes above 40 warnings, {many:N0} above 4,000.");
        Assert.True(many < few * 2,
            $"A key above 4,000 warnings allocated {many / 1024:N0} KB on the UI thread, above 40 {few / 1024:N0} KB: the stripe's marks and the markers' order were made again from all of them");
    }

    /// <summary>A frame over a file with a warning, an annotation, a change bar and a bookmark on each of 4,000 lines
    /// allocates on the UI thread under twice what it does over 40: what is drawn on the text is read for the lines on
    /// screen.</summary>
    [AvaloniaFact]
    public void A_frame_over_four_thousand_decorated_lines_costs_what_it_does_over_forty()
    {
        long few = BytesPerFrame(40);
        long many = BytesPerFrame(4000);
        TestContext.Current.TestOutputHelper?.WriteLine($"A frame allocated {few:N0} bytes over 40 decorated lines, {many:N0} over 4,000.");
        Assert.True(many < few * 2,
            $"A frame over 4,000 decorated lines allocated {many / 1024:N0} KB on the UI thread, over 40 {few / 1024:N0} KB: what it draws was read from every line's decorations");
    }

    /// <summary>What a blink of the caret in the middle of a file of decorated lines allocates on the UI thread.</summary>
    private static long BytesPerFrame(int lines)
    {
        EditorRig rig = new(string.Concat(Enumerable.Range(0, lines).Select(i => $"        int value{i} = {i};\n")));
        CodeEditor editor = rig.Editor;
        TextSnapshot text = rig.Document.Snapshot;
        editor.SetMarkers([.. Enumerable.Range(0, lines).Select(i => new Marker(new TextRange(text.GetLineStart(i) + 12, 5), MarkerKind.Warning, Unused))]);
        editor.SetLineAnnotations([.. Enumerable.Range(0, lines).Select(i => new LineAnnotation(i, "value = 1", LineAnnotationLook.Muted))]);
        editor.SetChangeMarks([.. Enumerable.Range(0, lines).Select(i => new ChangeMark(i, 1, i % 2 == 0 ? ChangeMarkKind.Added : ChangeMarkKind.Modified))]);
        editor.SetBookmarks([.. Enumerable.Range(0, lines)]);
        editor.Focus();
        editor.GoTo(new TextPosition(lines / 2, 0));
        return Ui.BlinkBytes(editor);
    }

    /// <summary>What a key typed at the top of a file of a warning a line allocates on the UI thread, its warnings set again
    /// moved through it as a host carries them (their moving left out, which is the host's), the stripe drawn.</summary>
    private static long BytesPerKey(int lines)
    {
        EditorRig rig = new(string.Concat(Enumerable.Range(0, lines).Select(i => $"        int value{i} = {i};\n")));
        CodeEditor editor = rig.Editor;
        TextSnapshot text = rig.Document.Snapshot;
        editor.SetMarkers([.. Enumerable.Range(0, lines).Select(i => new Marker(new TextRange(text.GetLineStart(i) + 12, 5), MarkerKind.Warning, Unused))]);
        long carrying = 0;
        rig.Document.Changed += (_, e) =>
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Marker[] moved = [.. editor.Markers.Select(m => m with { Range = TextEditing.MapRange(m.Range, e.Edits) })];
            carrying += GC.GetAllocatedBytesForCurrentThread() - before;
            editor.SetMarkers(moved);
        };
        editor.Focus();
        Ui.Settle();
        int built = editor.StripeMarksBuilt;
        const int Keys = 5;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Keys; i++)
        {
            rig.Type("x");
        }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated - carrying;
        Assert.True(editor.StripeMarksBuilt == built, $"The stripe's marks were worked out {editor.StripeMarksBuilt - built} times for keys that moved no line");
        List<CodeEditor.StripeMark> warnings = [.. editor.StripeMarks.Where(m => m.Kind == StripeMarkKind.Warning)];
        Assert.Equal(lines, warnings.Count);

        if (lines <= 40)
        {
            // Where the marks stand apart, a line's mark kept through the keys goes to its warning where they moved it.
            Point at = editor.TranslatePoint(editor.StripePoint(warnings[^1]), rig.Window)!.Value;
            rig.Window.MouseDown(at, MouseButton.Left);
            rig.Window.MouseUp(at, MouseButton.Left);
            Ui.Settle();
            Assert.Equal(text.GetLineStart(lines - 1) + 12 + Keys, editor.Selections.Primary.Caret);
        }
        return allocated / Keys;
    }

    /// <summary>A warning on each line on screen and a change down the whole file, as Git marks a new one: a click on the
    /// thumb over a warning goes to it, a drag from there drags the thumb, the track beside the change pages, and a
    /// warning's tick and the change's strip off the thumb take the caret to their lines.</summary>
    [AvaloniaFact]
    public void The_scrollbars_thumb_drags_from_over_the_marks_its_track_pages_beside_a_change_and_a_click_on_a_mark_goes_to_its_line()
    {
        const int Lines = 400;
        EditorRig rig = new(string.Concat(Enumerable.Range(0, Lines).Select(i => $"        int value{i} = {i};\n")));
        CodeEditor editor = rig.Editor;
        TextSnapshot text = rig.Document.Snapshot;
        editor.SetMarkers([.. Enumerable.Range(0, 30).Select(i => new Marker(new TextRange(text.GetLineStart(i) + 12, 5), MarkerKind.Warning, Unused))]);
        editor.SetChangeMarks([new ChangeMark(0, Lines, ChangeMarkKind.Added)]);
        Ui.Settle();
        Thumb thumb = ThumbOf(editor);
        Point grip = Centre(thumb, editor);
        Assert.Contains(editor.StripeMarks, m => m.Kind == StripeMarkKind.Warning && Math.Abs(editor.StripePoint(m).Y - grip.Y) <= 2);
        Ui.Click(editor, grip);
        int line = editor.CaretPosition.Line;
        Assert.True(line < 30 && editor.Selections.Primary.Caret == text.GetLineStart(line) + 12,
            $"A click on the thumb over the warnings left the caret at {editor.CaretPosition}, not at a warning");

        CheckBarTakesItsPresses(editor);

        // The last warning, drawn over its neighbours' ticks.
        Ui.Click(editor, editor.StripePoint(editor.StripeMarks.Last(m => m.Kind == StripeMarkKind.Warning)));
        Assert.True(editor.Selections.Primary.Caret == text.GetLineStart(29) + 12,
            $"A click on line 30's warning on the stripe left the caret on line {editor.CaretPosition.Line + 1}, not at the warning");
        editor.GoTo(new TextPosition(Lines - 1, 0));
        Ui.Settle();
        Ui.Click(editor, editor.StripePoint(Assert.Single(editor.StripeMarks, m => m.Kind == StripeMarkKind.Change)));
        Assert.True(editor.CaretPosition.Line == 0, $"A click on the change's strip left the caret on line {editor.CaretPosition.Line + 1}, not at the change's first line");
    }

    /// <summary>Holds an editor's scrollbar to taking its own presses under the stripe's marks: its thumb, pressed at its
    /// middle, drags the text down 100 px of track, a drag from beside it chooses no text, and the track below it, pressed
    /// at its middle beside a change's strip, pages it down, the caret staying where it was through all three.</summary>
    internal static void CheckBarTakesItsPresses(CodeEditor editor)
    {
        TopLevel window = TopLevel.GetTopLevel(editor)!;
        Thumb thumb = ThumbOf(editor);
        int caret = editor.Selections.Primary.Caret;
        double scrolled = editor.ScrollOffset.Y;
        Point grip = Centre(thumb, window);
        Ui.Drag(window, grip, grip + new Point(0, 100));
        double moved = Centre(thumb, window).Y - grip.Y;
        Assert.True(Math.Abs(moved - 100) <= 2,
            $"The thumb dragged 100 px down from over the stripe's marks moved {moved:0} px: the press on it was taken by a mark under it");
        Assert.True(editor.ScrollOffset.Y > scrolled, "Dragging the thumb from over the stripe's marks left the text where it was");
        Assert.True(editor.Selections.Primary.Caret == caret, $"Dragging the thumb took the caret to line {editor.CaretPosition.Line + 1}, a mark's under the thumb");

        ScrollBar bar = thumb.FindAncestorOfType<ScrollBar>()!;
        Point beside = new(Bounds(bar, window).Right - 1.5, Centre(thumb, window).Y);
        Ui.Drag(window, beside, beside + new Point(-300, 60));
        Assert.True(editor.Selections.Primary.Caret == caret && editor.Selections.Primary.IsEmpty,
            $"A drag from the bar beside its thumb chose {editor.Selections.Primary.Length} characters of the text under the bar, to line {editor.CaretPosition.Line + 1}");

        RepeatButton below = bar.GetVisualDescendants().OfType<RepeatButton>().Single(b => b.Name == "PART_PageDownButton");
        scrolled = editor.ScrollOffset.Y;
        Ui.Click(below, new Point(below.Bounds.Width / 2, 20));
        double paged = editor.ScrollOffset.Y - scrolled;
        Assert.True(paged > editor.Bounds.Height / 2, $"A click on the track below the thumb, beside a change's strip, scrolled {paged:0} px, not a page");
        Assert.True(editor.Selections.Primary.Caret == caret, $"A click on the track took the caret to line {editor.CaretPosition.Line + 1}, a mark's beside it");
    }

    private static Thumb ThumbOf(CodeEditor editor) =>
        editor.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Orientation.Vertical).GetVisualDescendants().OfType<Thumb>().First();

    private static Point Centre(Control control, Visual within) => Bounds(control, within).Center;

    private static Rect Bounds(Control control, Visual within) =>
        new(control.TranslatePoint(default, within) ?? throw new Xunit.Sdk.XunitException($"{control.GetType().Name} is not laid out"), control.Bounds.Size);

    [AvaloniaFact]
    public void The_stripes_marks_take_the_new_themes_colours_when_the_theme_changes()
    {
        EditorRig rig = new(string.Concat(Enumerable.Range(0, 40).Select(i => $"        int value{i} = {i};\n")));
        CodeEditor editor = rig.Editor;
        TextSnapshot text = rig.Document.Snapshot;
        editor.SetMarkers([.. Enumerable.Range(0, 40).Select(i => new Marker(new TextRange(text.GetLineStart(i) + 12, 5), MarkerKind.Warning, Unused))]);
        Ui.Settle();
        CodeEditor.StripeMark mark = editor.StripeMarks.First(m => m.Kind == StripeMarkKind.Warning && m.Row == 20);
        Point at = editor.TranslatePoint(editor.StripePoint(mark), rig.Window)!.Value;
        Assert.Equal(Warning(ThemeVariant.Dark), Ui.Pixel(rig.Window, at));

        rig.Window.RequestedThemeVariant = ThemeVariant.Light;
        Ui.Settle();
        Color shown = Ui.Pixel(rig.Window, at);
        Assert.True(shown == Warning(ThemeVariant.Light), $"In the light theme the stripe's warning is {shown}, the dark theme's colour left on it");
    }

    private static Color Warning(ThemeVariant theme) =>
        Application.Current!.TryFindResource(TokenKeys.Editor_Warning, theme, out object? brush) && brush is ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException($"The {theme} theme has no warning colour");
}
