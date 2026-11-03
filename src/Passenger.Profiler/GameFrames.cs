using System.Globalization;
using Passenger.Core;

namespace Passenger.Profiler;

/// <summary>A function Godot's profiler names in a game's frames, read from its signature ("res://wander.gd::10::wander"): the
/// script's file (a <c>res://</c> path, or a built-in script's), the line Godot gives it (its body's first, from one) and its
/// name. A call the script made into Godot's own API ("res://wander.gd::0::Node.get_parent") has line 0, its name the class's
/// and method's.</summary>
public sealed record ScriptFunction(string File, int Line, string Name)
{
    /// <summary>Whether it is a call into Godot's own API rather than a function of a script.</summary>
    public bool IsEngineCall => Line == 0;

    /// <summary>The function a signature names; one of another shape is its whole text, with no file.</summary>
    public static ScriptFunction Of(string signature)
    {
        // A built-in script's file holds "::" itself ("res://main.tscn::GDScript_x1y2"), so its line and name are the last parts.
        string[] parts = signature.Split("::");
        return parts.Length >= 3 && int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out int line)
            ? new ScriptFunction(string.Join("::", parts[..^2]), line, parts[^1])
            : new ScriptFunction(string.Empty, 0, signature);
    }
}

/// <summary>A script function's share of a frame as Godot measured it: the function (a number into the run's table), its
/// calls, and the time in it alone, in it and all it called, and in the calls it made into Godot's own API, in milliseconds.</summary>
public readonly record struct ScriptWork(int Function, int Calls, double SelfMs, double TotalMs, double EngineMs);

/// <summary>The time one of a Godot server's functions took in a frame ("physics_2d", "integrate_forces"), in milliseconds.</summary>
public readonly record struct ServerWork(string Server, string Function, double Ms);

/// <summary>A stage of what Godot's renderer drew in a frame, with its time on the CPU and on the GPU, in milliseconds.</summary>
public readonly record struct RenderArea(string Name, double CpuMs, double GpuMs);

/// <summary>A frame of a Godot game as Godot's own profiler measured it: its number, when it ended (milliseconds from the
/// trace's start), its time from its start to its drawing's end, the process part of it (the main loop's process and drawing),
/// its physics part (the longest physics step it ran), the physics step's length, and its script functions' own time; then
/// each server's functions and each script function's work, and the renderer's stages where the game draws.</summary>
public sealed record GameFrame(int Number, double EndMs, double FrameMs, double ProcessMs, double PhysicsMs, double StepMs, double ScriptMs,
    IReadOnlyList<ServerWork> Servers, IReadOnlyList<ScriptWork> Scripts)
{
    public double StartMs => EndMs - FrameMs;

    public IReadOnlyList<RenderArea> Rendered { get; init; } = [];
}

/// <summary>A script function's work over some frames: its calls, its own time, its time with what it called, and its calls
/// into Godot's own API, in milliseconds.</summary>
public sealed record ScriptSpot(ScriptFunction Function, int Calls, double SelfMs, double TotalMs, double EngineMs);

/// <summary>The C# the engine called in some frames, joined to them by time: the methods by their samples there, as hot
/// spots are, each frame's C# time (the samples in it on stacks the engine entered, at the run's sample period), how many
/// samples in all, and the time one stands for.</summary>
public sealed record GameJoin(IReadOnlyList<HotSpot> CSharp, IReadOnlyList<double> FrameMs, int Samples, double SampleMs);

/// <summary>A Godot game's own frame profile, recorded beside the samples while it was profiled: the engine and the project's
/// folder (where <c>res://</c> paths are), whether it drew (a headless game draws nothing, so has no render stages), the GPU it
/// drew with, the script functions its frames name, and the frames in order. Kept beside the run's trace
/// (<see cref="PathOf"/>), so a snapshot opened again has it too.</summary>
public sealed class GameProfile
{
    /// <summary>Whether a method is where Godot enters C#: its bridge, through which it calls a script's methods (its own
    /// notifications and other scripts' calls alike), or its delegates' calls (signals, Callables).</summary>
    public static bool EntersFromEngine(string method) =>
        method.StartsWith("Godot.Bridge.", StringComparison.Ordinal) || method.StartsWith("Godot.DelegateUtils.", StringComparison.Ordinal);

