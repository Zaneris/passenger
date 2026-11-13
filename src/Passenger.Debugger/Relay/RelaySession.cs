using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.Relay;

/// <summary>The engine's hold on one app's ICorDebug through the relay: its stand-ins, each call carried by its
/// interface's plan, and the calls answered here instead: what the app already said (<see cref="RelayAnswers"/>), an
/// enumerator's elements read many at a time (<see cref="EnumBuffer{T}"/>), a module's name and metadata where the host
/// has its copy, and the arguments no plan can size. One identity per object in the app, so the engine's comparisons
/// hold; each object let go in the app once the engine holds none of its stand-ins.</summary>
internal sealed unsafe partial class RelaySession
{
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_INVALIDARG = unchecked((int)0x80070057);
    private const int EnumBatch = 32;

    private readonly IRelayLink _link;
    private readonly DeviceModules _modules;
    private readonly Action<string>? _log;
    private readonly Lock _lock = new();
    private readonly Dictionary<ulong, RemoteObject> _objects = [];
    private readonly List<StandIn> _heldForTheStop = [];
    private readonly ConcurrentDictionary<string, int> _tally = new();
    private readonly nint[] _callbackInterfaces = new nint[4];
    private int _stop;

    public RelaySession(IRelayLink link, DeviceModules modules, Action<string>? log)
    {
        _link = link;
        _modules = modules;
        _log = log;
    }

    /// <summary>How many objects of the app the engine holds now, kept ones included.</summary>
    public int ObjectsHeld
    {
        get
        {
            lock (_lock)
            {
                return _objects.Count;
            }
        }
    }

    /// <summary>A stand-in for an object the app handed out with a reference of its own, given to the caller with one
    /// reference; a second pointer for an interface already stood in for lets its reference go at once.</summary>
    public nint Take(RemotePointer remote, InterfacePlan plan)
    {
        if (remote.Pointer == 0)
        {
            return 0;
        }
        ulong identity = remote.Identity != 0 ? remote.Identity : remote.Pointer;
        bool surplus = false;
        nint block;
        lock (_lock)
        {
            if (!_objects.TryGetValue(identity, out RemoteObject? owner))
            {
                owner = new RemoteObject(identity);
                _objects[identity] = owner;
            }
            owner.Kept |= RelayAnswers.Kept(plan.Type);
            if (owner.StandIns.TryGetValue(plan, out StandIn? known))
            {
                surplus = known.Pointer != 0;
                block = known.Block;
                if (!surplus)
                {
                    // An IUnknown made here holds no reference: the one handed out now is its.
                    known.Pointer = remote.Pointer;
                }
            }
            else
            {
                block = MakeStandIn(owner, plan, remote.Pointer);
            }
            owner.References++;
        }
        if (surplus)
        {
            _link.Release(remote.Pointer);
        }
        return block;
    }

    private nint MakeStandIn(RemoteObject owner, InterfacePlan plan, ulong pointer)
    {
        StandIn standIn = new(this, owner, plan, pointer);
        standIn.Block = RemoteObjects.MakeBlock(standIn);
        owner.StandIns[plan] = standIn;
        return standIn.Block;
    }

    public uint AddRef(StandIn standIn)
    {
        lock (_lock)
        {
            return (uint)++standIn.Owner.References;
        }
    }

    public uint Release(StandIn standIn)
    {
        List<ulong>? letGo = null;
        List<nint>? freed = null;
        EnumBuffer<nint>? enumeration = null;
        int left;
        lock (_lock)
        {
            RemoteObject owner = standIn.Owner;
            left = --owner.References;
            if (left > 0 || owner.Kept)
            {
                return (uint)Math.Max(left, 1);
            }
            if (_objects.TryGetValue(owner.Identity, out RemoteObject? current) && current == owner)
            {
                _objects.Remove(owner.Identity);
            }
            letGo = [];
            freed = [];
            foreach (StandIn each in owner.StandIns.Values)
            {
                if (each.Pointer != 0)
                {
                    letGo.Add(each.Pointer);
                }
                freed.Add(each.Block);
            }
            owner.StandIns.Clear();
            enumeration = owner.Enumeration;
            owner.Enumeration = null;
            // A lasting answer holds only kept objects, and a stop's answers are let go with the stop.
            owner.Answers.Clear();
        }
        enumeration?.Clear(ReleaseBlock);
        foreach (nint block in freed)
        {
            RemoteObjects.FreeBlock(block);
        }
        foreach (ulong pointer in letGo)
        {
            _link.Release(pointer);
        }
        return 0;
    }

