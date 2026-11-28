namespace Passenger.Debugger.Models.Response;

/// <summary>What a moment of a recording is: a call of the user's code entered, its arguments read, or one returning, its
/// value read in its caller; an exception thrown in the user's code (or reaching it), or caught there; or one of the
/// person's breakpoints passed by, its frame's values read.</summary>
public enum MomentKind
{
    Call,
    Return,
    Thrown,
    Caught,
    Passed,
}

/// <summary>A moment a recording kept, as its timeline lists it: its number in the session's recording, from one; what it
/// is, on which thread and when; the method's frame where it stood (a call at its first line, a return at its last); how
/// many recorded calls it ran inside; and what happened, in words ("Add(2, 3)", "Add() returned 5").</summary>
public sealed record RecordedMoment(int Index, MomentKind Kind, int ThreadId, DateTimeOffset At, StackFrameInfo Place, int Depth, string Said);

/// <summary>A recording's timeline on a thread: whether the session records now, its moments oldest first, how many older
/// ones it let go to keep within its bounds (<see cref="MostMoments"/>, <see cref="MostBytes"/> of values), how many it
/// recorded in all, and how long recording them held the program in all.</summary>
public sealed record RecordedTimeline(bool Recording, IReadOnlyList<RecordedMoment> Moments, int Dropped, int Recorded, TimeSpan Held, int MostMoments, long MostBytes);
