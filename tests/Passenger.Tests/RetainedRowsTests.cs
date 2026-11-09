using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The rows on screen are each drawn once and moved by the compositor as the view scrolls, and drawn again only
/// where a key changed them: what a scrolled or typed-into view shows is, pixel for pixel, what the same view shows with
/// every row drawn afresh.</summary>
public sealed class RetainedRowsTests
{
    [AvaloniaFact]
    public void A_view_the_wheel_scrolled_shows_every_row_as_the_rows_drawn_afresh_there_do()
    {
        EditorRig rig = new(string.Join('\n', Enumerable.Range(0, 400).Select(Line)));
        CodeEditor editor = rig.Editor;
        TextSnapshot text = rig.Document.Snapshot;
        int Find(int line, string what) => text.GetLineStart(line) + text.GetLine(line).ToString().IndexOf(what, StringComparison.Ordinal);
        int[] lines = [.. Enumerable.Range(0, 400)];
        editor.SetMarkers([.. lines.Where(i => i % 5 == 1).Select(i => new Marker(new TextRange(Find(i, "unused"), Find(i, " =")), MarkerKind.Warning, "never read"))]);
        editor.SetInlayHints([.. lines.Where(i => i % 5 == 0).Select(i => new InlayHint(Find(i, "3,"), "width:", PadRight: true))]);
        editor.SetLineAnnotations([.. lines.Where(i => i % 7 == 3).Select(i => new LineAnnotation(i, $"total: {i * 3}", LineAnnotationLook.Muted))]);
        editor.SetHighlights([.. lines.Where(i => i % 3 == 2 && i % 10 != 9).Select(i => new TextRange(Find(i, "total"), Find(i, "total") + 5))], 1);
        editor.SetUsageHighlights([.. lines.Where(i => i % 11 == 4 && i % 10 != 9).Select(i => new UsageHighlight(new TextRange(Find(i, "total"), Find(i, "total") + 5), IsWrite: i % 2 == 0))]);
        editor.SetFoldRegions([new FoldRegion(text.GetLineStart(120) + text.GetLine(120).Length, text.GetLineStart(130), "{...}", Folded: true)]);
        editor.ShowWhitespaces = true;
        Ui.Settle();
        Point over = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), rig.Window)!.Value;
        int rowsOnScreen = (int)(editor.Bounds.Height / editor.LineHeight);
        foreach (bool wrapped in new[] { false, true })
        {
            editor.SoftWrap = wrapped;
            editor.ScrollTo(new Vector(0, 0));
            Ui.Settle();
            long mostDrawn = 0;
            for (int step = 0; step < 40; step++)
            {
                // Wheel steps down, steps of a screen and more, then wheel steps back up, the caret on the first line out of view.
                double delta = step < 24 ? -1 : step < 30 ? -6 : 1;
                long drawn = editor.RowsDrawn;
                rig.Window.MouseWheel(over, new Vector(0, delta));
                Ui.Draw();
                if (Math.Abs(delta) == 1)
                {
                    mostDrawn = Math.Max(mostDrawn, editor.RowsDrawn - drawn);
                }
                if (step % 4 != 3)
                {
                    continue;
                }
                byte[] scrolled = Pixels(rig, editor);
                // Every row drawn again where it stands.
                editor.ShowWhitespaces = true;
                Ui.Draw();
                byte[] fresh = Pixels(rig, editor);
                int differs = scrolled.AsSpan().CommonPrefixLength(fresh);
                Assert.True(differs == scrolled.Length,
                    $"{(wrapped ? "Soft-wrapped" : "Not wrapped")}, scrolled to line {editor.LinesShown.First + 1}: the view differs from its rows drawn afresh at {differs / 4 % PixelWidth(rig, editor)}, "
                    + $"{differs / 4 / PixelWidth(rig, editor)} (pixels): a row kept through the scroll stands elsewhere or shows what it showed before");
            }
            Assert.True(mostDrawn < rowsOnScreen / 2,
                $"{(wrapped ? "Soft-wrapped" : "Not wrapped")}, a wheel's step drew as many as {mostDrawn} rows of the {rowsOnScreen} on screen: the rows on screen were drawn again, not moved");
        }
    }

    [AvaloniaFact]
    public void A_key_draws_again_only_the_rows_it_changed_and_those_the_caret_left_or_entered()
    {
        EditorRig rig = new(string.Join('\n', Enumerable.Range(0, 200).Select(Line)));
        CodeEditor editor = rig.Editor;
        TextSnapshot text = rig.Document.Snapshot;
        int Find(int line, string what) => text.GetLineStart(line) + text.GetLine(line).ToString().IndexOf(what, StringComparison.Ordinal);
        editor.SetMarkers([.. Enumerable.Range(0, 200).Where(i => i % 5 == 1).Select(i => new Marker(new TextRange(Find(i, "unused"), Find(i, " =")), MarkerKind.Warning, "never read"))]);
        editor.SetInlayHints([.. Enumerable.Range(0, 200).Where(i => i % 5 == 0).Select(i => new InlayHint(Find(i, "3,"), "width:", PadRight: true))]);
        editor.Selections = SelectionSet.Single(text.GetLineStart(12) + 8);
        Ui.Settle();
        int rowsOnScreen = (int)(editor.Bounds.Height / editor.LineHeight);

        long Drawn(string key, Action press)
        {
            long before = editor.RowsDrawn;
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            press();
            Ui.Settle();
            long drawn = editor.RowsDrawn - before;
            TestContext.Current.TestOutputHelper?.WriteLine($"{key}: {drawn} rows drawn of {rowsOnScreen} on screen, {(GC.GetAllocatedBytesForCurrentThread() - bytes) / 1024} KB on the UI thread");
            return drawn;
        }

        Drawn("every row drawn again", () => editor.ShowWhitespaces = editor.ShowWhitespaces);

        long typed = Drawn("a letter typed", () => rig.Window.KeyTextInput("x"));
        Assert.True(typed == 1, $"A letter typed in a line drew {typed} rows: rows it did not change were drawn again");
        long down = Drawn("Down", () => Ui.Press(rig.Window, "down"));
        Assert.True(down == 2, $"Down drew {down} rows, where only the line the caret left and the one it entered changed");
        long selected = Drawn("Shift+Down", () => Ui.Press(rig.Window, "shift down"));
        Assert.True(selected == 2, $"Shift+Down drew {selected} rows, where only the line it selected and the one the caret entered changed");
        long unselected = Drawn("Escape", () => Ui.Press(rig.Window, "escape"));
        Assert.True(unselected == 2, $"Escape drew {unselected} rows, where only the two lines the selection left changed");
        int line = editor.CaretPosition.Line;
        long split = Drawn("Enter", () => Ui.Press(rig.Window, "enter"));
        // The line split and those below it, each numbered one more; none above it.
        int below = editor.LinesShown.Last - line + 1;
        Assert.True(split <= below, $"Enter on line {line + 1} drew {split} rows, where the {below} from it down changed: the lines above it were drawn again");
        byte[] typedInto = Pixels(rig, editor);
        editor.ShowWhitespaces = editor.ShowWhitespaces;
        Ui.Settle();
        byte[] fresh = Pixels(rig, editor);
        int differs = typedInto.AsSpan().CommonPrefixLength(fresh);
        Assert.True(differs == typedInto.Length,
            $"After the keys the view differs from its rows drawn afresh at {differs / 4 % PixelWidth(rig, editor)}, {differs / 4 / PixelWidth(rig, editor)} (pixels): a row kept through a key shows what it showed before");
    }

    [AvaloniaFact]
    public void Rows_where_lines_were_deleted_or_put_back_show_what_an_editor_opened_on_the_text_shows()
    {
        // A file shorter than the view, as a new console project's Program.cs is.
        EditorRig small = new(string.Join('\n', SmallFile));
        int LineStart(EditorRig rig, int line) => rig.Document.Snapshot.GetLineStart(line);
        List<Seen> seen = [];
        small.PlaceCaret(LineStart(small, 5));
        small.Press("BACK_SPACE");
        See(seen, small, "Backspace at the start of line 6");
        small.PlaceCaret(LineStart(small, 2));
        small.Press("shift DOWN");
        small.Press("shift DOWN");
        small.Press("shift DOWN");
        small.Press("DELETE");
        See(seen, small, "Lines 3 to 5 selected and deleted");
        small.PlaceCaret(LineStart(small, 3) + 2);
        small.Press("meta X");
        See(seen, small, "Line 4 cut");
        small.Press("meta Z");
        See(seen, small, "The cut undone");
        small.Press("meta A");
        small.Press("DELETE");
        See(seen, small, "Every line selected and deleted");
        small.Press("meta Z");
        See(seen, small, "Every line put back");

        // A file longer than the view, seen at its end, where no row comes into view in place of those deleted.
        EditorRig large = new(string.Join('\n', Enumerable.Range(1, 120).Select(i => string.Create(CultureInfo.InvariantCulture, $"Console.WriteLine(\"line {i}\");"))));
        large.Editor.ScrollToEnd();
        large.PlaceCaret(LineStart(large, 115));
        large.Press("shift DOWN");
        large.Press("shift DOWN");
        large.Press("DELETE");
        See(seen, large, "Lines 116 and 117 of 120 selected and deleted at the file's end");
        large.Press("BACK_SPACE");
        See(seen, large, "Backspace at the start of line 116 at the file's end");

        foreach (Seen one in seen)
        {
            AssertDrawnAsAfresh(one);
        }
    }

    [AvaloniaFact]
    public void A_line_typed_into_after_rows_scrolled_away_shows_what_was_typed()
    {
        EditorRig rig = new(string.Join('\n', SmallFile));
        // A row scrolled out of view above and none come into view below, as a scroll in a short file leaves it.
        rig.Editor.ScrollTo(new Vector(0, rig.Editor.LineHeight));
        Ui.Settle();
        rig.PlaceCaret(rig.Document.Snapshot.GetLineStart(4) + 2);
        Ui.Settle();
        rig.Type("x");
        List<Seen> seen = [];
        See(seen, rig, "A letter typed on line 5 with line 1 scrolled out of view");
        rig.Press("BACK_SPACE");
        See(seen, rig, "The letter deleted again");
        foreach (Seen one in seen)
        {
            AssertDrawnAsAfresh(one);
        }
    }

    private static readonly string[] SmallFile =
    [
        "// A new console program", "Console.WriteLine(\"Hello, World!\");", "", "int x = 1;", "int y = 2;", "Console.WriteLine(x + y);", "",
        "var z = x * y;", "Console.WriteLine(z);", "", "Console.WriteLine(\"Done\");", "return;",
    ];

    /// <summary>What an editor showed after a step: its pixels, and the text, selections and scroll it showed them for.</summary>
    private sealed record Seen(string What, byte[] Drawn, string Text, SelectionSet Selections, Vector Scroll);

    private static void See(List<Seen> seen, EditorRig rig, string what)
    {
        Ui.Settle();
        seen.Add(new Seen(what, Pixels(rig, rig.Editor), rig.Text, rig.Editor.Selections, rig.Editor.ScrollOffset));
    }

    /// <summary>Holds what an editor showed after a step to what a new editor opened on the same text, selections and scroll
    /// shows, its rows each drawn once. The new editor takes the keys, so it is made only once every step is seen.</summary>
    private static void AssertDrawnAsAfresh(Seen seen)
    {
        EditorRig fresh = new(seen.Text);
        try
        {
            fresh.Editor.Selections = seen.Selections;
            fresh.Editor.ScrollTo(seen.Scroll);
            Ui.Settle();
            byte[] drawn = Pixels(fresh, fresh.Editor);
            int differs = seen.Drawn.AsSpan().CommonPrefixLength(drawn);
            Assert.True(differs == seen.Drawn.Length && drawn.Length == seen.Drawn.Length,
                $"{seen.What}: the view differs from an editor opened on the same text at {differs / 4 % PixelWidth(fresh, fresh.Editor)}, "
                + $"{differs / 4 / PixelWidth(fresh, fresh.Editor)} (pixels): a row the edit took away still shows what it showed before, over or under the row now there");
        }
        finally
        {
            fresh.Window.Close();
        }
    }

    private static string Line(int i) => (i % 10) switch
    {
        9 => "        // " + string.Concat(Enumerable.Repeat("a note long enough to wrap at the window's edge, ", 3)),
        _ => string.Create(CultureInfo.InvariantCulture, $"        int unused{i} = Of(3, {i}) + total;\t// {i}"),
    };

    /// <summary>The width, in pixels, of the editor's part compared: its gutter and text, short of the scroll bar.</summary>
    private static int PixelWidth(EditorRig rig, CodeEditor editor) => PixelSize.FromSize(new Size(editor.Bounds.Width - 40, 1), rig.Window.RenderScaling).Width;

    /// <summary>The editor's gutter and text as the compositor last drew them, short of the scroll bar and its stripe.</summary>
    private static byte[] Pixels(EditorRig rig, CodeEditor editor)
    {
        using Avalonia.Media.Imaging.WriteableBitmap frame = rig.Window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The window has drawn no frame.");
        Point from = editor.TranslatePoint(new Point(0, 0), rig.Window)!.Value;
        PixelRect area = new(PixelPoint.FromPoint(from, rig.Window.RenderScaling),
            PixelSize.FromSize(new Size(editor.Bounds.Width - 40, editor.Bounds.Height), rig.Window.RenderScaling));
        byte[] bytes = new byte[area.Width * area.Height * 4];
        System.Runtime.InteropServices.GCHandle pinned = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            frame.CopyPixels(area, pinned.AddrOfPinnedObject(), bytes.Length, area.Width * 4);
        }
        finally
        {
            pinned.Free();
        }
        return bytes;
    }
}