    private const string Magic = "passenger-game-frames";
    private const int Version = 1;

    internal GameProfile(string engine, string folder, bool draws, string? gpu, IReadOnlyList<ScriptFunction> functions, IReadOnlyList<GameFrame> frames)
    {
        Engine = engine;
        Folder = folder;
        Draws = draws;
        Gpu = gpu;
        Functions = functions;
        Frames = frames;
    }

    /// <summary>"Godot 4.7.2".</summary>
    public string Engine { get; }

    /// <summary>The game's project folder, which its <c>res://</c> paths are under.</summary>
    public string Folder { get; }

    /// <summary>Whether the game drew: one run headless did not, so its frames have no render stages.</summary>
    public bool Draws { get; }

    /// <summary>The GPU the game drew with, as Godot names it; null where it named none.</summary>
    public string? Gpu { get; }

    /// <summary>The script functions the frames name, by their number.</summary>
    public IReadOnlyList<ScriptFunction> Functions { get; }

    /// <summary>The frames, in order.</summary>
    public IReadOnlyList<GameFrame> Frames { get; }

    /// <summary>The file a run's game profile is kept in beside its trace.</summary>
    public static string PathOf(string trace) => Path.ChangeExtension(trace, ".gameframes");

    /// <summary>The script functions' work over some frames, the heaviest by its own time first.</summary>
    public IReadOnlyList<ScriptSpot> Spots(IEnumerable<GameFrame> frames)
    {
        Dictionary<int, (int Calls, double Self, double Total, double Engine)> summed = [];
        foreach (ScriptWork work in frames.SelectMany(f => f.Scripts))
        {
            (int Calls, double Self, double Total, double Engine) so = summed.GetValueOrDefault(work.Function);
            summed[work.Function] = (so.Calls + work.Calls, so.Self + work.SelfMs, so.Total + work.TotalMs, so.Engine + work.EngineMs);
        }
        return [.. summed
            .Select(s => new ScriptSpot(FunctionAt(s.Key), s.Value.Calls, s.Value.Self, s.Value.Total, s.Value.Engine))
            .OrderByDescending(s => s.SelfMs)
            .ThenBy(s => s.Function.Name, StringComparer.Ordinal)];
    }

    /// <summary>The roots a call tree shows beside a run's threads for some frames, each time as the run's samples count it
    /// (<paramref name="sampleMs"/> a sample): GDScript as Godot measured it, each function with its own time and its time
    /// with what it called, beside one another as Godot measures them, without their callers, and its calls into Godot's API;
    /// and Godot's own work, the frames' time beyond their scripts and the C# the engine called in each (<paramref name="csharpMs"/>).</summary>
    public IReadOnlyList<CallNode> CallRoots(IReadOnlyList<GameFrame> frames, IReadOnlyList<double> csharpMs, double sampleMs)
    {
        int Counted(double ms) => (int)Math.Round(Math.Max(0, ms) / sampleMs);
        IReadOnlyList<ScriptSpot> spots = Spots(frames);
        CallNode scripts = new(GDScriptRoot, "Godot", null) { Total = Counted(frames.Sum(f => f.ScriptMs)) };
        foreach (ScriptSpot spot in spots.Where(s => !s.Function.IsEngineCall))
        {
            CallNode function = scripts.Child(spot.Function.Name, Placed(spot.Function), null);
            function.Script = spot.Function;
            function.Self = Counted(spot.SelfMs);
            function.Total = Math.Max(function.Self, Counted(spot.TotalMs));
        }
        List<ScriptSpot> calls = [.. spots.Where(s => s.Function.IsEngineCall)];
        if (calls.Count > 0)
        {
            CallNode api = scripts.Child(ApiRoot, "Godot", null);
            foreach (ScriptSpot call in calls)
            {
                CallNode called = api.Child(call.Function.Name, $"from {Path.GetFileName(call.Function.File)}", null);
                called.Self = called.Total = Counted(call.SelfMs);
                api.Total += called.Total;
            }
        }
        double own = frames.Select((f, i) => f.FrameMs - f.ScriptMs - (i < csharpMs.Count ? csharpMs[i] : 0)).Where(ms => ms > 0).Sum();
        CallNode engine = new(EngineRoot, "Godot", null) { Self = Counted(own), Total = Counted(own) };
        return [scripts, engine];
    }

