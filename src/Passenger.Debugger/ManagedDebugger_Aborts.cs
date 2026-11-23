using System.Diagnostics;
using System.Threading.Channels;
using Passenger.Core;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>Stopping an evaluation past its time. The runtime takes an abort only where the thread runs managed code,
/// at a safe point and outside any catch or finally; asked anywhere else (a wait on a lock a paused thread holds, a
/// sleep, a read of input, a finally), it stays pending until the thread gets there, which for a wait is after the
/// program has gone on, inside what it waited for: a lock, then never let go. So an abort is asked only of a thread
/// caught, the program held a moment, where it takes as the thread goes on, and never a rude one, which runs no
/// finally.</summary>
public partial class ManagedDebugger
{
    /// <summary>Why an abort asked now of a thread running an evaluation, the program held, would not take as the thread goes
    /// on: it runs a catch or finally block, whatever it does inside it (a call, a wait, native code); it waits; or it runs
    /// outside the runtime or where the runtime cannot stop it. Null when it would.</summary>
    private string? WhyNoAbortTakes(ICorDebugThread thread)
    {
        // A handler on any of its frames holds the abort off until it is left, wherever the thread is above it.
        try
        {
            foreach (ICorDebugILFrame frame in EvaluationFrames(thread))
            {
                if (frame.TryGetIP(out uint offset, out _) is Cor.S_OK && FindModule(frame.Function.Module) is { } module
                    && module.MetadataReader.InHandler(frame.Function.Token, (int)offset, ModuleMetadataReader.VersionOf(frame)))
                {
                    return "it runs a catch or finally block";
                }
            }
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return "its frames could not be read";
        }
        if (thread.TryGetUserState(out CorDebugUserState state) is not Cor.S_OK)
        {
            return "its state could not be read";
        }
        if ((state & CorDebugUserState.USER_WAIT_SLEEP_JOIN) != 0)
        {
            return "it waits";
        }
        // Not at a safe point: in native code (a read of input) below its managed caller, or where no abort is taken yet.
        if ((state & CorDebugUserState.USER_UNSAFE_POINT) != 0 || thread.TryGetActiveFrame(out ICorDebugFrame? leaf) is not Cor.S_OK || leaf is not ICorDebugILFrame)
        {
            return "it runs outside managed code";
        }
        return null;
    }

    /// <summary>How many of a thread's innermost frames the engine's log names for an evaluation past its time.</summary>
    private const int FramesTold = 12;

    /// <summary>How many of a thread's innermost native frames the engine's log names where it stands outside managed code.</summary>
    private const int NativeFramesTold = 10;

    /// <summary>Where each thread of the held program stands, an evaluation's own first, the runtime's own threads among them,
    /// which hold still with the rest: what an evaluation past its time waits on, if anything, one of the others holds, and
    /// the runtime's frames among theirs (a type's initializer, a JIT compilation) say what. A thread with no name of the
    /// program's is named as the system names it, and on Windows one standing outside managed code by its native frames too,
    /// which say what it waits in (a lock, a sleep, a read) where its managed frames end.</summary>
    private string WhereThreadsStand(ICorDebugThread evaluating)
    {
        int id = evaluating.Id;
        List<ICorDebugThread> threads = [evaluating];
        string? unlisted = null;
        try
        {
            threads.AddRange(evaluating.Process.EnumerateThreads().Where(thread => thread.Id != id));
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            unlisted = $"  The other threads could not be listed: {error.Message}";
        }
        int? processId = evaluating.Process.TryGetID(out uint known) is Cor.S_OK ? (int)known : null;
        List<(int Id, string Told, bool Native)> stood = [.. threads.Select(thread => WhereThreadStands(thread, processId))];
        IReadOnlyDictionary<int, IReadOnlyList<string>> natively = OperatingSystem.IsWindows() && processId is { } program
            ? NativeStacks.Of(program, [.. stood.Where(s => s.Native).Select(s => s.Id)], NativeFramesTold)
            : new Dictionary<int, IReadOnlyList<string>>();
        IEnumerable<string> lines = stood.Select(s => natively.TryGetValue(s.Id, out IReadOnlyList<string>? frames)
            ? $"{s.Told}\n    natively: {string.Join(" < ", frames)}"
            : s.Told);
        return string.Join('\n', lines.Append(unlisted).OfType<string>().Prepend($"The evaluation on thread {id} is past its time, and the threads stand so:"));
    }