    private void ReleaseBlock(nint block)
    {
        if (RemoteObjects.Of(block) is { } standIn)
        {
            Release(standIn);
        }
    }

    private void AddRefBlock(nint block)
    {
        if (RemoteObjects.Of(block) is { } standIn)
        {
            AddRef(standIn);
        }
    }

    public int QueryInterface(StandIn standIn, Guid iid, nint* result)
    {
        RemoteObject owner = standIn.Owner;
        InterfacePlan? plan = iid == RemoteInterfaces.IUnknown.Iid ? RemoteInterfaces.IUnknown : RemoteInterfaces.OfIid(iid);
        if (plan is null)
        {
            return E_NOINTERFACE;
        }
        ulong through;
        lock (_lock)
        {
            if (owner.StandIns.TryGetValue(plan, out StandIn? known))
            {
                owner.References++;
                *result = known.Block;
                return 0;
            }
            if (plan == RemoteInterfaces.IUnknown)
            {
                owner.References++;
                *result = MakeStandIn(owner, plan, 0);
                return 0;
            }
            if (owner.Lacks.Contains(plan))
            {
                return E_NOINTERFACE;
            }
            through = standIn.Pointer != 0 ? standIn.Pointer : owner.StandIns.Values.FirstOrDefault(each => each.Pointer != 0)?.Pointer ?? 0;
        }
        if (through == 0)
        {
            return E_NOINTERFACE;
        }
        _tally.AddOrUpdate($"QI for {plan.Type.Name}", 1, (_, count) => count + 1);
        RelayReply reply = _link.Call(through, 0, [RelayArg.In(iid.ToByteArray()), RelayArg.Objects(1)]);
        if (reply.Hr < 0)
        {
            if (reply.Hr == E_NOINTERFACE)
            {
                lock (_lock)
                {
                    owner.Lacks.Add(plan);
                }
            }
            return reply.Hr;
        }
        *result = Take(reply.Objects[0][0], plan);
        return 0;
    }

