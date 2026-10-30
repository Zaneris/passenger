using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>An Avalonia frame's composite time: the compositor's drawings after it that one tick of the render loop makes,
/// each window's beginning as the one before it ends, and none past the next frame or a gap.</summary>
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

    private static UiFrame Frame(int number, double startMs, double durationMs) => new(number, startMs, durationMs, 1, 1, 0, 0, 0, 0, 0, []);
}
