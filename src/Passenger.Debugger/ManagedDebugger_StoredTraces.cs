using System.Buffers.Binary;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices.Marshalling;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>An exception's stack trace as its StackTrace property gives it, read from what the exception stores, running no
/// code in the program: the text it was given (deserialized, or set by the program) and the trace of an earlier throw
/// (rethrown, as an await rethrows) as they are, and the frames the runtime recorded as it was thrown (their methods
/// through the runtime's own reading of them, their addresses and flags from the stored bytes), each placed in the code
/// it ran, at that code's version, and written as <see cref="StackTraceText"/> writes them.</summary>
public partial class ManagedDebugger
{
    /// <summary>Set on a stored frame whose address the runtime already moved into the instruction that called.</summary>
    private const int FrameAddressAdjusted = 0x2;

    /// <summary>Set on a stored frame of a runtime-async method going on, whose address is where it goes on.</summary>
    private const int FrameContinuation = 0x8;

    /// <summary>What the exception's StackTrace gives (null for one never thrown), read from what it stores; false where
    /// that cannot be read whole (a type that writes its own, a frame whose code is gone), so its getter must say it.</summary>
    private bool TryStoredStackTrace(ICorDebugObjectValue exception, ICorDebugThread? thread, out string? trace)
    {
        trace = null;
        try
        {
            if (Declares(exception.ExactType, "get_StackTrace"))
            {
                return false;
            }
            string? earlier = StoredText(exception, "_remoteStackTraceString");
            if (StoredText(exception, "_stackTraceString") is { } given)
            {
                trace = earlier + given;
                return true;
            }
            if (ExceptionField(exception, "_stackTrace") is not ICorDebugReferenceValue { IsNull: false } stored)
            {
                trace = earlier;
                return true;
            }
            if (StoredFrames(exception, stored, thread) is not { } frames)
            {
                _logger?.Invoke($"The stack trace a {GetCorDebugTypeFriendlyName(exception.ExactType)} stores was not read whole: its getter gives it");
                return false;
            }
            trace = earlier + StackTraceText.Of(frames);
            return true;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            _logger?.Invoke($"The exception's stored stack trace could not be read: {error.Message}");
            return false;
        }
    }

    /// <summary>An exception's ToString as System.Exception's writes it, its inner exceptions' within it, from what each
    /// stores (an overridden Message by its getter, while code may run); null where a type in the chain writes its own
    /// ToString, or a stack trace cannot be read, so that ToString must run.</summary>
    private async Task<string?> ExceptionTextAsync(ICorDebugValue exception, ThreadId threadId, FrameStackDepth depth)
    {
        List<ThrownException> chain;
        try
        {
            chain = StoredChain(exception, _process!.GetThread(threadId.Value));
        }
        catch (Exception error) when (error is InvalidOperationException || Failures.OfRuntime(error))
        {
            return null;
        }
        try
        {
            // Past the chain's depth its innermost exceptions are not read.
            if (chain.Count > 10 || chain.Any(e => e.OwnText || !e.TraceRead))
            {
                return null;
            }
            foreach (ThrownException each in chain.Where(e => e.OwnMessage))
            {
                each.Message = await OwnMessageAsync(each, threadId, depth) ?? each.Message;
            }
            string? text = null;
            for (int at = chain.Count - 1; at >= 0; at--)
            {
                ThrownException each = chain[at];
                string message = each.OwnMessage ? each.Message : each.MadeMessage ?? $"Exception of type '{each.ClassName}' was thrown.";
                text = each.ClassName + (message.Length > 0 ? $": {message}" : "")
                    + (text is null ? "" : $"{Environment.NewLine} ---> {text}{Environment.NewLine}   --- End of inner exception stack trace ---")
                    + (each.StackTrace is { } trace ? Environment.NewLine + trace : "");
            }
            return text;
        }
        finally
        {
            foreach (ThrownException each in chain)
            {
                each.Held.TryDispose();
            }
        }
    }