    /// <summary>A call the engine made on a stand-in: answered here where it can be, else carried to the app.</summary>
    public int Dispatch(StandIn standIn, int slot, nint* args)
    {
        try
        {
            if (standIn.Plan.At(slot) is not { } method)
            {
                return E_NOTIMPL;
            }
            if (RelayAnswers.Changes(method.Name))
            {
                EndStop();
            }
            if (Answered(standIn, method, args) is int answered)
            {
                return answered;
            }
            if (typeof(ICorDebugEnum).IsAssignableFrom(standIn.Plan.Type) && Enumerated(standIn, slot, method, args) is int enumerated)
            {
                return enumerated;
            }
            AnswerScope scope = Rememberable(method) ? RelayAnswers.ScopeOf(method.Declaring, method.Name) : AnswerScope.None;
            AnswerKey key = scope == AnswerScope.None ? default : KeyOf(method, args);
            if (scope != AnswerScope.None && Recalled(standIn.Owner, key, method, args) is int recalled)
            {
                return recalled;
            }
            if (standIn.Pointer == 0 || method.Params.Any(each => each.Kind == ParamKind.Pointer))
            {
                // A host address the plan cannot size means nothing in the app, nor does an IUnknown made here.
                return E_NOTIMPL;
            }
            List<RelayArg> sent = [.. method.Params.Select((each, i) => Sent(each, args[i], args, method))];
            Tally(standIn.Plan, method);
            RelayReply reply = _link.Call(standIn.Pointer, (uint)slot, sent);
            nint[][] objects = Taken(reply, sent, method, args);
            Write(reply.Hr, reply.Outs, objects, sent, method, args);
            if (scope != AnswerScope.None && reply.Hr == 0)
            {
                Remember(standIn.Owner, key, scope, reply.Outs, objects);
            }
            return reply.Hr;
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
        {
            _log?.Invoke($"A call through the relay failed: {error.Message}");
            return E_FAIL;
        }
    }

    /// <summary>Whether a method's answer can be kept by its inputs: numbers in, values and objects out.</summary>
    internal static bool Rememberable(MethodPlan method) =>
        method.Params.Count(each => each.Kind == ParamKind.Value) <= 3
        && method.Params.All(each => each.Kind is ParamKind.Value or ParamKind.ValueOut or ParamKind.ArrayOut or ParamKind.InterfaceOut);

    private static AnswerKey KeyOf(MethodPlan method, nint* args)
    {
        Span<ulong> inputs = stackalloc ulong[3];
        int at = 0;
        for (int i = 0; i < method.Params.Length && at < 3; i++)
        {
            if (method.Params[i].Kind == ParamKind.Value)
            {
                inputs[at++] = (ulong)args[i];
            }
        }
        return new AnswerKey(method.Declaring, method.Name, inputs[0], inputs[1], inputs[2]);
    }

    private int? Recalled(RemoteObject owner, AnswerKey key, MethodPlan method, nint* args)
    {
        RememberedAnswer? answer;
        lock (_lock)
        {
            if (!owner.Answers.TryGetValue(key, out answer) || (answer.Stop >= 0 && answer.Stop != _stop))
            {
                return null;
            }
            foreach (nint block in answer.Objects.SelectMany(each => each))
            {
                if (RemoteObjects.Of(block) is { } standIn)
                {
                    standIn.Owner.References++;
                }
            }
        }
        List<RelayArg> sent = [.. method.Params.Select((each, i) => Sent(each, args[i], args, method))];
        Write(answer.Hr, answer.Outs, answer.Objects, sent, method, args);
        return answer.Hr;
    }

    private void Remember(RemoteObject owner, AnswerKey key, AnswerScope scope, IReadOnlyList<byte[]> outs, nint[][] objects)
    {
        List<StandIn> held = [.. objects.SelectMany(each => each).Select(RemoteObjects.Of).OfType<StandIn>()];
        lock (_lock)
        {
            if (scope == AnswerScope.Object && held.Any(each => !each.Owner.Kept))
            {
                // An answer that lasts holds no object that would otherwise be let go.
                return;
            }
            foreach (StandIn each in held)
            {
                each.Owner.References++;
                if (scope == AnswerScope.Stop)
                {
                    _heldForTheStop.Add(each);
                }
            }
            owner.Answers[key] = new RememberedAnswer(0, [.. outs], objects, scope == AnswerScope.Stop ? _stop : -1);
        }
    }

    /// <summary>The program runs or is written to: nothing it said while stopped holds, and what those answers held is
    /// let go.</summary>
    public void EndStop()
    {
        List<StandIn> held;
        lock (_lock)
        {
            _stop++;
            held = [.. _heldForTheStop];
            _heldForTheStop.Clear();
        }
        foreach (StandIn each in held)
        {
            Release(each);
        }
    }

    /// <summary>The stand-ins for each argument that brings objects back: one reference each, the caller's.</summary>
    private nint[][] Taken(RelayReply reply, List<RelayArg> sent, MethodPlan method, nint* args)
    {
        List<nint[]> taken = [];
        int objectsAt = 0;
        for (int i = 0; i < sent.Count; i++)
        {
            if (sent[i].Kind != RelayArgKind.Objects)
            {
                continue;
            }
            RemotePointer[] objects = reply.Objects[objectsAt++];
            Type declared = method.Params[i].ElementType;
            InterfacePlan plan = declared == typeof(object) ? RemoteInterfaces.OfIid(*(Guid*)args[0]) ?? RemoteInterfaces.IUnknown : RemoteInterfaces.Of(declared);
            nint[] blocks = new nint[objects.Length];
            for (int j = 0; j < objects.Length; j++)
            {
                if (reply.Hr >= 0)
                {
                    blocks[j] = Take(objects[j], plan);
                }
                else
                {
                    _link.Release(objects[j].Pointer);
                }
            }
            taken.Add(blocks);
        }
        return [.. taken];
    }

    /// <summary>What the app wrote, put where the engine looks for it.</summary>
    private void Write(int hr, IReadOnlyList<byte[]> outs, nint[][] objects, List<RelayArg> sent, MethodPlan method, nint* args)
    {
        int outAt = 0;
        int objectsAt = 0;
        for (int i = 0; i < sent.Count; i++)
        {
            RelayArg arg = sent[i];
            if (arg.Kind is RelayArgKind.Out or RelayArgKind.InOut)
            {
                byte[] written = outs[outAt++];
                if (args[i] != 0)
                {
                    written.CopyTo(new Span<byte>((void*)args[i], written.Length));
                }
                if (method.Params[i].ElementType == typeof(CorDebugExceptionObjectStackFrame))
                {
                    HostModulesOf(new Span<CorDebugExceptionObjectStackFrame>((void*)args[i], written.Length / sizeof(CorDebugExceptionObjectStackFrame)));
                }
            }
            else if (arg.Kind == RelayArgKind.Objects)
            {
                nint[] blocks = objects[objectsAt++];
                nint* into = (nint*)args[i];
                for (int j = 0; j < blocks.Length; j++)
                {
                    into[j] = blocks[j];
                }
            }
        }
    }

    /// <summary>An exception's recorded frames name their modules by the app's pointers, each with its reference: each
    /// becomes its stand-in.</summary>
    private void HostModulesOf(Span<CorDebugExceptionObjectStackFrame> frames)
    {
        InterfacePlan module = RemoteInterfaces.Of(typeof(ICorDebugModule));
        for (int i = 0; i < frames.Length; i++)
        {
            ulong pointer = (ulong)frames[i].pModule;
            if (pointer == 0)
            {
                continue;
            }
            Guid unknown = RemoteInterfaces.IUnknown.Iid;
            RelayReply identity = _link.Call(pointer, 0, [RelayArg.In(unknown.ToByteArray()), RelayArg.Objects(1)]);
            RemotePointer found = identity.Hr >= 0 ? identity.Objects[0][0] : default;
            _link.Release(found.Pointer);
            frames[i].pModule = Take(new RemotePointer(pointer, found.Identity), module);
        }
    }

    private RelayArg Sent(ParamPlan parameter, nint value, nint* args, MethodPlan method)
    {
        switch (parameter.Kind)
        {
            case ParamKind.Value:
                return RelayArg.Number(parameter.ElementSize switch
                {
                    1 => (byte)value,
                    2 => (ushort)value,
                    4 => (uint)value,
                    _ => (ulong)value,
                });
            case ParamKind.Interface:
                return RelayArg.Number(PointerOf(value));
            case ParamKind.String:
                if (value == 0)
                {
                    return RelayArg.Number(0);
                }
                string text = new((char*)value);
                return RelayArg.In(Encoding.Unicode.GetBytes(text + "\0"));
            case ParamKind.InterfaceOut:
                return RelayArg.Objects(1);
            case ParamKind.ValueOut:
                return RelayArg.Out(parameter.ElementSize);
            case ParamKind.ValueInOut:
                return RelayArg.InOut(new ReadOnlySpan<byte>((void*)value, parameter.ElementSize).ToArray());
            case ParamKind.ArrayIn:
            {
                if (value == 0)
                {
                    return RelayArg.Number(0);
                }
                int count = (int)(uint)args[parameter.CountIndex];
                if (parameter.ElementType.IsInterface)
                {
                    byte[] pointers = new byte[count * 8];
                    for (int j = 0; j < count; j++)
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(pointers.AsSpan(j * 8), PointerOf(((nint*)value)[j]));
                    }
                    return RelayArg.In(pointers);
                }
                return RelayArg.In(new ReadOnlySpan<byte>((void*)value, count * parameter.ElementSize).ToArray());
            }
            case ParamKind.ArrayOut:
            {
                if (value == 0)
                {
                    return RelayArg.Number(0);
                }
                int count = (int)(uint)args[parameter.CountIndex];
                return parameter.ElementType.IsInterface ? RelayArg.Objects(count) : RelayArg.Out(count * parameter.ElementSize);
            }
            default:
                throw new InvalidOperationException($"No way to send {parameter.Name} of {method.Name}");
        }
    }

