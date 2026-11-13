using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.Relay;

/// <summary>The calls a stand-in answers here rather than in the app.</summary>
internal sealed unsafe partial class RelaySession
{
    private readonly ConcurrentDictionary<ulong, string?> _moduleFiles = new();
    private readonly ConcurrentDictionary<nint, StandIn> _moduleOfScope = new();

    /// <summary>The engine's callbacks, which stay in the IDE; a module's name, place and metadata, read on the host; a
    /// generic value's bytes, as many as its size; a thread's or a stack walk's context, as many bytes as asked. A call
    /// with any other argument no plan can size is refused rather than sent.</summary>
    private int? Answered(StandIn standIn, MethodPlan method, nint* args)
    {
        Type type = standIn.Plan.Type;
        if (type == typeof(ICorDebug) && method.Name == nameof(ICorDebug.TrySetManagedHandler))
        {
            HoldCallbacks(args[0]);
            return _link.Call(standIn.Pointer, 5, [RelayArg.Number(_link.Callback)]).Hr;
        }
        if (type == typeof(ICorDebugModule))
        {
            string? file = HostFileOf(standIn);
            switch (method.Name)
            {
                case nameof(ICorDebugModule.TryGetName) when file is not null:
                    return WriteName(file, (uint)args[0], (uint*)args[1], (char*)args[2]);
                case nameof(ICorDebugModule.TryIsInMemory) when file is not null:
                    *(int*)args[0] = 0;
                    return 0;
                case nameof(ICorDebugModule.TryGetMetaDataInterface):
                {
                    int opened = file is not null ? _modules.OpenMetadata(file, (Guid*)args[0], (nint*)args[1]) : MetadataFromApp(standIn, (Guid*)args[0], (nint*)args[1]);
                    if (opened >= 0)
                    {
                        _moduleOfScope[*(nint*)args[1]] = standIn;
                    }
                    return opened;
                }
            }
        }
        if (type == typeof(ICorDebugAppDomain) && method.Name == nameof(ICorDebugAppDomain.TryGetModuleFromMetaDataInterface))
        {
            *(nint*)args[1] = 0;
            if (!_moduleOfScope.TryGetValue(args[0], out StandIn? module))
            {
                return E_INVALIDARG;
            }
            AddRef(module);
            *(nint*)args[1] = module.Block;
            return 0;
        }
        if (type == typeof(ICorDebugGenericValue) && method.Name is nameof(ICorDebugGenericValue.TryGetValue) or nameof(ICorDebugGenericValue.TrySetValue))
        {
            RelayReply size = _link.Call(standIn.Pointer, SlotOf(typeof(ICorDebugGenericValue), nameof(ICorDebugValue.TryGetSize)), [RelayArg.Out(4)]);
            if (size.Hr < 0)
            {
                return size.Hr;
            }
            int bytes = BinaryPrimitives.ReadInt32LittleEndian(size.Outs[0]);
            uint slot = SlotOf(typeof(ICorDebugGenericValue), method.Name);
            if (method.Name == nameof(ICorDebugGenericValue.TryGetValue))
            {
                RelayReply got = _link.Call(standIn.Pointer, slot, [RelayArg.Out(bytes)]);
                got.Outs[0].CopyTo(new Span<byte>((void*)args[0], bytes));
                return got.Hr;
            }
            return _link.Call(standIn.Pointer, slot, [RelayArg.In(new ReadOnlySpan<byte>((void*)args[0], bytes).ToArray())]).Hr;
        }
        if (type == typeof(ICorDebugProcess) && method.Name is nameof(ICorDebugProcess.TryGetThreadContext) or nameof(ICorDebugProcess.TrySetThreadContext))
        {
            int size = (int)(uint)args[1];
            byte* context = (byte*)args[2];
            RelayArg room = method.Name == nameof(ICorDebugProcess.TryGetThreadContext) ? RelayArg.Out(size) : RelayArg.InOut(new ReadOnlySpan<byte>(context, size).ToArray());
            RelayReply reply = _link.Call(standIn.Pointer, SlotOf(type, method.Name), [RelayArg.Number((uint)args[0]), RelayArg.Number((uint)size), room]);
            if (reply.Hr >= 0)
            {
                reply.Outs[0].AsSpan(0, Math.Min(size, reply.Outs[0].Length)).CopyTo(new Span<byte>(context, size));
            }
            return reply.Hr;
        }
        if (type == typeof(ICorDebugStackWalk) && method.Name == nameof(ICorDebugStackWalk.TryGetContext))
        {
            int room = (int)(uint)args[1];
            RelayReply reply = _link.Call(standIn.Pointer, SlotOf(type, method.Name), [RelayArg.Number((uint)args[0]), RelayArg.Number((uint)room), RelayArg.Out(4), RelayArg.Out(room)]);
            if (args[2] != 0)
            {
                *(uint*)args[2] = BinaryPrimitives.ReadUInt32LittleEndian(reply.Outs[0]);
            }
            if (reply.Hr >= 0 && args[3] != 0)
            {
                reply.Outs[1].CopyTo(new Span<byte>((void*)args[3], room));
            }
            return reply.Hr;
        }
        if (type == typeof(ICorDebugStackWalk) && method.Name == nameof(ICorDebugStackWalk.TrySetContext))
        {
            int size = (int)(uint)args[1];
            return _link.Call(standIn.Pointer, SlotOf(type, method.Name), [RelayArg.Number((uint)args[0]), RelayArg.Number((uint)size), RelayArg.In(new ReadOnlySpan<byte>((void*)args[2], size).ToArray())]).Hr;
        }
        return null;
    }

