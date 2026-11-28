using System.Runtime.InteropServices;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>A recording's returns no local holds: a value returned to code that is not the user's read from the register
/// the call returns it in, and the calls of runtime-async methods, which have no state machine: an await that suspends
/// leaves the method's frame and its callers', and the runtime later calls it again where it goes on, on whichever thread.
/// A recorded call let go of while it may be suspended is set aside, and taken up again by the frame it goes on in, found
/// by its method and its arguments, so its later calls, its completion and a stop in it keep its run.</summary>
public partial class ManagedDebugger
{
    /// <summary>Recorded calls of runtime-async methods let go of before they completed, the newest last.</summary>
    private readonly List<OpenCall> _suspended = [];

    /// <summary>Lets go of a thread's open calls that <paramref name="gone"/> says have left, a runtime-async one set aside.</summary>
    private void LetGo(List<OpenCall> open, Predicate<OpenCall> gone)
    {
        foreach (OpenCall call in open)
        {
            if (call.Resumable && gone(call))
            {
                SetAside(call);
            }
        }
        open.RemoveAll(gone);
    }

    /// <summary>Lets go of a thread's open calls from <paramref name="from"/> on, newer than one that returned.</summary>
    private void LetGoFrom(List<OpenCall> open, int from)
    {
        for (int i = from; i < open.Count; i++)
        {
            if (open[i].Resumable)
            {
                SetAside(open[i]);
            }
        }
        open.RemoveRange(from, open.Count - from);
    }

    /// <summary>Lets go of a thread's open calls at or below a frame, which have returned or been unwound: a callee's frame
    /// lies below its caller's at both ends, a sibling's no higher at one of them.</summary>
    private void LetGoBelow(List<OpenCall> open, StackRange frame) =>
        LetGo(open, call => call.Frame.Start.Value <= frame.Start.Value || call.Frame.End.Value <= frame.End.Value);

    private void SetAside(OpenCall call)
    {
        _suspended.Add(call);
        if (_suspended.Count > MostOpenAsync)
        {
            _suspended.RemoveAt(0);
        }
    }

    /// <summary>The recorded call a runtime-async frame goes on, where none of the thread's open calls is it: the one of its
    /// method set aside or open elsewhere on the stack or another thread called with the arguments the frame holds, else the
    /// only one, taken up as this thread's at the frame's place; null for a frame of no such method, or where several could be it.</summary>
    private OpenCall? Resumed(int threadId, List<OpenCall> open, ICorDebugILFrame frame, StackRange range)
    {
        if (FindModule(frame.Function.Module) is not { } module || !_recordedModules.Contains(module) || !module.MetadataReader.IsRuntimeAsync(frame.Function.Token))
        {
            return null;
        }
        int token = frame.Function.Token;
        List<(OpenCall Call, List<OpenCall> From)> candidates = [.. _suspended.Where(call => call.Module == module && call.Token == token).Select(call => (call, _suspended))];
        foreach (List<OpenCall> others in _openCalls.Values)
        {
            candidates.AddRange(others.Where(call => call.Resumable && call.Module == module && call.Token == token && call.Frame != range).Select(call => (call, others)));
        }
        if (candidates.Count == 0)
        {
            return null;
        }
        string key = ArgumentsKey(frame);
        // Calls whose arguments read alike (objects are told apart by nothing that lasts) are no call of the frame's for sure.
        List<(OpenCall Call, List<OpenCall> From)> same = [.. candidates.Where(each => each.Call.Arguments == key)];
        if ((same.Count == 1 ? same[0] : same.Count == 0 && candidates.Count == 1 ? candidates[0] : default) is not { Call: not null } chosen)
        {
            _logger?.Invoke($"Recording: {candidates.Count} calls of {candidates[0].Call.Label} could be the one going on on thread {threadId}, so none is taken for it");
            return null;
        }
        (OpenCall taken, List<OpenCall> from) = chosen;
        from.Remove(taken);
        StackRange? callerRange = frame.TryGetCaller(out ICorDebugFrame? up) is Cor.S_OK && up is not null ? RangeOf(up) : null;
        OpenCall resumed = taken with { Frame = range, CallerFrame = callerRange };
        open.Add(resumed);
        _logger?.Invoke($"Recording: {taken.Label} of moment {taken.Moment} goes on on thread {threadId}{(taken.Arguments == key ? "" : ", its arguments since changed")}");
        return resumed;
    }

    /// <summary>What a frame's arguments hold, to tell its method's calls apart: a primitive's bytes, whether a reference
    /// is null (where an object lies moves as the heap is compacted).</summary>
    private static string ArgumentsKey(ICorDebugILFrame frame)
    {
        try
        {
            return string.Join(",", frame.Arguments.Select(argument => argument switch
            {
                ICorDebugGenericValue primitive => Convert.ToHexString(primitive.GetValueAsBytes()),
                ICorDebugReferenceValue reference => reference.TryGetValue(out CORDB_ADDRESS at) is Cor.S_OK && at.Value == 0 ? "null" : "object",
                _ => "?",
            }));
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return error.Message;
        }
    }

    /// <summary>The value a call returned to a site whose code keeps none, read from the register it returns it in as the
    /// caller's frame stands at the return address: for a method returning what a register holds whole, on a processor whose
    /// registers ICorDebug names here; null otherwise, and through the relay, whose program's processor may be another.</summary>
    private unsafe ICorDebugValue? ReturnedInRegister(ICorDebugILFrame frame, OpenCall call)
    {
        if (_throughRelay || frame is not ICorDebugNativeFrame native || call.Module.MetadataReader.RegisterReturn(call.Token) is not byte element)
        {
            return null;
        }
        bool real = element is 0x0C or 0x0D;
        CorDebugRegister? register = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => real ? CorDebugRegister.REGISTER_AMD64_XMM0 : CorDebugRegister.REGISTER_AMD64_RAX,
            Architecture.Arm64 => real ? CorDebugRegister.REGISTER_ARM64_V0 : CorDebugRegister.REGISTER_ARM64_X0,
            _ => null,
        };
        if (register is not { } held)
        {
            return null;
        }
        byte signature = element;
        return native.TryGetLocalRegisterValue(held, 1, (nuint)(&signature), out ICorDebugValue value) is Cor.S_OK ? value : null;
    }
}