    private static ulong PointerOf(nint standIn)
    {
        if (standIn == 0)
        {
            return 0;
        }
        return RemoteObjects.Of(standIn) is { Pointer: not 0 } known ? known.Pointer
            : RemoteObjects.Of(standIn) is { } unknown ? unknown.Owner.StandIns.Values.FirstOrDefault(each => each.Pointer != 0)?.Pointer ?? 0
            : throw new InvalidOperationException("An object of the host's own was passed to the app");
    }

    /// <summary>Counts the calls carried to the app by method, and says them at each Continue, the end of a stop.</summary>
    private void Tally(InterfacePlan plan, MethodPlan method)
    {
        _tally.AddOrUpdate($"{plan.Type.Name}.{method.Name}", 1, (_, count) => count + 1);
        if (method.Name != nameof(ICorDebugController.TryContinue) || _log is null)
        {
            return;
        }
        (int trips, double waited) = _link.TakeTally();
        KeyValuePair<string, int>[] most = [.. _tally.OrderByDescending(each => each.Value).Take(8)];
        _tally.Clear();
        _log($"Relay since the last Continue: {trips} round trips, {waited:F0} ms waiting, {ObjectsHeld} objects held; most carried: {string.Join(", ", most.Select(each => $"{each.Key} {each.Value}"))}");
    }

