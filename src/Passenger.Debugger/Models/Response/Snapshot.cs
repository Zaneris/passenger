namespace Passenger.Debugger.Models.Response;

/// <summary>A snapshot point's capture: the thread's frames and the values read of them where the point's hit stood, copied
/// whole into plain rows, each row's members (and each frame's locals) by a reference of the snapshot's own among
/// <see cref="Values"/>; the point's own expressions as read in its top frame; how long the program was held for it.</summary>
public sealed record Snapshot(int BreakpointId, int ThreadId, string? ThreadName, DateTimeOffset At, TimeSpan Paused, IReadOnlyList<SnapshotFrame> Frames,
    IReadOnlyDictionary<int, IReadOnlyList<VariableInfo>> Values, IReadOnlyList<VariableInfo> Asked, string? Cut);

/// <summary>A frame of a snapshot, its locals by their reference among the snapshot's values, 0 where they were not read.</summary>
public sealed record SnapshotFrame(StackFrameInfo Frame, int Locals);
