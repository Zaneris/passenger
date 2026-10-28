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

    /// <summary>Keys typed above 4,000 warnings, which the document carries through each, keep every mark and allocate on
    /// the UI thread under twice what they do above 40, and so does an Enter, which moves every mark down a row; a click on
    /// a mark goes to its warning as the text stands.</summary>
    [AvaloniaFact]
    public void Keys_and_an_Enter_above_four_thousand_warnings_cost_what_they_do_above_forty()
    {
        (long fewKey, long fewEnter) = BytesPerKey(40);
        (long manyKey, long manyEnter) = BytesPerKey(4000);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A key allocated {fewKey:N0} bytes above 40 warnings, {manyKey:N0} above 4,000; an Enter {fewEnter:N0} and {manyEnter:N0}.");
        Assert.True(manyKey < fewKey * 2,
            $"A key above 4,000 warnings allocated {manyKey / 1024:N0} KB on the UI thread, above 40 {fewKey / 1024:N0} KB: the markers, the stripe's marks or their order were made again from all of them");
        Assert.True(manyEnter < fewEnter * 2,
            $"An Enter above 4,000 warnings allocated {manyEnter / 1024:N0} KB on the UI thread, above 40 {fewEnter / 1024:N0} KB: the stripe's marks or their messages were made anew for all of them");
    }

    /// <summary>With soft wrap on, keys typed into a wrapped line above 4,000 warnings on wrapped lines, which leave every line
    /// on as many rows, keep every mark where it was: the stripe's marks are never worked out again, and a key allocates on
    /// the UI thread under twice what it does above 40. Edits in wrapped lines whose words are matches, which move their rows,
    /// leave each mark where it now stands.</summary>
    [AvaloniaFact]
    public void Keys_in_a_wrapped_line_above_four_thousand_wrapped_warnings_keep_the_stripe_and_cost_what_they_do_above_forty()
    {
        (long few, int fewBuilt) = BytesPerWrappedKey(40);
        (long many, int manyBuilt) = BytesPerWrappedKey(4000);
        TestContext.Current.TestOutputHelper?.WriteLine($"Wrapped, a key allocated {few:N0} bytes above 40 warnings, {many:N0} above 4,000.");
        Assert.True(fewBuilt == 0 && manyBuilt == 0,
            $"Wrapped, the stripe's marks were worked out {fewBuilt} times for keys above 40 warnings and {manyBuilt} above 4,000, the keys moving no mark's row");
        Assert.True(many < few * 2,
            $"Wrapped, a key above 4,000 warnings allocated {many / 1024:N0} KB on the UI thread, above 40 {few / 1024:N0} KB: the stripe's marks were made again for all of them");
    }

    /// <summary>Long lines that wrap onto several rows at the rig's width, all as long, a warning on each, and a few words
    /// short of another row.</summary>
    private static string Wrapped(int lines) =>
        string.Concat(Enumerable.Range(0, lines).Select(i => $"        int value{i:0000} = {i:0000}; // {string.Concat(Enumerable.Repeat("words ", 40))}end\n"));

    /// <summary>What a key typed at the start of the first of some wrapped lines with a warning each allocates on the UI
    /// thread, the stripe drawn after each, and how many times the keys had its marks worked out; above 40, with every word
    /// then a match, edits anywhere in the lines hold each mark to where it stands after each.</summary>
    private static (long Key, int Built) BytesPerWrappedKey(int lines)
    {
        EditorRig rig = new(Wrapped(lines));
        CodeEditor editor = rig.Editor;
        editor.SoftWrap = true;
        TextSnapshot text = rig.Document.Snapshot;
        editor.SetMarkers([.. Enumerable.Range(0, lines).Select(i => new Marker(new TextRange(text.GetLineStart(i) + 12, 9), MarkerKind.Warning, Unused))]);
        editor.Focus();
        Ui.Settle();
        int[] rows = [.. editor.StripeMarks.Where(m => m.Kind == StripeMarkKind.Warning).Select(m => m.Row)];
        int each = rows[1] - rows[0];
        Assert.True(each > 1 && rows.Select((row, line) => row == line * each).All(same => same), $"The lines do not each wrap onto {each} rows: [{string.Join(", ", rows.Take(5))}…]");
        int built = editor.StripeMarksBuilt;
        const int Keys = 5;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Keys; i++)
        {
            rig.Type("x");
        }
        long keys = GC.GetAllocatedBytesForCurrentThread() - allocated;
        built = editor.StripeMarksBuilt - built;
        Assert.Equal(rows, editor.StripeMarks.Where(m => m.Kind == StripeMarkKind.Warning).Select(m => m.Row));

        if (lines <= 40)
        {
            // Every word a match, which keys anywhere push across their rows' breaks: kept or worked out, each mark is where it stands.
            string all = rig.Document.Snapshot.ToString();
            editor.SetHighlights([.. Enumerable.Range(0, all.Length - 5).Where(at => string.CompareOrdinal(all, at, "words", 0, 5) == 0).Select(at => new TextRange(at, 5))], 0);
            Ui.Settle();
            Random random = new(561);
            string[] typed = ["x", " ", "words ", "a long word "];
            for (int i = 0; i < 200; i++)
            {
                LineSpan line = rig.Document.Snapshot.GetLine(random.Next(lines));
                int at = line.Start + random.Next(line.Length + 1);
                rig.PlaceCaret(at);
                if (at > line.Start && random.Next(3) == 0)
                {
                    rig.Press("BACK_SPACE");
                }
                else
                {
                    rig.Type(typed[random.Next(typed.Length)]);
                }
                List<string> kept = Shown(editor);
                editor.SetBookmarks([i % lines]);
                Ui.Settle();
                List<string> fresh = Shown(editor);
                int differs = kept.Zip(fresh).TakeWhile(pair => pair.First == pair.Second).Count();
                Assert.True(kept.Count == fresh.Count && differs == kept.Count,
                    $"After {i + 1} edits the stripe's mark {differs} of {kept.Count} stood as {kept.ElementAtOrDefault(differs)}, where worked out afresh, of {fresh.Count}, it is {fresh.ElementAtOrDefault(differs)}");
            }
        }
        return (keys / Keys, built);
    }

    /// <summary>The stripe's marks but the caret's and the bookmarks', each by its kind and rows.</summary>
    private static List<string> Shown(CodeEditor editor) =>
        [.. editor.StripeMarks.Where(m => m.Kind is not (StripeMarkKind.Caret or StripeMarkKind.Bookmark)).Select(m => $"{m.Kind} at row {m.Row}, {m.Rows} high")];

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

    /// <summary>What a key and an Enter typed at the top of a file of a warning a line allocate on the UI thread, the
    /// warnings set once, as a host sets them when its server reports, the stripe drawn after each.</summary>
    private static (long Key, long Enter) BytesPerKey(int lines)
    {
        EditorRig rig = new(string.Concat(Enumerable.Range(0, lines).Select(i => $"        int value{i} = {i};\n")));
        CodeEditor editor = rig.Editor;
        TextSnapshot text = rig.Document.Snapshot;
        editor.SetMarkers([.. Enumerable.Range(0, lines).Select(i => new Marker(new TextRange(text.GetLineStart(i) + 12, 5), MarkerKind.Warning, Unused))]);
        editor.Focus();
        Ui.Settle();
        int built = editor.StripeMarksBuilt;
        const int Keys = 5;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Keys; i++)
        {
            rig.Type("x");
        }
        long keys = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(editor.StripeMarksBuilt == built, $"The stripe's marks were worked out {editor.StripeMarksBuilt - built} times for keys that moved no line");
        List<CodeEditor.StripeMark> warnings = [.. editor.StripeMarks.Where(m => m.Kind == StripeMarkKind.Warning)];
        Assert.Equal(lines, warnings.Count);
        Assert.Equal(text.GetLineStart(lines - 1) + 12 + Keys, editor.Markers[lines - 1].Range.Start);

        if (lines <= 40)
        {
            // Where the marks stand apart, a line's mark kept through the keys goes to its warning where they moved it.
            Point at = editor.TranslatePoint(editor.StripePoint(warnings[^1]), rig.Window)!.Value;
            rig.Window.MouseDown(at, MouseButton.Left);
            rig.Window.MouseUp(at, MouseButton.Left);
            Ui.Settle();
            Assert.Equal(text.GetLineStart(lines - 1) + 12 + Keys, editor.Selections.Primary.Caret);
            editor.Selections = SelectionSet.Single(0);
            Ui.Settle();
        }

        // The first aside, whose redraw of thousands of marks allocates once what later ones reuse.
        rig.Press("ENTER");
        Ui.Settle();
        const int Enters = 3;
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Enters; i++)
        {
            rig.Press("ENTER");
            Ui.Settle();
        }
        long enters = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(editor.StripeMarksBuilt == built + 1 + Enters, $"The stripe's marks were worked out {editor.StripeMarksBuilt - built} times for {1 + Enters} Enters");
        warnings = [.. editor.StripeMarks.Where(m => m.Kind == StripeMarkKind.Warning)];
        Assert.Equal(lines, warnings.Count);
        Assert.Equal(1 + Enters + lines - 1, warnings[^1].Line);
        Assert.Equal(Unused, warnings[^1].Message);
        return (keys / Keys, enters / Enters);
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
