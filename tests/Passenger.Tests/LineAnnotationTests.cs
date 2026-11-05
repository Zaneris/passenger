using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Text after lines' code as the editor draws it (a debugger's or a running game's values): the pointer resting on
/// an annotation is told which one it rests on, and annotations once drawn cost a scroll frame only their drawing, never
/// their words laid out again.</summary>
public sealed class LineAnnotationTests
{
    [AvaloniaFact]
    public void The_pointer_resting_on_a_line_end_annotation_is_told_which_one_and_past_a_line_with_none_nothing()
    {
        EditorRig rig = new("int frames = 0;\nfloat speed = 1;\nint other = 2;\n");
        CodeEditor editor = rig.Editor;
        LineAnnotation frames = new(0, "frames: 41", LineAnnotationLook.Muted);
        LineAnnotation second = new(0, "second on its line", LineAnnotationLook.Muted);
        LineAnnotation speed = new(1, "speed: 312.5", LineAnnotationLook.Muted);
        editor.SetLineAnnotations([frames, second, speed]);
        Ui.Settle();
        List<HoverEventArgs> heard = [];
        editor.Hovered += (_, e) => heard.Add(e);

        foreach (LineAnnotation annotation in (LineAnnotation[])[speed, second])
        {
            Rect box = editor.AnnotationBox(annotation.Line, annotation.Text) ?? throw new InvalidOperationException($"The editor drew no {annotation.Text}");
            int was = heard.Count;
            rig.Window.MouseMove(editor.TranslatePoint(box.Center, rig.Window)!.Value, RawInputModifiers.None);
            Ui.WaitFor(() => heard.Count > was, () => $"the pointer resting on {annotation.Text} to be heard");
            Assert.True(heard[^1].Annotation == annotation, $"Resting on {annotation.Text}, the editor said {heard[^1].Annotation?.Text ?? "no annotation"}");
            LineSpan line = rig.Document.Snapshot.GetLine(annotation.Line);
            Assert.Equal(line.Start + line.Length, heard[^1].Offset);
        }

        Rect past = editor.AnnotationBox(speed.Line, speed.Text)!.Value;
        int before = heard.Count;
        rig.Window.MouseMove(editor.TranslatePoint(new Point(past.Center.X, past.Center.Y + editor.LineHeight), rig.Window)!.Value, RawInputModifiers.None);
        Ui.Hold(TimeSpan.FromSeconds(1), "the hover's delay past the end of a line with no annotation");
        Assert.True(heard.Count == before, $"Past the end of a line with no annotation the editor said the pointer rests on {heard[^1].Annotation?.Text ?? "code"}");
    }

    /// <summary>A scroll a pixel at a time over a screen of annotated lines allocates on the UI thread, above the same scroll
    /// with none, only what each annotation's drawing records (a few hundred bytes), a small part of what laying them out
    /// did once.</summary>
    [AvaloniaFact]
    public void Annotations_once_drawn_cost_a_scroll_frame_their_drawing_and_never_their_words_laid_out_again()
    {
        const int Lines = 400;
        EditorRig rig = new(string.Join('\n', Enumerable.Range(0, Lines).Select(i => $"    private int member{i} = {i};")));
        CodeEditor editor = rig.Editor;
        // The stripe's marks are another control's frame.
        editor.ShowErrorStripe = false;
        double top = 100 * editor.LineHeight;
        editor.ScrollTo(new Vector(0, top));
        Ui.Settle();
        long plain = LeastScrollFrame();
        editor.SetLineAnnotations([.. Enumerable.Range(0, Lines).Select(i => new LineAnnotation(i, $"member{i}: {i * 3.5}", LineAnnotationLook.Muted))]);
        long laidOut = Frame(top);
        long annotated = LeastScrollFrame();
        (int first, int last) = editor.LinesShown;
        int shown = last - first + 1;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A scroll frame allocated {plain:N0} bytes with no annotations, {annotated:N0} with {shown} drawn; the frame that laid them out {laidOut:N0}");
        Assert.True(shown > 10, $"The screen shows {shown} lines");
        Assert.True(annotated - plain < 1024 * shown && annotated - plain < (laidOut - plain) / 4,
            $"A scroll frame of {shown} annotations drawn already allocated {annotated:N0} bytes against {plain:N0} with none ({(annotated - plain) / shown} each), "
            + $"where laying them out the first time took {laidOut:N0}: their words are laid out again as the view scrolls");

        long LeastScrollFrame()
        {
            long least = long.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                least = Math.Min(least, Frame(top + (i % 2)));
            }
            return least;
        }

        long Frame(double y)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            editor.ScrollTo(new Vector(0, y));
            editor.InvalidateVisual();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
