using Passenger.Profiler;

namespace Passenger.UnitTests.Profiler;

/// <summary>A Godot game's own frames beside the samples: each placed on the trace's clock no later than it came nor than the
/// next began, the script functions' work summed over frames, and the C# the engine called joined to the frames it ran in by
/// time, each frame its own samples, a sample on the line between two the later one's.</summary>
public sealed class GameFramesTests
{
    private static readonly DateTime Began = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static GameFrame Frame(int number, double frameMs, params ScriptWork[] scripts) => new(number, 0, frameMs, frameMs, 0, 1000.0 / 60, 0, [], scripts);

    private static double TraceMs(DateTime at) => (at - Began).TotalMilliseconds;

    [Fact]
    public void Frames_sent_together_end_where_the_later_ones_arrival_less_their_time_allows()
    {
        IReadOnlyList<GameFrame> placed = GameProfile.Placed(
        [
            (Frame(2, 5), Began.AddMilliseconds(20)),
            (Frame(1, 5), Began.AddMilliseconds(20)),
            (Frame(3, 4), Began.AddMilliseconds(40)),
        ], TraceMs);

        Assert.Equal([(1, 10.0, 15.0), (2, 15.0, 20.0), (3, 36.0, 40.0)], placed.Select(f => (f.Number, f.StartMs, f.EndMs)));
    }

    [Fact]
    public void Script_functions_sum_over_frames_the_heaviest_by_their_own_time_first_and_an_unnamed_number_says_so()
    {
        GameProfile game = new("Godot 4.7.2", "/games/spinner", draws: false, null,
            [ScriptFunction.Of("res://wander.gd::6::_process"), ScriptFunction.Of("res://wander.gd::10::wander")], []);
        GameFrame[] frames =
        [
            Frame(1, 7, new ScriptWork(0, 1, 0.01, 2.5, 0), new ScriptWork(1, 1, 0.3, 0.3, 0)),
            Frame(2, 7, new ScriptWork(0, 1, 0.02, 2.6, 0.1), new ScriptWork(1, 2, 0.4, 0.4, 0), new ScriptWork(7, 1, 0.05, 0.05, 0)),
        ];

        Assert.Equal([("wander", 3, 0.7, 0.7, 0.0), ("function #7", 1, 0.05, 0.05, 0.0), ("_process", 2, 0.03, 5.1, 0.1)],
            game.Spots(frames).Select(s => (s.Function.Name, s.Calls, Math.Round(s.SelfMs, 2), Math.Round(s.TotalMs, 2), Math.Round(s.EngineMs, 2))));
    }

    [Fact]
    public void The_CSharp_the_engine_called_is_joined_to_each_frame_by_its_samples_there_and_other_threads_work_is_left_out()
    {
        const string Bridge = "Godot.Bridge.CSharpInstanceBridge.Call(int, int, int, int, int, int)";
        MethodCode spin = new("/games/spinner/Spinner.dll", 0x06000002);
        Snapshot snapshot = Snapshot.Of(
        [
            ([("Main.Spin()", "Spinner", spin), ("Main._Process(float64)", "Spinner", null), (Bridge, "GodotSharp", null), ("[Godot]", "Godot", null)], 10),
            ([("[Godot]", "Godot", null), ("Godot.GodotObject.Call(class Godot.StringName)", "GodotSharp", null), ("Main._Process(float64)", "Spinner", null),
                (Bridge, "GodotSharp", null), ("[Godot]", "Godot", null)], 5),
            ([("Worker.Run()", "Spinner", null), ("System.Threading.Thread.StartCallback()", "System.Private.CoreLib", null)], 5),
        ]);
        GameFrame[] frames = [Frame(1, 12) with { EndMs = 12 }, Frame(2, 8) with { EndMs = 20 }];

        GameJoin joined = snapshot.Joined(frames);

        Assert.Equal([12.0, 3.0], joined.FrameMs);
        Assert.Equal(15, joined.Samples);
        Assert.Equal([("Main.Spin()", 10, 10), ("[Godot]", 5, 5), ("Main._Process(float64)", 0, 15), ("Godot.GodotObject.Call(StringName)", 0, 5)],
            joined.CSharp.Select(h => (h.Name, h.Self, h.Total)));
        Assert.Equal(spin, joined.CSharp[0].Code);
        Assert.Equal([3.0], snapshot.Joined([frames[1]]).FrameMs);
    }
}