    /// <summary>A thread's name, its states and its innermost frames, or why they could not be read, and whether it stands
    /// outside managed code.</summary>
    private (int Id, string Told, bool Native) WhereThreadStands(ICorDebugThread thread, int? processId)
    {
        int id = thread.Id;
        try
        {
            string user = thread.TryGetUserState(out CorDebugUserState state) is Cor.S_OK ? state.ToString() : "user state unread";
            string debug = thread.TryGetDebugState(out CorDebugThreadState held) is Cor.S_OK ? held.ToString() : "debug state unread";
            List<string> frames = [.. thread.EnumerateChains()
                .SelectMany(chain => chain.IsManaged ? chain.EnumerateFrames().Select(FrameTold) : [NativeCode])];
            string name = thread.GetThreadNameOrNull() ?? (_leftRunning.Contains(id) ? "the runtime's tiering worker"
                : processId is { } program && RuntimeThreads.SystemName(program, id) is { Length: > 0 } system ? $"named \"{system}\" by the system" : "no name");
            string start = AtItsFirstInstruction(thread) ? ", at its first instruction" : "";
            return (id, $"  {id} ({name}, {user}, {debug}{start}): {string.Join(" < ", frames.Take(FramesTold))}", frames.FirstOrDefault() is null or NativeCode);
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return (id, $"  {id}: not read: {error.Message}", false);
        }
    }

    /// <summary>What the frames a thread stands in outside managed code are told as.</summary>
    private const string NativeCode = "[Native code]";

    /// <summary>The method an evaluation on a thread runs, the program held: its frame where the evaluation began, named as
    /// the call stack names it; null where no code of its own runs there (the runtime making an object) or the frames cannot
    /// be read.</summary>
    private string? EvaluationRunning(ICorDebugThread thread)
    {
        try
        {
            return EvaluationFrames(thread).LastOrDefault() is { } begun ? FrameTold(begun) : null;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return null;
        }
    }

    private string FrameTold(ICorDebugFrame frame) => frame switch
    {
        ICorDebugILFrame code when FindModule(code.Function.Module) is { } module => GetMethodFormattedName(module, code.Function.Token, code.TypeParameters),
        ICorDebugInternalFrame stub => stub.FrameType.ToDisplayName(),
        _ => UnknownMethod,
    };

    /// <summary>The frames of the evaluation a thread runs, above the one it began from.</summary>
    private static IEnumerable<ICorDebugILFrame> EvaluationFrames(ICorDebugThread thread)
    {
        foreach (ICorDebugChain chain in thread.EnumerateChains())
        {
            if (chain.IsManaged)
            {
                foreach (ICorDebugFrame frame in chain.EnumerateFrames())
                {
                    if (frame is ICorDebugInternalFrame { FrameType: CorDebugInternalFrameType.STUBFRAME_FUNC_EVAL })
                    {
                        yield break;
                    }
                    if (frame is ICorDebugILFrame code)
                    {
                        yield return code;
                    }
                }
            }
            if (chain.Reason is CorDebugChainReason.CHAIN_FUNC_EVAL)
            {
                yield break;
            }
        }
    }