    /// <summary>The call tree's root of the frames' GDScript.</summary>
    public const string GDScriptRoot = "GDScript, as Godot's profiler measured it";

    /// <summary>The call tree's row of the frames' scripts' calls into Godot's API, under <see cref="GDScriptRoot"/>.</summary>
    public const string ApiRoot = "Scripts' calls into Godot's API";

    /// <summary>The call tree's root of the time the frames took in Godot's own work.</summary>
    public const string EngineRoot = "Godot's own work in its frames";

    /// <summary>Where a script function is, as a row says it: its file's name and its line ("wander.gd:10").</summary>
    public static string Placed(ScriptFunction function) => function.Line > 0 ? $"{Path.GetFileName(function.File)}:{function.Line}" : Path.GetFileName(function.File);

    /// <summary>A function by its number, or one standing for a number the frames name that no signature told.</summary>
    public ScriptFunction FunctionAt(int number) => number >= 0 && number < Functions.Count ? Functions[number] : new ScriptFunction(string.Empty, 0, $"function #{number}");

    /// <summary>The servers' functions' time over some frames, by server then function in the order Godot gave them.</summary>
    public static IReadOnlyList<ServerWork> Servers(IEnumerable<GameFrame> frames)
    {
        Dictionary<(string Server, string Function), double> summed = [];
        List<(string Server, string Function)> order = [];
        foreach (ServerWork work in frames.SelectMany(f => f.Servers))
        {
            if (!summed.TryGetValue((work.Server, work.Function), out double so))
            {
                order.Add((work.Server, work.Function));
            }
            summed[(work.Server, work.Function)] = so + work.Ms;
        }
        return [.. order.Select(o => new ServerWork(o.Server, o.Function, summed[o]))];
    }

    /// <summary>The renderer's stages' time over some frames, in the order Godot gave them.</summary>
    public static IReadOnlyList<RenderArea> Rendered(IEnumerable<GameFrame> frames)
    {
        Dictionary<string, (double Cpu, double Gpu)> summed = new(StringComparer.Ordinal);
        List<string> order = [];
        foreach (RenderArea area in frames.SelectMany(f => f.Rendered))
        {
            if (!summed.TryGetValue(area.Name, out (double Cpu, double Gpu) so))
            {
                order.Add(area.Name);
            }
            summed[area.Name] = (so.Cpu + area.CpuMs, so.Gpu + area.GpuMs);
        }
        return [.. order.Select(n => new RenderArea(n, summed[n].Cpu, summed[n].Gpu))];
    }

    /// <summary>Why the frames kept beside the trace could not be read, where they could not; the profile then has none.</summary>
    public string? Why { get; private init; }

