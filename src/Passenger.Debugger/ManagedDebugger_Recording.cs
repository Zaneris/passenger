using System.Diagnostics;
using Passenger.Dap;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;

namespace Passenger.Debugger;

/// <summary>Step back debugging: while a session records, each method of the user's code that its symbols place (none the
/// compiler wrote, none its code says is not the user's) has a breakpoint at its start, where a call's arguments are read,
/// and each call site of the user's code it is called from one at the call's return address, placed as the call is first
/// met, where ICorDebug reads the value it returned. Each read runs none of the program's code, as a snapshot's does, and
/// is copied into plain rows before the thread goes on. What it keeps is bounded (moments, and the size of their values),
/// the oldest let go first; a moment is shown as a past stop, its method's frame above the recorded calls it ran in.</summary>
public partial class ManagedDebugger
{
    /// <summary>How many moments a recording keeps, the oldest let go past them.</summary>
    internal const int MostMoments = 10_000;

    /// <summary>How much of its values' text a recording keeps, in bytes, the oldest moments let go past it.</summary>
    internal const long MostRecordedBytes = 32L * 1024 * 1024;

    /// <summary>How many levels of members below each argument and returned value a moment reads.</summary>
    internal const int RecordedLevels = 1;

    /// <summary>How many values a moment reads in all.</summary>
    internal const int RecordedValues = 200;

    /// <summary>Why a value a recording would run code to read is not read: a getter, a ToString, a debugger view.</summary>
    internal const string RecordingRunsNoCode = "Not read: a recording runs none of the program's code";

    /// <summary>How long a moment's words give each value, the rest cut.</summary>
    private const int SaidValueLength = 40;

    private bool _records;

    /// <summary>The breakpoints at the start of each method recorded, each with its module.</summary>
    private readonly Dictionary<ICorDebugFunctionBreakpoint, ModuleInfo> _callBreakpoints = new(ReferenceEqualityComparer.Instance);

    private readonly HashSet<ModuleInfo> _recordedModules = new(ReferenceEqualityComparer.Instance);

    /// <summary>The breakpoints at the return addresses of calls recorded, each with the site it is at.</summary>
    private readonly Dictionary<ICorDebugFunctionBreakpoint, ReturnSite> _returnBreakpoints = new(ReferenceEqualityComparer.Instance);

    /// <summary>Each return address of the user's code a recorded call was made from, by its code's start and its offset in it.</summary>
    private readonly Dictionary<(ulong Code, uint Offset), ReturnSite> _returnSites = [];

    /// <summary>What a recording says of each method it records, read once.</summary>
    private readonly Dictionary<(ModuleInfo Module, int Token), RecordedMethod> _recordedMethods = [];

    /// <summary>The recorded calls each thread is inside, the newest last.</summary>
    private readonly Dictionary<int, List<OpenCall>> _openCalls = [];

    private readonly List<KeptMoment> _moments = [];
    private int _momentsDropped;
    private int _momentsRecorded;
    private long _momentBytes;
    private TimeSpan _recordingHeld;

    /// <summary>A call site's return address: its key, its frame where the call is made (none where its name is an
    /// instantiation's, which another may share the code of), the IL offset of the call whose value is read there and its
    /// breakpoint, both none for a call returning no value, or one the runtime keeps none of.</summary>
    private sealed record ReturnSite((ulong Code, uint Offset) Key, StackFrameInfo? Place, int? CallOffset, ICorDebugFunctionBreakpoint? Breakpoint);

    /// <summary>A method a recording records: its label, whether it returns a value, and its frame at its start (none for a
    /// generic one, whose frames name each instantiation).</summary>
    private sealed record RecordedMethod(string Label, bool ReturnsValue, StackFrameInfo? Place);

