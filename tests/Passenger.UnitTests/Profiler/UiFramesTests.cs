using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>An Avalonia frame's composite time: the compositor's drawings after it that one tick of the render loop makes,
/// each window's beginning as the one before it ends, and none past the next frame or a gap; and whether a run went through
/// Avalonia's code at all, however the trace names its modules.</summary>
public sealed class UiFramesTests
{
    [Fact]
    public void Three_adjoining_drawings_after_a_frame_all_count_to_its_composite_time()
    {
        // A frame from 0 to 10 ms, then three windows drawn back to back, 3 ms each.
        IReadOnlyList<UiFrame> frames = UiFrames.Composited([Frame(1, 0, 10)], [(11, 3), (14, 3), (17, 3)]);

        Assert.Equal(9, Assert.Single(frames).CompositeMs);
    }

    [Fact]
    public void A_drawing_after_a_gap_or_past_the_next_frame_counts_to_no_frame_before_it()
    {
        // The second frame begins at 30: the drawing at 25, after a gap, is the compositor's alone, and the one at 41 the second's.
        IReadOnlyList<UiFrame> frames = UiFrames.Composited([Frame(2, 30, 10), Frame(1, 0, 10)], [(11, 3), (14, 2), (25, 1), (41, 4)]);

        Assert.Equal([(1, 5.0), (2, 4.0)], frames.Select(f => (f.Number, f.CompositeMs)));
    }

    /// <summary>A profile from a program's start names the module of code compiled as it ran in lower case
    /// (passenger.inspector, system.text.json in a recording of an Avalonia app's start), and an Avalonia app's code is
    /// all compiled so: such a run went through Avalonia's code as surely as one naming its modules as written.</summary>
    [Fact]
    public void A_run_went_through_avalonias_code_whether_the_trace_names_its_modules_in_lower_case_or_as_written()
    {
        Assert.True(UiFrames.RanAvalonia([new HotSpot("Hot.Spin()", "spins", 90, 90), new HotSpot("Avalonia.Media.Color.Parse(String)", "avalonia.base", 10, 10)]),
            "A run whose hot spots are in avalonia.base, as a profile from its start names the module, is not taken for an Avalonia app's");
        Assert.True(UiFrames.RanAvalonia([new HotSpot("Avalonia.Layout.Layoutable.Measure(Size)", "Avalonia.Base", 10, 10)]));
        Assert.False(UiFrames.RanAvalonia([new HotSpot("Hot.Spin()", "spins", 90, 90), new HotSpot("System.Math.Sqrt(Double)", "system.private.corelib", 10, 10)]));
    }

    private static UiFrame Frame(int number, double startMs, double durationMs) => new(number, startMs, durationMs, 1, 1, 0, 0, 0, 0, 0, []);
}
