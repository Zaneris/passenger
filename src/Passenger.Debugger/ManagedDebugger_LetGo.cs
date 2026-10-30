using System.Collections.Concurrent;
using System.Diagnostics;
using Passenger.Core;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>Letting go of a program held at a breakpoint or a step's end. With CET's shadow stacks (x64 Windows, any
/// .NET 9+ apphost) a thread resumes from the runtime's handling of a patch or a single step only by the debugger
/// setting its context out of process, which mscordbi's Detach waits for only once it has answered one such request:
/// let go before, such a thread finds no debugger to answer and the program ends with 0xC0000409, so the engine runs it
/// off its stop first.</summary>
public partial class ManagedDebugger
{
    /// <summary>The thread of the stop shown when the runtime stopped it inside its handling of a breakpoint or a step's end
    /// (a patch it hit or a single step): one that is resumed through that handling, not from where it stands.</summary>
    private int? _stoppedInDispatch;

    /// <summary>Where a thread stands: its leaf frame, by its place on the stack, and the native offset in it.</summary>
    private readonly record struct ThreadPlace(CORDB_ADDRESS FrameStart, CORDB_ADDRESS FrameEnd, uint Offset);

    /// <summary>The thread an event of a patch or a single step stopped, which the runtime resumes through its handling.</summary>
    private static int? DispatchThread(CorDebugManagedCallbackEventArgs callbackEvent) => callbackEvent switch
    {
        BreakpointCorDebugManagedCallbackEventArgs hit => hit.Thread.Id,
        StepCompleteCorDebugManagedCallbackEventArgs step => step.Thread.Id,
        DataBreakpointCorDebugManagedCallbackEventArgs write => write.Thread.Id,
        _ => null,
    };

    /// <summary>Runs the program a moment and stops it again, still held, until every thread held inside the runtime's
    /// handling of a breakpoint or a step's end has moved on from where it stood (<paramref name="held"/>), or the
    /// program has ended, four rounds are spent, or the client's time (<paramref name="until"/>) is up. Its breakpoints
    /// and steppers are gone by now, so none stops it again; the events that come meanwhile are let go, and another
    /// thread's breakpoint queued behind the stop joins those watched.</summary>
    private void RunOffDispatch(ICorDebugProcess process, Dictionary<int, ThreadPlace?> held, long? until)
    {
        if (!held.Any(h => h.Value is { } place && PlaceOf(process, h.Key) == place))
        {
            return;
        }
        // Never disposed: a callback under way as the handler is taken off may still add to it.
        BlockingCollection<CorDebugManagedCallbackEventArgs> events = [];
        void Take(object? sender, CorDebugManagedCallbackEventArgs callbackEvent)
        {
            if (callbackEvent is ExitProcessCorDebugManagedCallbackEventArgs)
            {
                _exited.Set();
            }
            events.Add(callbackEvent);
        }
        void Note(CorDebugManagedCallbackEventArgs callbackEvent)
        {
            if (DispatchThread(callbackEvent) is int thread && !_exited.IsSet)
            {
                held.TryAdd(thread, PlaceOf(process, thread));
            }
        }

        _callbacks.OnAnyEvent += Take;
        try
        {
            TimeSpan running = TimeSpan.FromMilliseconds(20);
            for (int round = 1; round <= 4; round++, running *= 5)
            {
                if (until is long by)
                {
                    TimeSpan left = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), by);
                    if (left <= TimeSpan.Zero)
                    {
                        _logger?.Invoke("Letting go: the client's time for it is up, with threads still held");
                        return;
                    }
                    running = running < left ? running : left;
                }
                // Every hold continued (the stop shown, a pause's, events put off): the runtime resumes the threads it held.
                for (int holds = 0; holds < 16 && !_exited.IsSet && process.TryIsRunning(out bool isRunning) is Cor.S_OK && !isRunning; holds++)
                {
                    if (process.TryContinue(false) is not Cor.S_OK)
                    {
                        break;
                    }
                }
                Stopwatch since = Stopwatch.StartNew();
                for (TimeSpan left = running; !_exited.IsSet && left > TimeSpan.Zero; left = running - since.Elapsed)
                {
                    if (events.TryTake(out CorDebugManagedCallbackEventArgs? callbackEvent, left) && !_exited.IsSet)
                    {
                        Note(callbackEvent);
                        process.TryContinue(false);
                    }
                }
                int stopped = _exited.IsSet ? Cor.CORDBG_E_PROCESS_TERMINATED : process.TryStop(0);
                if (stopped is not Cor.S_OK)
                {
                    // Ending, its runtime's word of the end on its way: the session ends as at an exit, not by a detach.
                    bool ended = stopped is Cor.CORDBG_E_PROCESS_TERMINATED && _exited.Wait(TimeSpan.FromSeconds(5));
                    _logger?.Invoke($"Letting go: the program {(ended ? "ended" : $"could not be stopped (0x{stopped:X8})")} as it ran off its stop");
                    return;
                }
                // Events dispatched as it stopped stay unanswered, as at any detach.
                while (events.TryTake(out CorDebugManagedCallbackEventArgs? callbackEvent))
                {
                    Note(callbackEvent);
                }
                int still = held.Count(h => h.Value is { } place && PlaceOf(process, h.Key) == place);
                _logger?.Invoke($"Letting go: after {running.TotalMilliseconds:0} ms running, {held.Count - still} of {held.Count} held threads have moved on");
                if (still == 0)
                {
                    return;
                }
            }
        }
        finally
        {
            _callbacks.OnAnyEvent -= Take;
        }
    }

    /// <summary>Waits out the evaluations given up that still run in the program before it is let go: Detach is refused
    /// while one runs, and one that returns once the debugger has gone holds its thread for good. The program runs
    /// until each has ended, other events let go, each waited for to the end of its own time
    /// (<see cref="EvalStatus.DueAt"/>) or the client's (<paramref name="until"/>), but one that could not be stopped.
    /// None is aborted: an abort fires wherever its thread then is, which can end the program.</summary>
    private void EndAbandonedEvaluations(ICorDebugProcess process, long? until)
    {
        Dictionary<int, long> left = EvalStatus.DueAt.Where(d => EvalStatus.Abandoned.ContainsKey(d.Key)).ToDictionary();
        if (left.Count == 0)
        {
            return;
        }
        // Never disposed: a callback under way as the handler is taken off may still add to it.
        BlockingCollection<CorDebugManagedCallbackEventArgs> events = [];
        void Take(object? sender, CorDebugManagedCallbackEventArgs callbackEvent)
        {
            if (callbackEvent is ExitProcessCorDebugManagedCallbackEventArgs)
            {
                _exited.Set();
            }
            events.Add(callbackEvent);
        }
        _callbacks.OnAnyEvent += Take;
        try
        {
            for (int holds = 0; holds < 16 && !_exited.IsSet && process.TryIsRunning(out bool isRunning) is Cor.S_OK && !isRunning; holds++)
            {
                if (process.TryContinue(false) is not Cor.S_OK)
                {
                    break;
                }
            }
            Stopwatch since = Stopwatch.StartNew();
            while (left.Count > 0 && !_exited.IsSet && Waited(left) is { } wait && wait > TimeSpan.Zero)
            {
                if (events.TryTake(out CorDebugManagedCallbackEventArgs? callbackEvent, wait) && !_exited.IsSet)
                {
                    if (EvaluationThread(callbackEvent) is { } thread)
                    {
                        left.Remove(thread.Id);
                        // Its value let go, as its end would have been had the program gone on (LetGoAbandonedEvaluation).
                        if (EvalStatus.Abandoned.Remove(thread.Id, out ICorDebugEval? ended) && ended.TryGetResult(out ICorDebugValue? value) is Cor.S_OK
                            && value is ICorDebugHandleValue handle)
                        {
                            handle.TryDispose();
                        }
                        EvalStatus.DueAt.Remove(thread.Id);
                    }
                    process.TryContinue(false);
                }
            }
            _logger?.Invoke(left.Count == 0 ? "Letting go: the evaluations given up have ended"
                : $"Letting go: {Plural.Of(left.Count, "evaluation")} given up had not ended after {since.Elapsed.TotalSeconds:0.0} s, their time up");
            if (!_exited.IsSet)
            {
                process.TryStop(0);
            }
        }
        finally
        {
            _callbacks.OnAnyEvent -= Take;
        }

        // To the end of the latest time still to come of those left, or the client's, whichever is first.
        TimeSpan? Waited(Dictionary<int, long> running)
        {
            long by = running.Values.Max();
            if (until is long client && client < by)
            {
                by = client;
            }
            return Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), by);
        }
    }

    private static ThreadPlace? PlaceOf(ICorDebugProcess process, int threadId) =>
        process.TryGetThread((uint)threadId, out ICorDebugThread thread) is Cor.S_OK
        && thread.TryGetActiveFrame(out ICorDebugFrame frame) is Cor.S_OK && frame is ICorDebugNativeFrame native
        && native.TryGetIP(out uint offset) is Cor.S_OK && RangeOf(frame) is { } range
            ? new ThreadPlace(range.Start, range.End, offset)
            : null;
}