    /// <summary>A recorded call not yet returned: its moment, where its frame and its caller's lie on the stack, the return
    /// address it goes back to, its method's frame where it began and its label, the frame it was called from, the
    /// recorded call that frame is, the run it is part of (<see cref="KeptMoment.Flow"/>), whether its return is read
    /// at its own closing brace, as for a call code that is not the user's made, and for a runtime-async method's, which
    /// may go on in another frame, what its arguments held (<see cref="ArgumentsKey"/>).</summary>
    private sealed record OpenCall(int Moment, StackRange Frame, StackRange? CallerFrame, (ulong Code, uint Offset)? Return, StackFrameInfo Place, ModuleInfo Module,
        int Token, string Label, IReadOnlyList<StackFrameInfo> Callers, int? Parent, int Flow, bool AtItsEnd = false, bool Resumable = false, string? Arguments = null);

    /// <summary>A moment kept: what the timeline lists, the frame it was called from and the recorded call that is, the call it
    /// returns from, its values by references of its own, its top frame's rows, how long reading it held the program, the
    /// size of its own values, what it left out, and its run: the thread a run of calls began on, which an async call's
    /// moments keep whichever thread its code goes on on after an await.</summary>
    private sealed record KeptMoment(RecordedMoment Moment, string? ThreadName, IReadOnlyList<StackFrameInfo> Callers, int? Parent, IReadOnlyDictionary<int, IReadOnlyList<VariableInfo>> Values,
        int Top, IReadOnlyList<VariableInfo> TopRows, TimeSpan Held, long Bytes, string? Cut, int Flow);

    /// <summary>Starts or stops recording: the user's modules loaded so far take their breakpoints, and each loading after
    /// takes its own; stopped, every breakpoint of it goes, its moments kept.</summary>
    public void Record(bool on)
    {
        if (_records == on)
        {
            return;
        }
        _records = on;
        if (on)
        {
            foreach (ModuleInfo module in _modules.Values.ToList())
            {
                BindRecording(module);
            }
            return;
        }
        foreach (ICorDebugFunctionBreakpoint breakpoint in _callBreakpoints.Keys.Concat(_returnBreakpoints.Keys))
        {
            breakpoint.TryActivate(false);
        }
        foreach (ICorDebugFunctionBreakpoint breakpoint in _asyncBreakpoints.Keys.Concat(_endBreakpoints.Keys))
        {
            breakpoint.TryActivate(false);
        }
        _endBreakpoints.Clear();
        _endsWatched.Clear();
        foreach (PendingAsync call in _openAsync)
        {
            call.Machine?.TryDispose();
        }
        _callBreakpoints.Clear();
        _returnBreakpoints.Clear();
        _returnSites.Clear();
        _recordedMethods.Clear();
        _asyncBreakpoints.Clear();
        _asyncByMoveNext.Clear();
        _openAsync.Clear();
        _recordedModules.Clear();
        _openCalls.Clear();
        _suspended.Clear();
        _logger?.Invoke($"Recording stopped, {Core.Numbers.Said(_moments.Count)} moments kept");
    }

    /// <summary>A breakpoint at the start of each method of a module of the user's code that a recording records.</summary>
    private void BindRecording(ModuleInfo module)
    {
        if (!_records || !module.IsUserCode || !module.MetadataReader.HasSymbols || !_recordedModules.Add(module))
        {
            return;
        }
        int bound = 0;
        foreach (int token in module.MetadataReader.RecordedMethodTokens())
        {
            try
            {
                ICorDebugFunctionBreakpoint breakpoint = module.Module.GetFunctionFromToken(token).ILCode.CreateBreakpoint(0);
                breakpoint.Activate(true);
                _callBreakpoints[breakpoint] = module;
                bound++;
            }
            catch (Exception error) when (Failures.OfRuntime(error))
            {
                _logger?.Invoke($"Not recorded: method 0x{token:X} of {module.ModuleName}: {error.Message}");
            }
        }
        bound += BindAsyncRecording(module);
        _logger?.Invoke($"Recording the calls of {Core.Numbers.Said(bound)} methods of {module.ModuleName}");
    }

