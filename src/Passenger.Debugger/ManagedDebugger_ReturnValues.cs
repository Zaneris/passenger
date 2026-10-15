using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using Passenger.Debugger.Interop;
using Passenger.Debugger.Models.Response;
using Passenger.Debugger.PresentationHintModels;

namespace Passenger.Debugger;

/// <summary>What the calls a step ran returned, as Rider lists them at the top of Variables ("Count() returned") and as
/// Visual Studio names them for an expression: $ReturnValue the last, $ReturnValue1 on in the order they returned. ICorDebug
/// reads a call's value only at the instruction after the call (ICorDebugCode3.GetReturnValueLiveOffset,
/// ICorDebugILFrame3.GetReturnValueForILOffset), so a step over puts a breakpoint there for each call of its statement, and
/// a step that returns to a caller reads the call it lands after. Each value is kept by a strong handle, a value type boxed
/// first, as the registers it is read from are the program's again once it runs.</summary>
public partial class ManagedDebugger
{
    /// <summary>A value a call returned: the label of the method, a handle to the value (none for null, or for one that could
    /// not be kept), its type's name for an expression's alias, and what to show for one not kept.</summary>
    private sealed record ReturnedValue(string Method, ICorDebugHandleValue? Value, string TypeName, string AliasType, string? Lost);

    /// <summary>The values the last step's calls returned, in the order they returned, shown at that step's stop on the
    /// thread that stepped and forgotten when the program goes on.</summary>
    private readonly List<ReturnedValue> _returned = [];

    private int? _returnedThread;

    /// <summary>A step over's breakpoints after each call of its statement, where the call's value is still to be read: on
    /// the thread that steps, in the frame it steps in (a recursive call's own frame is another).</summary>
    private sealed record ReturnWatch(int ThreadId, StackRange Frame, IReadOnlyList<WatchedCall> Calls);

    private sealed record WatchedCall(ICorDebugFunctionBreakpoint Breakpoint, int CallOffset, string Method);

    private ReturnWatch? _returnWatch;

    /// <summary>Watches the calls a step over of [start, end) of a frame will make for the values they return.</summary>
    private void WatchReturns(ICorDebugThread thread, ICorDebugILFrame frame, int start, int end)
    {
        EndReturnWatch();
        if (RangeOf(frame) is not { } range || frame is not ICorDebugNativeFrame native || FindModule(frame.Function.Module) is not { } module
            || native.TryGetCode(out ICorDebugCode? code) is not Cor.S_OK || code is not ICorDebugCode3 returns)
        {
            return;
        }
        (int ip, CorDebugMappingResult mapping) = frame.IP;
        List<WatchedCall> calls = [];
        foreach (ModuleMetadataReader.CallSite call in module.MetadataReader.CallSites(frame.Function.Token, start, end, ModuleMetadataReader.VersionOf(frame)))
        {
            // A call before the stop has run already: the one the stop is right after (a step out's) included.
            bool toCome = call.Offset > ip || (call.Offset == ip && mapping.HasFlag(CorDebugMappingResult.MAPPING_EXACT));
            if (call.Kind != ModuleMetadataReader.CallKind.Call || !toCome || module.MetadataReader.DescribeCall(call.Token) is not { ReturnType: not "void" } called
                || IsCompilersOwn(called))
            {
                continue;
            }
            uint[] offsets = new uint[4];
            int found = returns.TryGetReturnValueLiveOffset(checked((uint)call.Offset), (uint)offsets.Length, out uint fetched, offsets);
            if (found is not Cor.S_OK)
            {
                _logger?.Invoke($"No place to read what the call at IL_{call.Offset:X4} returns: 0x{found:X8}");
                continue;
            }
            _logger?.Invoke($"Watching IL_{call.Offset:X4}, {called.Name}, at native {string.Join(", ", offsets.Take((int)Math.Min(fetched, (uint)offsets.Length)))}");
            foreach (uint offset in offsets.Take((int)Math.Min(fetched, (uint)offsets.Length)))
            {
                ICorDebugFunctionBreakpoint breakpoint = code.CreateBreakpoint(checked((int)offset));
                breakpoint.Activate(true);
                calls.Add(new WatchedCall(breakpoint, call.Offset, MethodLabel(called.Name)));
            }
        }
        _returnWatch = calls.Count == 0 ? null : new ReturnWatch(thread.Id, range, calls);
    }

