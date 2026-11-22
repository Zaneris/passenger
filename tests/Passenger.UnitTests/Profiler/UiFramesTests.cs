using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>An Avalonia frame's composite time: the compositor's drawings after it that one tick of the render loop makes,
/// each window's beginning as the one before it ends, and none past the next frame or a gap; and whether a run went through
/// Avalonia's code at all, however the trace names its modules.</summary>
public sealed class UiFramesTests
{
    [Fact]
    public void An_Avalonia_frames_composite_time_is_the_drawings_one_tick_makes_and_a_run_went_through_Avalonia_however_its_modules_are_named() => Cases.All(
        ("three adjoining drawings after a frame all count to its composite time", () =>
        {
            // A frame from 0 to 10 ms, then three windows drawn back to back, 3 ms each.
            IReadOnlyList<UiFrame> frames = UiFrames.Composited([Frame(1, 0, 10)], [(11, 3), (14, 3), (17, 3)]);

            Assert.Equal(9, Assert.Single(frames).CompositeMs);
        }),
        ("a run went through Avalonia's code whether the trace names its modules in lower case or as written", () =>
        {
            Assert.True(UiFrames.RanAvalonia([new HotSpot("Hot.Spin()", "spins", 90, 90), new HotSpot("Avalonia.Media.Color.Parse(String)", "avalonia.base", 10, 10)]),
                "A run whose hot spots are in avalonia.base, as a profile from its start names the module, is not taken for an Avalonia app's");
            Assert.False(UiFrames.RanAvalonia([new HotSpot("Hot.Spin()", "spins", 90, 90), new HotSpot("System.Math.Sqrt(Double)", "system.private.corelib", 10, 10)]));
        }));

    private static UiFrame Frame(int number, double startMs, double durationMs) => new(number, startMs, durationMs, 1, 1, 0, 0, 0, 0, 0, []);
}