    /// <summary>Takes a hit of a recording's own breakpoint, held since <paramref name="came"/>; false for any other.</summary>
    private async Task<bool> TakeRecordedAsync(ICorDebugThread thread, ICorDebugFunctionBreakpoint breakpoint, long came)
    {
        if (_callBreakpoints.TryGetValue(breakpoint, out ModuleInfo? module))
        {
            await RecordCallAsync(thread, module, came);
            return true;
        }
        if (_returnBreakpoints.TryGetValue(breakpoint, out ReturnSite? site))
        {
            await RecordReturnAsync(thread, site, came);
            return true;
        }
        if (_asyncBreakpoints.TryGetValue(breakpoint, out AsyncPoint? point))
        {
            await (point.Kind switch
            {
                AsyncPointKind.Begins => RecordAsyncCallAsync(thread, point, came),
                AsyncPointKind.Yields => RecordAsyncYieldAsync(thread, point, came),
                _ => RecordAsyncReturnAsync(thread, point, came),
            });
            return true;
        }
        if (_endBreakpoints.TryGetValue(breakpoint, out (ModuleInfo Module, int Token, int Local) end))
        {
            await RecordEndAsync(thread, end.Local, came);
            return true;
        }
        return false;
    }

    /// <summary>The program goes on from a recording's hit, a step under way with it: what the read left in the variable
    /// store let go, and a pause asked for meanwhile taken here rather than lost.</summary>
    private void GoOnFromRecorded()
    {
        _variableManager.ClearAndDisposeHandleValues();
        _frameReferenceManager.Clear();
        Continue(clearVariables: false);
    }

    /// <summary>A call entered: its arguments read, the return address it goes back to watched where it returns a value, and
    /// it kept as the thread's newest open call.</summary>
    private async Task RecordCallAsync(ICorDebugThread thread, ModuleInfo module, long came)
    {
        if (TopFrame(thread) is not ICorDebugILFrame frame || RangeOf(frame) is not { } range)
        {
            return;
        }
        List<OpenCall> open = OpenOn(thread.Id);
        LetGoBelow(open, range);
        ICorDebugILFrame? caller = frame.TryGetCaller(out ICorDebugFrame? up) is Cor.S_OK ? up as ICorDebugILFrame : null;
        StackRange? callerRange = caller is null ? null : RangeOf(caller);
        (int? parent, int flow, ICorDebugILFrame? parentFrame) = ParentOf(thread.Id, open, frame);
        int token = frame.Function.Token;
        RecordedMethod method = MethodRecorded(module, frame);
        // Code not the user's keeps no value where it is returned to, and an await that suspends leaves a caller: read at the end.
        bool fromUsers = caller is not null && FindModule(caller.Function.Module) is { IsUserCode: true };
        bool resumable = module.MetadataReader.IsRuntimeAsync(token);
        bool atItsEnd = resumable ? WatchEnd(module, token) : method.ReturnsValue && !fromUsers && WatchEnd(module, token);
        ReturnSite? site = caller is null ? null : ReturnSiteOf(caller, method.ReturnsValue && !atItsEnd && !resumable);
        StackFrameInfo place = method.Place ?? DescribeFrame(0, frame);
        StackFrameInfo? callerPlace = caller is null ? null : site?.Place ?? DescribeFrame(0, caller);
        IReadOnlyList<StackFrameInfo> callers = CallersTo(callerPlace, callerRange, parentFrame);

        Capture capture = NewRecordingCapture(null);
        List<VariableInfo> rows = [];
        int topReference = await ReadRecordedAsync(capture, rows, () => AddArguments(module, frame.Function, rows, new ThreadId(thread.Id), new FrameStackDepth(0)));
        IReadOnlyList<VariableInfo> top = capture.Values[topReference];
        int index = _momentsRecorded + 1;
        string args = string.Join(", ", top.Where(row => row.Name != "this").Select(row => Cut(row.Value)));
        string said = method.Label.EndsWith("()", StringComparison.Ordinal) ? $"{method.Label[..^2]}({args})" : method.Label;
        Keep(new KeptMoment(new RecordedMoment(index, MomentKind.Call, thread.Id, DateTimeOffset.Now, place, DepthUnder(parent, open.Count), said), ThreadNameOf(thread), callers, parent,
            capture.Values, topReference, top, Stopwatch.GetElapsedTime(came), BytesOf(capture.Values, 0), capture.Said, flow));
        open.Add(new OpenCall(index, range, callerRange, atItsEnd || site?.Breakpoint is null ? null : site.Key, place, module, token, method.Label, callers, parent, flow, atItsEnd,
            resumable, resumable ? ArgumentsKey(frame) : null));
        if (open.Count > MostOpenCalls)
        {
            open.RemoveAt(0);
        }
    }