    /// <summary>The call a breakpoint is after, when it is one of the step's.</summary>
    private WatchedCall? WatchedCallOf(ICorDebugFunctionBreakpoint breakpoint) => _returnWatch?.Calls.FirstOrDefault(call => call.Breakpoint == breakpoint);

    /// <summary>Reads what a watched call returned, when the thread is the stepping one, in the frame it steps in. A value
    /// type's box is made by code the debugger runs in the program, after which the step's stepper never stops (it ran on
    /// to the next exception handler): it is put aside first, and a stepper made anew steps on through the rest of the
    /// statement.</summary>
    private async Task TakeWatchedReturn(ICorDebugThread thread, WatchedCall call)
    {
        _logger?.Invoke($"Return-value watch after IL_{call.CallOffset:X4} hit on thread {thread.Id}, expected thread {_returnWatch?.ThreadId}, evaluating={EvalStatus.ThreadId}, async={_asyncStepper?.State}");
        if (_returnWatch is not { } watch || thread.Id != watch.ThreadId || EnumerateFramesForThread(thread).FirstOrDefault() is not ICorDebugILFrame frame
            || RangeOf(frame) != watch.Frame || Read(frame, call.CallOffset) is not { } value)
        {
            _logger?.Invoke($"The breakpoint after the call at IL_{call.CallOffset:X4} is not the step's: another frame's, or no value is there");
            return;
        }
        if (value is ICorDebugReferenceValue || _stepper is not { } stepper)
        {
            await Take(thread, value, call.Method);
            return;
        }
        stepper.Deactivate();
        _stepper = null;
        try
        {
            await Take(thread, value, call.Method);
        }
        finally
        {
            SetupStepper(thread, StepType.StepOver);
        }
    }

    /// <summary>A step that returned to a caller (a step out, or a step past a method's end) stops right after the call that
    /// returned: its value is read there, the latest call of the statement before the stop that has one.</summary>
    private async Task TakeValueReturnedTo(ICorDebugThread thread, ICorDebugILFrame frame)
    {
        if (FindModule(frame.Function.Module) is not { } module)
        {
            return;
        }
        int token = frame.Function.Token;
        (int ip, _) = frame.IP;
        int? version = ModuleMetadataReader.VersionOf(frame);
        if (module.MetadataReader.GetStartAndEndSequencePointIlOffsetsForIlOffset(token, ip, version) is not var (start, _))
        {
            return;
        }
        foreach (ModuleMetadataReader.CallSite call in module.MetadataReader.CallSites(token, start, ip + 1, version).Reverse())
        {
            if (call.Kind == ModuleMetadataReader.CallKind.Call && module.MetadataReader.DescribeCall(call.Token) is { ReturnType: not "void" } called
                && !IsCompilersOwn(called) && await ReadReturnedValue(thread, frame, call.Offset, MethodLabel(called.Name)))
            {
                return;
            }
        }
    }

    /// <summary>Reads and keeps what the call at an IL offset of a frame returned; false when the frame is not right after
    /// that call. The frame is the program's no longer once a value type's box is made.</summary>
    private async Task<bool> ReadReturnedValue(ICorDebugThread thread, ICorDebugILFrame frame, int callOffset, string method)
    {
        if (Read(frame, callOffset) is not { } value)
        {
            return false;
        }
        await Take(thread, value, method);
        return true;
    }

    /// <summary>What the call at an IL offset of a frame returned, read from the registers; null when the frame is not
    /// right after that call.</summary>
    private static ICorDebugValue? Read(ICorDebugILFrame frame, int callOffset) =>
        frame is ICorDebugILFrame3 returns && returns.TryGetReturnValueForILOffset(checked((uint)callOffset), out ICorDebugValue? value) is Cor.S_OK ? value : null;