    /// <summary>Aborts an evaluation left running in the program for a request that left its stop once the time evaluations
    /// are given (<see cref="Extensions.EvaluationTimeout"/>) is up, as it would have been had it been waited for: one that
    /// never returns would hold its thread for good. As at a stop, it is looked at through the abort's window
    /// (<see cref="Extensions.AbortWait"/>) and asked only where the abort takes (<see cref="WhyNoAbortTakes"/>); one never
    /// found there runs on to its own end. Nothing once it has ended, or the session has.</summary>
    private async Task AbortWhenDueAsync(int thread, ICorDebugEval eval, TimeSpan after)
    {
        try
        {
            await Task.Delay(after > TimeSpan.Zero ? after : TimeSpan.Zero, _ending.Token).ConfigureAwait(false);
            string? lastWhy = null;
            bool asked = false;
            long until = Stopwatch.GetTimestamp() + Extensions.Ticks(Extensions.AbortWait);
            while (Stopwatch.GetTimestamp() < until)
            {
                using (await DapRequestAndRuntimeEventLock.LockAsync(_ending.Token).ConfigureAwait(false))
                {
                    // Its end may be among the events queued meanwhile, and let go with them.
                    await DrainRuntimeEventQueue().ConfigureAwait(false);
                    if (!EvalStatus.Abandoned.TryGetValue(thread, out ICorDebugEval? running) || running != eval || _process is not { } process)
                    {
                        return;
                    }
                    // A program stopped already (at a stop shown, or an event's) is looked at as it is.
                    bool heldForLook = process.TryIsRunning(out bool isRunning) is Cor.S_OK && isRunning;
                    if (heldForLook && process.TryStop(0) is not Cor.S_OK)
                    {
                        return;
                    }
                    string? why = asked ? null : WhyNoAbortTakes(eval.Thread);
                    if (!asked && why is null)
                    {
                        _logger?.Invoke($"The evaluation left running on thread {thread} has had its {Durations.Said(Extensions.EvaluationTimeout)} and runs managed code, and is aborted");
                        eval.TryAbort();
                        asked = true;
                        until = Stopwatch.GetTimestamp() + Extensions.Ticks(Extensions.AbortWait);
                    }
                    else if (why is not null && why != lastWhy)
                    {
                        _logger?.Invoke($"The evaluation left running on thread {thread} has had its {Durations.Said(Extensions.EvaluationTimeout)}, and is not aborted: {why}");
                        lastWhy = why;
                    }
                    if (heldForLook)
                    {
                        process.TryContinue(false);
                    }
                }
                await Task.Delay(Extensions.LookEvery, _ending.Token).ConfigureAwait(false);
            }
            _logger?.Invoke(asked ? $"The abort asked of the evaluation left running on thread {thread} has not taken; it runs on"
                : $"The evaluation left running on thread {thread} runs on to its own end, unaborted");
        }
        catch (OperationCanceledException)
        {
            // The session ended first, letting the program go.
        }
    }

    /// <summary>Lets every thread run until the evaluation left running on a thread returns, for a person's evaluation with
    /// every thread let go on that thread, within the time an evaluation is given, what the others meet meanwhile passed
    /// over (<see cref="EvalStatus.LetThreadsRun"/>). Its end holds the program where it began, the stop shown, and is
    /// not let go on; one that has not returned by then leaves the program held again. Whether it returned.</summary>
    private async Task<bool> LetAbandonedReturnAsync(ICorDebugThread thread)
    {
        if (_process is not { } process)
        {
            return false;
        }
        _logger?.Invoke($"The evaluation left running on thread {thread.Id} is let return, every thread let go");
        using CancellationTokenSource time = new(Extensions.EvaluationTimeout);
        ChannelReader<CorDebugManagedCallbackEventArgs> reader = _runtimeEventChannel.Reader;
        EvalStatus.ThreadId = thread.Id;
        bool returned = false;
        try
        {
            process.Continue(false);
            while (!returned && await reader.WaitToReadAsync(time.Token))
            {
                if (!reader.TryRead(out CorDebugManagedCallbackEventArgs? callbackEvent))
                {
                    continue;
                }
                if (EvaluationThread(callbackEvent)?.Id == thread.Id && EvalStatus.Abandoned.ContainsKey(thread.Id))
                {
                    ForgetAbandonedEvaluation(callbackEvent);
                    returned = true;
                    continue;
                }
                await OnAnyEvent(this, callbackEvent).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger?.Invoke($"The evaluation left running on thread {thread.Id} had not returned {Durations.Said(Extensions.EvaluationTimeout)} after every thread was let go");
        }
        finally
        {
            EvalStatus.ThreadId = null;
            if (!returned)
            {
                // Held again, as the stop shows it: an event still queued holds it too, until its handler lets it go on.
                int held = process.TryStop(0);
                if (held is not Cor.S_OK)
                {
                    _logger?.Invoke($"The program could not be held again with the evaluation left running on thread {thread.Id}: 0x{held:X8}");
                }
            }
        }
        return returned;
    }
}
