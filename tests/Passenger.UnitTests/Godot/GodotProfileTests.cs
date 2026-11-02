using Passenger.App.Godot;
using Passenger.Profiler;

namespace Passenger.UnitTests.Godot;

/// <summary>What Godot's own profilers send while turned on, as Passenger reads it: the messages a headless Godot 4.7 game
/// sent once profiler:servers was on (their bytes taken from one, after their length), a script function's signature and
/// a call into Godot's API, a frame with its servers and script functions, the run's total and the hardware; and the
/// renderer's stages encoded as Godot encodes them, which a headless game never sends.</summary>
public class GodotProfileTests
{
    /// <summary>The signature of wander, a GDScript function whose body begins at line 10, numbered 2.</summary>
    private const string Signature =
        "HAAAAAMAAAAEAAAAGgAAAHNlcnZlcnM6ZnVuY3Rpb25fc2lnbmF0dXJlAAACAAAAAQAAABwAAAACAAAAFQAAABsAAAByZXM6Ly93YW5kZXIuZ2Q6"
        + "OjEwOjp3YW5kZXIAAgAAAAIAAAA=";

    /// <summary>A call wander.gd made into Godot's own API, numbered 4.</summary>
    private const string EngineCall =
        "HAAAAAMAAAAEAAAAGgAAAHNlcnZlcnM6ZnVuY3Rpb25fc2lnbmF0dXJlAAACAAAAAQAAABwAAAACAAAAFQAAACMAAAByZXM6Ly93YW5kZXIuZ2Q6"
        + "OjA6Ok5vZGUuZ2V0X3BhcmVudAACAAAABAAAAA==";

    /// <summary>Frame 181: the audio, physics_3d and physics_2d servers' functions, then five script functions' work.</summary>
    private const string Frame =
        "HAAAAAMAAAAEAAAAFQAAAHNlcnZlcnM6cHJvZmlsZV9mcmFtZQAAAAIAAAABAAAAHAAAAEMAAAACAAAAtQAAAAMAAQCHNCpwsg18PwMAAQA7cw8J"
        + "3/t7PwMAAQCN7bWg98bwPgMAAQARERERERGRPwMAAQCV2LW93ZI8PwIAAAADAAAAFQAAAAwAAABhdWRpb190aHJlYWQCAAAABAAAABUAAAAMAAAA"
        + "YXVkaW9fc2VydmVyAwAAAAAAAAAVAAAADAAAAGF1ZGlvX2RyaXZlcgMAAAAAAAAAFQAAAAoAAABwaHlzaWNzXzNkAAACAAAADAAAABUAAAAQAAAA"
        + "aW50ZWdyYXRlX2ZvcmNlcwMAAAAAAAAAFQAAABAAAABnZW5lcmF0ZV9pc2xhbmRzAwAAAAAAAAAVAAAAEQAAAHNldHVwX2NvbnN0cmFpbnRzAAAA"
        + "AwAAAAAAAAAVAAAAEQAAAHNvbHZlX2NvbnN0cmFpbnRzAAAAAwAAAAAAAAAVAAAAFAAAAGludGVncmF0ZV92ZWxvY2l0aWVzAwAAAAAAAAAVAAAA"
        + "DQAAAGZsdXNoX3F1ZXJpZXMAAAADAAEAje21oPfGwD4VAAAACgAAAHBoeXNpY3NfMmQAAAIAAAAMAAAAFQAAABAAAABpbnRlZ3JhdGVfZm9yY2Vz"
        + "AwAAAAAAAAAVAAAAEAAAAGdlbmVyYXRlX2lzbGFuZHMDAAAAAAAAABUAAAARAAAAc2V0dXBfY29uc3RyYWludHMAAAADAAAAAAAAABUAAAARAAAA"
        + "c29sdmVfY29uc3RyYWludHMAAAADAAAAAAAAABUAAAAUAAAAaW50ZWdyYXRlX3ZlbG9jaXRpZXMDAAAAAAAAABUAAAANAAAAZmx1c2hfcXVlcmll"
        + "cwAAAAMAAAAAAAAAAgAAABkAAAACAAAAAAAAAAIAAAABAAAAAwAAAAAAAAADAAEAog4r3PKRZD8DAAAAAAAAAAIAAAABAAAAAgAAAAEAAAADAAEA"
        + "VOQQcXMqyT4DAAEA7ISX4NQHYj8DAAAAAAAAAAIAAAACAAAAAgAAAAEAAAADAAEAqU2c3O9QND8DAAEAqU2c3O9QND8DAAAAAAAAAAIAAAADAAAA"
        + "AgAAAAEAAAADAAEARtJu9DEfID8DAAEARtJu9DEfID8DAAAAAAAAAAIAAAAEAAAAAgAAAAEAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAA=";

    /// <summary>The run's sums as profiling ended, which its frames hold already.</summary>
    private const string Total =
        "HAAAAAMAAAAEAAAAFQAAAHNlcnZlcnM6cHJvZmlsZV90b3RhbAAAAAIAAAABAAAAHAAAACEAAAACAAAAHQIAAAMAAQB5Xb9gN2x7PwMAAQB5Xb9g"
        + "N2x7PwMAAAAAAAAAAwABABEREREREZE/AwABAI84ZAPpYsM/AgAAAAAAAAACAAAAGQAAAAIAAAAAAAAAAgAAAGkBAAADAAEAD9b/OcyXFz8DAAEA"
        + "h/vIrUm36z8DAAAAAAAAAAIAAAABAAAAAgAAAGkBAAADAAEAhsYTQZyHMz8DAAEAtWtCWmNQ6D8DAAAAAAAAAAIAAAACAAAAAgAAAGkBAAADAAEA"
        + "xTvAkxYuuz8DAAEAxTvAkxYuuz8DAAAAAAAAAAIAAAADAAAAAgAAAGoBAAADAAEAOUNxx5v8pj8DAAEAOUNxx5v8pj8DAAAAAAAAAAIAAAAFAAAA"
        + "AgAAAAAAAAADAAAAAAAAAAMAAAAAAAAAAwAAAAAAAAA=";