    private async Task Take(ICorDebugThread thread, ICorDebugValue value, string method)
    {
        if (_returnedThread != thread.Id)
        {
            ForgetReturnedValues();
            _returnedThread = thread.Id;
        }
        _returned.Add(await Keep(value, thread, method));
    }

    /// <summary>A value read from the registers, kept past the stop: a reference by a strong handle to its object, a value
    /// type by one to a box of it made by the program (its bytes read first, as the box's making runs the program).</summary>
    private async Task<ReturnedValue> Keep(ICorDebugValue value, ICorDebugThread thread, string method)
    {
        ICorDebugType type = value.ExactType;
        string typeName = GetCorDebugTypeFriendlyName(type);
        string aliasType = AliasTypeName(type);
        try
        {
            if (value is ICorDebugReferenceValue reference)
            {
                return reference.IsNull
                    ? new ReturnedValue(method, null, typeName, aliasType, null)
                    : new ReturnedValue(method, ((ICorDebugHeapValue2)reference.Dereference()).CreateHandle(CorDebugHandleType.HANDLE_STRONG), typeName, aliasType, null);
            }
            ICorDebugClass boxClass = type.Type is CorElementType.VALUETYPE
                ? type.Class
                : CorElementToValueClassMap.GetValueOrDefault(type.Type) ?? throw new InvalidOperationException($"No class to box a {typeName} in");
            ICorDebugType[] typeArguments = type.TypeParameters;
            byte[] bytes = (value.UnwrapDebugValue() as ICorDebugGenericValue ?? throw new InvalidOperationException($"The {typeName} returned cannot be read whole")).GetValueAsBytes();
            ICorDebugValue boxed = await thread.CreateEval().NewParameterizedObjectNoConstructorAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, boxClass,
                typeArguments.Length, typeArguments.Length == 0 ? null : typeArguments, throwOnException: true) ?? throw new InvalidOperationException($"No box was made for the {typeName} returned");
            ICorDebugHandleValue handle = boxed as ICorDebugHandleValue ?? ((ICorDebugHeapValue2)boxed.UnwrapDebugValue()).CreateHandle(CorDebugHandleType.HANDLE_STRONG);
            ICorDebugGenericValue inside = (ICorDebugGenericValue)handle.UnwrapDebugValue();
            unsafe
            {
                fixed (byte* pointer = bytes)
                {
                    inside.SetValue((nint)pointer);
                }
            }
            return new ReturnedValue(method, handle, typeName, aliasType, null);
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or InvalidCastException or EvalException)
        {
            _logger?.Invoke($"What {method} returned could not be kept: {error.Message}");
            return new ReturnedValue(method, null, typeName, aliasType, $"{{{typeName}}}");
        }
    }

    /// <summary>A call the compiler wrote rather than the user: an await's (GetAwaiter, IsCompleted, GetResult) or an
    /// interpolated string's, whose values no one asked for.</summary>
    private static bool IsCompilersOwn(ModuleMetadataReader.CalledMethod called)
    {
        string type = called.DeclaringType is { } declaring && declaring.IndexOf('<') is int generic and > 0 ? declaring[..generic] : called.DeclaringType ?? "";
        return called.Name == "GetAwaiter" || type.EndsWith("Awaiter", StringComparison.Ordinal) || type.EndsWith("Awaitable", StringComparison.Ordinal)
            || type.EndsWith("MethodBuilder", StringComparison.Ordinal) || type == "DefaultInterpolatedStringHandler";
    }

    /// <summary>The method a call names as its row names it: a property's accessor by the property, a local function by its
    /// own name, anything else by its name and "()".</summary>
    private static string MethodLabel(string name)
    {
        if (name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal))
        {
            return name[4..];
        }
        // A local function: <Containing>g__Name|0_0.
        int local = name.IndexOf(">g__", StringComparison.Ordinal);
        if (local >= 0 && name.IndexOf('|', local) is int end and > 0)
        {
            name = name[(local + 4)..end];
        }
        return $"{name}()";
    }

    /// <summary>The return values' rows, at the top of the frame a step stopped in.</summary>
    private async Task AddReturnedValues(List<VariableInfo> result, ThreadId threadId, FrameStackDepth stackDepth)
    {
        if (stackDepth.Value != 0 || _returnedThread != threadId.Value)
        {
            return;
        }
        for (int i = 0; i < _returned.Count; i++)
        {
            ReturnedValue returned = _returned[i];
            string name = $"{returned.Method} returned";
            string evaluateName = ReturnValueName(i);
            if (returned.Value is not { } handle)
            {
                result.Add(new VariableInfo { Name = name, EvaluateName = evaluateName, Value = returned.Lost ?? "null", Type = returned.TypeName, VariablesReference = 0 });
                continue;
            }
            await WithFailureHandling(result, name, async () =>
            {
                (string? friendlyTypeName, string? value, ICorDebugValue? debuggerProxyInstance, bool resultIsError) = await GetValueForCorDebugValueAsync(handle, threadId, stackDepth, true);
                result.Add(new VariableInfo
                {
                    Name = name,
                    EvaluateName = evaluateName,
                    Value = value,
                    Type = friendlyTypeName,
                    PresentationHint = resultIsError ? new VariablePresentationHint { Attributes = AttributesValue.FailedEvaluation } : null,
                    // The object itself, not the handle, which is the step's to let go, not the stop's.
                    VariablesReference = GetVariablesReference(handle.Dereference(), friendlyTypeName, threadId, stackDepth, debuggerProxyInstance),
                });
            });
        }
    }

    /// <summary>Whether a stopped thread's top frame has return values to show.</summary>
    private bool HasReturnedValues(ThreadId threadId, FrameStackDepth stackDepth) => stackDepth.Value == 0 && _returnedThread == threadId.Value && _returned.Count > 0;

    /// <summary>An expression's name for the i-th value returned (from zero): $ReturnValue for the last, $ReturnValue1 on
    /// for the others, in the order they returned.</summary>
    private string ReturnValueName(int index) => index == _returned.Count - 1 ? "$ReturnValue" : $"$ReturnValue{index + 1}";

    /// <summary>The value an expression's $ReturnValue (0: the last) or $ReturnValueN (from one) names on a thread, and
    /// whether there is one: a null value is there, with no handle.</summary>
    internal bool TryGetReturnedValue(ThreadId threadId, int index, out ICorDebugHandleValue? value)
    {
        value = null;
        if (_returnedThread != threadId.Value || _returned.Count == 0 || index < 0 || index > _returned.Count)
        {
            return false;
        }
        value = _returned[index == 0 ? _returned.Count - 1 : index - 1].Value;
        return true;
    }

    /// <summary>The return values' names and types, for the expression compiler's aliases: $ReturnValue, then $ReturnValue1 on.</summary>
    internal IReadOnlyList<(string Name, string Type)> ReturnValueAliases(ThreadId threadId) => _returnedThread != threadId.Value || _returned.Count == 0
        ? []
        : [("$ReturnValue", _returned[^1].AliasType), .. _returned.Select((returned, i) => ($"$ReturnValue{i + 1}", returned.AliasType))];

    /// <summary>A $ReturnValue name's index (0 for $ReturnValue), any letter case, as Roslyn's expression compiler reads it.</summary>
    internal static int? ReturnValueIndex(string expression)
    {
        const string name = "$ReturnValue";
        if (!expression.StartsWith(name, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string rest = expression[name.Length..];
        return rest.Length == 0 ? 0 : rest.All(char.IsAsciiDigit) && int.TryParse(rest, out int index) && index > 0 ? index : null;
    }

    private void EndReturnWatch()
    {
        if (_returnWatch is not { } watch)
        {
            return;
        }
        _returnWatch = null;
        foreach (WatchedCall call in watch.Calls)
        {
            call.Breakpoint.TryActivate(false);
        }
    }

    /// <summary>Lets go of the last step's return values, as the program goes on from its stop.</summary>
    private void ForgetReturnedValues()
    {
        foreach (ReturnedValue returned in _returned)
        {
            returned.Value?.TryDispose();
        }
        _returned.Clear();
        _returnedThread = null;
    }

    /// <summary>A type's assembly-qualified name, as the expression compiler takes an alias's type: the program's own
    /// assemblies by their full names, a generic type's arguments within, object for what has no such name.</summary>
    internal string AliasTypeName(ICorDebugType type)
    {
        try
        {
            (string name, string assembly) = ReflectionName(type);
            return $"{name}, {assembly}";
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or BadImageFormatException)
        {
            return typeof(object).AssemblyQualifiedName!;
        }
    }

    private (string Name, string Assembly) ReflectionName(ICorDebugType type)
    {
        string? primitive = type.Type switch
        {
            CorElementType.BOOLEAN => "System.Boolean",
            CorElementType.CHAR => "System.Char",
            CorElementType.I1 => "System.SByte",
            CorElementType.U1 => "System.Byte",
            CorElementType.I2 => "System.Int16",
            CorElementType.U2 => "System.UInt16",
            CorElementType.I4 => "System.Int32",
            CorElementType.U4 => "System.UInt32",
            CorElementType.I8 => "System.Int64",
            CorElementType.U8 => "System.UInt64",
            CorElementType.R4 => "System.Single",
            CorElementType.R8 => "System.Double",
            CorElementType.I => "System.IntPtr",
            CorElementType.U => "System.UIntPtr",
            CorElementType.STRING => "System.String",
            CorElementType.OBJECT => "System.Object",
            _ => null,
        };
        if (primitive is not null)
        {
            ModuleInfo coreLib = _modules.Values.FirstOrDefault(module => module.ModuleName == "System.Private.CoreLib.dll")
                ?? throw new InvalidOperationException("CoreLib is not loaded");
            return (primitive, AssemblyName(coreLib));
        }
        if (type.Type is CorElementType.SZARRAY or CorElementType.ARRAY)
        {
            (string element, string assembly) = ReflectionName(type.FirstTypeParameter);
            return (type.Type is CorElementType.SZARRAY ? $"{element}[]" : $"{element}[{new string(',', type.Rank - 1)}]", assembly);
        }
        if (type.Type is not (CorElementType.CLASS or CorElementType.VALUETYPE))
        {
            throw new InvalidOperationException($"No reflection name for a {type.Type}");
        }
        ICorDebugClass @class = type.Class;
        ModuleInfo module = FindModule(@class.Module) ?? throw new InvalidOperationException(ModuleWithoutMetadata);
        MetadataReader reader = module.MetadataReader.PeMetadataReader;
        string name = NestedName(reader, (TypeDefinitionHandle)System.Reflection.Metadata.Ecma335.MetadataTokens.Handle(@class.Token));
        ICorDebugType[] arguments = type.TypeParameters;
        if (arguments.Length > 0)
        {
            name += $"[{string.Join(",", arguments.Select(argument => ReflectionName(argument)).Select(argument => $"[{argument.Name}, {argument.Assembly}]"))}]";
        }
        return (name, AssemblyName(module));

        static string NestedName(MetadataReader reader, TypeDefinitionHandle handle)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            string own = reader.GetString(definition.Name);
            if (!definition.GetDeclaringType().IsNil)
            {
                return $"{NestedName(reader, definition.GetDeclaringType())}+{own}";
            }
            string @namespace = reader.GetString(definition.Namespace);
            return @namespace.Length == 0 ? own : $"{@namespace}.{own}";
        }

        static string AssemblyName(ModuleInfo module)
        {
            MetadataReader reader = module.MetadataReader.PeMetadataReader;
            return reader.IsAssembly ? reader.GetAssemblyDefinition().GetAssemblyName().FullName : module.ModuleName;
        }
    }
}