    /// <summary>How many recorded calls a moment ran in: one more than the call it ran in, where that is still kept.</summary>
    private int DepthUnder(int? parent, int otherwise) => parent is int above && Find(above) is { } up ? up.Moment.Depth + 1 : otherwise;

    /// <summary>How many recorded calls a thread is followed inside, the outermost let go past them.</summary>
    private const int MostOpenCalls = 10_000;

    /// <summary>What a recording says of a method, read at its first call.</summary>
    private RecordedMethod MethodRecorded(ModuleInfo module, ICorDebugILFrame frame)
    {
        int token = frame.Function.Token;
        if (!_recordedMethods.TryGetValue((module, token), out RecordedMethod? method))
        {
            ModuleMetadataReader.CalledMethod? described = module.MetadataReader.DescribeCall(token);
            StackFrameInfo place = DescribeFrame(0, frame);
            if (place.Source is null && module.MetadataReader.VisibleSequencePoints(token) is [{ Document: { } document } first, ..])
            {
                // Begun in code of no line (a record's constructor setting its fields): placed at its first line.
                (place.Source, place.Line, place.Column, place.EndLine, place.EndColumn) = (document, first.StartLine, first.StartColumn, first.EndLine, first.EndColumn);
            }
            method = new RecordedMethod(LabelOf(described), described is { ReturnType: not "void", Name: not (".ctor" or ".cctor") },
                place.Name.Contains('<', StringComparison.Ordinal) ? null : place);
            _recordedMethods[(module, token)] = method;
        }
        return method;
    }

    /// <summary>A call returned to a watched return address: the thread's open call that went out from it found, the value the
    /// call returned read where the runtime keeps one, and the moment kept beside its arguments as they were at the call.</summary>
    private async Task RecordReturnAsync(ICorDebugThread thread, ReturnSite site, long came)
    {
        if (TopFrame(thread) is not ICorDebugILFrame frame || RangeOf(frame) is not { } range)
        {
            return;
        }
        List<OpenCall> open = OpenOn(thread.Id);
        int at = open.FindLastIndex(call => call.CallerFrame == range && call.Return == site.Key);
        if (at < 0)
        {
            // A call recorded none of returned here: what lies below this frame has gone.
            LetGo(open, call => call.Frame.Start.Value < range.Start.Value);
            return;
        }
        OpenCall call = open[at];
        LetGoFrom(open, at + 1);
        open.RemoveAt(at);
        await KeepReturnAsync(thread, call, site.CallOffset is int offset ? Read(frame, offset) : ReturnedInRegister(frame, call), EndOf(call), at, came);
    }

