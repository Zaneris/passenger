using System.Diagnostics;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;

namespace Passenger.Debugger;

/// <summary>A recording's async methods: the user's code of one runs in its state machine's MoveNext, called again after
/// each await that waits, so a call is kept where that code begins (its opening brace, which only the first run passes), its
/// parameters read from the state machine's fields, and where it completes (its closing brace), its value read from the
/// local the builder is given it from. The state machine, held by a weak handle, says which call a later run is of, on
/// whatever thread it goes on, and the run a call of it is part of goes on with it (<see cref="KeptMoment.Flow"/>).</summary>
public partial class ManagedDebugger
{
    /// <summary>How many async calls not yet completed a recording follows, the oldest let go past them.</summary>
    internal const int MostOpenAsync = 1_000;

    /// <summary>A breakpoint of an async method's MoveNext: where a call's code begins, or where it completes.</summary>
    private sealed record AsyncPoint(ModuleInfo Module, ModuleMetadataReader.AsyncMethod Method, bool Begins);

    private readonly Dictionary<ICorDebugFunctionBreakpoint, AsyncPoint> _asyncBreakpoints = new(ReferenceEqualityComparer.Instance);

    /// <summary>The async methods recorded, by their module and their MoveNext.</summary>
    private readonly Dictionary<(ModuleInfo Module, int MoveNext), ModuleMetadataReader.AsyncMethod> _asyncByMoveNext = [];

    /// <summary>An async call begun and not yet completed: its moment, its MoveNext, its state machine (none for one that is
    /// a struct, which moves into a box at its first await), the frame it was called from and the recorded call that is,
    /// its run, its label, and how many recorded calls it began in.</summary>
    private sealed record PendingAsync(int Moment, ModuleInfo Module, int MoveNext, ICorDebugHandleValue? Machine, IReadOnlyList<StackFrameInfo> Callers, int? Parent, int Flow,
        string Label, int Depth);

    private readonly List<PendingAsync> _openAsync = [];

    /// <summary>Breakpoints where each async method of a module begins and completes; how many methods took them.</summary>
    private int BindAsyncRecording(ModuleInfo module)
    {
        int bound = 0;
        foreach (ModuleMetadataReader.AsyncMethod method in module.MetadataReader.RecordedAsyncMethods())
        {
            try
            {
                ICorDebugCode code = module.Module.GetFunctionFromToken(method.MoveNext).ILCode;
                foreach ((int offset, bool begins) in new[] { (method.Begins, true), (method.Ends, false) })
                {
                    ICorDebugFunctionBreakpoint breakpoint = code.CreateBreakpoint(offset);
                    breakpoint.Activate(true);
                    _asyncBreakpoints[breakpoint] = new AsyncPoint(module, method, begins);
                }
                _asyncByMoveNext[(module, method.MoveNext)] = method;
                bound++;
            }
            catch (Exception error) when (Failures.OfRuntime(error))
            {
                _logger?.Invoke($"Not recorded: async method 0x{method.Kickoff:X} of {module.ModuleName}: {error.Message}");
            }
        }
        return bound;
    }

    /// <summary>The recorded call a frame runs in and its run: the first frame above it that is one of the thread's open calls
    /// or runs an open async call's code, whatever code lies between; none, the run the thread's own, where there is none.</summary>
    private (int? Parent, int Flow, ICorDebugILFrame? Frame) ParentOf(int threadId, List<OpenCall> open, ICorDebugILFrame frame)
    {
        ICorDebugFrame? up = frame;
        for (int hop = 0; hop < 32 && up is not null; hop++)
        {
            up = up.TryGetCaller(out ICorDebugFrame? next) is Cor.S_OK ? next : null;
            if (up is not ICorDebugILFrame code)
            {
                continue;
            }
            if (RangeOf(code) is { } range && open.FindLast(call => call.Frame == range) is { } inside)
            {
                return (inside.Moment, inside.Flow, code);
            }
            if (AsyncCallRunIn(code) is { } running)
            {
                return (running.Moment, running.Flow, code);
            }
        }
        return (null, threadId, null);
    }

