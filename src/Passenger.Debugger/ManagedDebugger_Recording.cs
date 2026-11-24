using System.Diagnostics;
using Passenger.Dap;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;

namespace Passenger.Debugger;

/// <summary>Historical debugging: while a session records, each method of the user's code that its symbols place (none the
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
    /// address it goes back to, its method's frame where it began and its label, the frame it was called from, and the
    /// recorded call that frame is.</summary>
    private sealed record OpenCall(int Moment, StackRange Frame, StackRange? CallerFrame, (ulong Code, uint Offset)? Return, StackFrameInfo Place, ModuleInfo Module,
        int Token, string Label, StackFrameInfo? Caller, int? Parent);

    /// <summary>A moment kept: what the timeline lists, the frame it was called from and the recorded call that is, the call it
    /// returns from, its values by references of its own, its top frame's rows, how long reading it held the program, the
    /// size of its own values and what it left out.</summary>
    private sealed record KeptMoment(RecordedMoment Moment, string? ThreadName, StackFrameInfo? Caller, int? Parent, IReadOnlyDictionary<int, IReadOnlyList<VariableInfo>> Values,
        int Top, IReadOnlyList<VariableInfo> TopRows, TimeSpan Held, long Bytes, string? Cut);

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
        _callBreakpoints.Clear();
        _returnBreakpoints.Clear();
        _returnSites.Clear();
        _recordedMethods.Clear();
        _recordedModules.Clear();
        _openCalls.Clear();
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
        // What lies at or below this frame has returned or been unwound.
        open.RemoveAll(call => call.Frame.Start.Value <= range.Start.Value);
        ICorDebugILFrame? caller = frame.TryGetCaller(out ICorDebugFrame? up) is Cor.S_OK ? up as ICorDebugILFrame : null;
        StackRange? callerRange = caller is null ? null : RangeOf(caller);
        int? parent = open.Count > 0 && callerRange is { } from && open[^1].Frame == from ? open[^1].Moment : null;
        int token = frame.Function.Token;
        RecordedMethod method = MethodRecorded(module, frame);
        ReturnSite? site = caller is null ? null : ReturnSiteOf(caller, method.ReturnsValue);
        StackFrameInfo place = method.Place ?? DescribeFrame(0, frame);
        StackFrameInfo? callerPlace = site is null ? null : site.Place ?? DescribeFrame(0, caller!);

        Capture capture = NewRecordingCapture(null);
        List<VariableInfo> rows = [];
        int topReference = await ReadRecordedAsync(capture, rows, () => AddArguments(module, frame.Function, rows, new ThreadId(thread.Id), new FrameStackDepth(0)));
        IReadOnlyList<VariableInfo> top = capture.Values[topReference];
        int index = _momentsRecorded + 1;
        string args = string.Join(", ", top.Where(row => row.Name != "this").Select(row => Cut(row.Value)));
        string said = method.Label.EndsWith("()", StringComparison.Ordinal) ? $"{method.Label[..^2]}({args})" : method.Label;
        Keep(new KeptMoment(new RecordedMoment(index, MomentKind.Call, thread.Id, DateTimeOffset.Now, place, open.Count, said), ThreadNameOf(thread), callerPlace, parent,
            capture.Values, topReference, top, Stopwatch.GetElapsedTime(came), BytesOf(capture.Values, 0), capture.Said));
        open.Add(new OpenCall(index, range, callerRange, site?.Breakpoint is null ? null : site.Key, place, module, token, method.Label, callerPlace, parent));
    }

    /// <summary>What a recording says of a method, read at its first call.</summary>
    private RecordedMethod MethodRecorded(ModuleInfo module, ICorDebugILFrame frame)
    {
        int token = frame.Function.Token;
        if (!_recordedMethods.TryGetValue((module, token), out RecordedMethod? method))
        {
            ModuleMetadataReader.CalledMethod? described = module.MetadataReader.DescribeCall(token);
            StackFrameInfo place = DescribeFrame(0, frame);
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
            open.RemoveAll(call => call.Frame.Start.Value < range.Start.Value);
            return;
        }
        OpenCall call = open[at];
        open.RemoveRange(at, open.Count - at);
        KeptMoment? called = Find(call.Moment);
        Capture capture = NewRecordingCapture(called?.Values);
        List<VariableInfo> rows = [];
        string name = ReturnedRow(call.Label);
        int returnedReference = site.CallOffset is int offset && Read(frame, offset) is { } value
            ? await ReadRecordedAsync(capture, rows, () => AddValueRow(rows, name, null, value, new ThreadId(thread.Id), new FrameStackDepth(0)))
            : capture.Add([]);
        IReadOnlyList<VariableInfo> returned = capture.Values[returnedReference];
        IReadOnlyList<VariableInfo> top = [.. returned, .. called?.TopRows ?? []];
        int topReference = capture.Add(top);
        string said = returned.Count > 0 ? $"{name} {Cut(returned[0].Value)}" : name;
        Keep(new KeptMoment(new RecordedMoment(_momentsRecorded + 1, MomentKind.Return, thread.Id, DateTimeOffset.Now, EndOf(call), at, said), ThreadNameOf(thread), call.Caller,
            call.Parent, capture.Values, topReference, top, Stopwatch.GetElapsedTime(came), BytesOf(capture.Values, called?.Values.Count ?? 0), capture.Said));
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

    /// <summary>The return address a frame of the user's code goes back to, its place read once, watched by a breakpoint of
    /// the recording's from the first call through it that returns a value; null where it is not the user's code.</summary>
    private ReturnSite? ReturnSiteOf(ICorDebugILFrame caller, bool returnsValue)
    {
        if (FindModule(caller.Function.Module) is not { IsUserCode: true } module || caller is not ICorDebugNativeFrame native
            || native.TryGetIP(out uint ip) is not Cor.S_OK || native.TryGetCode(out ICorDebugCode? code) is not Cor.S_OK || code is null
            || code.TryGetAddress(out CORDB_ADDRESS start) is not Cor.S_OK)
        {
            return null;
        }
        (ulong, uint) key = (start.Value, ip);
        if (_returnSites.TryGetValue(key, out ReturnSite? known))
        {
            return known;
        }
        StackFrameInfo place = DescribeFrame(0, caller);
        ReturnSite site = new(key, place.Name.Contains('<', StringComparison.Ordinal) ? null : place, null, null);
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
        foreach (ModuleMetadataReader.CallSite call in module.MetadataReader.CallSites(caller.Function.Token, 0, int.MaxValue, ModuleMetadataReader.VersionOf(caller)))
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

    /// <summary>The recording's timeline on a thread (every thread's where none is named), oldest first.</summary>
    public RecordedTimeline Timeline(int? threadId) =>
        new(_records, [.. _moments.Where(kept => threadId is not int only || kept.Moment.ThreadId == only).Select(kept => kept.Moment)], _momentsDropped, _momentsRecorded,
            _recordingHeld, MostMoments, MostRecordedBytes);

    /// <summary>A moment as a past stop: its method's frame where it stood, its values as they were, then the frames of the
    /// recorded calls it ran in, each where it made the next call; null for one let go.</summary>
    public Snapshot? MomentAt(int index)
    {
        if (Find(index) is not { } kept)
        {
            return null;
        }
        List<SnapshotFrame> frames = [new SnapshotFrame(Framed(kept.Moment.Place, 1), kept.Top)];
        (StackFrameInfo? caller, int? parent) = (kept.Caller, kept.Parent);
        while (caller is not null)
        {
            frames.Add(new SnapshotFrame(Framed(caller, frames.Count + 1), 0));
            (caller, parent) = parent is int above && Find(above) is { } up ? (up.Caller, up.Parent) : (null, null);
        }
        return new Snapshot(0, kept.Moment.ThreadId, kept.ThreadName, kept.Moment.At, kept.Held, frames, kept.Values, [], kept.Cut);
    }
}
