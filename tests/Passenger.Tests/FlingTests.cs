using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.Editor;
using Passenger.Languages.CSharp;

namespace Passenger.Tests;

/// <summary>A fling through a long C# file leaves the collector little to promote: each line laid out in one run for each
/// look, blanks adding none, each run shaped on its own so the formatter splits no buffer it would leave to its
/// finalizer. Counts, not times; collections forced and finalizers counted, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class FlingTests
{
    [AvaloniaFact]
    public void Blanks_between_tokens_add_no_run_to_a_line_so_it_is_laid_out_in_as_many_runs_as_without_them()
    {
        long Runs(string line)
        {
            EditorRig rig = new(string.Join('\n', Enumerable.Repeat(line, 20)));
            long before = rig.Editor.RunsFormatted;
            using CSharpHighlighter csharp = new(rig.Document);
            rig.Editor.Highlighter = csharp;
            Ui.Settle();
            long runs = rig.Editor.RunsFormatted - before;
            rig.Window.Close();
            return runs;
        }
        long spaced = Runs("        int total = Of(3, 4) + seed;");
        long packed = Runs("int total=Of(3,4)+seed;");
        Assert.True(spaced > 0 && spaced == packed,
            $"Twenty lines with blanks between their tokens were laid out in {spaced} runs, and the same tokens without them in {packed}: each blank between two colours makes a glyph run and a buffer split of its own");
    }

    [AvaloniaFact]
    public void A_fling_through_lines_of_many_colours_leaves_the_finalizer_next_to_nothing_per_line()
    {
        const int Lines = 2400;
        EditorRig rig = new(string.Join('\n', Enumerable.Range(0, Lines).Select(Line)));
        using CSharpHighlighter csharp = new(rig.Document);
        rig.Editor.Highlighter = csharp;
        Ui.Settle();
        CodeEditor editor = rig.Editor;
        Point over = editor.TranslatePoint(new Point(editor.Bounds.Width / 2, editor.Bounds.Height / 2), rig.Window)!.Value;
        double screen = Math.Floor(editor.Bounds.Height / 50);
        long runs = editor.RunsFormatted;
        int first = editor.LinesShown.First;
        long left = 0;
        while (editor.LinesShown.Last < Lines - 1)
        {
            // A few frames between full collections, too few to start one of their own: each counts what they left.
            Collected();
            for (int frame = 0; frame < 4; frame++)
            {
                rig.Window.MouseWheel(over, new Vector(0, -screen));
                Ui.Draw();
            }
            left += Collected();
        }
        int flung = editor.LinesShown.First - first;
        Assert.True(editor.RunsFormatted - runs > 4L * flung, $"The fling through {flung} lines laid out only {editor.RunsFormatted - runs} runs: its lines were not of many colours");
        Assert.True(left <= flung / 10,
            $"A fling through {flung} lines of several colours left the finalizer {left} objects, where its lines' layouts leave it none: each buffer the formatter splits waits for it, "
            + "is promoted with what it holds, and takes the pooled arrays it shares out of the pool");
    }

    /// <summary>The finalizable objects a full collection found dead, the queue emptied before: what the work since the last
    /// such call left the finalizer.</summary>
    private static long Collected()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long found = GC.GetGCMemoryInfo(GCKind.FullBlocking).FinalizationPendingCount;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return found;
    }

    private static string Line(int i) => (i % 10) switch
    {
        9 => "        // " + string.Concat(Enumerable.Repeat("a note of one colour, ", 3)),
        _ => string.Create(CultureInfo.InvariantCulture, $"        int unused{i} = Of(3, {i}) + total * \"text {i}\".Length;"),
    };
}