    /// <summary>A call's return kept: the value it returned (none where none was read), beside its arguments as they were at
    /// its call, at <paramref name="place"/>, in the call's run.</summary>
    private async Task KeepReturnAsync(ICorDebugThread thread, OpenCall call, ICorDebugValue? value, StackFrameInfo place, int depth, long came)
    {
        KeptMoment? called = Find(call.Moment);
        Capture capture = NewRecordingCapture(called?.Values);
        List<VariableInfo> rows = [];
        string name = ReturnedRow(call.Label);
        int returnedReference = value is not null
            ? await ReadRecordedAsync(capture, rows, () => AddValueRow(rows, name, null, value, new ThreadId(thread.Id), new FrameStackDepth(0)))
            : capture.Add([]);
        IReadOnlyList<VariableInfo> returned = capture.Values[returnedReference];
        IReadOnlyList<VariableInfo> top = [.. returned, .. called?.TopRows ?? []];
        int topReference = capture.Add(top);
        string said = returned.Count > 0 ? $"{name} {Cut(returned[0].Value)}" : name;
        Keep(new KeptMoment(new RecordedMoment(_momentsRecorded + 1, MomentKind.Return, thread.Id, DateTimeOffset.Now, place, called?.Moment.Depth ?? depth, said), ThreadNameOf(thread), call.Callers,
            call.Parent, capture.Values, topReference, top, Stopwatch.GetElapsedTime(came), BytesOf(capture.Values, called?.Values.Count ?? 0), capture.Said, call.Flow));
    }

    /// <summary>The breakpoints at the closing braces of methods whose calls code that is not the user's made, and of
    /// runtime-async methods, each with the method and the local its value is in there, -1 for one returning none.</summary>
    private readonly Dictionary<ICorDebugFunctionBreakpoint, (ModuleInfo Module, int Token, int Local)> _endBreakpoints = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether each method's closing brace is watched, by its module and token.</summary>
    private readonly Dictionary<(ModuleInfo Module, int Token), bool> _endsWatched = [];