    internal static uint SlotOf(Type type, string method) =>
        (uint)(3 + Array.FindIndex(RemoteInterfaces.Of(type).Methods, each => each.Name == method));

    /// <summary>The host's copy of a module, found by the name the app's runtime gives it.</summary>
    private string? HostFileOf(StandIn module) => _moduleFiles.GetOrAdd(module.Owner.Identity, _ =>
    {
        RelayReply reply = _link.Call(module.Pointer, SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryGetName)), [RelayArg.Number(1024), RelayArg.Out(4), RelayArg.Out(2048)]);
        if (reply.Hr < 0)
        {
            return null;
        }
        int length = Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(reply.Outs[0]) - 1);
        string onDevice = Encoding.Unicode.GetString(reply.Outs[1], 0, Math.Min(length * 2, reply.Outs[1].Length));
        string? found = _modules.HostFileOf(onDevice);
        _log?.Invoke($"Module {onDevice} is {found ?? "not on the host: its metadata is read from the app's memory"}");
        return found;
    });

    private static int WriteName(string file, uint capacity, uint* written, char* into)
    {
        if (written is not null)
        {
            *written = (uint)file.Length + 1;
        }
        if (into is not null && capacity > 0)
        {
            int count = (int)Math.Min(capacity - 1, (uint)file.Length);
            file.AsSpan(0, count).CopyTo(new Span<char>(into, count));
            into[count] = '\0';
        }
        return 0;
    }

    /// <summary>The metadata of a module the host has no copy of (loaded from bytes, or from somewhere the build did not
    /// put it), read out of its image in the app's memory and opened on the host, so no metadata pointer of the app's
    /// reaches the engine. A dynamic module has no image to read.</summary>
    private int MetadataFromApp(StandIn module, Guid* iid, nint* result)
    {
        *result = 0;
        uint isDynamic = SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryIsDynamic));
        RelayReply dynamic = _link.Call(module.Pointer, isDynamic, [RelayArg.Out(4)]);
        if (dynamic.Hr < 0 || BinaryPrimitives.ReadInt32LittleEndian(dynamic.Outs[0]) != 0)
        {
            return E_NOTIMPL;
        }
        RelayReply where = _link.Call(module.Pointer, SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryGetBaseAddress)), [RelayArg.Out(8)]);
        RelayReply size = _link.Call(module.Pointer, SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryGetSize)), [RelayArg.Out(4)]);
        RelayReply process = _link.Call(module.Pointer, SlotOf(typeof(ICorDebugModule), nameof(ICorDebugModule.TryGetProcess)), [RelayArg.Objects(1)]);
        ulong processPointer = process.Hr >= 0 ? process.Objects[0][0].Pointer : 0;
        try
        {
            if (where.Hr < 0 || size.Hr < 0 || processPointer == 0)
            {
                return E_FAIL;
            }
            ulong image = BinaryPrimitives.ReadUInt64LittleEndian(where.Outs[0]);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(size.Outs[0]);
            byte[]? metadata = ModuleImages.Metadata(image, length, (address, count) => ReadMemory(processPointer, address, count));
            if (metadata is null)
            {
                _log?.Invoke($"No metadata found in the module's image at 0x{image:x} ({length} bytes)");
                return E_FAIL;
            }
            return _modules.OpenMetadataInMemory(module.Owner.Identity, metadata, iid, result);
        }
        finally
        {
            _link.Release(processPointer);
        }
    }

    private byte[]? ReadMemory(ulong process, ulong address, int count)
    {
        uint slot = SlotOf(typeof(ICorDebugProcess), nameof(ICorDebugProcess.TryReadMemory));
        RelayReply read = _link.Call(process, slot, [RelayArg.Number(address), RelayArg.Number((uint)count), RelayArg.Out(count), RelayArg.Out(8)]);
        return read.Hr >= 0 && BinaryPrimitives.ReadUInt64LittleEndian(read.Outs[1]) == (ulong)count ? read.Outs[0] : null;
    }

    // ---- Enumerators ----------------------------------------------------------------------------------------------

    /// <summary>An enumerator's Next read many elements at a time, and its Skip, Reset and Clone kept true to what it
    /// holds; null for a call carried as any other.</summary>
    private int? Enumerated(StandIn standIn, int slot, MethodPlan method, nint* args)
    {
        RemoteObject owner = standIn.Owner;
        EnumBuffer<nint> buffer;
        lock (_lock)
        {
            buffer = owner.Enumeration ??= new EnumBuffer<nint>();
        }
        switch (method.Name)
        {
            case nameof(ICorDebugValueEnum.TryNext) when method.Params is [_, { Kind: ParamKind.ArrayOut, ElementType.IsInterface: true } elements, _]:
            {
                InterfacePlan plan = RemoteInterfaces.Of(elements.ElementType);
                int count = (int)(uint)args[0];
                int fetched;
                int result;
                lock (owner.Gate)
                {
                    result = buffer.Next(count, new Span<nint>((void*)args[1], count), out fetched, (want, into) => Fetch(standIn, slot, plan, want, into), EnumBatch);
                }
                if (args[2] != 0)
                {
                    *(uint*)args[2] = (uint)fetched;
                }
                return result;
            }
            case nameof(ICorDebugEnum.TrySkip):
                lock (owner.Gate)
                {
                    return buffer.Skip((int)(uint)args[0], ReleaseBlock, rest => _link.Call(standIn.Pointer, (uint)slot, [RelayArg.Number((uint)rest)]).Hr);
                }
            case nameof(ICorDebugEnum.TryReset):
                lock (owner.Gate)
                {
                    buffer.Clear(ReleaseBlock);
                    return _link.Call(standIn.Pointer, (uint)slot, []).Hr;
                }
            case nameof(ICorDebugEnum.TryClone):
            {
                RelayReply reply = _link.Call(standIn.Pointer, (uint)slot, [RelayArg.Objects(1)]);
                nint clone = reply.Hr >= 0 ? Take(reply.Objects[0][0], RemoteInterfaces.Of(typeof(ICorDebugEnum))) : 0;
                if (RemoteObjects.Of(clone) is { } cloned)
                {
                    lock (owner.Gate)
                    {
                        EnumBuffer<nint> copy;
                        lock (_lock)
                        {
                            copy = cloned.Owner.Enumeration ??= new EnumBuffer<nint>();
                        }
                        buffer.CopyTo(copy, block =>
                        {
                            AddRefBlock(block);
                            return block;
                        });
                    }
                }
                *(nint*)args[0] = clone;
                return reply.Hr;
            }
            default:
                return null;
        }
    }

    private int Fetch(StandIn enumerator, int slot, InterfacePlan plan, int want, List<nint> into)
    {
        _tally.AddOrUpdate($"{enumerator.Plan.Type.Name}.TryNext", 1, (_, count) => count + 1);
        RelayReply reply = _link.Call(enumerator.Pointer, (uint)slot, [RelayArg.Number((uint)want), RelayArg.Objects(want), RelayArg.Out(4)]);
        int fetched = BinaryPrimitives.ReadInt32LittleEndian(reply.Outs[0]);
        RemotePointer[] pointers = reply.Objects[0];
        for (int i = 0; i < pointers.Length; i++)
        {
            if (i < fetched && pointers[i].Pointer != 0)
            {
                into.Add(Take(pointers[i], plan));
            }
            else
            {
                _link.Release(pointers[i].Pointer);
            }
        }
        return reply.Hr;
    }
}
