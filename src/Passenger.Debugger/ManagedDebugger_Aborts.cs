using System.Diagnostics;
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
                        _logger?.Invoke($"The evaluation left running on thread {thread} has had its {Extensions.EvaluationTimeout.TotalSeconds:0.#} s and runs managed code, and is aborted");
                        eval.TryAbort();
                        asked = true;
                        until = Stopwatch.GetTimestamp() + Extensions.Ticks(Extensions.AbortWait);
                    }
                    else if (why is not null && why != lastWhy)
                    {
                        _logger?.Invoke($"The evaluation left running on thread {thread} has had its {Extensions.EvaluationTimeout.TotalSeconds:0.#} s, and is not aborted: {why}");
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
}