    /// <summary>The frames kept beside a trace (<see cref="GameRecording.Write"/>) read back, each placed on the trace's clock
    /// (<paramref name="traceMs"/> turning a time into milliseconds from its start; null for a run of frames alone, timed from
    /// the start of the first frame to arrive); null where none were kept, and none, saying why, where they cannot be read.</summary>
    internal static GameProfile? ReadBeside(string trace, Func<DateTime, double>? traceMs)
    {
        string path = PathOf(trace);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return Read(path, traceMs);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            return new GameProfile("Godot", string.Empty, false, null, [], []) { Why = $"The game's frames kept in {Path.GetFileName(path)} could not be read: {unread.Message}" };
        }
    }

    private static GameProfile Read(string path, Func<DateTime, double>? traceMs)
    {
        using BinaryReader reader = new(File.OpenRead(path));
        if (reader.ReadString() != Magic || reader.ReadInt32() != Version)
        {
            throw new IOException($"{Path.GetFileName(path)} is not a game's frames Passenger wrote");
        }
        string engine = reader.ReadString();
        string folder = reader.ReadString();
        bool draws = reader.ReadBoolean();
        string? gpu = reader.ReadBoolean() ? reader.ReadString() : null;
        ScriptFunction[] functions = new ScriptFunction[reader.ReadInt32()];
        for (int i = 0; i < functions.Length; i++)
        {
            functions[i] = ScriptFunction.Of(reader.ReadString());
        }
        int count = reader.ReadInt32();
        List<(GameFrame Frame, DateTime Received)> frames = new(count);
        for (int i = 0; i < count; i++)
        {
            DateTime received = new(reader.ReadInt64(), DateTimeKind.Utc);
            int number = reader.ReadInt32();
            double frameMs = reader.ReadDouble();
            double processMs = reader.ReadDouble();
            double physicsMs = reader.ReadDouble();
            double stepMs = reader.ReadDouble();
            double scriptMs = reader.ReadDouble();
            ServerWork[] servers = new ServerWork[reader.ReadInt32()];
            for (int s = 0; s < servers.Length; s++)
            {
                servers[s] = new ServerWork(reader.ReadString(), reader.ReadString(), reader.ReadDouble());
            }
            ScriptWork[] scripts = new ScriptWork[reader.ReadInt32()];
            for (int s = 0; s < scripts.Length; s++)
            {
                scripts[s] = new ScriptWork(reader.ReadInt32(), reader.ReadInt32(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
            }
            RenderArea[] rendered = new RenderArea[reader.ReadInt32()];
            for (int r = 0; r < rendered.Length; r++)
            {
                rendered[r] = new RenderArea(reader.ReadString(), reader.ReadDouble(), reader.ReadDouble());
            }
            frames.Add((new GameFrame(number, 0, frameMs, processMs, physicsMs, stepMs, scriptMs, servers, scripts) { Rendered = rendered }, received));
        }
        // A run of frames alone begins as its first frame to arrive did.
        (GameFrame Frame, DateTime Received)? earliest = frames.Count > 0 ? frames.MinBy(f => f.Received) : null;
        DateTime first = earliest is { } came ? came.Received.AddMilliseconds(-came.Frame.FrameMs) : default;
        return new GameProfile(engine, folder, draws, gpu, functions, Placed(frames, traceMs ?? (at => (at - first).TotalMilliseconds)));
    }

    /// <summary>Frames in order, each ended where its arrival allows: no later than it came, nor than the next frame began. A
    /// game sends its frames in batches, so a frame's arrival bounds its end only loosely, and a later one's arrival,
    /// less the frames between, often bounds it closer.</summary>
    internal static IReadOnlyList<GameFrame> Placed(IReadOnlyList<(GameFrame Frame, DateTime Received)> frames, Func<DateTime, double> traceMs)
    {
        List<(GameFrame Frame, DateTime Received)> ordered = [.. frames.OrderBy(f => f.Frame.Number)];
        GameFrame[] placed = new GameFrame[ordered.Count];
        double bound = double.PositiveInfinity;
        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            (GameFrame frame, DateTime received) = ordered[i];
            double end = Math.Min(traceMs(received), bound);
            placed[i] = frame with { EndMs = end };
            bound = end - frame.FrameMs;
        }
        return placed;
    }

    /// <summary>Writes a recording's frames, as <see cref="Read"/> reads them.</summary>
    internal static void Write(string path, string engine, string folder, bool draws, string? gpu, IReadOnlyList<string> signatures,
        IReadOnlyList<(GameFrame Frame, DateTime Received)> frames)
    {
        AtomicFile.Write(path, stream =>
        {
            using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(engine);
            writer.Write(folder);
            writer.Write(draws);
            writer.Write(gpu is not null);
            if (gpu is not null)
            {
                writer.Write(gpu);
            }
            writer.Write(signatures.Count);
            foreach (string signature in signatures)
            {
                writer.Write(signature);
            }
            writer.Write(frames.Count);
            foreach ((GameFrame frame, DateTime received) in frames)
            {
                writer.Write(received.Ticks);
                writer.Write(frame.Number);
                writer.Write(frame.FrameMs);
                writer.Write(frame.ProcessMs);
                writer.Write(frame.PhysicsMs);
                writer.Write(frame.StepMs);
                writer.Write(frame.ScriptMs);
                writer.Write(frame.Servers.Count);
                foreach (ServerWork server in frame.Servers)
                {
                    writer.Write(server.Server);
                    writer.Write(server.Function);
                    writer.Write(server.Ms);
                }
                writer.Write(frame.Scripts.Count);
                foreach (ScriptWork script in frame.Scripts)
                {
                    writer.Write(script.Function);
                    writer.Write(script.Calls);
                    writer.Write(script.SelfMs);
                    writer.Write(script.TotalMs);
                    writer.Write(script.EngineMs);
                }
                writer.Write(frame.Rendered.Count);
                foreach (RenderArea area in frame.Rendered)
                {
                    writer.Write(area.Name);
                    writer.Write(area.CpuMs);
                    writer.Write(area.GpuMs);
                }
            }
        }, null, AtomicFile.Temporary, static (temporary, target) => File.Move(temporary, target, overwrite: true));
    }
}