    /// <summary>The CPU and the GPU (none, headless) the game draws with.</summary>
    private const string Hardware =
        "HAAAAAMAAAAEAAAAFAAAAHZpc3VhbDpoYXJkd2FyZV9pbmZvAgAAAAEAAAAcAAAAAgAAAAQAAAAMAAAAQXBwbGUgTTIgTWF4BAAAAAAAAAA=";

    private static GodotMessage Message(string base64) => GodotMessage.Read(Convert.FromBase64String(base64));

    [Fact]
    public void A_signature_reads_as_its_number_and_its_script_its_bodys_line_and_its_name()
    {
        GodotMessage message = Message(Signature);
        Assert.Equal(GodotWords.FunctionSignature, message.Name);
        Assert.Equal((2, "res://wander.gd::10::wander"), GodotProfile.Signature(message.Data));
        Assert.Equal(new ScriptFunction("res://wander.gd", 10, "wander"), ScriptFunction.Of("res://wander.gd::10::wander"));
        ScriptFunction call = ScriptFunction.Of(GodotProfile.Signature(Message(EngineCall).Data)!.Value.Signature);
        Assert.True(call is { File: "res://wander.gd", Line: 0, Name: "Node.get_parent", IsEngineCall: true }, $"Godot's API call read as {call}, so its row would name no call into Godot");
    }

    [Fact]
    public void A_built_in_scripts_signature_keeps_the_scene_in_its_file()
    {
        Assert.Equal(new ScriptFunction("res://main.tscn::GDScript_k3x9a", 4, "_ready"), ScriptFunction.Of("res://main.tscn::GDScript_k3x9a::4::_ready"));
        Assert.Equal(new ScriptFunction(string.Empty, 0, "no signature"), ScriptFunction.Of("no signature"));
    }

    [Fact]
    public void A_frame_reads_its_times_in_milliseconds_its_servers_functions_and_its_script_functions_work()
    {
        GodotMessage message = Message(Frame);
        Assert.Equal(GodotWords.ServersFrame, message.Name);
        GameFrame frame = GodotProfile.Frame(message.Data) ?? throw new Xunit.Sdk.XunitException("The frame read as nothing, so the profile would hold none of Godot's frames");
        Assert.Equal(181, frame.Number);
        Assert.Equal((6.849, 6.832, 0.016, 16.667, 0.436), (Math.Round(frame.FrameMs, 3), Math.Round(frame.ProcessMs, 3), Math.Round(frame.PhysicsMs, 3), Math.Round(frame.StepMs, 3), Math.Round(frame.ScriptMs, 3)));
        Assert.Equal(["audio_thread", "physics_3d", "physics_2d"], frame.Servers.Select(s => s.Server).Distinct());
        Assert.Equal(["integrate_forces", "generate_islands", "setup_constraints", "solve_constraints", "integrate_velocities", "flush_queries"],
            frame.Servers.Where(s => s.Server == "physics_3d").Select(s => s.Function));
        Assert.Equal(0.002, Math.Round(frame.Servers.Single(s => s is { Server: "physics_3d", Function: "flush_queries" }).Ms, 3));
        Assert.Equal([(0, 1, 0.0, 2.511), (1, 1, 0.003, 2.201), (2, 1, 0.31, 0.31), (3, 1, 0.123, 0.123), (4, 1, 0.0, 0.0)],
            frame.Scripts.Select(s => (s.Function, s.Calls, Math.Round(s.SelfMs, 3), Math.Round(s.TotalMs, 3))));
    }

    [Fact]
    public void The_runs_total_and_the_hardware_read_as_Godot_sent_them()
    {
        GameFrame total = GodotProfile.Frame(Message(Total).Data) ?? throw new Xunit.Sdk.XunitException("The total read as nothing");
        Assert.True(total is { Number: 541, Servers.Count: 0, Scripts.Count: 5 }, $"The total read as frame {total.Number} with {total.Servers.Count} servers and {total.Scripts.Count} functions");
        Assert.Equal(361, total.Scripts.Single(s => s.Function == 0).Calls);
        Assert.Equal(string.Empty, GodotProfile.Gpu(Message(Hardware).Data));
        Assert.Equal("Apple M1", GodotProfile.Gpu(["Apple M1 Pro", new GodotName("Apple M1")]));
    }

    [Fact]
    public void The_renderers_stages_read_with_their_cpu_and_gpu_times_and_data_too_short_reads_as_none()
    {
        GodotMessage message = GodotMessage.Read(GodotMessage.Frame(GodotWords.VisualFrame, 92L, 6L, new GodotName("Render Opaque"), 1.5, 2.25, new GodotName("Post"), 0.25, 0.5)[4..]);
        Assert.Equal([new RenderArea("Render Opaque", 1.5, 2.25), new RenderArea("Post", 0.25, 0.5)], GodotProfile.Rendered(message.Data));
        Assert.Empty(GodotProfile.Rendered([92L, 9L, "Render", 1.5]));
        Assert.Null(GodotProfile.Frame([181L, 0.006, 0.006]));
        Assert.Null(GodotProfile.Frame([181L, 0.006, 0.006, 0.0, 0.016, 0.0, 1L, "audio_thread", 40L, "audio_server", 0.0, 0L]));
    }
}
