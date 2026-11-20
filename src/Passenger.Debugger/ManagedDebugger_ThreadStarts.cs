using Passenger.Core;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>Threads standing at a method's first instruction, where the runtime stops a thread it has just started to
/// announce it: while one is held there, the runtime counts it as standing where no collection can run, and a collection
/// asked meanwhile waits for it. An evaluation holds every other thread, so one held there would keep the evaluation's
/// collection (a GC.Collect, one an allocation brings) waiting for good: each is left running as the evaluation is let
/// go, the program held again at once and after each short go until it has left that place, and is then held where it
/// stands. The stop the evaluation is set up from is left as it is: no go of the program comes before the evaluation's.</summary>
public partial class ManagedDebugger
{
    /// <summary>How often a thread standing at its first instruction is let run on, the program held again after, before it
    /// is held where it stands.</summary>
    private const int TriesPastAStart = 16;

    /// <summary>The value of <see cref="_continued"/> when the threads were last found clear of their first instruction:
    /// held while evaluations run at one stop, they stand where they stood until the program is let go.</summary>
    private int _startsClearAt = -1;

    /// <summary>The threads the hold of the evaluation being let go left running at their first instruction, and how they
    /// stood, for <see cref="ClearStartsAsync"/>.</summary>
    private (List<ICorDebugThread> Threads, string Named)? _leftAtTheirStarts;

    /// <summary>Whether a thread's innermost frame stands at its method's first instruction, where a thread the runtime
    /// stopped is counted as standing where no collection can run.</summary>
    private static bool AtItsFirstInstruction(ICorDebugThread thread)
    {
        try
        {
            return thread.TryGetActiveFrame(out ICorDebugFrame? leaf) is Cor.S_OK && leaf is ICorDebugNativeFrame native
                && native.TryGetIP(out uint offset) is Cor.S_OK && offset == 0;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return false;
        }
    }