/// <summary>A Godot game's frame profile as it comes over its debugger's connection while the game is profiled: the script
/// functions' signatures by Godot's numbers, each frame with when it came, the renderer's stages where the game draws, and
/// the GPU. Taken on any thread; <see cref="Stop"/> ends it, after which nothing more is taken, and the profiler writes it beside
/// the run's trace.</summary>
public sealed class GameRecording(string engine, string folder, bool draws)
{
    /// <summary>The most frames kept, a quarter of an hour at 240 frames a second: past it, the oldest go.</summary>
    private const int MostFrames = 216_000;

    private readonly Lock gate = new();
    private readonly List<string> signatures = [];
    private readonly List<(GameFrame Frame, DateTime Received)> frames = [];
    private IReadOnlyList<RenderArea>? pending;
    private string? gpu;
    private bool stopped;

    /// <summary>The recording ended: the game is to stop sending its frames.</summary>
    public event EventHandler? Stopped;

    public bool Draws => draws;

    /// <summary>Whether it has ended.</summary>
    public bool IsStopped
    {
        get
        {
            lock (gate)
            {
                return stopped;
            }
        }
    }

    /// <summary>How many frames it holds.</summary>
    public int Count
    {
        get
        {
            lock (gate)
            {
                return frames.Count;
            }
        }
    }

    /// <summary>The signature Godot numbers a script function by from now in this recording.</summary>
    public void Signature(int number, string signature)
    {
        lock (gate)
        {
            if (stopped || number < 0 || number > 100_000)
            {
                return;
            }
            while (signatures.Count <= number)
            {
                signatures.Add(string.Empty);
            }
            signatures[number] = signature;
        }
    }

    /// <summary>A frame, as it came.</summary>
    public void Took(GameFrame frame, DateTime received)
    {
        lock (gate)
        {
            if (stopped)
            {
                return;
            }
            if (pending is { } areas)
            {
                frame = frame with { Rendered = areas };
                pending = null;
            }
            frames.Add((frame, received));
            if (frames.Count > MostFrames)
            {
                frames.RemoveRange(0, MostFrames / 10);
            }
        }
    }

    /// <summary>The renderer's stages Godot measured last, which it numbers by its own count of frames drawn and resolves a
    /// frame or two late: they go with the frame that came last, or, before any did, with the next to come.</summary>
    public void Rendered(IReadOnlyList<RenderArea> areas)
    {
        lock (gate)
        {
            if (stopped || areas.Count == 0)
            {
                return;
            }
            if (frames.Count > 0 && frames[^1].Frame.Rendered.Count == 0)
            {
                frames[^1] = (frames[^1].Frame with { Rendered = areas }, frames[^1].Received);
                return;
            }
            pending = areas;
        }
    }

    /// <summary>The GPU the game draws with, as Godot names it.</summary>
    public void Hardware(string named)
    {
        lock (gate)
        {
            gpu = named.Length > 0 ? named : gpu;
        }
    }

    /// <summary>Ends the recording; true the first time.</summary>
    public bool Stop()
    {
        lock (gate)
        {
            if (stopped)
            {
                return false;
            }
            stopped = true;
        }
        Stopped?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Writes what it holds beside a trace (<see cref="GameProfile.PathOf"/>).</summary>
    internal void Write(string trace)
    {
        string[] named;
        (GameFrame Frame, DateTime Received)[] taken;
        string? drewWith;
        lock (gate)
        {
            named = [.. signatures];
            taken = [.. frames];
            drewWith = gpu;
        }
        GameProfile.Write(GameProfile.PathOf(trace), engine, folder, draws, drewWith, named, taken);
    }
}