    /// <summary>What an exception's own Message getter says, null where it does not run (implicit evaluation is off) or fails.</summary>
    private async Task<string?> OwnMessageAsync(ThrownException exception, ThreadId threadId, FrameStackDepth depth)
    {
        if (EvalStatus.ImplicitOff is not null && !EvalStatus.Explicit)
        {
            return null;
        }
        ICorDebugValue? value;
        try
        {
            value = await exception.Held.GetPropertyValue(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, GetIlFrameForThreadIdAndStackDepth(threadId, depth), "Message");
        }
        catch (Exception error) when (error is EvalException || Failures.OfRuntime(error))
        {
            return null;
        }
        try
        {
            return value?.UnwrapDebugValue() is ICorDebugStringValue text ? text.String : null;
        }
        finally
        {
            if (value is ICorDebugHandleValue handle)
            {
                handle.TryDispose();
            }
        }
    }

    /// <summary>A type's name as reflection's Type.ToString writes it: nested types joined by '+', type arguments in brackets.</summary>
    private static string TypeToString(ICorDebugType type)
    {
        switch (type.Type)
        {
            case CorElementType.SZARRAY:
                return TypeToString(type.FirstTypeParameter) + "[]";
            case CorElementType.ARRAY:
                return $"{TypeToString(type.FirstTypeParameter)}[{new string(',', type.Rank - 1)}]";
            case CorElementType.PTR:
                return TypeToString(type.FirstTypeParameter) + "*";
            case CorElementType.BYREF:
                return TypeToString(type.FirstTypeParameter) + "&";
        }
        if (MetadataTypeNames.PrimitiveOf(type.Type) is { } primitive)
        {
            return primitive.SystemName;
        }
        ICorDebugClass declared = type.Class;
        string name = MetadataTypeNames.Of(declared.Module.GetMetaDataInterface<IMetaDataImport>(), declared.Token);
        ICorDebugType[] arguments = type.TypeParameters;
        return arguments.Length == 0 ? name : $"{name}[{string.Join(",", arguments.Select(argument => TypeToString(argument)))}]";
    }

    private static string? StoredText(ICorDebugObjectValue exception, string field) =>
        ExceptionField(exception, field)?.UnwrapDebugValue() is ICorDebugStringValue text ? text.String : null;

