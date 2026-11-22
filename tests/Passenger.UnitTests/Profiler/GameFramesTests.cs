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

    /// <summary>A game's frames, script functions and C# samples read into the call tree, Passenger's own left
    /// out.</summary>
    [Fact]
    public void A_games_frames_read_into_the_call_tree() => Cases.All(
        (nameof(A_games_frames_give_the_call_tree_its_GDScript_functions_with_their_own_times_and_Godots_own_work_beside_the_threads),
            A_games_frames_give_the_call_tree_its_GDScript_functions_with_their_own_times_and_Godots_own_work_beside_the_threads),
        (nameof(The_CSharp_the_engine_called_is_joined_to_each_frame_by_its_samples_there_and_other_threads_work_is_left_out),
            The_CSharp_the_engine_called_is_joined_to_each_frame_by_its_samples_there_and_other_threads_work_is_left_out),
        (nameof(The_work_of_a_script_passenger_put_in_the_game_is_written_into_no_frame_and_its_time_into_no_total),
            The_work_of_a_script_passenger_put_in_the_game_is_written_into_no_frame_and_its_time_into_no_total));

    private void A_games_frames_give_the_call_tree_its_GDScript_functions_with_their_own_times_and_Godots_own_work_beside_the_threads()
    {
        GameProfile game = new("Godot 4.7.2", "/games/spinner", draws: false, null,
            [ScriptFunction.Of("res://wander.gd::6::_process"), ScriptFunction.Of("res://wander.gd::10::wander"), ScriptFunction.Of("res://wander.gd::0::Node.get_parent")], []);
        GameFrame[] frames =
        [
            Frame(1, 10, new ScriptWork(0, 1, 0.5, 4.5, 0), new ScriptWork(1, 1, 3, 3, 0), new ScriptWork(2, 2, 1, 1, 0)) with { ScriptMs = 4.5 },
            Frame(2, 12, new ScriptWork(0, 1, 0.5, 4.5, 0), new ScriptWork(1, 1, 3, 3, 0)) with { ScriptMs = 3.5 },
        ];

        // Each time in samples of half a millisecond; the C# the engine called took 2 and 3 ms of the frames.
        IReadOnlyList<CallNode> roots = game.CallRoots(frames, [2, 3], 0.5);

        Assert.Equal([(GameProfile.GDScriptRoot, 0, 16), (GameProfile.EngineRoot, 18, 18)], roots.Select(r => (r.Name, r.Self, r.Total)));
        Assert.Equal([("_process", "wander.gd:6", 2, 18), ("wander", "wander.gd:10", 12, 12), (GameProfile.ApiRoot, "Godot", 0, 2)],
            roots[0].Ordered.Select(n => (n.Name, n.Module, n.Self, n.Total)));
        Assert.Equal(game.FunctionAt(1), roots[0].Ordered[1].Script);
        Assert.Equal([("Node.get_parent", "from wander.gd", 2, 2)], roots[0].Ordered[2].Ordered.Select(n => (n.Name, n.Module, n.Self, n.Total)));
        Assert.True(roots[0].Ordered[2].Script is null && roots[1].Script is null, "A row of Godot's API or of its own work was taken for a GDScript function");
    }

    private void The_CSharp_the_engine_called_is_joined_to_each_frame_by_its_samples_there_and_other_threads_work_is_left_out()
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
    /// <summary>Passenger's helper in a profiled game is none of the game's work: its functions and its calls into Godot's API
    /// are written into no frame, and its own time is in no frame's script time, process time or whole.</summary>
    private void The_work_of_a_script_passenger_put_in_the_game_is_written_into_no_frame_and_its_time_into_no_total()
    {
        string folder = Directory.CreateTempSubdirectory("passenger-unit-").FullName;
        try
        {
            string trace = Path.Combine(folder, "Ticker-20261101-120000.nettrace");
            new FileStream(trace, FileMode.CreateNew, FileAccess.Write).Dispose();
            GameRecording recording = new("Godot 4.7.2", "/games/ticker", draws: false) { Passengers = file => file.EndsWith("passenger_agent.gd", StringComparison.Ordinal) };
            recording.Signature(0, "res://ticker.gd::7::_process");
            recording.Signature(1, "/state/cache/godot/passenger_agent.gd::38::asked");
            recording.Signature(2, "/state/cache/godot/passenger_agent.gd::0::Node.get_node");
            recording.Took(Frame(1, 5, new ScriptWork(0, 1, 0.2, 0.2, 0), new ScriptWork(1, 2, 0.5, 0.75, 0.25), new ScriptWork(2, 2, 0.25, 0.25, 0)) with { ScriptMs = 0.95 },
                Began.AddMilliseconds(5));
            recording.Took(Frame(2, 5, new ScriptWork(0, 1, 0.3, 0.3, 0)) with { ScriptMs = 0.3 }, Began.AddMilliseconds(10));
            recording.Stop();
            recording.Write(trace);

            Snapshot snapshot = Snapshot.Read(trace);

            Assert.Equal(["_process"], snapshot.Game!.Spots(snapshot.GameFrames).Select(s => s.Function.Name));
            Assert.Equal([(4.25, 4.25, 0.2), (5.0, 5.0, 0.3)], snapshot.GameFrames.Select(f => (Math.Round(f.FrameMs, 2), Math.Round(f.ProcessMs, 2), Math.Round(f.ScriptMs, 2))));
            Assert.DoesNotContain(snapshot.Game.CallRoots(snapshot.GameFrames, [], 0.05).SelectMany(r => r.Ordered), n => n.Name is "asked" or GameProfile.ApiRoot);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