    /// <summary>The open async call whose code a frame runs: one of its MoveNext, its state machine the call's.</summary>
    private PendingAsync? AsyncCallRunIn(ICorDebugILFrame frame)
    {
        if (FindModule(frame.Function.Module) is not { } module || !_asyncByMoveNext.ContainsKey((module, frame.Function.Token)))
        {
            return null;
        }
        int token = frame.Function.Token;
        ulong? machine = MachineAt(frame);
        for (int i = _openAsync.Count - 1; i >= 0; i--)
        {
            PendingAsync call = _openAsync[i];
            if (call.Module != module || call.MoveNext != token)
            {
                continue;
            }
            // A struct's state machine is told apart by nothing that lasts: the newest call of the method is taken.
            if (call.Machine is null || machine is null
                || call.Machine.TryGetValue(out CORDB_ADDRESS now) is Cor.S_OK && now.Value == machine)
            {
                return call;
            }
        }
        return null;
    }

    /// <summary>Where the state machine a MoveNext frame runs lies now, for one that is an object; null for a struct's.</summary>
    private static ulong? MachineAt(ICorDebugILFrame frame) =>
        frame.Arguments is [ICorDebugReferenceValue machine, ..] && machine.Type == CorElementType.CLASS
        && machine.TryGetValue(out CORDB_ADDRESS at) is Cor.S_OK && at.Value != 0
            ? at.Value
            : null;