    /// <summary>Whether a type below System.Exception declares a method of this name, overriding Exception's.</summary>
    private static bool Declares(ICorDebugType type, string method)
    {
        for (ICorDebugType? each = type; each is not null; each = each.Base)
        {
            ICorDebugClass declaring = each.Class;
            IMetaDataImport metadata = declaring.Module.GetMetaDataInterface<IMetaDataImport>();
            if (GetCorDebugTypeFriendlyName(each) == "System.Exception")
            {
                return false;
            }
            if (metadata.EnumMethodsWithName(declaring.Token, method).Any())
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The frames an exception stores, each with its line where its module has symbols of its own; null where any
    /// cannot be read, or one with symbols cannot be placed in code the program still has.</summary>
    private List<TracedFrame>? StoredFrames(ICorDebugObjectValue exception, ICorDebugReferenceValue stored, ICorDebugThread? thread)
    {
        if (CoreLib is not { } coreLib || StoredElements(stored, coreLib.MetadataReader.PointerSize) is not { } elements
            || RecordedFrames(exception) is not { } recorded || recorded.Count != elements.Count)
        {
            return null;
        }
        List<(ICorDebugCode Code, ulong Start, ulong End)>? live = null;
        List<TracedFrame> frames = [];
        for (int at = 0; at < recorded.Count; at++)
        {
            (ICorDebugModule corModule, ulong ip, int token, bool lastOfEarlier) = recorded[at];
            (ulong storedIp, int flags) = elements[at];
            // The runtime's reading moves the newest frame's address back by one on x64.
            if (storedIp != ip && storedIp != ip + 1)
            {
                _logger?.Invoke($"The exception's stored frame {at} is at 0x{storedIp:x}, where the runtime reads 0x{ip:x}");
                return null;
            }
            // A method of no metadata of its own (a dynamic method, a stub) is written only by the getter.
            if (FindModule(corModule) is not { } module || !Defines(module, token))
            {
                return null;
            }
            (string File, int Line)? source = null;
            if (module.MetadataReader.HasSymbols && !module.SymbolsFromDecompiled)
            {
                live ??= LiveCode(thread);
                if (CodeAt(storedIp, corModule, token, live) is not { } code)
                {
                    _logger?.Invoke($"The code of the exception's stored frame {at} (0x{token:x8} in {module.ModuleName}) is not the program's now");
                    return null;
                }
                ulong offset = storedIp - code.Start;
                bool adjust = (flags & (FrameAddressAdjusted | FrameContinuation)) == 0 && offset > 0;
                int? version = code.Code.TryGetVersionNumber(out uint number) is Cor.S_OK ? (int)number : null;
                source = CompiledOffsets.ILAt(code.Code, checked((uint)(adjust ? offset - 1 : offset))) is { } il ? PlaceOf(module, token, il, version) : null;
            }
            frames.Add(new TracedFrame(module.MetadataReader.PeMetadataReader, token, source, lastOfEarlier));
        }
        return frames;
    }

    /// <summary>Whether a token names a method the module's metadata defines.</summary>
    private static bool Defines(ModuleInfo module, int token) =>
        token >>> 24 == 0x06 && (token & 0xFFFFFF) is int row && row > 0
        && row <= module.MetadataReader.PeMetadataReader.GetTableRowCount(TableIndex.MethodDef);

    /// <summary>Where a method's code at a version is written at an IL offset: a statement Hot Reload has moved since, where
    /// it now is, as the frame's own place shows it.</summary>
    private (string File, int Line)? PlaceOf(ModuleInfo module, int token, int il, int? version)
    {
        if (module.MetadataReader.GetSourceLocationForOffset(token, il, version) is not { } place)
        {
            return null;
        }
        return version is int at && _statementUpdates.TryGetValue((module.MetadataReader.Mvid, token, at, il), out StatementUpdate? moved)
            ? (place.sourceFilePath, moved.StartLine + 1)
            : (place.sourceFilePath, place.startLine);
    }

    /// <summary>The stored trace's addresses and flags, from its bytes: a header (its count in the first four), then each
    /// frame's address, stack pointer, method and flags, a pointer each. The trace is the byte array, or an object array
    /// holding it first. Null for one shaped otherwise.</summary>
    private List<(ulong Ip, int Flags)>? StoredElements(ICorDebugReferenceValue stored, int pointerSize)
    {
        ICorDebugValue held = stored.Dereference();
        if (held is ICorDebugArrayValue { Count: > 0 } objects && objects.ExactType.FirstTypeParameter.Type is CorElementType.CLASS or CorElementType.OBJECT)
        {
            held = objects.GetElementAtPosition(0) is ICorDebugReferenceValue { IsNull: false } first ? first.Dereference() : held;
        }
        if (held is not ICorDebugArrayValue { Count: > 0 } array || array.ExactType.FirstTypeParameter.Type is not (CorElementType.I1 or CorElementType.U1))
        {
            return null;
        }
        (byte[] bytes, nuint read) = _process!.ReadMemory(array.GetElementAtPosition(0).Address, array.Count);
        int header = pointerSize == 8 ? 16 : ExpressionEvaluator.Cil.CilValue.RuntimeVersion >= 9 ? 12 : 8;
        int element = 4 * pointerSize;
        if ((int)read < 4 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) is var count && header + (long)count * element > (long)read)
        {
            return null;
        }
        List<(ulong Ip, int Flags)> elements = [];
        for (int at = 0; at < count; at++)
        {
            ReadOnlySpan<byte> each = bytes.AsSpan(header + at * element, element);
            ulong ip = pointerSize == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(each) : BinaryPrimitives.ReadUInt32LittleEndian(each);
            elements.Add((ip, BinaryPrimitives.ReadInt32LittleEndian(each[(3 * pointerSize)..])));
        }
        return elements;
    }

    /// <summary>The frames the runtime reads the exception to store: each one's module, address, method and whether it ends
    /// an earlier throw's trace. Null where it will not read them.</summary>
    private static unsafe List<(ICorDebugModule Module, ulong Ip, int Token, bool LastOfEarlier)>? RecordedFrames(ICorDebugObjectValue exception)
    {
        if (exception is not ICorDebugExceptionObjectValue thrown || thrown.TryEnumerateExceptionCallStack(out ICorDebugExceptionObjectCallStackEnum? stack) is not Cor.S_OK
            || stack is null || stack.TryGetCount(out uint count) is not Cor.S_OK)
        {
            return null;
        }
        CorDebugExceptionObjectStackFrame[] frames = new CorDebugExceptionObjectStackFrame[count];
        if (count > 0 && (stack.TryNext(count, frames, out uint fetched) is not (Cor.S_OK or 1) || fetched != count))
        {
            Release(frames);
            return null;
        }
        List<(ICorDebugModule Module, ulong Ip, int Token, bool LastOfEarlier)> recorded = [];
        try
        {
            foreach (CorDebugExceptionObjectStackFrame frame in frames)
            {
                if (frame.pModule == 0 || ComInterfaceMarshaller<ICorDebugModule>.ConvertToManaged((void*)frame.pModule) is not { } module)
                {
                    return null;
                }
                recorded.Add((module, frame.ip.Value, (int)frame.methodDef, frame.isLastForeignExceptionFrame != 0));
            }
            return recorded;
        }
        finally
        {
            Release(frames);
        }

        static void Release(CorDebugExceptionObjectStackFrame[] frames)
        {
            foreach (CorDebugExceptionObjectStackFrame frame in frames)
            {
                if (frame.pModule != 0)
                {
                    ComInterfaceMarshaller<ICorDebugModule>.Free((void*)frame.pModule);
                }
            }
        }
    }

    /// <summary>The compiled code of the thread's frames, where each one's code lies: the code a stored frame on the stack
    /// still is in, whatever its version or instantiation.</summary>
    private static List<(ICorDebugCode Code, ulong Start, ulong End)> LiveCode(ICorDebugThread? thread)
    {
        List<(ICorDebugCode Code, ulong Start, ulong End)> live = [];
        if (thread is null)
        {
            return live;
        }
        foreach (ICorDebugFrame frame in EnumerateFramesForThread(thread))
        {
            if (frame is ICorDebugNativeFrame native && native.TryGetCode(out ICorDebugCode? code) is Cor.S_OK && code is not null && SpanOf(code) is { } span)
            {
                live.Add((code, span.Start, span.End));
            }
        }
        return live;
    }

    /// <summary>The compiled code holding an address: one of the thread's frames', else the method's own as the runtime has
    /// compiled it now; null where neither holds it.</summary>
    private static (ICorDebugCode Code, ulong Start)? CodeAt(ulong ip, ICorDebugModule module, int token, List<(ICorDebugCode Code, ulong Start, ulong End)> live)
    {
        foreach ((ICorDebugCode code, ulong start, ulong end) in live)
        {
            if (start <= ip && ip < end)
            {
                return (code, start);
            }
        }
        return module.TryGetFunctionFromToken(token, out ICorDebugFunction? function) is Cor.S_OK && function is not null
            && function.TryGetNativeCode(out ICorDebugCode? current) is Cor.S_OK && current is not null
            && SpanOf(current) is { } span && span.Start <= ip && ip < span.End
                ? (current, span.Start)
                : null;
    }

    private static (ulong Start, ulong End)? SpanOf(ICorDebugCode code) =>
        code.TryIsIL(out bool il) is Cor.S_OK && !il && code.TryGetAddress(out CORDB_ADDRESS start) is Cor.S_OK && code.TryGetSize(out uint size) is Cor.S_OK
            ? (start.Value, start.Value + size)
            : null;
}