    /// <summary>As an evaluation on the thread given holds the others, the program held, leaves running each other thread
    /// standing at its first instruction, but one whose event waits to be handled where it stands, for
    /// <see cref="ClearStartsAsync"/> to let past it once the evaluation is let go.</summary>
    private void LeaveStartsRunning(ICorDebugThread evaluating)
    {
        _leftAtTheirStarts = null;
        if (_startsClearAt == _continued)
        {
            return;
        }
        HashSet<int> waiting = [.. _deferredEvents.Concat(_behindStep).Select(WaitingThread).OfType<ICorDebugThread>().Select(thread => thread.Id)];
        List<ICorDebugThread> starting;
        try
        {
            starting = [.. evaluating.Process.EnumerateThreads().Where(thread => thread.Id != evaluating.Id && !waiting.Contains(thread.Id) && AtItsFirstInstruction(thread))];
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"The threads could not be looked at for one at its first instruction: {error.Message}");
            return;
        }
        if (starting.Count == 0)
        {
            _startsClearAt = _continued;
            return;
        }
        foreach (ICorDebugThread thread in starting)
        {
            thread.TrySetDebugState(CorDebugThreadState.THREAD_RUN);
        }
        _leftAtTheirStarts = (starting, StartsTold(starting));
    }

    /// <summary>With the evaluation on the thread given let go, lets each thread its hold left at its first instruction
    /// (<see cref="LeaveStartsRunning"/>) run past it, then holds it where it stands until the evaluation ends.</summary>
    private async Task ClearStartsAsync(ICorDebugThread evaluating)
    {
        if (_leftAtTheirStarts is not ({ } threads, string named) || _process is not { } process)
        {
            return;
        }
        _leftAtTheirStarts = null;
        if (await LetPastThenHoldAsync(process, threads, named, $"as an evaluation on thread {evaluating.Id} was let go").ConfigureAwait(false))
        {
            _startsClearAt = _continued;
        }
    }

    /// <summary>A thread the runtime announced while an evaluation runs, from its first instruction, its event let go: let
    /// run on past it, then held where it stands until the evaluation ends, as the threads the evaluation's hold took are.
    /// Left running, it would pass a breakpoint whose event waits for the evaluation's end and run on, so the stop shown
    /// later would not be where the thread is. Named as it stood when announced.</summary>
    private async Task HoldPastItsStartAsync(ICorDebugThread thread, int evaluating, string named)
    {
        if (_process is { } process)
        {
            await LetPastThenHoldAsync(process, [thread], named, $"made during an evaluation on thread {evaluating}").ConfigureAwait(false);
        }
    }

    /// <summary>With the program running and an evaluation under way, lets the threads given run past their first
    /// instruction (<see cref="LetPastTheirStartsAsync"/>), then holds each where it stands, as the threads the
    /// evaluation's hold took are, and lets the evaluation go on. Whether every one of them left that place.</summary>
    private async Task<bool> LetPastThenHoldAsync(ICorDebugProcess process, List<ICorDebugThread> threads, string named, string when)
    {
        List<ICorDebugThread> starting = [.. threads];
        if (!await LetPastTheirStartsAsync(process, starting, named, when).ConfigureAwait(false))
        {
            return false;
        }
        foreach (ICorDebugThread thread in threads)
        {
            Hold(thread, when);
            _logger?.Invoke($"Held: thread {thread.Id}, {when}, {(starting.Contains(thread) ? "still at" : "past")} its first instruction");
        }
        process.TryContinue(false);
        return starting.Count == 0;
    }

    /// <summary>With the program running, holds it again at once, and again after each go, until none of the threads given
    /// stands at its first instruction, an event comes (which holds the program until it is handled), or the tries run out;
    /// those still there are left in the list. A thread let go may still be on its way out of the runtime's stop when the
    /// program is held again, so each go is given longer. Whether the program is held.</summary>
    private async Task<bool> LetPastTheirStartsAsync(ICorDebugProcess process, List<ICorDebugThread> starting, string named, string when)
    {
        List<ICorDebugThread> given = [.. starting];
        int goes = 0;
        bool came = false;
        while (true)
        {
            goes++;
            int wait = goes <= 2 ? 0 : Math.Min(1 << (goes - 3), 8);
            if (wait > 0)
            {
                await Task.Delay(wait).ConfigureAwait(false);
            }
            if (process.TryStop(0) is var held and not Cor.S_OK)
            {
                _logger?.Invoke($"The program could not be held again with threads {named} let run past their first instruction: 0x{held:X8}");
                return false;
            }
            starting.RemoveAll(thread => !AtItsFirstInstruction(thread));
            came = _runtimeEventChannel.Reader.TryPeek(out _);
            if (starting.Count == 0 || goes == TriesPastAStart || came)
            {
                break;
            }
            if (process.TryContinue(false) is var gone and not Cor.S_OK)
            {
                _logger?.Invoke($"The program could not be let go again for threads {named} to run past their first instruction: 0x{gone:X8}");
                return false;
            }
        }
        string now = string.Join(", ", given.Select(thread => $"{thread.Id} in {StandTold(thread)}"));
        _logger?.Invoke(starting.Count == 0
            ? $"Let past their first instruction, {when}, in {Plural.Of(goes, "go", "goes")}: threads {named}, now {now}"
            : $"Still at their first instruction, {when}, {(came ? "an event come" : "the tries spent")} after {Plural.Of(goes, "go", "goes")}: threads {string.Join(", ", starting.Select(thread => thread.Id))} of {named}");
        return true;
    }

    /// <summary>Each thread given and where it stands, read while the program is held.</summary>
    private string StartsTold(IEnumerable<ICorDebugThread> threads) => string.Join(", ", threads.Select(thread => $"{thread.Id} ({StandTold(thread)})"));

    /// <summary>The method a thread's innermost frame runs, as the call stack names it, or native code.</summary>
    private string StandTold(ICorDebugThread thread)
    {
        try
        {
            return thread.TryGetActiveFrame(out ICorDebugFrame? leaf) is Cor.S_OK && leaf is not null ? FrameTold(leaf) : "[Native code]";
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return UnknownMethod;
        }
    }
}