    /// <summary>Watches where a method returns what a local holds, as a debug build returns at its closing brace, from its
    /// first call by code that is not the user's, or of a runtime-async method, whose closing brace is watched too where it
    /// completes a task of no value; false for one that returns otherwise (an expression body's).</summary>
    private bool WatchEnd(ModuleInfo module, int token)
    {
        if (_endsWatched.TryGetValue((module, token), out bool watched))
        {
            return watched;
        }
        watched = false;
        try
        {
            ICorDebugCode code = module.Module.GetFunctionFromToken(token).ILCode;
            IReadOnlyList<(int Offset, int Local)> ends = module.MetadataReader.ReturnsOfLocals(token);
            if (ends.Count == 0 && module.MetadataReader.IsRuntimeAsync(token) && module.MetadataReader.DescribeCall(token) is { ReturnType: "Task" or "ValueTask" }
                && module.MetadataReader.ClosingReturn(token) is int closing)
            {
                ends = [(closing, -1)];
            }
            foreach ((int offset, int local) in ends)
            {
                ICorDebugFunctionBreakpoint breakpoint = code.CreateBreakpoint(offset);
                breakpoint.Activate(true);
                _endBreakpoints[breakpoint] = (module, token, local);
                watched = true;
            }
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"No end watched of method 0x{token:X} of {module.ModuleName}: {error.Message}");
        }
        _endsWatched[(module, token)] = watched;
        return watched;
    }

    /// <summary>A call reaching its closing brace: kept as returning, its value read from the local it returns, where it is
    /// one whose return is read there; any other call of the method passes on.</summary>
    private async Task RecordEndAsync(ICorDebugThread thread, int local, long came)
    {
        if (TopFrame(thread) is not ICorDebugILFrame frame || RangeOf(frame) is not { } range)
        {
            return;
        }
        List<OpenCall> open = OpenOn(thread.Id);
        int at = open.FindLastIndex(call => call.Frame == range);
        if (at < 0 && Resumed(thread.Id, open, frame, range) is not null)
        {
            at = open.Count - 1;
        }
        if (at < 0 || !open[at].AtItsEnd)
        {
            return;
        }
        OpenCall call = open[at];
        LetGoFrom(open, at + 1);
        open.RemoveAt(at);
        ICorDebugValue? value = local >= 0 ? TryLocalOf(frame, local) : null;
        await KeepReturnAsync(thread, call, value, DescribeFrame(0, frame), at, came);
    }

    /// <summary>A recording's capture, its words its own, after <paramref name="seed"/>'s rows where it has some.</summary>
    private static Capture NewRecordingCapture(IReadOnlyDictionary<int, IReadOnlyList<VariableInfo>>? seed) =>
        new(RecordingRunsNoCode, $"Not read: a recording reads {RecordedLevels} level of members below each value", RecordedValues,
            $"Not read: a recording reads {Core.Numbers.Said(RecordedValues)} values at each moment", seed);

    /// <summary>Rows <paramref name="read"/> adds to <paramref name="rows"/>, read running none of the program's code, then
    /// copied with their members into the capture; the copies' reference, an empty one's where the read failed.</summary>
    private async Task<int> ReadRecordedAsync(Capture capture, List<VariableInfo> rows, Func<Task> read)
    {
        string? wasOff = EvalStatus.ImplicitOff;
        EvalStatus.ImplicitOff = RecordingRunsNoCode;
        Queue<Opening> opening = new();
        try
        {
            await read();
            capture.Count += rows.Count;
            int top = capture.Add([.. rows.Where(Kept).Select(row => Copied(row, RecordedLevels, capture, opening))]);
            while (opening.TryDequeue(out Opening open))
            {
                await OpenAsync(open, capture, opening);
            }
            return top;
        }
        catch (Exception error) when (error is EvalException or ArgumentException or InvalidOperationException || Failures.OfRuntime(error))
        {
            _logger?.Invoke($"A recorded moment was cut short: {error}");
            capture.Cut($"what came after a read that failed ({error.Message})");
            return capture.Add([]);
        }
        finally
        {
            EvalStatus.ImplicitOff = wasOff;
        }
    }

    /// <summary>The return address a frame goes back to, its place read once, watched by a breakpoint of the recording's from
    /// the first call through it whose value is read there; null where it has none.</summary>
    private ReturnSite? ReturnSiteOf(ICorDebugILFrame caller, bool returnsValue)
    {
        if (FindModule(caller.Function.Module) is not { } module || caller is not ICorDebugNativeFrame native
            || native.TryGetIP(out uint ip) is not Cor.S_OK || native.TryGetCode(out ICorDebugCode? code) is not Cor.S_OK || code is null
            || code.TryGetAddress(out CORDB_ADDRESS start) is not Cor.S_OK)
        {
            return null;
        }
        (ulong, uint) key = (start.Value, ip);
        // A site of code that is not the user's may call through a delegate one method whose end is watched and another not.
        if (_returnSites.TryGetValue(key, out ReturnSite? known) && (known.Breakpoint is not null || !returnsValue))
        {
            return known;
        }
        StackFrameInfo place = known?.Place ?? DescribeFrame(0, caller);
        ReturnSite site = known ?? new(key, place.Name.Contains('<', StringComparison.Ordinal) ? null : place, null, null);
        if (returnsValue)
        {
            try
            {
                ICorDebugFunctionBreakpoint breakpoint = code.CreateBreakpoint(checked((int)ip));
                breakpoint.Activate(true);
                site = site with { CallOffset = CallReturningTo(module, caller, code, ip), Breakpoint = breakpoint };
                _returnBreakpoints[breakpoint] = site;
            }
            catch (Exception error) when (Failures.OfRuntime(error))
            {
                _logger?.Invoke($"No return watched at native {ip} of method 0x{caller.Function.Token:X}: {error.Message}");
            }
        }
        _returnSites[key] = site;
        return site;
    }

    /// <summary>The IL offset of the call of a frame's method whose value the runtime keeps at a native offset, the return
    /// address the call goes back to; null for a call returning none.</summary>
    private static int? CallReturningTo(ModuleInfo module, ICorDebugILFrame caller, ICorDebugCode code, uint ip)
    {
        if (code is not ICorDebugCode3 returns)
        {
            return null;
        }
        uint[] offsets = new uint[4];
        foreach (ModuleMetadataReader.CallSite call in module.MetadataReader.CallSites(caller.Function.Token, 0, int.MaxValue, module.MetadataReader.VersionOf(caller)))
        {
            if (call.Kind == ModuleMetadataReader.CallKind.Call
                && returns.TryGetReturnValueLiveOffset(checked((uint)call.Offset), (uint)offsets.Length, out uint fetched, offsets) is Cor.S_OK
                && offsets.AsSpan(0, (int)Math.Min(fetched, (uint)offsets.Length)).Contains(ip))
            {
                return call.Offset;
            }
        }
        return null;
    }

    /// <summary>Where a call's method ends, its last visible line, for the moment it returns.</summary>
    private static StackFrameInfo EndOf(OpenCall call)
    {
        StackFrameInfo place = Framed(call.Place, 0);
        if (call.Module.MetadataReader.VisibleSequencePoints(call.Token) is { Count: > 0 } points && points[^1] is { Document: { } document } last)
        {
            (place.Source, place.Line, place.Column, place.EndLine, place.EndColumn) = (document, last.StartLine, last.StartColumn, last.EndLine, last.EndColumn);
        }
        return place;
    }

    /// <summary>A method's label as a moment says it: a constructor by its type, a property's accessor by the property,
    /// a top-level program's entry point as Main.</summary>
    private static string LabelOf(ModuleMetadataReader.CalledMethod? method) => method?.Name switch
    {
        null => "a method",
        ".ctor" or ".cctor" => $"new {method.DeclaringType}()",
        "<Main>$" => "Main()",
        _ => MethodLabel(method.Name),
    };

    private static string Cut(string value) => value.Length <= SaidValueLength ? value : $"{value[..(SaidValueLength - 1)]}…";

    private string? ThreadNameOf(ICorDebugThread thread) =>
        _threads.GetValueOrDefault(thread.Id)?.Name ?? (thread.Id == _mainThreadId ? "Main Thread" : null);

    private List<OpenCall> OpenOn(int threadId)
    {
        if (!_openCalls.TryGetValue(threadId, out List<OpenCall>? open))
        {
            _openCalls[threadId] = open = [];
        }
        return open;
    }

    /// <summary>The size of a moment's own values' text, those of its references past <paramref name="after"/>.</summary>
    private static long BytesOf(IReadOnlyDictionary<int, IReadOnlyList<VariableInfo>> values, int after) =>
        values.Where(entry => entry.Key > after).Sum(entry => entry.Value.Sum(row => 2L * (row.Name.Length + row.Value.Length + (row.Type?.Length ?? 0)) + 64));

    /// <summary>Keeps a moment, letting the oldest go, a tenth at a time, while the recording keeps more than its bounds.</summary>
    private void Keep(KeptMoment kept)
    {
        _moments.Add(kept);
        _momentsRecorded++;
        _momentBytes += kept.Bytes;
        _recordingHeld += kept.Held;
        while (_moments.Count > MostMoments || _momentBytes > MostRecordedBytes && _moments.Count > 1)
        {
            int drop = Math.Max(1, Math.Min(_moments.Count - 1, MostMoments / 10));
            _momentBytes -= _moments.Take(drop).Sum(moment => moment.Bytes);
            _moments.RemoveRange(0, drop);
            _momentsDropped += drop;
        }
    }

    /// <summary>A kept moment by its number, null once let go.</summary>
    private KeptMoment? Find(int index) =>
        _moments.Count > 0 && index - _moments[0].Moment.Index is int at && at >= 0 && at < _moments.Count ? _moments[at] : null;

    private static StackFrameInfo Framed(StackFrameInfo frame, int id) => new()
    {
        Id = id,
        Name = frame.Name,
        Line = frame.Line,
        EndLine = frame.EndLine,
        Column = frame.Column,
        EndColumn = frame.EndColumn,
        Source = frame.Source,
        IsUserCode = frame.IsUserCode,
        IsResolved = frame.IsResolved,
        DecompiledSourceInfo = frame.DecompiledSourceInfo,
    };

    /// <summary>The recording's timeline of the run a stopped thread is in (<see cref="FlowOf"/>; every run's where none is
    /// named), oldest first.</summary>
    public RecordedTimeline Timeline(int? threadId)
    {
        int? flow = threadId is int stopped ? FlowOf(stopped) : null;
        return new(_records, [.. _moments.Where(kept => flow is not int only || kept.Flow == only).Select(kept => kept.Moment)], _momentsDropped, _momentsRecorded,
            _recordingHeld, MostMoments, MostRecordedBytes);
    }

    /// <summary>A moment as a past stop: its method's frame where it stood, its values as they were, then the frames of the
    /// recorded calls it ran in, each where it made the next call; null for one let go.</summary>
    public Snapshot? MomentAt(int index)
    {
        if (Find(index) is not { } kept)
        {
            return null;
        }
        Dictionary<int, IReadOnlyList<VariableInfo>> values = new(kept.Values);
        List<SnapshotFrame> frames = [new SnapshotFrame(Framed(kept.Moment.Place, 1), kept.Top)];
        (IReadOnlyList<StackFrameInfo> callers, int? parent) = (kept.Callers, kept.Parent);
        for (int level = 0; level < 256 && callers.Count > 0; level++)
        {
            // The last frame a moment was called through is its recorded call's, which shows its arguments as at its call.
            KeptMoment? up = parent is int above ? Find(above) : null;
            for (int i = 0; i < callers.Count; i++)
            {
                frames.Add(new SnapshotFrame(Framed(callers[i], frames.Count + 1), up is not null && i == callers.Count - 1 ? Joined(up, values) : 0));
            }
            if (up is null)
            {
                break;
            }
            (callers, parent) = (up.Callers, up.Parent);
        }
        return new Snapshot(0, kept.Moment.ThreadId, kept.ThreadName, kept.Moment.At, kept.Held, frames, values, [], kept.Cut);
    }

    /// <summary>Another moment's values joined to <paramref name="values"/> under references after theirs, its top rows' the
    /// reference given.</summary>
    private static int Joined(KeptMoment other, Dictionary<int, IReadOnlyList<VariableInfo>> values)
    {
        int by = values.Count == 0 ? 0 : values.Keys.Max();
        foreach ((int reference, IReadOnlyList<VariableInfo> rows) in other.Values)
        {
            values[reference + by] = [.. rows.Select(row => new VariableInfo
            {
                Name = row.Name,
                EvaluateName = row.EvaluateName,
                DeclaringType = row.DeclaringType,
                Value = row.Value,
                Type = row.Type,
                VariablesReference = row.VariablesReference > 0 ? row.VariablesReference + by : row.VariablesReference,
                PresentationHint = row.PresentationHint,
                PassedOver = row.PassedOver,
                ImplicitOff = row.ImplicitOff,
                Mark = row.Mark,
            })];
        }
        return other.Top + by;
    }

    /// <summary>The frames a call was made through, top first: the one that made it, where there is one, and the recorded
    /// call it runs in where that is not the one (code that is not the user's between).</summary>
    private IReadOnlyList<StackFrameInfo> CallersTo(StackFrameInfo? caller, StackRange? callerRange, ICorDebugILFrame? parentFrame)
    {
        if (parentFrame is null || RangeOf(parentFrame) == callerRange)
        {
            return caller is null ? [] : [caller];
        }
        StackFrameInfo parent = DescribeFrame(0, parentFrame);
        return caller is null ? [parent] : [caller, parent];
    }
}