    // ---- Callbacks ------------------------------------------------------------------------------------------------

    private static readonly string[][] s_callbackKinds =
    [
        ["ooo", "oooi", "oo", "ooi", "ooo", "ooo", "o", "o", "oo", "oo", "oo", "oo", "oo", "oo", "oii", "ooiss", "ooiiss", "oo", "oo", "oo", "oo", "o", "oo", "ooo", "oooi", "oooi"],
        ["ooooi", "ois", "oi", "oi", "oooiii", "ooii", "ooo", "ooo"],
        ["oo"],
        ["o", "o", "oobi"],
    ];

    private static readonly Type[] s_callbackTypes = [typeof(ICorDebugManagedCallback), typeof(ICorDebugManagedCallback2), typeof(ICorDebugManagedCallback3), typeof(ICorDebugManagedCallback4)];

    private void HoldCallbacks(nint engineCallbacks)
    {
        for (int i = 0; i < 4; i++)
        {
            Guid iid = s_callbackTypes[i].GUID;
            nint found = 0;
            nint* vtable = *(nint**)engineCallbacks;
            int hr = ((delegate* unmanaged[Cdecl]<nint, Guid*, nint*, int>)vtable[0])(engineCallbacks, &iid, &found);
            _callbackInterfaces[i] = hr >= 0 ? found : 0;
        }
    }

    /// <summary>Hands the app's runtime's callbacks to the engine, in order, on a thread of their own.</summary>
    public void HandOnCallbacks()
    {
        Thread callbacks = new(() =>
        {
            foreach (RelayEvent each in _link.Events.GetConsumingEnumerable())
            {
                HandOn(each);
            }
            _log?.Invoke("The app's relay is gone: no more of its runtime's callbacks");
        })
        { IsBackground = true, Name = "Passenger relay callbacks" };
        callbacks.Start();
    }

    private void HandOn(RelayEvent each)
    {
        // A callback is a new stop: what the program said before it no longer holds.
        EndStop();
        int iface = each.Interface - 1;
        nint target = iface is >= 0 and < 4 ? _callbackInterfaces[iface] : 0;
        string kinds = iface is >= 0 and < 4 && each.Slot < s_callbackKinds[iface].Length ? s_callbackKinds[iface][each.Slot] : "";
        MethodPlan? method = kinds.Length > 0 ? RemoteInterfaces.Of(s_callbackTypes[iface]).Methods[each.Slot] : null;
        nint* a = stackalloc nint[7];
        List<nint> copies = [];
        List<nint> taken = [];
        int at = each.Offset;
        byte[] body = each.Body;
        for (int i = 0; i < kinds.Length; i++)
        {
            switch (kinds[i])
            {
                case 'o':
                {
                    RemotePointer pointer = new(BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at)), BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(at + 8)));
                    at += 16;
                    if (method!.Params[i].Kind != ParamKind.Interface)
                    {
                        // A stream of symbols in the app is nothing the engine can read here.
                        _link.Release(pointer.Pointer);
                        a[i] = 0;
                        break;
                    }
                    a[i] = Take(pointer, RemoteInterfaces.Of(method.Params[i].ElementType));
                    taken.Add(a[i]);
                    break;
                }
                case 'i':
                    a[i] = (nint)BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(at));
                    at += 8;
                    break;
                default:
                {
                    int length = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(at));
                    nint copy = (nint)NativeMemory.AllocZeroed((nuint)length + 2);
                    body.AsSpan(at + 4, length).CopyTo(new Span<byte>((void*)copy, length));
                    at += 4 + length;
                    a[i] = copy;
                    copies.Add(copy);
                    break;
                }
            }
        }
        if (target != 0 && method is not null)
        {
            nint* vtable = *(nint**)target;
            ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint, nint, nint, nint, int>)vtable[3 + each.Slot])(target, a[0], a[1], a[2], a[3], a[4], a[5], a[6]);
        }
        foreach (nint copy in copies)
        {
            NativeMemory.Free((void*)copy);
        }
        // The callback's arguments are the caller's: the engine took its own references to what it keeps.
        foreach (nint block in taken)
        {
            ReleaseBlock(block);
        }
    }
}
