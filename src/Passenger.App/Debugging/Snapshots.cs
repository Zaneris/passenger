using Passenger.Core;
using Passenger.Dap;

namespace Passenger.App.Debugging;

/// <summary>A snapshot point's capture as the folder keeps it, for its debug session and after it: what the debugger read
/// (<see cref="SnapshotEvent"/>), the point it is of, the program it was taken in, and its number among the folder's, from
/// one. Its frames and values read as a stop's do, each row's members by their reference among its own.</summary>
public sealed class TakenSnapshot(SnapshotEvent taken, string point, string program, int number, DapMoment? moment = null)
{
    /// <summary>The recorded moment it is, where it is one of a recording's rather than a snapshot point's capture.</summary>
    public DapMoment? Moment { get; } = moment;

    public SnapshotEvent Taken { get; } = taken;

    /// <summary>The point it is of as the Breakpoints window names it: "Program.cs:12".</summary>
    public string Point { get; } = point;

    /// <summary>The program it was taken in, as its Debug tab names it.</summary>
    public string Program { get; } = program;

    public int Number { get; } = number;

    /// <summary>How long the program was held for it.</summary>
    public TimeSpan Paused => TimeSpan.FromMilliseconds(Taken.PausedMilliseconds);

    /// <summary>Its thread as the Debug window's threads name it.</summary>
    public DapThread Thread => new(Taken.ThreadId, Taken.ThreadName ?? string.Empty);

    /// <summary>Its frames, its thread's from the top.</summary>
    public IReadOnlyList<DapStackFrame> Frames => [.. Taken.Frames.Select(f => f.Frame)];

    /// <summary>The frame shown first: the top one with a source, else the top one.</summary>
    public DapStackFrame? Top => Frames.FirstOrDefault(f => f.Source?.Path is not null) ?? Frames.FirstOrDefault();

    /// <summary>The rows a reference names: a frame's locals, a row's members.</summary>
    public IReadOnlyList<DapVariable> Values(int reference) => Taken.Values.TryGetValue(reference, out IReadOnlyList<DapVariable>? rows) ? rows : [];

    /// <summary>A frame's locals as they were read, none where they were not.</summary>
    public IReadOnlyList<DapVariable> LocalsOf(DapStackFrame frame) =>
        Taken.Frames.FirstOrDefault(f => f.Frame.Id == frame.Id) is { Locals: > 0 } read ? Values(read.Locals) : [];

    /// <summary>What it is, when it was taken and how long the program was held for it, plainly: "Snapshot 3 of Program.cs:12,
    /// taken at 14:02:11.532 on Main Thread. The program was held 1.3 ms."</summary>
    public string Said => Moment is { } recorded
        ? $"Recorded moment {Numbers.Said(recorded.Index)}: {recorded.Said}{(recorded.Kind == DapMomentKinds.Call ? " called" : "")}, at {Time} on {ThreadWords}. "
            + recorded.Kind switch
            {
                DapMomentKinds.Call => "Its arguments are as they were then.",
                DapMomentKinds.Return => "Its arguments are as they were at its call.",
                _ => "Its values are as they were then.",
            }
        : $"Snapshot {Number} of {Point}, taken at {Time} on {ThreadWords}. The program was held {Durations.Said(Paused)}.";

    private string ThreadWords => string.IsNullOrEmpty(Taken.ThreadName) ? $"thread {Taken.ThreadId}" : Taken.ThreadName;

    /// <summary>Why a frame below its top shows no values: what it reads.</summary>
    public string Unread(DapStackFrame frame) => Moment is not null ? "A recording reads the values of each call's own frame alone"
        : frame.PresentationHint == FrameHints.Subtle ? "A snapshot reads the values of the user's code alone" : "A snapshot reads the values of its top frames of the user's code";

    /// <summary>When it was taken, to the millisecond, by the machine's clock where Passenger runs.</summary>
    public string Time => Taken.At.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Its thread as a person reads it.</summary>
    public string ThreadName => string.IsNullOrEmpty(Taken.ThreadName) ? $"thread {Taken.ThreadId}" : $"{Taken.ThreadName} ({Taken.ThreadId})";
}
