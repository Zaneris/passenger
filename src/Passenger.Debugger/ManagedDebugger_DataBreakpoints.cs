using System.Runtime.InteropServices;
using Passenger.Dap;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>A data breakpoint as the client asks for it: the data it watches (a <see cref="DataBreakpointAnswer"/>'s id),
/// and what a line breakpoint has besides: a condition, a hit count, a message to log and whether a hit stops.</summary>
public sealed record DataBreakpointRequest(string DataId, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool Suspend = true);

/// <summary>What a data breakpoint could watch: its data's id, or none and why not, and what it is ("count on Counter").</summary>
public sealed record DataBreakpointAnswer(string? DataId, string Description);

/// <summary>What became of a data breakpoint asked for: its id, and whether it watches, or why not.</summary>
public sealed record DataBreakpointResult(int Id, bool Verified, string? Message);

/// <summary>Data breakpoints (Set Data Breakpoint on a member in Variables): the program stops at the line that changes
/// a field of an object, an auto-property's backing field or an array element. The runtime watches memory for a
/// debugger on Windows x64 alone (FEATURE_DATABREAKPOINT): the debugger puts the address in a debug register of every
/// thread, and the runtime tells of a write in managed code (ICorDebugManagedCallback4.DataBreakpoint) at the first
/// place after it where the thread can stop. The object is pinned while watched, as the runtime advises over
/// re-arming.</summary>
public partial class ManagedDebugger
{
    private const int DataSlots = 4;

    /// <summary>The most bytes one debug register watches.</summary>
    private const int MostWatched = 8;

    private const string NotAMember = "A data breakpoint watches a field or property of an object, or an element of an array, and this is neither";

    /// <summary>Why no data breakpoint can be set where the debugger runs, or null where one can: Windows on x64, the
    /// one system whose .NET runtime watches memory for a debugger.</summary>
    public static string? DataBreakpointsUnavailable { get; } =
        OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64 && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? null
            : $"Data breakpoints are only available on Windows x64, the one system whose .NET runtime supports them; this is {SystemName()} on {RuntimeInformation.OSArchitecture}";

    private static string SystemName() => OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : RuntimeInformation.OSDescription;

    /// <summary>What a data id names: a place in one object (the object kept by a weak handle, and pinned while watched),
    /// how many bytes, how its value is read from the object, and the bytes and value last seen there.</summary>
    private sealed class DataTarget(string id, ICorDebugHandleValue weak, ulong offset, int size, string description, Func<ICorDebugValue, ICorDebugValue> read)
    {
        public string Id { get; } = id;
        public ICorDebugHandleValue Weak { get; } = weak;
        public ulong Offset { get; } = offset;
        public int Size { get; } = size;
        public string Description { get; } = description;
        public Func<ICorDebugValue, ICorDebugValue> Read { get; } = read;
        public ICorDebugHandleValue? Pinned { get; set; }
        public ulong Address { get; set; }
        public byte[] Last { get; set; } = [];
        public string? Shown { get; set; }

        /// <summary>Where the object is now, or 0 once it has been collected.</summary>
        public ulong ObjectAddress() => Weak.TryIsNull(out bool gone) == Cor.S_OK && !gone && Weak.TryDereference(out ICorDebugValue? value) == Cor.S_OK
            && value.TryGetAddress(out CORDB_ADDRESS address) == Cor.S_OK ? address.Value : 0;
    }

    /// <summary>A data breakpoint set: its id, as asked for, on what, its hits so far and the debugger's word on it.</summary>
    private sealed class DataWatch(int id, DataBreakpointRequest request, DataTarget target) : IHitBreakpoint
    {
        public int Id { get; } = id;
        public DataBreakpointRequest Request { get; } = request;
        public DataTarget Target { get; } = target;
        public int HitCount { get; set; }
        public string? Message { get; set; }
        public string? Condition => Request.Condition;
        public string? HitCondition => Request.HitCondition;
        public string? LogMessage => Request.LogMessage;
        public bool Suspend => Request.Suspend;
    }

    /// <summary>Every data id given this session: one per place, given again when asked of again.</summary>
    private readonly List<DataTarget> _dataTargets = [];
    private readonly List<DataWatch> _dataWatches = [];

    /// <summary>What each debug register watches, the same on every thread.</summary>
    private readonly DataTarget?[] _dataSlots = new DataTarget?[DataSlots];
    private int _lastDataId;

    /// <summary>A data breakpoint's stop: the frame that wrote, shown at the statement of the write rather than where the
    /// thread is, which is past it and often the next statement's start; until the program goes on.</summary>
    private (int ThreadId, StackRange Frame, int ILOffset)? _writtenAt;

    /// <summary>A line breakpoint's stop where its IL offset's sequence point is not its own (bound short of its point, where
    /// the JIT maps code, or at an XAML attribute's setter run under its element's point): the frame is shown where the
    /// breakpoint is, until the program goes on.</summary>
    private (int ThreadId, StackRange Frame, ModuleMetadataReader.ResolvedBreakpoint Place)? _stoppedShort;

    /// <summary>Whether a member of a container (an object's field or property, an array's element, by the variables
    /// reference its members were listed by and its name there) can be watched; its data's id when it can.</summary>
    public DataBreakpointAnswer DataBreakpointInfo(int? variablesReference, string name)
    {
        if (DataBreakpointsUnavailable is { } unavailable)
        {
            return new DataBreakpointAnswer(null, unavailable);
        }
        if (_process is null)
        {
            return new DataBreakpointAnswer(null, "Nothing is being debugged");
        }
        if (_process.TryIsRunning(out bool running) == Cor.S_OK && running)
        {
            return new DataBreakpointAnswer(null, "The program is running: a data breakpoint is set on a value where it is paused");
        }
        if (variablesReference is not int reference || _variableManager.GetReference(reference) is not { } container)
        {
            return new DataBreakpointAnswer(null, variablesReference is int earlier && _variableManager.IsFromEarlierStop(earlier)
                ? "This value is from an earlier stop, and the program has run on since"
                : NotAMember);
        }
        try
        {
            return container.ReferenceKind switch
            {
                StoredReferenceKind.StaticClassVariable => new DataBreakpointAnswer(null, "Static fields and properties are not supported"),
                StoredReferenceKind.StackVariable when container.DebuggerProxyInstance is not null =>
                    new DataBreakpointAnswer(null, $"These are the members its debugger view shows; its own are under {DapRows.RawView}"),
                StoredReferenceKind.StackVariable or StoredReferenceKind.RawView or StoredReferenceKind.ArrayRange => Locate(container.ObjectValue!, name, container.ThreadId),
                _ => new DataBreakpointAnswer(null, NotAMember),
            };
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"dataBreakpointInfo {name}: {error}");
            return new DataBreakpointAnswer(null, $"The debugger could not read {name}: {error.Message}");
        }
    }

    /// <summary>The place in an object (or array) that a member of it, by its name in Variables, is.</summary>
    private DataBreakpointAnswer Locate(ICorDebugValue container, string name, ThreadId threadId)
    {
        ICorDebugValue unwrapped = container.UnwrapDebugValue();
        if (unwrapped is ICorDebugArrayValue array)
        {
            int rank = array.Rank;
            if (Indices(name) is not { } indices || indices.Length != rank)
            {
                return new DataBreakpointAnswer(null, NotAMember);
            }
            return Place(array, array.GetElement(rank, indices), name, GetCorDebugTypeFriendlyName(array.ExactType), threadId,
                value => ((ICorDebugArrayValue)value).GetElement(rank, indices));
        }
        if (unwrapped is not ICorDebugObjectValue objectValue || unwrapped is ICorDebugStringValue)
        {
            return new DataBreakpointAnswer(null, NotAMember);
        }
        ICorDebugType type = objectValue.ExactType;
        if (type.Type is CorElementType.VALUETYPE)
        {
            return new DataBreakpointAnswer(null, "Fields and properties of structs are not supported");
        }
        (ICorDebugClass? declaring, mdFieldDef field, string? problem) = FieldOf(type, name);
        if (declaring is null)
        {
            return new DataBreakpointAnswer(null, problem ?? NotAMember);
        }
        return Place(objectValue, objectValue.GetFieldValue(declaring, field), name, GetCorDebugTypeFriendlyName(type), threadId,
            value => ((ICorDebugObjectValue)value).GetFieldValue(declaring, field));
    }

    /// <summary>The indices of an element as Variables names it: "[3]", "[1, 2]".</summary>
    private static uint[]? Indices(string name)
    {
        if (name.Length < 3 || name[0] != '[' || name[^1] != ']')
        {
            return null;
        }
        string[] parts = name[1..^1].Split(',', StringSplitOptions.TrimEntries);
        uint[] indices = new uint[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!uint.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out indices[i]))
            {
                return null;
            }
        }
        return indices;
    }

    /// <summary>The instance field a member is, by its name: a field of the type or a base, else an auto-property's backing
    /// field. Rider's words for a property it cannot watch.</summary>
    private static (ICorDebugClass? Declaring, mdFieldDef Field, string? Problem) FieldOf(ICorDebugType type, string name)
    {
        for (ICorDebugType? each = type; each is not null; each = each.Base)
        {
            ICorDebugClass declaring = each.Class;
            IMetaDataImport metadata = declaring.Module.GetMetaDataInterface<IMetaDataImport>();
            foreach (mdFieldDef field in metadata.EnumFieldsWithName(declaring.Token, name))
            {
                return field.IsStatic(metadata) || field.IsLiteral(metadata)
                    ? (null, default, "Static fields and properties are not supported")
                    : (declaring, field, null);
            }
        }
        for (ICorDebugType? each = type; each is not null; each = each.Base)
        {
            ICorDebugClass declaring = each.Class;
            IMetaDataImport metadata = declaring.Module.GetMetaDataInterface<IMetaDataImport>();
            if (metadata.GetPropertyWithName(declaring.Token, name) is not { IsNil: false } property)
            {
                continue;
            }
            (_, _, _, _, _, _, _, _, mdMethodDef setter, mdMethodDef getter, _, _) = metadata.GetPropertyProps(property);
            if (!getter.IsNil && getter.IsStatic(metadata))
            {
                return (null, default, "Static fields and properties are not supported");
            }
            if (!setter.IsNil && !IsCompilerGenerated(metadata, setter))
            {
                return (null, default, "The breakpoint cannot be set. Data breakpoints for properties with custom setters are not supported.");
            }
            mdFieldDef backing = metadata.EnumFieldsWithName(declaring.Token, $"<{name}>k__BackingField").FirstOrDefault();
            if (getter.IsNil || !IsCompilerGenerated(metadata, getter) || backing.IsNil)
            {
                return (null, default, "The breakpoint cannot be set. The property doesn't have a backing field.");
            }
            return (declaring, backing, null);
        }
        return (null, default, null);
    }

    private static bool IsCompilerGenerated(IMetaDataImport metadata, mdMethodDef method) =>
        metadata.TryGetCustomAttributeByName(method, "System.Runtime.CompilerServices.CompilerGeneratedAttribute", out _, out _) == Cor.S_OK;

    /// <summary>The data id of a member's place in its object: the one given before for that place, else a new one, its
    /// object held by a weak handle so it is found again however the collector moves it before it is watched.</summary>
    private DataBreakpointAnswer Place(ICorDebugValue owner, ICorDebugValue member, string name, string ownerType, ThreadId threadId, Func<ICorDebugValue, ICorDebugValue> read)
    {
        string description = $"{name} on {ownerType}";
        ulong objectAddress = owner.Address.Value;
        ulong address = member.TryGetAddress(out CORDB_ADDRESS at) == Cor.S_OK ? at.Value : 0;
        if (objectAddress == 0 || address < objectAddress)
        {
            return new DataBreakpointAnswer(null, $"The breakpoint cannot be set. '{name}' cannot be found on the managed heap and cannot be tracked.");
        }
        int size = member.Size;
        if (size > MostWatched)
        {
            return new DataBreakpointAnswer(null, $"The breakpoint cannot be set. The hardware does not support monitoring of the requested number of bytes ({size}). The maximum number of bytes to track is {MostWatched}.");
        }
        if (size is not (1 or 2 or 4 or 8) || address % (ulong)size != 0)
        {
            return new DataBreakpointAnswer(null, $"The breakpoint cannot be set. The hardware watches {size} bytes only at an address that is a multiple of {size}, which '{name}' is not at.");
        }
        ulong offset = address - objectAddress;
        if (_dataTargets.FirstOrDefault(t => t.Offset == offset && t.Size == size && t.ObjectAddress() == objectAddress) is { } known)
        {
            return new DataBreakpointAnswer(known.Id, known.Description);
        }
        if (owner is not ICorDebugHeapValue2 heap)
        {
            return new DataBreakpointAnswer(null, $"The breakpoint cannot be set. '{name}' cannot be found on the managed heap and cannot be tracked.");
        }
        DataTarget target = new($"data:{++_lastDataId}", heap.CreateHandle(CorDebugHandleType.HANDLE_WEAK_TRACK_RESURRECTION), offset, size, description, read)
        {
            Shown = Shown(member),
        };
        _logger?.Invoke($"Data {target.Id}: {description}, {size} bytes at +{offset} of the object at 0x{objectAddress:X} (thread {threadId.Value})");
        _dataTargets.Add(target);
        return new DataBreakpointAnswer(target.Id, description);
    }

    /// <summary>A member's value as Variables shows it, without running the program's code to show it.</summary>
    private static string Shown(ICorDebugValue value)
    {
        ResolvedCorDebugValue resolved = ResolveCorDebugValue(value);
        return resolved.Value is { } present ? GetValueForCorDebugValue(present, true).Value : "null";
    }

    /// <summary>Sets the data breakpoints to those asked for, kept and counted on as a file's line breakpoints are, one on the
    /// same data counting on from the hits of the one it replaces; the places no longer watched are let go (their objects
    /// unpinned), and new ones are pinned and put in a free debug register of every thread. The program, stopped for it if
    /// it runs, goes on as it was.</summary>
    public List<DataBreakpointResult> SetDataBreakpoints(IReadOnlyList<DataBreakpointRequest> requests)
    {
        _logger?.Invoke($"SetDataBreakpoints: {string.Join(", ", requests.Select(r => r.DataId))}");
        (DataWatch? Kept, int Hits)[] matched = Reconcile(requests, _dataWatches, w => w.Request, static (was, now) => was.DataId == now.DataId, out _);
        _dataWatches.Clear();
        List<DataBreakpointResult> results = [];
        for (int i = 0; i < requests.Count; i++)
        {
            DataBreakpointRequest request = requests[i];
            DataWatch? watch = matched[i].Kept;
            string? refused = Unreadable(request.Condition, request.HitCondition) ?? DataBreakpointsUnavailable;
            DataTarget? target = watch?.Target ?? _dataTargets.FirstOrDefault(t => t.Id == request.DataId);
            if (refused is null && target is null)
            {
                refused = "The data it watched is from another session";
            }
            if (refused is null && watch is null && _dataWatches.Select(w => w.Target).Append(target!).Distinct().Count() > DataSlots)
            {
                refused = $"The breakpoint cannot be set. The maximum number of data breakpoints for the current architecture is {DataSlots}.";
            }
            int id = watch?.Id ?? _breakpointManager.NextId();
            if (refused is null)
            {
                _dataWatches.Add(watch ?? new DataWatch(id, request, target!) { HitCount = matched[i].Hits });
            }
            results.Add(new DataBreakpointResult(id, refused is null, refused ?? watch?.Message));
        }
        if (_process is not null && !_exited.IsSet)
        {
            ArmDataBreakpoints(results);
        }
        return results;
    }

    /// <summary>Puts each watched place in a debug register and frees those no longer watched, on every thread; a place
    /// whose object has been collected, or cannot be pinned, is refused after all, saying why.</summary>
    private void ArmDataBreakpoints(List<DataBreakpointResult> results)
    {
        ArgumentNullException.ThrowIfNull(_process);
        bool stopped = false;
        if (_process.TryIsRunning(out bool running) == Cor.S_OK && running)
        {
            _process.Stop(0);
            stopped = true;
        }
        try
        {
            HashSet<DataTarget> wanted = [.. _dataWatches.Select(w => w.Target)];
            bool changed = false;
            for (int slot = 0; slot < DataSlots; slot++)
            {
                if (_dataSlots[slot] is { } gone && !wanted.Contains(gone))
                {
                    Unpin(gone);
                    _dataSlots[slot] = null;
                    changed = true;
                }
            }
            foreach (DataTarget target in wanted.Where(t => !_dataSlots.Contains(t)))
            {
                if (Pin(target) is { } why)
                {
                    foreach (DataWatch refused in _dataWatches.Where(w => w.Target == target).ToList())
                    {
                        _dataWatches.Remove(refused);
                        int at = results.FindIndex(r => r.Id == refused.Id);
                        if (at >= 0)
                        {
                            results[at] = results[at] with { Verified = false, Message = why };
                        }
                    }
                    continue;
                }
                _dataSlots[Array.IndexOf(_dataSlots, null)] = target;
                changed = true;
            }
            if (changed)
            {
                ApplyDataRegisters();
            }
        }
        finally
        {
            if (stopped)
            {
                _process.Continue(false);
            }
        }
    }

    /// <summary>Pins a place's object where it is, so its address holds through every collection while it is watched, and
    /// reads what is there now; null when pinned, else why it cannot be.</summary>
    private string? Pin(DataTarget target)
    {
        if (target.Weak.TryIsNull(out bool gone) != Cor.S_OK || gone || target.Weak.TryDereference(out ICorDebugValue? value) != Cor.S_OK)
        {
            return $"The object {target.Description} was in has been collected";
        }
        if (value is not ICorDebugHeapValue4 pinnable || pinnable.TryCreatePinnedHandle(out ICorDebugHandleValue? pinned) != Cor.S_OK)
        {
            return "The program's runtime cannot pin the object to watch it: data breakpoints need .NET 5 or later";
        }
        target.Pinned = pinned;
        target.Address = pinned.Dereference().Address.Value + target.Offset;
        target.Last = ReadData(target);
        _logger?.Invoke($"Data {target.Id}: {target.Description} watched at 0x{target.Address:X}");
        return null;
    }

    private static void Unpin(DataTarget target)
    {
        target.Pinned?.TryDispose();
        target.Pinned = null;
        target.Address = 0;
    }

    private byte[] ReadData(DataTarget target) => _process!.ReadMemory(new CORDB_ADDRESS(target.Address), target.Size).buffer;

    /// <summary>Sets every thread's debug registers to what the slots hold.</summary>
    private void ApplyDataRegisters()
    {
        foreach (int threadId in _threads.Keys)
        {
            ApplyDataRegisters(threadId);
        }
    }

    private void ApplyDataRegisters(int threadId)
    {
        if (!OperatingSystem.IsWindows() || _process is null)
        {
            return;
        }
        (ulong Address, int Size)?[] slots = [.. _dataSlots.Select(t => t is null ? ((ulong, int)?)null : (t.Address, t.Size))];
        DebugRegisters.Apply(_process, threadId, slots, _logger);
    }

    /// <summary>Takes the debug registers out of every thread and lets go of the objects watched, as the debugger lets go
    /// of the program: a write it no longer watches would otherwise end it, no debugger there to take the trap.</summary>
    private void DisarmDataBreakpoints()
    {
        if (_dataSlots.All(t => t is null))
        {
            return;
        }
        for (int slot = 0; slot < DataSlots; slot++)
        {
            if (_dataSlots[slot] is { } target)
            {
                Unpin(target);
                _dataSlots[slot] = null;
            }
        }
        try
        {
            ApplyDataRegisters();
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"Data breakpoints could not be taken out of the program: {error.Message}");
        }
    }

    /// <summary>A write the runtime caught: it stops the program when it changed the value (the same value written
    /// again does not), its hit count and condition allow, and it suspends; it is shown at the statement that wrote,
    /// the value before and after in the stop's words.</summary>
    private async Task HandleDataBreakpoint(DataBreakpointCorDebugManagedCallbackEventArgs written)
    {
        ICorDebugThread thread = written.Thread;
        if (PutOffDuringEvaluation(written, thread))
        {
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            // The runtime tells of writes on Windows alone, where it has the debug registers to watch them with.
            Continue(clearVariables: false);
            return;
        }
        // Dr6 says which register the write hit; it is cleared, so no later trap of the thread is taken for this one.
        DataTarget? target = DebugRegisters.Hit(written.Context).Select(slot => _dataSlots[slot]).FirstOrDefault(t => t is not null);
        ApplyDataRegisters(thread.Id);
        DataWatch? watch = target is null ? null : _dataWatches.FirstOrDefault(w => w.Target == target);
        if (target is null || watch is null)
        {
            _logger?.Invoke($"A write caught on thread {thread.Id} is no data breakpoint's now");
            Continue(clearVariables: false);
            return;
        }
        byte[] now = ReadData(target);
        if (now.AsSpan().SequenceEqual(target.Last))
        {
            _logger?.Invoke($"Data {target.Id}: written with the value it had");
            Continue(clearVariables: false);
            return;
        }
        string? before = target.Shown;
        target.Last = now;
        target.Shown = target.Pinned is { } pinned ? Shown(target.Read(pinned.Dereference())) : null;
        if (!await HitStopsAsync(thread, watch, $"the data breakpoint on {target.Description}",
            () => $"Data breakpoint reached: {target.Description} is changed from {before ?? "?"} to {target.Shown ?? "?"}"))
        {
            Continue(clearVariables: true);
            return;
        }
        EndStepForStop();
        ICorDebugFrame? top = EnumerateFramesForThread(thread).FirstOrDefault();
        _writtenAt = top is null ? null : WritingStatement(thread, top);
        SourceInfo? source = top is ICorDebugILFrame ? GetSourceInfoAtFrame(top, _justMyCode is false) : null;
        string change = $"Previous Value: {before ?? "?"}, New Value: {target.Shown ?? "?"}";
        _logger?.Invoke($"Data {target.Id}: {target.Description} is changed ({change})");
        ReportStop(thread.Id, StopReasons.DataBreakpoint, source?.FilePath, source?.StartLine ?? 0, source?.StartColumn ?? 0, [watch.Id], change);
    }

    /// <summary>The statement a frame's last instruction ran in: the write a data breakpoint caught, which the thread has
    /// gone past by one instruction (or by the call to the collector's write barrier).</summary>
    private (int ThreadId, StackRange Frame, int ILOffset)? WritingStatement(ICorDebugThread thread, ICorDebugFrame frame)
    {
        if (frame is not ICorDebugNativeFrame native || RangeOf(frame) is not { } range || native.TryGetIP(out uint ip) != Cor.S_OK || ip == 0
            || native.TryGetCode(out ICorDebugCode? code) != Cor.S_OK)
        {
            return null;
        }
        if (code.TryGetILToNativeMapping(0, out uint count, null) != Cor.S_OK || count == 0)
        {
            return null;
        }
        COR_DEBUG_IL_TO_NATIVE_MAP[] map = new COR_DEBUG_IL_TO_NATIVE_MAP[count];
        if (code.TryGetILToNativeMapping(count, out count, map) != Cor.S_OK)
        {
            return null;
        }
        uint wrote = ip - 1;
        foreach (COR_DEBUG_IL_TO_NATIVE_MAP entry in map.Take((int)count))
        {
            // The prolog's, the epilog's and code of no IL (-2, -3, -1) name no statement.
            if (entry.nativeStartOffset <= wrote && wrote < entry.nativeEndOffset && (int)entry.ilOffset >= 0)
            {
                return (thread.Id, range, (int)entry.ilOffset);
            }
        }
        return null;
    }

    /// <summary>The IL offset a frame is shown at: the statement that wrote, for the frame a data breakpoint stopped in.</summary>
    private int? WrittenAt(ICorDebugFrame frame) => _writtenAt is { } written && RangeOf(frame) == written.Frame ? written.ILOffset : null;

    /// <summary>Where a breakpoint is, for the frame it stopped in when that is not its IL offset's sequence point.</summary>
    private ModuleMetadataReader.ResolvedBreakpoint? StoppedShortAt(ICorDebugFrame frame) =>
        _stoppedShort is { } held && RangeOf(frame) == held.Frame ? held.Place : null;

    /// <summary>A step from a data breakpoint's stop, shown at the statement that wrote while the thread is already at the
    /// next statement's start: the rest of the statement has run, so Step Over and Step Into end where the thread is,
    /// without running it on. True when it did.</summary>
    private bool StepPastWrite(ICorDebugThread thread, StepType type)
    {
        if (type is StepType.StepOut || _writtenAt is not { } written || written.ThreadId != thread.Id
            || EnumerateFramesForThread(thread).FirstOrDefault() is not ICorDebugILFrame top || RangeOf(top) != written.Frame)
        {
            return false;
        }
        SourceInfo? shown = GetSourceInfoAtFrame(top, _justMyCode is false);
        _writtenAt = null;
        SourceInfo? actual = GetSourceInfoAtFrame(top, _justMyCode is false);
        if (actual is not { } at || shown is { } same && same.StartLine == at.StartLine && same.StartColumn == at.StartColumn)
        {
            return false;
        }
        ReportStop(thread.Id, StopReasons.Step, at.FilePath, at.StartLine, at.StartColumn, null);
        return true;
    }

    /// <summary>Lets go of the session's data: the handles of every place asked of, the objects watched unpinned.</summary>
    private void ForgetDataBreakpoints(bool exited)
    {
        if (!exited)
        {
            DisarmDataBreakpoints();
            foreach (DataTarget target in _dataTargets)
            {
                target.Weak.TryDispose();
            }
        }
        Array.Clear(_dataSlots);
        _dataTargets.Clear();
        _dataWatches.Clear();
        _writtenAt = null;
    }
}