    /// <summary>A weak handle to the state machine a MoveNext frame runs, where it is an object; none for a struct's.</summary>
    private ICorDebugHandleValue? MachineHandle(ICorDebugILFrame frame)
    {
        try
        {
            return MachineAt(frame) is not null && frame.Arguments[0] is ICorDebugReferenceValue machine && machine.Dereference() is ICorDebugHeapValue2 held
                ? held.CreateHandle(CorDebugHandleType.HANDLE_WEAK_TRACK_RESURRECTION)
                : null;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"No handle to a state machine: {error.Message}");
            return null;
        }
    }

    /// <summary>The frame of the user's code an async call was made from: the first above its MoveNext that is the user's,
    /// passing over the builder's Start and the method the user wrote, which only starts its state machine.</summary>
    private ICorDebugILFrame? UserCallerOf(ICorDebugILFrame frame, ModuleMetadataReader.AsyncMethod method)
    {
        ICorDebugFrame? up = frame;
        for (int hop = 0; hop < 8 && up is not null; hop++)
        {
            up = up.TryGetCaller(out ICorDebugFrame? next) is Cor.S_OK ? next : null;
            // A method of no line of its own (a top-level program's entry stub) is the compiler's.
            if (up is ICorDebugILFrame caller && FindModule(caller.Function.Module) is { IsUserCode: true } module && caller.Function.Token != method.Kickoff
                && module.MetadataReader.VisibleSequencePoints(caller.Function.Token).Count > 0)
            {
                return caller;
            }
        }
        return null;
    }

    /// <summary>An async call begun: its parameters read from its state machine, which a weak handle keeps for its later
    /// runs, and it kept as the thread's newest open call until its first await.</summary>
    private async Task RecordAsyncCallAsync(ICorDebugThread thread, AsyncPoint point, long came)
    {
        if (TopFrame(thread) is not ICorDebugILFrame frame || RangeOf(frame) is not { } range)
        {
            return;
        }
        List<OpenCall> open = OpenOn(thread.Id);
        LetGoBelow(open, range);
        ICorDebugILFrame? caller = UserCallerOf(frame, point.Method);
        StackRange? callerRange = caller is null ? null : RangeOf(caller);
        (int? parent, int flow, ICorDebugILFrame? parentFrame) = ParentOf(thread.Id, open, frame);
        ModuleInfo module = point.Module;
        StackFrameInfo place = DescribeFrame(0, frame);
        IReadOnlyList<StackFrameInfo> callers = CallersTo(caller is null ? null : DescribeFrame(0, caller), callerRange, parentFrame);
        string label = LabelOf(module.MetadataReader.DescribeCall(point.Method.Kickoff));

        Capture capture = NewRecordingCapture(null);
        List<VariableInfo> rows = [];
        int topReference = await ReadRecordedAsync(capture, rows, async () =>
        {
            // The state machine's fields hold the parameters, beside the locals it hoists, which are not yet set.
            List<VariableInfo> held = [];
            ThreadId threadId = new(thread.Id);
            ICorDebugValue? hoisted = await AddArguments(module, frame.Function, held, threadId, new FrameStackDepth(0));
            await AddLocalVariables(module, frame.Function, held, threadId, new FrameStackDepth(0), hoisted);
            rows.AddRange(held.Where(row => row.Name == "this" || point.Method.Parameters.Contains(row.Name)));
        });
        IReadOnlyList<VariableInfo> top = capture.Values[topReference];
        int index = _momentsRecorded + 1;
        string args = string.Join(", ", point.Method.Parameters.Select(name => top.FirstOrDefault(row => row.Name == name)?.Value ?? "?").Select(Cut));
        string said = label.EndsWith("()", StringComparison.Ordinal) ? $"{label[..^2]}({args})" : label;
        int depth = DepthUnder(parent, open.Count);
        Keep(new KeptMoment(new RecordedMoment(index, MomentKind.Call, thread.Id, DateTimeOffset.Now, place, depth, said), ThreadNameOf(thread), callers, parent,
            capture.Values, topReference, top, Stopwatch.GetElapsedTime(came), BytesOf(capture.Values, 0), capture.Said, flow));
        open.Add(new OpenCall(index, range, callerRange, null, place, module, frame.Function.Token, label, callers, parent, flow));
        _openAsync.Add(new PendingAsync(index, module, point.Method.MoveNext, MachineHandle(frame), callers, parent, flow, label, depth));
        if (_openAsync.Count > MostOpenAsync)
        {
            _openAsync[0].Machine?.TryDispose();
            _openAsync.RemoveAt(0);
        }
    }

    /// <summary>An async call completing at its closing brace, on whatever thread it went on on: the call its state machine
    /// began found, the value it completes with read from the local the builder is given it from, and the moment kept beside
    /// its parameters as they were at its call, in the call's run.</summary>
    private async Task RecordAsyncReturnAsync(ICorDebugThread thread, AsyncPoint point, long came)
    {
        if (TopFrame(thread) is not ICorDebugILFrame frame)
        {
            return;
        }
        PendingAsync? call = AsyncCallRunIn(frame);
        if (call is not null)
        {
            _openAsync.Remove(call);
            call.Machine?.TryDispose();
        }
        string label = call?.Label ?? LabelOf(point.Module.MetadataReader.DescribeCall(point.Method.Kickoff));
        KeptMoment? called = call is null ? null : Find(call.Moment);
        Capture capture = NewRecordingCapture(called?.Values);
        List<VariableInfo> rows = [];
        string name = ReturnedRow(label);
        int returnedReference = point.Method.Result is int slot && frame.TryGetLocalVariable(checked((uint)slot), out ICorDebugValue? value) is Cor.S_OK && value is not null
            ? await ReadRecordedAsync(capture, rows, () => AddValueRow(rows, name, null, value, new ThreadId(thread.Id), new FrameStackDepth(0)))
            : capture.Add([]);
        IReadOnlyList<VariableInfo> returned = capture.Values[returnedReference];
        IReadOnlyList<VariableInfo> top = [.. returned, .. called?.TopRows ?? []];
        int topReference = capture.Add(top);
        string said = returned.Count > 0 ? $"{name} {Cut(returned[0].Value)}" : name;
        Keep(new KeptMoment(new RecordedMoment(_momentsRecorded + 1, MomentKind.Return, thread.Id, DateTimeOffset.Now, DescribeFrame(0, frame), call?.Depth ?? 0, said),
            ThreadNameOf(thread), call?.Callers ?? [], call?.Parent, capture.Values, topReference, top, Stopwatch.GetElapsedTime(came), BytesOf(capture.Values, called?.Values.Count ?? 0),
            capture.Said, call?.Flow ?? thread.Id));
    }

    /// <summary>The run a stopped thread is in: that of the newest recorded call its frames are in (a call open on it, or an
    /// async call whose code it runs), else its own.</summary>
    private int FlowOf(int threadId)
    {
        if (!_records && _openCalls.Count == 0 || _threads.GetValueOrDefault(threadId)?.Thread is not { } thread)
        {
            return threadId;
        }
        List<OpenCall> open = OpenOn(threadId);
        try
        {
            foreach (ICorDebugFrame frame in EnumerateFramesForThread(thread).Take(256))
            {
                if (frame is not ICorDebugILFrame code)
                {
                    continue;
                }
                if (RangeOf(code) is { } range && open.FindLast(call => call.Frame == range) is { } inside)
                {
                    return inside.Flow;
                }
                if (AsyncCallRunIn(code) is { } running)
                {
                    return running.Flow;
                }
            }
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"The run of thread {threadId} is not told: {error.Message}");
        }
        return threadId;
    }
}
