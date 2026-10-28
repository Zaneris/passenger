using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using Passenger.Debugger.ExpressionEvaluator.Compiler;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;

internal readonly record struct CilInterpretationResult(ICorDebugValue Value, ICorDebugHandleValue? OwnedResultHandle);

internal sealed class CilInterpreter(ManagedDebugger debugger, RuntimeAssemblyPrimitiveTypeClasses primitiveTypes)
{
    /// <summary>What an expression stopped before it finished says.</summary>
    internal const string TookTooLong = "The expression took too long to evaluate";

    public async Task<CilInterpretationResult> InterpretAsync(CompiledEvaluationMethod compiled, CompiledExpressionEvaluationContext context, Lazy<DelegateMaterializerAssembly> delegateMaterializer)
    {
        using EvaluationHandleScope handles = new EvaluationHandleScope();
        MethodDefinition method = compiled.MetadataReader.GetMethodDefinition(compiled.EntryMethod);
        MethodBodyBlock body = compiled.PeReader.GetMethodBody(method.RelativeVirtualAddress);
        DecodedMethod decoded = compiled.GetDecodedMethod(compiled.EntryMethod);
        ICorDebugILFrame? frame = context.RootValue is null ? debugger.GetIlFrameForThreadIdAndStackDepth(context.ThreadId, context.StackDepth) : null;
        Func<ICorDebugILFrame> refind = () => debugger.GetIlFrameForThreadIdAndStackDepth(context.ThreadId, context.StackDepth);
        EvaluationMetadataResolver resolver = CreateResolver(compiled, context, frame);
        ICilLocation[] arguments = CreateArguments(frame, context, refind);
        ICilLocation[] locals = CreateLocals(compiled, resolver, frame, body.LocalSignature, context.RootValue is not null, refind);
        await using DebuggeeDelegateAssemblyLoader delegateAssemblyLoader = new DebuggeeDelegateAssemblyLoader(debugger, compiled, resolver, context, handles, delegateMaterializer);
        Interpretation run = new Interpretation(compiled, resolver, context, handles, delegateAssemblyLoader, Stopwatch.GetTimestamp() + (long)(Extensions.EvaluationTimeout.TotalSeconds * Stopwatch.Frequency),
            debugger.EvalStatus.Leaving);
        CilValue result = await InterpretAsync(run, decoded, arguments, locals);
        ICorDebugValue value = result.Value is EvaluationDelegate
            ? await MaterializeDelegateAsync(result, compiled, resolver, delegateAssemblyLoader, context, handles)
            : await MaterializeAsync(result, context, handles, resolver.ResolveMethodReturnType(compiled.EntryMethod), resolver);
        return new CilInterpretationResult(value, handles.DetachIfOwned(value));
    }

    /// <summary>One expression's interpretation, shared by the evaluation methods it calls: what it has made, and when it
    /// is given up (a <see cref="Stopwatch"/> timestamp, or a request that leaves the stop), so a loop it runs never holds
    /// the engine's lock for good.</summary>
    private sealed record Interpretation(
        CompiledEvaluationMethod Compiled,
        EvaluationMetadataResolver Resolver,
        CompiledExpressionEvaluationContext Context,
        EvaluationHandleScope Handles,
        DebuggeeDelegateAssemblyLoader Delegates,
        long Deadline,
        CancellationToken Leaving)
    {
        public Dictionary<string, ICilLocation> SyntheticVariables { get; } = new(StringComparer.Ordinal);
        public Dictionary<FieldDefinitionHandle, ICilLocation> StaticFields { get; } = [];
        public HashSet<EvaluationClosure> Closures { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private EvaluationMetadataResolver CreateResolver(CompiledEvaluationMethod compiled, CompiledExpressionEvaluationContext context, ICorDebugILFrame? frame)
    {
        (ICorDebugType[]? typeGenericArguments, ICorDebugType[]? methodGenericArguments) = context.RootValue is not null
            ? (context.RootValue.ExactType.TypeParameters, [])
            : SplitFrameTypeParameters(debugger, frame);
        ICorDebugModule preferredModule = context.RootValue is not null ? context.RootValue.ExactType.Class.Module : frame!.Function.Module;
        ModuleInfo currentFrameModule = debugger.GetModuleInfoForModule(preferredModule);
        return new EvaluationMetadataResolver(
            debugger,
            compiled.MetadataReader,
            compiled.PeReader,
            context.Thread.AppDomain,
            typeGenericArguments,
            methodGenericArguments,
            currentFrameModule);
    }

    private static ICilLocation[] CreateArguments(ICorDebugILFrame? frame, CompiledExpressionEvaluationContext context, Func<ICorDebugILFrame> refind)
    {
        if (context.RootValue is not null)
        {
            return [new CorDebugLocation(context.RootValue)];
        }

        ICorDebugILFrame ilFrame = frame ?? throw new InvalidOperationException("Arguments need a frame");
        return [.. Enumerable.Range(0, ilFrame.Arguments.Length).Select(i => (ICilLocation)new FrameSlotLocation(ilFrame, i, argument: true, refind))];
    }

    private static ICilLocation[] CreateLocals(CompiledEvaluationMethod compiled, EvaluationMetadataResolver resolver, ICorDebugILFrame? frame, StandaloneSignatureHandle localSignature, bool isTypeContext, Func<ICorDebugILFrame> refind)
    {
        int localCount = localSignature.IsNil
            ? 0
            : compiled.MetadataReader.GetStandaloneSignature(localSignature)
                .DecodeLocalSignature(LocalCountSignatureProvider.Instance, genericContext: null).Length;
        ICorDebugValue[]? frameLocals = frame?.LocalVariables;
        ICilLocation[] result = new ICilLocation[localCount];
        for (int i = 0; i < result.Length; i++)
        {
            int local = i;
            result[i] = !isTypeContext && i < frameLocals!.Length
                ? new FrameSlotLocation(frame!, i, argument: false, refind)
                : new TemporaryLocation(CilValue.Null(), () => resolver.ResolveEvaluationLocalType(localSignature, local));
        }
        return result;
    }

    private static ICilLocation[] CreateTemporaryLocals(EvaluationMetadataResolver resolver, StandaloneSignatureHandle localSignature)
    {
        int count = resolver.GetEvaluationLocalCount(localSignature);
        ICilLocation[] result = new ICilLocation[count];
        for (int i = 0; i < count; i++)
        {
            int local = i;
            result[i] = new TemporaryLocation(CilValue.Null(), () => resolver.ResolveEvaluationLocalType(localSignature, local));
        }

        return result;
    }

    private static (ICorDebugType[] TypeArguments, ICorDebugType[] MethodArguments) SplitFrameTypeParameters(ManagedDebugger debugger, ICorDebugILFrame? frame)
    {
        if (frame is null)
        {
            return ([], []);
        }

        ICorDebugType[] typeParameters;
        try
        {
            typeParameters = frame.TypeParameters;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return ([], []);
        }
        int declaringTypeArity = GetDeclaringTypeArity(debugger, frame);
        return (typeParameters.Take(declaringTypeArity).ToArray(), typeParameters.Skip(declaringTypeArity).ToArray());
    }

    private static int GetDeclaringTypeArity(ManagedDebugger debugger, ICorDebugILFrame frame)
    {
        try
        {
            mdTypeDef declaringTypeToken = frame.Function.Class.Token;
            ModuleInfo moduleInfo = debugger.GetModuleInfoForModule(frame.Function.Module);
            return moduleInfo.MetadataReader.PeMetadataReader
                .GetTypeDefinition((TypeDefinitionHandle)MetadataTokens.Handle(declaringTypeToken))
                .GetGenericParameters()
                .Count();
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return 0;
        }
    }

    private async Task<CilValue> InterpretAsync(Interpretation run, DecodedMethod decoded, ICilLocation[] arguments, ICilLocation[] locals)
    {
        IReadOnlyList<CilInstruction> instructions = decoded.Instructions;
        IReadOnlyDictionary<int, int> offsets = decoded.Offsets;
        EvaluationMetadataResolver resolver = run.Resolver;
        CompiledExpressionEvaluationContext context = run.Context;
        EvaluationHandleScope handles = run.Handles;
        Stack<CilValue> stack = new Stack<CilValue>();
        int index = 0;
        ResolvedCilType? constrainedType = null;
        while (index < instructions.Count)
        {
            if (Stopwatch.GetTimestamp() > run.Deadline)
            {
                throw new ManagedDebugger.EvalException($"{TookTooLong}: it was stopped after {Extensions.EvaluationTimeout.TotalSeconds:0.#} s");
            }
            if (run.Leaving.IsCancellationRequested)
            {
                throw new ManagedDebugger.EvalException(Extensions.NotEvaluated);
            }
            CilInstruction instruction = instructions[index++];
            try
            {
                switch (instruction.Code)
                {
                    case ILOpCode.Nop or ILOpCode.Break:
                        break;
                    case ILOpCode.Constrained:
                        constrainedType = resolver.ResolveTypeToken(instruction.Token);
                        break;
                    case ILOpCode.Ldnull:
                        stack.Push(CilValue.Null());
                        break;
                    case ILOpCode.Ldstr:
                        stack.Push(CilValue.FromPrimitive(resolver.ResolveUserString(instruction.Token)));
                        break;
                    case ILOpCode.Ldtoken:
                        stack.Push(MetadataTokens.EntityHandle(instruction.Token) is { Kind: HandleKind.FieldDefinition } constants
                            ? CilValue.FromVirtual(new EvaluationFieldToken((FieldDefinitionHandle)constants))
                            : CilValue.FromPrimitive(resolver.ResolveTypeToken(instruction.Token)));
                        break;
                    case >= ILOpCode.Ldc_i4_m1 and <= ILOpCode.Ldc_i4_8:
                        stack.Push(CilValue.FromPrimitive((int)instruction.Code - (int)ILOpCode.Ldc_i4_0));
                        break;
                    case ILOpCode.Ldc_i4 or ILOpCode.Ldc_i4_s:
                        stack.Push(CilValue.FromPrimitive(Convert.ToInt32(instruction.Operand)));
                        break;
                    case ILOpCode.Ldc_i8:
                        stack.Push(CilValue.FromPrimitive((long)instruction.Operand!));
                        break;
                    case ILOpCode.Ldc_r4:
                        stack.Push(CilValue.FromPrimitive((float)instruction.Operand!));
                        break;
                    case ILOpCode.Ldc_r8:
                        stack.Push(CilValue.FromPrimitive((double)instruction.Operand!));
                        break;
                    case >= ILOpCode.Ldarg_0 and <= ILOpCode.Ldarg_3:
                        Load(arguments[(int)instruction.Code - (int)ILOpCode.Ldarg_0]);
                        break;
                    case ILOpCode.Ldarg or ILOpCode.Ldarg_s:
                        Load(arguments[instruction.Index]);
                        break;
                    case ILOpCode.Ldarga or ILOpCode.Ldarga_s:
                        stack.Push(CilValue.FromLocation(arguments[instruction.Index]));
                        break;
                    case ILOpCode.Starg or ILOpCode.Starg_s:
                        await StoreAsync(arguments[instruction.Index], stack.Pop(), context, handles);
                        break;
                    case >= ILOpCode.Ldloc_0 and <= ILOpCode.Ldloc_3:
                        Load(locals[(int)instruction.Code - (int)ILOpCode.Ldloc_0]);
                        break;
                    case ILOpCode.Ldloc or ILOpCode.Ldloc_s:
                        Load(locals[instruction.Index]);
                        break;
                    case ILOpCode.Ldloca or ILOpCode.Ldloca_s:
                        stack.Push(CilValue.FromLocation(locals[instruction.Index]));
                        break;
                    case >= ILOpCode.Stloc_0 and <= ILOpCode.Stloc_3:
                        await StoreAsync(locals[(int)instruction.Code - (int)ILOpCode.Stloc_0], stack.Pop(), context, handles);
                        break;
                    case ILOpCode.Stloc or ILOpCode.Stloc_s:
                        await StoreAsync(locals[instruction.Index], stack.Pop(), context, handles);
                        break;
                    case ILOpCode.Dup:
                        stack.Push(stack.Peek());
                        break;
                    case ILOpCode.Pop:
                        stack.Pop();
                        break;
                    case ILOpCode.Ldftn or ILOpCode.Ldvirtftn:
                        if (instruction.Code is ILOpCode.Ldvirtftn)
                        {
                            stack.Pop();
                        }
                        stack.Push(CilValue.FromVirtual(MetadataTokens.EntityHandle(instruction.Token).Kind == HandleKind.MethodDefinition
                            ? new EvaluationFunctionPointer(instruction.Token)
                            : CreateRuntimeFunctionPointer(resolver.ResolveMethod(instruction.Token))));
                        break;
                    case ILOpCode.Neg:
                        stack.Push(CilNumerics.Negate(stack.Pop()));
                        break;
                    case ILOpCode.Not:
                        stack.Push(CilNumerics.Not(stack.Pop()));
                        break;
                    case ILOpCode.Add or ILOpCode.Sub or ILOpCode.Mul or ILOpCode.Div or ILOpCode.Div_un or ILOpCode.Rem or ILOpCode.Rem_un
                        or ILOpCode.And or ILOpCode.Or or ILOpCode.Xor or ILOpCode.Shl or ILOpCode.Shr or ILOpCode.Shr_un
                        or ILOpCode.Add_ovf or ILOpCode.Add_ovf_un or ILOpCode.Sub_ovf or ILOpCode.Sub_ovf_un or ILOpCode.Mul_ovf or ILOpCode.Mul_ovf_un:
                    {
                        CilValue right = stack.Pop();
                        stack.Push(CilNumerics.Binary(instruction.Code, stack.Pop(), right));
                        break;
                    }
                    case ILOpCode.Ceq or ILOpCode.Cgt or ILOpCode.Cgt_un or ILOpCode.Clt or ILOpCode.Clt_un:
                    {
                        CilValue right = stack.Pop();
                        stack.Push(CilValue.FromPrimitive(CilNumerics.Compare(instruction.Code, stack.Pop(), right) ? 1 : 0));
                        break;
                    }
                    case >= ILOpCode.Conv_i1 and <= ILOpCode.Conv_u8 or ILOpCode.Conv_r_un or >= ILOpCode.Conv_ovf_i1_un and <= ILOpCode.Conv_ovf_u_un
                        or >= ILOpCode.Conv_ovf_i1 and <= ILOpCode.Conv_ovf_u8 or ILOpCode.Conv_u2 or ILOpCode.Conv_u1 or ILOpCode.Conv_i
                        or ILOpCode.Conv_ovf_i or ILOpCode.Conv_ovf_u or ILOpCode.Conv_u:
                        stack.Push(CilNumerics.Convert(instruction.Code, stack.Pop()));
                        break;
                    case ILOpCode.Br or ILOpCode.Br_s:
                        index = offsets[instruction.Target];
                        break;
                    case ILOpCode.Brtrue or ILOpCode.Brtrue_s or ILOpCode.Brfalse or ILOpCode.Brfalse_s:
                    {
                        bool onTrue = instruction.Code is ILOpCode.Brtrue or ILOpCode.Brtrue_s;
                        if (stack.Pop().IsTrue() == onTrue)
                        {
                            index = offsets[instruction.Target];
                        }
                        break;
                    }
                    case >= ILOpCode.Beq_s and <= ILOpCode.Blt_un_s or >= ILOpCode.Beq and <= ILOpCode.Blt_un:
                    {
                        CilValue right = stack.Pop();
                        if (CilNumerics.Branch(instruction.Code, stack.Pop(), right))
                        {
                            index = offsets[instruction.Target];
                        }
                        break;
                    }
                    case ILOpCode.Switch:
                    {
                        int selected = stack.Pop().AsInt32();
                        int[] targets = (int[])instruction.Operand!;
                        if ((uint)selected < (uint)targets.Length)
                        {
                            index = offsets[targets[selected]];
                        }
                        break;
                    }
                    case ILOpCode.Ldobj or >= ILOpCode.Ldind_i1 and <= ILOpCode.Ldind_ref:
                        stack.Push(handles.Root(stack.Pop().Dereference()));
                        break;
                    case ILOpCode.Stobj or >= ILOpCode.Stind_ref and <= ILOpCode.Stind_r8 or ILOpCode.Stind_i:
                    {
                        CilValue value = stack.Pop();
                        ICilLocation address = stack.Pop().Location ?? throw new InvalidOperationException("stind requires a managed location");
                        await StoreAsync(address, value, context, handles);
                        break;
                    }
                    case ILOpCode.Cpobj:
                    {
                        CilValue source = stack.Pop().Dereference();
                        ICilLocation destination = stack.Pop().Location ?? throw new InvalidOperationException("cpobj requires a managed location");
                        await StoreAsync(destination, source, context, handles);
                        break;
                    }
                    case ILOpCode.Initobj:
                    {
                        ResolvedCilType type = resolver.ResolveTypeToken(instruction.Token);
                        ICilLocation location = stack.Pop().Location ?? throw new InvalidOperationException("initobj requires a managed location");
                        location.Write(await CreateDefaultValueAsync(type, resolver, context, handles));
                        break;
                    }
                    case ILOpCode.Newarr:
                    {
                        uint length = checked((uint)stack.Pop().AsInt32());
                        ResolvedCilType elementCilType = resolver.ResolveTypeToken(instruction.Token);
                        ICorDebugValue? array = await CreateArrayAsync(elementCilType, resolver.GetCorDebugType(elementCilType), length, resolver, context, handles);
                        stack.Push(array is null ? CilValue.Null() : CilValue.FromCorValue(array));
                        break;
                    }
                    case ILOpCode.Ldlen:
                        stack.Push(CilValue.FromPrimitive(GetArrayValue(stack.Pop()).Count));
                        break;
                    case ILOpCode.Ldelema:
                    {
                        int element = stack.Pop().AsInt32();
                        stack.Push(CilValue.FromLocation(new CorDebugLocation(GetArrayValue(stack.Pop()).GetElementAtPosition(element))));
                        break;
                    }
                    case ILOpCode.Ldelem or >= ILOpCode.Ldelem_i1 and <= ILOpCode.Ldelem_ref:
                    {
                        int element = stack.Pop().AsInt32();
                        stack.Push(handles.Root(new CorDebugLocation(GetArrayValue(stack.Pop()).GetElementAtPosition(element)).Read()));
                        break;
                    }
                    case ILOpCode.Stelem or >= ILOpCode.Stelem_i and <= ILOpCode.Stelem_ref:
                    {
                        CilValue value = stack.Pop();
                        int element = stack.Pop().AsInt32();
                        ICorDebugArrayValue array = GetArrayValue(stack.Pop());
                        new CorDebugLocation(array.GetElementAtPosition(element)).Write(await MaterializeForStoreAsync(value, context, handles));
                        break;
                    }
                    case ILOpCode.Isinst or ILOpCode.Castclass:
                        stack.Push(await CastAsync(instruction, stack.Pop(), run));
                        break;
                    case ILOpCode.Box:
                        stack.Push(await BoxAsync(stack.Pop(), resolver.GetCorDebugType(resolver.ResolveTypeToken(instruction.Token)), context, handles));
                        break;
                    case ILOpCode.Unbox_any:
                    {
                        ICorDebugBoxValue boxed = Unbox(stack.Pop(), resolver.ResolveTypeToken(instruction.Token), resolver);
                        stack.Push(CilValue.FromCorValue(boxed.Object));
                        break;
                    }
                    case ILOpCode.Unbox:
                    {
                        // The address of a box's value, which must be the type asked for, as unbox.any's is.
                        ICorDebugBoxValue boxed = Unbox(stack.Pop(), resolver.ResolveTypeToken(instruction.Token), resolver);
                        stack.Push(CilValue.FromLocation(new CorDebugLocation(boxed.Object)));
                        break;
                    }
                    case ILOpCode.Ldfld or ILOpCode.Ldflda or ILOpCode.Stfld or ILOpCode.Ldsfld or ILOpCode.Ldsflda or ILOpCode.Stsfld:
                        await FieldAsync(instruction, stack, run);
                        break;
                    case ILOpCode.Newobj:
                        stack.Push(await NewObjectAsync(instruction, stack, run));
                        break;
                    case ILOpCode.Call or ILOpCode.Callvirt:
                    {
                        ResolvedCilType? callConstrainedType = constrainedType;
                        constrainedType = null;
                        await CallAsync(instruction, stack, callConstrainedType, run);
                        break;
                    }
                    case ILOpCode.Ret:
                        return stack.Count == 0 ? CilValue.Null() : stack.Pop();
                    default:
                        throw new NotSupportedException($"CIL opcode '{instruction.OpCode.Name}' at IL_{instruction.Offset:X4} is not supported yet");
                }
            }
            catch (ArithmeticException thrown)
            {
                // What the program would throw running the instruction itself, told as code run in the program throws.
                throw new ManagedDebugger.EvalException($"Evaluation threw {thrown.GetType().FullName}: {thrown.Message}");
            }
            catch (Exception ex) when (ex is not NotSupportedException and not ManagedDebugger.EvalException)
            {
                throw new InvalidOperationException($"CIL execution failed at IL_{instruction.Offset:X4} ({instruction.OpCode.Name}): {ex.GetType().Name}: {ex.Message}", ex);
            }
        }

        throw new InvalidOperationException("Generated evaluation method ended without ret");

        void Load(ICilLocation location) => stack.Push(handles.Root(location.Read()).WithSourceLocation(location));
    }

    private async Task<CilValue> CastAsync(CilInstruction instruction, CilValue source, Interpretation run)
    {
        if (source.IsNull)
        {
            return CilValue.Null();
        }
        ResolvedCilType targetType = run.Resolver.ResolveTypeToken(instruction.Token);
        if (await IsInstanceOfTypeAsync(source, targetType, run.Resolver, run.Context, run.Handles))
        {
            return source;
        }
        return instruction.Code is ILOpCode.Isinst
            ? CilValue.Null()
            : throw new InvalidCastException($"InvalidCastException: Cannot cast the debuggee value to '{targetType.RuntimeType?.ToString() ?? "the requested type"}'");
    }

    /// <summary>The box unbox and unbox.any take apart, which must hold the very type they name.</summary>
    private static ICorDebugBoxValue Unbox(CilValue source, ResolvedCilType targetType, EvaluationMetadataResolver resolver)
    {
        if (source.Location is not null)
        {
            source = source.Dereference();
        }
        if (source.IsNull)
        {
            throw new NullReferenceException();
        }
        ICorDebugBoxValue boxed = (source.CorValue is ICorDebugReferenceValue reference ? reference.Dereference() as ICorDebugBoxValue : source.CorValue as ICorDebugBoxValue)
            ?? throw new InvalidCastException("The CIL value is not boxed");
        if (!IsUnboxCompatible(boxed.Object, targetType, resolver))
        {
            throw new InvalidCastException($"InvalidCastException: Cannot unbox the debuggee value to '{targetType.RuntimeType?.ToString() ?? "the requested type"}'");
        }
        return boxed;
    }

    /// <summary>ldfld, ldflda, stfld and their static forms, of a field of the program's or of one the expression's
    /// compilation made (a closure's).</summary>
    private async Task FieldAsync(CilInstruction instruction, Stack<CilValue> stack, Interpretation run)
    {
        EvaluationMetadataResolver resolver = run.Resolver;
        if (MetadataTokens.EntityHandle(instruction.Token) is { Kind: HandleKind.FieldDefinition } evaluationField)
        {
            FieldDefinitionHandle handle = (FieldDefinitionHandle)evaluationField;
            switch (instruction.Code)
            {
                case ILOpCode.Ldfld:
                {
                    ICilLocation location = GetEvaluationClosure(stack.Pop()).Fields[handle];
                    stack.Push(run.Handles.Root(location.Read()).WithSourceLocation(location));
                    break;
                }
                case ILOpCode.Ldflda:
                    stack.Push(CilValue.FromLocation(GetEvaluationClosure(stack.Pop()).Fields[handle]));
                    break;
                case ILOpCode.Stfld:
                {
                    CilValue value = stack.Pop();
                    EvaluationClosure receiver = GetEvaluationClosure(stack.Pop());
                    ICilLocation location = receiver.Fields[handle];
                    if (location is TemporaryLocation && value.SourceLocation is { } sourceLocation)
                    {
                        receiver.FieldBindings[handle] = sourceLocation;
                    }
                    location.Write(value);
                    break;
                }
                case ILOpCode.Ldsfld:
                    stack.Push(GetEvaluationStaticField(run.StaticFields, handle).Read());
                    break;
                case ILOpCode.Ldsflda:
                    stack.Push(resolver.HasEvaluationFieldData(handle)
                        ? CilValue.FromVirtual(new EvaluationFieldToken(handle))
                        : CilValue.FromLocation(GetEvaluationStaticField(run.StaticFields, handle)));
                    break;
                default:
                    GetEvaluationStaticField(run.StaticFields, handle).Write(stack.Pop());
                    break;
            }
            return;
        }
        ResolvedRuntimeField field = resolver.ResolveField(instruction.Token);
        switch (instruction.Code)
        {
            case ILOpCode.Ldfld:
                stack.Push(run.Handles.Root(CilValue.FromCorValue(GetFieldReceiver(stack.Pop()).GetFieldValue(field.DeclaringType.Class, field.Token))));
                break;
            case ILOpCode.Ldflda:
                stack.Push(CilValue.FromLocation(new CorDebugLocation(GetFieldReceiver(stack.Pop()).GetFieldValue(field.DeclaringType.Class, field.Token))));
                break;
            case ILOpCode.Stfld:
            {
                CilValue value = stack.Pop();
                ICorDebugObjectValue receiver = GetFieldReceiver(stack.Pop());
                new CorDebugLocation(receiver.GetFieldValue(field.DeclaringType.Class, field.Token)).Write(await MaterializeForStoreAsync(value, run.Context, run.Handles));
                break;
            }
            case ILOpCode.Ldsfld:
                stack.Push(run.Handles.Root(CilValue.FromCorValue(await GetStaticFieldValue(field, resolver, run.Context))));
                break;
            case ILOpCode.Ldsflda:
                stack.Push(CilValue.FromLocation(new CorDebugLocation(await GetStaticFieldValue(field, resolver, run.Context))));
                break;
            default:
                new CorDebugLocation(await GetStaticFieldValue(field, resolver, run.Context)).Write(await MaterializeForStoreAsync(stack.Pop(), run.Context, run.Handles));
                break;
        }
    }

    private async Task<CilValue> NewObjectAsync(CilInstruction instruction, Stack<CilValue> stack, Interpretation run)
    {
        EvaluationMetadataResolver resolver = run.Resolver;
        EvaluationHandleScope handles = run.Handles;
        if (MetadataTokens.EntityHandle(instruction.Token) is { Kind: HandleKind.MethodDefinition } evaluationConstructor)
        {
            MethodDefinitionHandle constructorHandle = (MethodDefinitionHandle)evaluationConstructor;
            MethodSignature<string> signature = resolver.ResolveEvaluationMethodSignature(constructorHandle);
            if (signature.ParameterTypes.Length != 0)
            {
                throw new NotSupportedException("Generated evaluation constructors with parameters are not supported");
            }

            EvaluationClosure evaluationClosure = new EvaluationClosure(resolver.GetEvaluationMethodDeclaringType(constructorHandle), constructorHandle);
            foreach (FieldDefinitionHandle field in resolver.GetEvaluationInstanceFields(evaluationClosure.Type))
            {
                evaluationClosure.Fields[field] = new TemporaryLocation(CilValue.Null());
            }
            run.Closures.Add(evaluationClosure);
            return CilValue.FromVirtual(evaluationClosure);
        }
        if (resolver.TryResolveArrayMethod(instruction.Token, out string arrayMethod, out ResolvedCilType? arrayType, out int parameters) && arrayMethod is ".ctor")
        {
            return await NewArrayAsync(arrayType, Pop(stack, parameters), run);
        }
        ResolvedRuntimeMethod constructor = resolver.ResolveMethod(instruction.Token);
        CilValue[] constructorArguments = Pop(stack, constructor.Signature.ParameterTypes.Length);
        if (constructorArguments is [var delegateTarget, { Value: EvaluationFunctionPointer function }])
        {
            return CilValue.FromVirtual(new EvaluationDelegate(function, delegateTarget, constructor.DeclaringType));
        }
        if (constructorArguments is [{ Value: EvaluationFieldToken constants }, var count]
            && resolver.GetRuntimeTypeName(constructor.DeclaringType) is "System.Span`1" or "System.ReadOnlySpan`1")
        {
            // A span over constants the compilation laid out, as a u8 string makes: over a pinned array of them.
            byte[] data = resolver.GetEvaluationFieldData(constants.Field);
            constructorArguments = [await PinnedArrayAsync(constructor.DeclaringType.TypeArguments[0], count.AsInt32(), made => Fill(made, data), run)];
            constructor = EvaluationMetadataResolver.ResolveMethodOf(constructor.DeclaringType, ".ctor", "!0[]");
        }
        else if (constructor.Signature.ParameterTypes is ["!0&"] && resolver.GetRuntimeTypeName(constructor.DeclaringType) is "System.Span`1" or "System.ReadOnlySpan`1")
        {
            // A span over one value, as a char joined to a string makes: over an array of one, which outlives the call.
            CilValue one = await MaterializeForStoreAsync(constructorArguments[0].Dereference(), run.Context, handles);
            constructorArguments = [await PinnedArrayAsync(constructor.DeclaringType.TypeArguments[0], 1, array => new CorDebugLocation(array.GetElementAtPosition(0)).Write(one), run)];
            constructor = EvaluationMetadataResolver.ResolveMethodOf(constructor.DeclaringType, ".ctor", "!0[]");
        }
        ICorDebugValue[] argumentValues = new ICorDebugValue[constructorArguments.Length];
        List<(ICilLocation Location, ICilLocation Lent)> temporaryByRefArguments = [];
        for (int argument = 0; argument < argumentValues.Length; argument++)
        {
            argumentValues[argument] = constructor.Signature.ParameterTypes[argument].EndsWith('&')
                ? await MaterializeByRefArgumentAsync(constructorArguments[argument], resolver, run.Context, handles, temporaryByRefArguments)
                : await MaterializeForCallAsync(constructorArguments[argument], run.Context, handles);
        }
        if (EvaluationMetadataResolver.IsByRefLike(constructor.DeclaringType))
        {
            RefuseLentCopies(constructor, argumentValues, resolver);
        }
        ICorDebugType[] typeArguments = constructor.DeclaringType.TypeArguments.IsDefaultOrEmpty
            ? []
            : constructor.DeclaringType.TypeArguments.Select(resolver.GetCorDebugType).ToArray();
        ResolvedRuntimeMethod? stringMaker = resolver.GetRuntimeTypeName(constructor.DeclaringType) == "System.String" ? StringMaker(constructor) : null;
        ICorDebugEval eval = run.Context.Thread.CreateEval();
        ICorDebugValue? newValue;
        try
        {
            newValue = handles.Track(stringMaker is { } maker
                ? await eval.CallParameterizedFunctionAsync(
                    debugger.ProcessRuntimeEventsUntilEvalEvent,
                    debugger.EvalStatus,
                    maker.Function,
                    0,
                    null,
                    argumentValues.Length,
                    argumentValues,
                    throwOnException: true)
                : await eval.NewParameterizedObjectAsync(
                    debugger.ProcessRuntimeEventsUntilEvalEvent,
                    debugger.EvalStatus,
                    constructor.Function,
                    typeArguments.Length,
                    typeArguments.Length == 0 ? null : typeArguments,
                    argumentValues.Length,
                    argumentValues,
                    throwOnException: true));
        }
        finally
        {
            WriteBackTemporaryByRefArguments(temporaryByRefArguments, handles);
        }
        return newValue is null ? CilValue.Null() : CilValue.FromCorValue(newValue);
    }

    /// <summary>The static String.Ctor a string's constructor stands for, of the same parameters: the runtime makes a string
    /// only through one, and refuses to make one as an object.</summary>
    private static ResolvedRuntimeMethod StringMaker(ResolvedRuntimeMethod constructor)
    {
        string refused = $"Not evaluated: the program's runtime makes a string only through a String.Ctor, and has none taking ({string.Join(", ", constructor.Signature.ParameterTypes)})";
        ResolvedRuntimeMethod maker;
        try
        {
            maker = EvaluationMetadataResolver.ResolveMethodOf(constructor.DeclaringType, "Ctor", [.. constructor.Signature.ParameterTypes]);
        }
        catch (MissingMethodException)
        {
            throw new ManagedDebugger.EvalException(refused);
        }
        return maker.IsStatic ? maker : throw new ManagedDebugger.EvalException(refused);
    }

    private static CilValue[] Pop(Stack<CilValue> stack, int count)
    {
        CilValue[] values = new CilValue[count];
        for (int i = count - 1; i >= 0; i--)
        {
            values[i] = stack.Pop();
        }
        return values;
    }

    /// <summary>call or callvirt: of one of the debugger's intrinsics, of a method the expression's compilation made (run
    /// here), of one the interpreter does itself, or of the program's, run in it.</summary>
    private async Task CallAsync(CilInstruction instruction, Stack<CilValue> stack, ResolvedCilType? constrainedType, Interpretation run)
    {
        EvaluationMetadataResolver resolver = run.Resolver;
        CompiledExpressionEvaluationContext context = run.Context;
        EvaluationHandleScope handles = run.Handles;
        if (resolver.TryResolveDebuggerIntrinsic(instruction.Token, out string? debuggerIntrinsic))
        {
            await DebuggerIntrinsicAsync(debuggerIntrinsic, stack, run);
            return;
        }
        if (resolver.TryResolveEvaluationMethod(instruction.Token, out ResolvedEvaluationMethod evaluationMethod))
        {
            CilValue[] methodArguments = Pop(stack, evaluationMethod.Signature.ParameterTypes.Length + (evaluationMethod.IsStatic ? 0 : 1));
            ICilLocation[] parameters = new ICilLocation[methodArguments.Length];
            for (int parameter = 0; parameter < parameters.Length; parameter++)
            {
                parameters[parameter] = new TemporaryLocation(await OwnAsync(methodArguments[parameter], context, handles));
            }
            CilValue methodResult = await InterpretAsync(
                run,
                run.Compiled.GetDecodedMethod(evaluationMethod.Handle),
                parameters,
                CreateTemporaryLocals(resolver, resolver.GetEvaluationMethodBody(evaluationMethod.Handle).LocalSignature));
            if (evaluationMethod.Signature.ReturnType != MetadataTypeNames.Of(PrimitiveTypeCode.Void))
            {
                stack.Push(methodResult);
            }
            return;
        }
        if (resolver.TryResolveArrayMethod(instruction.Token, out string accessor, out ResolvedCilType? arrayType, out _) && accessor is not ".ctor")
        {
            await ArrayAccessorAsync(accessor, arrayType.ArrayRank, stack, context, handles);
            return;
        }
        ResolvedRuntimeMethod method = resolver.ResolveMethod(instruction.Token);
        CilValue[] argumentValues = Pop(stack, method.Signature.ParameterTypes.Length);
        CilValue? receiverValue = method.IsStatic ? null : stack.Pop();

        if (resolver.GetRuntimeTypeName(method.DeclaringType) == "System.Type" && method.Name == "GetTypeFromHandle")
        {
            ResolvedCilType tokenType = argumentValues[0].Value as ResolvedCilType
                ?? throw new InvalidOperationException("GetTypeFromHandle requires a type token");
            ICorDebugValue? typeValue = await GetSystemTypeAsync(tokenType, resolver, context, handles);
            stack.Push(typeValue is null ? CilValue.Null() : CilValue.FromTypeToken(tokenType, typeValue));
            return;
        }
        if (argumentValues is [.., { Value: EvaluationFieldToken constants }])
        {
            await ConstantsAsync(method, argumentValues, constants, stack, run);
            return;
        }
        (bool Handled, CilValue? Result) intrinsic = await TryExecuteIntrinsicCallAsync(method, receiverValue, argumentValues, resolver, context, handles);
        if (intrinsic.Handled)
        {
            if (intrinsic.Result is not null)
            {
                stack.Push(intrinsic.Result);
            }
            return;
        }
        if (receiverValue?.Value is StringBuilder || receiverValue?.Location?.Read().Value is StringBuilder || argumentValues.Any(a => a.Value is StringBuilder))
        {
            throw new InvalidOperationException($"Unhandled interpolated-string call '{resolver.GetRuntimeTypeName(method.DeclaringType)}.{method.Name}'");
        }

        ICorDebugValue[] callArguments = new ICorDebugValue[method.Signature.ParameterTypes.Length + (method.IsStatic ? 0 : 1)];
        List<(ICilLocation Location, ICilLocation Lent)> temporaryByRefArguments = [];
        (ICilLocation Home, ICorDebugValue Box)? constrainedCopyBack = null;
        for (int argument = 0; argument < argumentValues.Length; argument++)
        {
            callArguments[argument + (method.IsStatic ? 0 : 1)] = method.Signature.ParameterTypes[argument].EndsWith('&')
                ? await MaterializeByRefArgumentAsync(argumentValues[argument], resolver, context, handles, temporaryByRefArguments)
                : await MaterializeForCallAsync(argumentValues[argument], run.Compiled, resolver, run.Delegates, context, handles);
        }
        if (receiverValue is not null)
        {
            CilValue receiver = receiverValue;
            if (receiver.Location is not null)
            {
                receiver = receiver.Dereference();
            }

            if (receiverValue.Location is TemporaryLocation { Written: false, DeclaredType: { } declared } home && !IsReferenceType(declared))
            {
                // A struct's constructor or method called on a variable of the evaluation's own before it holds anything.
                receiver = await CreateDefaultValueAsync(declared, resolver, context, handles);
                home.Write(receiver);
            }
            if (receiver.IsNull)
            {
                throw new NullReferenceException();
            }

            callArguments[0] = receiver.Value is EvaluationDelegate or EvaluationClosure
                ? await MaterializeForCallAsync(receiver, run.Compiled, resolver, run.Delegates, context, handles)
                : await MaterializeReceiverAsync(receiver, context, constrainedType, resolver, handles);
            // A constrained call on a struct runs on a boxed copy, whose changes go back where the struct lives.
            if (constrainedType is not null && receiverValue.Location is { } receiverHome
                && callArguments[0] != receiver.CorValue && callArguments[0].UnwrapDebugValue() is ICorDebugGenericValue)
            {
                constrainedCopyBack = (receiverHome, callArguments[0]);
            }
        }

        if (method.Signature.ReturnType.EndsWith('&'))
        {
            RefuseLentCopies(method, callArguments, resolver);
        }

        int declaringTypeArity = resolver.GetRuntimeTypeGenericArity(method.DeclaringType);
        ICorDebugType[] declaringTypeArguments = !method.IsStatic && declaringTypeArity > 0 && callArguments[0].ExactType is { } receiverType
            ? receiverType.TypeParameters.Take(declaringTypeArity).ToArray()
            : method.DeclaringType.TypeArguments.IsDefaultOrEmpty
                ? []
                : method.DeclaringType.TypeArguments.Select(resolver.GetCorDebugType).ToArray();
        ICorDebugType[] methodTypeArguments = method.MethodTypeArguments.IsDefaultOrEmpty
            ? []
            : method.MethodTypeArguments.Select(resolver.GetCorDebugType).ToArray();
        ICorDebugType[] typeArguments = [.. declaringTypeArguments, .. methodTypeArguments];
        ICorDebugEval eval = context.Thread.CreateEval();
        ICorDebugValue? callResult;
        try
        {
            callResult = handles.Track(await eval.CallParameterizedFunctionAsync(
                debugger.ProcessRuntimeEventsUntilEvalEvent,
                debugger.EvalStatus,
                method.Function,
                typeArguments.Length,
                typeArguments.Length == 0 ? null : typeArguments,
                callArguments.Length,
                callArguments,
                throwOnException: true));
        }
        finally
        {
            WriteBackTemporaryByRefArguments(temporaryByRefArguments, handles);
            SynchronizeEvaluationClosures(run.Closures.Select(CilValue.FromVirtual), handles);
            if (constrainedCopyBack is { } copy)
            {
                copy.Home.Write(CilValue.FromDebuggeeValue(copy.Box.UnwrapDebugValue()));
            }
        }
        if (callResult is not null && IsByRefLike(callResult))
        {
            RefuseLentCopies(method, callArguments, resolver);
        }
        if (method.Signature.ReturnType != MetadataTypeNames.Of(PrimitiveTypeCode.Void))
        {
            stack.Push(callResult is null
                ? CilValue.Null()
                : method.Signature.ReturnType.EndsWith('&') ? CilValue.FromLocation(new CorDebugLocation(callResult)) : CilValue.FromCorValue(callResult));
        }
    }

    /// <summary>A call the expression compiler makes to the debugger itself: a variable the expression declares, its
    /// address or value, the exception the thread is stopped at, or a value a call returned.</summary>
    private async Task DebuggerIntrinsicAsync(string intrinsic, Stack<CilValue> stack, Interpretation run)
    {
        switch (intrinsic)
        {
            case "CreateVariable":
            {
                // The custom type's payload and its id, which the debugger's variables need not.
                stack.Pop();
                stack.Pop();
                string name = stack.Pop().Value as string ?? throw new InvalidOperationException("Synthetic variable name is unavailable");
                ResolvedCilType type = stack.Pop().Value as ResolvedCilType ?? throw new InvalidOperationException("Synthetic variable type is unavailable");
                run.SyntheticVariables[name] = await CreateSyntheticVariableLocationAsync(type, run.Resolver, run.Context, run.Handles);
                break;
            }
            case "GetVariableAddress":
                stack.Push(CilValue.FromLocation(SyntheticVariable(stack.Pop())));
                break;
            case "GetObjectByAlias":
                stack.Push(run.Handles.Root(SyntheticVariable(stack.Pop()).Read()));
                break;
            case "GetException":
            {
                ICorDebugValue exception = debugger.GetCurrentException(run.Context.ThreadId)
                    ?? throw new InvalidOperationException("No current exception is available");
                stack.Push(run.Handles.Root(CilValue.FromCorValue(exception)));
                break;
            }
            case "GetReturnValue":
            {
                int index = stack.Pop().AsInt32();
                if (!debugger.TryGetReturnedValue(run.Context.ThreadId, index, out ICorDebugHandleValue? returned))
                {
                    throw new InvalidOperationException("No call has returned a value since the last step");
                }
                // As an object, as the compiler takes it: a value type's box, which the code it compiled unboxes.
                stack.Push(returned is null ? CilValue.Null() : CilValue.FromDebuggeeValue(returned));
                break;
            }
            default:
                throw new NotSupportedException($"Debugger intrinsic '{intrinsic}' is not supported");
        }

        ICilLocation SyntheticVariable(CilValue nameValue)
        {
            string name = nameValue.Value as string ?? throw new InvalidOperationException("Synthetic variable name is unavailable");
            return run.SyntheticVariables.TryGetValue(name, out ICilLocation? location)
                ? location
                : throw new InvalidOperationException($"Synthetic variable '{name}' is unavailable");
        }
    }

    private static EvaluationFunctionPointer CreateRuntimeFunctionPointer(ResolvedRuntimeMethod method) =>
        new(MetadataTokens.GetToken(method.Handle), method);

    private static EvaluationClosure GetEvaluationClosure(CilValue value)
    {
        if (value.Location is not null)
        {
            value = value.Dereference();
        }

        return value.Value as EvaluationClosure ?? throw new InvalidOperationException("Generated evaluation field requires an evaluation object");
    }

    private static void SynchronizeEvaluationClosures(IEnumerable<CilValue> values, EvaluationHandleScope handles)
    {
        HashSet<EvaluationClosure> visited = new HashSet<EvaluationClosure>(ReferenceEqualityComparer.Instance);
        foreach (CilValue value in values)
        {
            SynchronizeEvaluationClosure(value, handles, visited);
        }
    }

    private static void SynchronizeEvaluationClosure(CilValue value, EvaluationHandleScope handles, HashSet<EvaluationClosure> visited)
    {
        if (value.Location is not null)
        {
            value = value.Dereference();
        }

        if (value.Value is EvaluationDelegate evaluationDelegate)
        {
            SynchronizeEvaluationClosure(evaluationDelegate.Target, handles, visited);
            return;
        }
        if (value.Value is not EvaluationClosure evaluationClosure ||
            evaluationClosure.MaterializedValue is null ||
            !visited.Add(evaluationClosure))
        {
            return;
        }

        ICorDebugObjectValue materializedObject = evaluationClosure.MaterializedValue.UnwrapDebugValueToObject();
        ICorDebugClass materializedClass = evaluationClosure.MaterializedValue.ExactType.Class;
        foreach ((FieldDefinitionHandle fieldHandle, ICilLocation? location) in evaluationClosure.Fields)
        {
            CilValue currentValue = location.Read();
            if (currentValue.Value is EvaluationClosure or EvaluationDelegate)
            {
                SynchronizeEvaluationClosure(currentValue, handles, visited);
                continue;
            }
            ICorDebugValue fieldValue = materializedObject.GetFieldValue(materializedClass, (mdFieldDef)MetadataTokens.GetToken(fieldHandle));
            CilValue synchronizedValue = handles.Root(CilValue.FromCorValue(fieldValue));
            location.Write(synchronizedValue);
            if (evaluationClosure.FieldBindings.TryGetValue(fieldHandle, out ICilLocation? binding))
            {
                binding.Write(synchronizedValue);
            }
        }
    }

    private static ICilLocation GetEvaluationStaticField(Dictionary<FieldDefinitionHandle, ICilLocation> fields, FieldDefinitionHandle handle)
    {
        if (!fields.TryGetValue(handle, out ICilLocation? field))
        {
            fields[handle] = field = new TemporaryLocation(CilValue.Null());
        }

        return field;
    }

    private async Task<ICorDebugValue> MaterializeAsync(
        CilValue value,
        CompiledExpressionEvaluationContext context,
        EvaluationHandleScope handles,
        ResolvedCilType? expectedType = null,
        EvaluationMetadataResolver? resolver = null)
    {
        // A pointer is itself, whatever its pointed-at type reads as.
        if (value.CorValue is not null && (expectedType?.Primitive is null or PrimitiveTypeCode.String or PrimitiveTypeCode.Object
            || value.CorValue is ICorDebugReferenceValue { Type: CorElementType.PTR }))
        {
            return value.CorValue;
        }

        ICorDebugEval eval = context.Thread.CreateEval();
        CorElementType? expectedElementType = GetPrimitiveElementType(expectedType?.Primitive);
        if (value.Value is null && expectedElementType is { } primitiveElementType && value.CorValue?.UnwrapDebugValue() is ICorDebugGenericValue sourceGeneric)
        {
            ICorDebugGenericValue primitiveResult = await CreatePrimitiveAsync(eval, primitiveElementType, resolver, handles);
            byte[] sourceBytes = sourceGeneric.GetValueAsBytes();
            if (sourceBytes.Length != primitiveResult.Size && value.TryGetInt64(out long integer))
            {
                // An enum goes through its value, signed as its type is: an sbyte enum's -1 is never 255.
                sourceBytes = CilValueEncoding.GetBytesForSize(integer, primitiveResult.Size);
            }
            unsafe
            {
                fixed (byte* pointer = sourceBytes)
                {
                    primitiveResult.SetValue((nint)pointer);
                }
            }
            return primitiveResult;
        }
        if (value.Value is null)
        {
            return eval.CreateValue(CorElementType.CLASS, null);
        }

        if (value.Value is string text)
        {
            return handles.Track(await eval.NewStringAsync(debugger.ProcessRuntimeEventsUntilEvalEvent, debugger.EvalStatus, text, throwOnException: true))!;
        }

        if (expectedType?.RuntimeType is { } runtimeType && resolver is not null)
        {
            ICorDebugValue typedResult = handles.Track(await eval.NewParameterizedObjectNoConstructorAsync(
                debugger.ProcessRuntimeEventsUntilEvalEvent,
                debugger.EvalStatus,
                runtimeType.Class,
                0,
                null,
                throwOnException: true)) ?? throw new InvalidOperationException("Failed to create evaluation result value type");
            new CorDebugLocation(typedResult).Write(value);
            return typedResult;
        }

        CorElementType elementType = expectedElementType ?? value.Value switch
        {
            bool => CorElementType.BOOLEAN,
            char => CorElementType.CHAR,
            sbyte => CorElementType.I1,
            byte => CorElementType.U1,
            short => CorElementType.I2,
            ushort => CorElementType.U2,
            int => CorElementType.I4,
            uint => CorElementType.U4,
            long => CorElementType.I8,
            ulong => CorElementType.U8,
            float => CorElementType.R4,
            double => CorElementType.R8,
            _ => throw new NotSupportedException($"Cannot materialize CIL value '{value.Value.GetType().Name}'")
        };
        ICorDebugGenericValue generic = await CreatePrimitiveAsync(eval, elementType, resolver, handles);
        object materializedValue = elementType == CorElementType.BOOLEAN ? value.IsTrue() : value.Value;
        byte[] bytes = CilValueEncoding.GetBytes(materializedValue, elementType);
        unsafe
        {
            fixed (byte* pointer = bytes)
            {
                generic.SetValue((nint)pointer);
            }
        }
        return generic;
    }

    /// <summary>A primitive value for the debugger to fill. ICorDebug makes a native integer only as a call's result, so one
    /// is what the program's own conversion from a 64-bit integer returns, as wide as the program's native integers.</summary>
    private async Task<ICorDebugGenericValue> CreatePrimitiveAsync(ICorDebugEval eval, CorElementType elementType, EvaluationMetadataResolver? resolver, EvaluationHandleScope handles)
    {
        if (elementType is not (CorElementType.I or CorElementType.U))
        {
            return (ICorDebugGenericValue)eval.CreateValue(elementType, null);
        }
        EvaluationMetadataResolver types = resolver ?? throw new NotSupportedException($"Cannot make a {elementType} value here");
        ResolvedRuntimeMethod conversion = elementType is CorElementType.I
            ? types.ResolveRuntimeMethod("System", "IntPtr", "op_Explicit", MetadataTypeNames.Of(PrimitiveTypeCode.Int64))
            : types.ResolveRuntimeMethod("System", "UIntPtr", "op_Explicit", MetadataTypeNames.Of(PrimitiveTypeCode.UInt64));
        ICorDebugValue zero = eval.CreateValue(elementType is CorElementType.I ? CorElementType.I8 : CorElementType.U8, null);
        ICorDebugValue made = handles.Track(await eval.CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            conversion.Function,
            0,
            null,
            1,
            [zero],
            throwOnException: true)) ?? throw new InvalidOperationException($"The program made no {elementType} value");
        return made.UnwrapDebugValue() as ICorDebugGenericValue ?? throw new InvalidOperationException($"The program's {elementType} value cannot be written");
    }

    /// <summary>A primitive value type's element type, null for any other type.</summary>
    private static CorElementType? GetPrimitiveElementType(PrimitiveTypeCode? primitive) =>
        primitive is { } code && MetadataTypeNames.ElementTypeOf(code) is var element
            && element is not (CorElementType.VOID or CorElementType.STRING or CorElementType.OBJECT or CorElementType.TYPEDBYREF or CorElementType.END)
            ? element : null;

    private async ValueTask<ICorDebugValue> GetStaticFieldValue(ResolvedRuntimeField field, EvaluationMetadataResolver resolver, CompiledExpressionEvaluationContext context)
    {
        ICorDebugType type = resolver.GetCorDebugType(field.DeclaringType);
        return await debugger.GetStaticFieldValueAsync(type, field.Token, context.ThreadId, context.StackDepth);
    }

    private async Task<CilValue> CreateDefaultValueAsync(ResolvedCilType type, EvaluationMetadataResolver resolver, CompiledExpressionEvaluationContext context, EvaluationHandleScope handles)
    {
        if (GetPrimitiveElementType(type.Primitive) is { } primitiveType)
        {
            return primitiveType switch
            {
                CorElementType.R4 => CilValue.FromPrimitive(0f),
                CorElementType.R8 => CilValue.FromPrimitive(0d),
                CorElementType.I8 => CilValue.FromPrimitive(0L),
                CorElementType.U8 => CilValue.FromPrimitive(0UL),
                CorElementType.I when CilValue.PointerSize == 8 => CilValue.FromPrimitive(0L),
                CorElementType.U when CilValue.PointerSize == 8 => CilValue.FromPrimitive(0UL),
                _ => CilValue.FromPrimitive(0)
            };
        }
        if (IsReferenceType(type))
        {
            return CilValue.Null();
        }
        ResolvedRuntimeType runtimeType = type.RuntimeType ?? throw new NotSupportedException("Initializing this CIL type is not supported");
        ICorDebugType[] typeArguments = runtimeType.TypeArguments.IsDefaultOrEmpty
            ? []
            : runtimeType.TypeArguments.Select(resolver.GetCorDebugType).ToArray();
        ICorDebugEval eval = context.Thread.CreateEval();
        ICorDebugValue value = handles.Track(await eval.NewParameterizedObjectNoConstructorAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            runtimeType.Class,
            typeArguments.Length,
            typeArguments.Length == 0 ? null : typeArguments,
            throwOnException: true))
            ?? throw new InvalidOperationException("Failed to create a default value type");
        return CilValue.InBox(value);
    }

    private static bool IsReferenceType(ResolvedCilType type)
    {
        if (type.Primitive is PrimitiveTypeCode.String or PrimitiveTypeCode.Object)
        {
            return true;
        }

        if (type.ElementType is not null)
        {
            return true;
        }

        return type.RuntimeType is { } runtimeType && !EvaluationMetadataResolver.IsValueType(runtimeType);
    }

    private async Task<ICilLocation> CreateSyntheticVariableLocationAsync(ResolvedCilType type, EvaluationMetadataResolver resolver, CompiledExpressionEvaluationContext context, EvaluationHandleScope handles)
    {
        ICorDebugValue arrayReference = await CreateArrayAsync(type, resolver.GetCorDebugType(type), 1, resolver, context, handles)
            ?? throw new InvalidOperationException("Failed to allocate synthetic variable storage");
        if (arrayReference.UnwrapDebugValue() is not ICorDebugArrayValue)
        {
            throw new InvalidOperationException("Failed to allocate synthetic variable storage");
        }

        return new SyntheticVariableLocation(arrayReference);
    }

    private async Task<ICorDebugValue?> CreateArrayAsync(
        ResolvedCilType elementCilType,
        ICorDebugType elementType,
        uint length,
        EvaluationMetadataResolver resolver,
        CompiledExpressionEvaluationContext context,
        EvaluationHandleScope handles)
    {
        ICorDebugEval eval = context.Thread.CreateEval();
        if (IsEvalAllocatableArrayElementType(elementType))
        {
            return handles.Track(await eval.NewParameterizedArrayAsync(debugger.ProcessRuntimeEventsUntilEvalEvent, debugger.EvalStatus, elementType, length, throwOnException: true));
        }

        // NewArray makes only primitive and reference arrays; others come from Array.CreateInstance.
        ICorDebugValue arrayType = await GetSystemTypeAsync(elementCilType, resolver, context, handles) ?? throw new InvalidOperationException("Failed to resolve the element type for array allocation");
        ResolvedRuntimeMethod createInstance = resolver.ResolveRuntimeMethod("System", "Array", "CreateInstance", "System.Type", MetadataTypeNames.Of(PrimitiveTypeCode.Int32));
        ICorDebugValue lengthValue = await MaterializeAsync(CilValue.FromPrimitive(checked((int)length)), context, handles);
        return handles.Track(await eval.CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            createInstance.Function,
            0,
            null,
            2,
            [arrayType, lengthValue],
            throwOnException: true));
    }

    /// <summary>A multi-dimensional array's constructor, of each dimension's length or of its lower bound and length, run as
    /// newobj runs it: the array made in the program by Array.CreateInstance, a negative length throwing as it would there.</summary>
    private async Task<CilValue> NewArrayAsync(ResolvedCilType arrayType, CilValue[] arguments, Interpretation run)
    {
        int rank = arrayType.ArrayRank;
        bool bounded = arguments.Length == 2 * rank;
        if (!bounded && arguments.Length != rank)
        {
            throw new NotSupportedException($"An array constructor of {arguments.Length} parameters for rank {rank} is not supported");
        }
        int[] lowerBounds = new int[rank];
        int[] lengths = new int[rank];
        for (int dimension = 0; dimension < rank; dimension++)
        {
            lowerBounds[dimension] = bounded ? arguments[2 * dimension].AsInt32() : 0;
            lengths[dimension] = arguments[bounded ? 2 * dimension + 1 : dimension].AsInt32() is >= 0 and int length ? length : throw new OverflowException();
        }
        EvaluationMetadataResolver resolver = run.Resolver;
        ICorDebugValue elementType = await GetSystemTypeAsync(arrayType.ElementType!, resolver, run.Context, run.Handles)
            ?? throw new InvalidOperationException("Failed to resolve the element type for array allocation");
        string integers = MetadataTypeNames.Of(PrimitiveTypeCode.Int32) + "[]";
        ResolvedRuntimeMethod createInstance = resolver.ResolveRuntimeMethod("System", "Array", "CreateInstance", "System.Type", integers, integers);
        ICorDebugValue[] createArguments = [elementType, await IntegersAsync(lengths, run), await IntegersAsync(lowerBounds, run)];
        ICorDebugValue? array = run.Handles.Track(await run.Context.Thread.CreateEval().CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            createInstance.Function,
            0,
            null,
            createArguments.Length,
            createArguments,
            throwOnException: true));
        return array is null ? CilValue.Null() : CilValue.FromCorValue(array);
    }

    /// <summary>An int array of the program's holding the values given, for a call that takes one.</summary>
    private async Task<ICorDebugValue> IntegersAsync(int[] values, Interpretation run)
    {
        ResolvedCilType int32 = new ResolvedCilType(PrimitiveTypeCode.Int32, null);
        ICorDebugValue array = await CreateArrayAsync(int32, run.Resolver.GetCorDebugType(int32), (uint)values.Length, run.Resolver, run.Context, run.Handles)
            ?? throw new InvalidOperationException("The program made no array for a call's integers");
        ICorDebugArrayValue elements = (ICorDebugArrayValue)array.UnwrapDebugValue();
        for (int position = 0; position < values.Length; position++)
        {
            new CorDebugLocation(elements.GetElementAtPosition(position)).Write(CilValue.FromPrimitive(values[position]));
        }
        return array;
    }

    private bool IsEvalAllocatableArrayElementType(ICorDebugType elementType)
    {
        if (elementType.Type is not CorElementType.VALUETYPE)
        {
            return true;
        }

        // A token names a type only within its module: a struct of the user's can share a primitive's token.
        ICorDebugClass elementClass = elementType.Class;
        return primitiveTypes.CorElementToValueClassMap.Values.Any(valueClass => valueClass.Token == elementClass.Token
            && valueClass.Module.BaseAddress == elementClass.Module.BaseAddress);
    }

    /// <summary>
    /// Verifies that a boxed value can be unboxed to <paramref name="targetType"/>: the boxed value must be an
    /// exact primitive match or an exact runtime type match. Enum/underlying-type and interface-compatible boxes
    /// are intentionally not accepted (unbox.any is only emitted for value-type targets).
    /// </summary>
    private static bool IsUnboxCompatible(ICorDebugValue boxedObject, ResolvedCilType targetType, EvaluationMetadataResolver resolver)
    {
        ICorDebugValue unwrapped = boxedObject.UnwrapDebugValue();
        if (targetType.Primitive is { } primitive)
        {
            CorElementType? expectedElementType = GetPrimitiveElementType(primitive);
            return unwrapped is ICorDebugGenericValue generic && expectedElementType is not null && generic.Type == expectedElementType;
        }
        if (targetType.RuntimeType is { } runtimeType)
        {
            ICorDebugType exactType = boxedObject.ExactType;
            if (exactType.Type is not (CorElementType.VALUETYPE or CorElementType.CLASS))
            {
                return false;
            }

            // The same definition, and, for a generic one, the same arguments: a Pair<int> is no Pair<string>.
            return exactType.Class.Token == runtimeType.Class.Token
                && exactType.Class.Module.BaseAddress == runtimeType.Class.Module.BaseAddress
                && (runtimeType.TypeArguments.IsDefaultOrEmpty
                    || exactType.TypeParameters.Select(ManagedDebugger.GetCorDebugTypeFriendlyName)
                        .SequenceEqual(runtimeType.TypeArguments.Select(a => ManagedDebugger.GetCorDebugTypeFriendlyName(resolver.GetCorDebugType(a)))));
        }
        return false;
    }

    private async Task<bool> IsInstanceOfTypeAsync(CilValue value, ResolvedCilType targetType, EvaluationMetadataResolver resolver, CompiledExpressionEvaluationContext context, EvaluationHandleScope handles)
    {
        ICorDebugValue typeValue = await GetSystemTypeAsync(targetType, resolver, context, handles)
            ?? throw new InvalidOperationException("Failed to resolve the target System.Type");
        ResolvedRuntimeMethod method = resolver.ResolveRuntimeMethod("System", "Type", "IsInstanceOfType", MetadataTypeNames.Of(PrimitiveTypeCode.Object));
        ICorDebugValue sourceValue = value.CorValue ?? throw new NotSupportedException("Runtime type checks require a debuggee value");
        ICorDebugEval eval = context.Thread.CreateEval();
        ICorDebugValue? result = handles.Track(await eval.CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            method.Function,
            0,
            null,
            2,
            [typeValue, sourceValue],
            throwOnException: true));
        return result is not null && CilValue.FromCorValue(result).IsTrue();
    }

    private async Task<ICorDebugValue?> GetSystemTypeAsync(ResolvedCilType type, EvaluationMetadataResolver resolver, CompiledExpressionEvaluationContext context, EvaluationHandleScope handles)
    {
        ResolvedRuntimeMethod getType = resolver.ResolveRuntimeMethod("System", "Type", "GetType", MetadataTypeNames.Of(PrimitiveTypeCode.String));
        ICorDebugEval eval = context.Thread.CreateEval();
        ICorDebugValue typeName = handles.Track(await eval.NewStringAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            resolver.GetAssemblyQualifiedTypeName(type),
            throwOnException: true))!;
        return handles.Track(await eval.CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            getType.Function,
            0,
            null,
            1,
            [typeName],
            throwOnException: true));
    }

    private static ICorDebugArrayValue GetArrayValue(CilValue value) =>
        value.CorValue?.UnwrapDebugValue() as ICorDebugArrayValue
        ?? throw new NullReferenceException("Array reference is null");

    /// <summary>A multi-dimensional array's Get, Set or Address, on the element its indices name, as ldelem, stelem and
    /// ldelema are on a vector's; an index outside its dimension throws as the program would.</summary>
    private async Task ArrayAccessorAsync(string accessor, int rank, Stack<CilValue> stack, CompiledExpressionEvaluationContext context, EvaluationHandleScope handles)
    {
        CilValue? stored = accessor == "Set" ? stack.Pop() : null;
        CilValue[] indices = Pop(stack, rank);
        ICorDebugArrayValue array = GetArrayValue(stack.Pop());
        uint[] dimensions = array.GetDimensions(rank);
        uint[] lowest = array.HasBaseIndicies() ? array.GetBaseIndicies(rank) : new uint[rank];
        uint[] at = new uint[rank];
        for (int dimension = 0; dimension < rank; dimension++)
        {
            long index = indices[dimension].AsInt32();
            if (index < (int)lowest[dimension] || index - (int)lowest[dimension] >= dimensions[dimension])
            {
                throw new IndexOutOfRangeException("Index was outside the bounds of the array.");
            }
            at[dimension] = unchecked((uint)index);
        }
        CorDebugLocation element = new(array.GetElement(rank, at));
        switch (accessor)
        {
            case "Get":
                stack.Push(handles.Root(element.Read()));
                break;
            case "Address":
                stack.Push(CilValue.FromLocation(element));
                break;
            default:
                element.Write(await MaterializeForStoreAsync(stored!, context, handles));
                break;
        }
    }

    private static ICorDebugObjectValue GetFieldReceiver(CilValue receiver)
    {
        ICorDebugValue? corValue = receiver.CorValue;
        if (corValue is null && receiver.Location is IDebuggeeLocation directLocation)
        {
            corValue = directLocation.Value;
        }
        else if (corValue is null && receiver.Location is not null)
        {
            corValue = receiver.Location.Read().CorValue;
        }

        return corValue?.UnwrapDebugValueToObject() ?? throw new NullReferenceException("Instance field receiver is null");
    }

    private async Task<CilValue> BoxAsync(CilValue value, ICorDebugType targetType, CompiledExpressionEvaluationContext context, EvaluationHandleScope handles)
    {
        if (value.Location is not null)
        {
            value = value.Dereference();
        }

        byte[] sourceBytes = value.CorValue?.UnwrapDebugValue() is ICorDebugGenericValue sourceGeneric
            ? sourceGeneric.GetValueAsBytes()
            : value.Value is { } primitive
                ? CilValueEncoding.GetBytes(primitive, GetPrimitiveElementType(primitive))
                : throw new InvalidOperationException("Cannot box a null value");
        ICorDebugType[] typeArguments = targetType.TypeParameters;
        ICorDebugEval eval = context.Thread.CreateEval();
        ICorDebugValue boxed = handles.Track(await eval.NewParameterizedObjectNoConstructorAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            targetType.Class,
            typeArguments.Length,
            typeArguments.Length == 0 ? null : typeArguments,
            throwOnException: true))
            ?? throw new InvalidOperationException("Failed to box CIL value");
        ICorDebugGenericValue boxedGeneric = (ICorDebugGenericValue)boxed.UnwrapDebugValue();
        unsafe
        {
            fixed (byte* pointer = sourceBytes)
            {
                boxedGeneric.SetValue((nint)pointer);
            }
        }
        return CilValue.FromCorValue(boxed);
    }

    private async Task<ICorDebugValue> MaterializeForCallAsync(CilValue value, CompiledExpressionEvaluationContext context, EvaluationHandleScope handles)
    {
        if (value.Location is not null)
        {
            value = value.Dereference();
        }

        if (value.CorValue is not null)
        {
            return value.CorValue;
        }

        return await MaterializeAsync(value, context, handles);
    }

    private async Task<ICorDebugValue> MaterializeForCallAsync(
        CilValue value,
        CompiledEvaluationMethod compiled,
        EvaluationMetadataResolver resolver,
        DebuggeeDelegateAssemblyLoader delegateAssemblyLoader,
        CompiledExpressionEvaluationContext context,
        EvaluationHandleScope handles)
    {
        if (value.Location is not null)
        {
            value = value.Dereference();
        }

        return value.Value switch
        {
            EvaluationDelegate => await MaterializeDelegateAsync(value, compiled, resolver, delegateAssemblyLoader, context, handles),
            EvaluationClosure evaluationClosure => await MaterializeEvaluationClosureAsync(evaluationClosure, compiled, resolver, delegateAssemblyLoader, context, handles),
            _ => await MaterializeForCallAsync(value, context, handles)
        };
    }

    private async Task<ICorDebugValue> MaterializeDelegateAsync(
        CilValue value,
        CompiledEvaluationMethod compiled,
        EvaluationMetadataResolver resolver,
        DebuggeeDelegateAssemblyLoader delegateAssemblyLoader,
        CompiledExpressionEvaluationContext context,
        EvaluationHandleScope handles)
    {
        EvaluationDelegate delegateValue = value.Value as EvaluationDelegate ?? throw new InvalidOperationException("CIL value is not an evaluation delegate");
        DelegateMaterializerAssembly materializer = delegateAssemblyLoader.Materializer;
        int delegateAssemblySessionId = 0;
        Guid methodModuleVersionId;
        if (delegateValue.Function.RuntimeMethod is { } runtimeMethod)
        {
            methodModuleVersionId = runtimeMethod.DeclaringType.Module.MetadataReader.Mvid;
        }
        else
        {
            methodModuleVersionId = compiled.ModuleVersionId;
            delegateAssemblySessionId = await delegateAssemblyLoader.GetSessionIdAsync();
        }

        ICorDebugValue target = delegateValue.Target.IsNull
            ? await MaterializeAsync(CilValue.Null(), context, handles)
            : await MaterializeForCallAsync(delegateValue.Target, compiled, resolver, delegateAssemblyLoader, context, handles);
        ICorDebugFunction factory = await delegateAssemblyLoader.GetMaterializerFunctionAsync(materializer.MethodName);
        ICorDebugValue sessionIdArgument = await MaterializeAsync(CilValue.FromPrimitive(delegateAssemblySessionId), context, handles);
        ICorDebugValue methodModule = await MaterializeAsync(CilValue.FromPrimitive(methodModuleVersionId.ToString("D")), context, handles);
        ICorDebugValue methodToken = await MaterializeAsync(CilValue.FromPrimitive(delegateValue.Function.MethodToken), context, handles);
        string delegateTypeName = resolver.GetAssemblyQualifiedTypeName(new ResolvedCilType(null, delegateValue.DelegateType));
        ICorDebugValue delegateType = await MaterializeAsync(CilValue.FromPrimitive(delegateTypeName), context, handles);
        ICorDebugValue contextModule = await MaterializeAsync(CilValue.FromPrimitive(resolver.CurrentFrameModuleVersionId.ToString("D")), context, handles);
        ICorDebugValue result = await context.Thread.CreateEval().CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            factory,
            0,
            null,
            6,
            [sessionIdArgument, methodModule, methodToken, delegateType, contextModule, target],
            throwOnException: true) ?? throw new InvalidOperationException("Delegate materializer returned no value");
        return handles.Root(CilValue.FromCorValue(result)).CorValue!;
    }

    private async Task<ICorDebugValue> MaterializeEvaluationClosureAsync(
        EvaluationClosure evaluationClosure,
        CompiledEvaluationMethod compiled,
        EvaluationMetadataResolver resolver,
        DebuggeeDelegateAssemblyLoader delegateAssemblyLoader,
        CompiledExpressionEvaluationContext context,
        EvaluationHandleScope handles)
    {
        if (evaluationClosure.MaterializedValue is not null)
        {
            return evaluationClosure.MaterializedValue;
        }

        int delegateAssemblySessionId = await delegateAssemblyLoader.GetSessionIdAsync();
        ICorDebugFunction createObject = await delegateAssemblyLoader.GetMaterializerFunctionAsync("CreateObject");
        ICorDebugValue sessionIdArgument = await MaterializeAsync(CilValue.FromPrimitive(delegateAssemblySessionId), context, handles);
        ICorDebugValue constructorToken = await MaterializeAsync(CilValue.FromPrimitive(MetadataTokens.GetToken(evaluationClosure.Constructor)), context, handles);
        ICorDebugValue created = await context.Thread.CreateEval().CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            createObject,
            0,
            null,
            2,
            [sessionIdArgument, constructorToken],
            throwOnException: true) ?? throw new InvalidOperationException("Failed to materialize generated closure object");
        ICorDebugValue value = handles.Root(CilValue.FromCorValue(created)).CorValue!;
        evaluationClosure.MaterializedValue = value;
        ICorDebugFunction setField = await delegateAssemblyLoader.GetMaterializerFunctionAsync("SetField");
        foreach ((FieldDefinitionHandle fieldHandle, ICilLocation? location) in evaluationClosure.Fields)
        {
            CilValue fieldValue = location.Read();
            if (fieldValue.Location is not null)
            {
                fieldValue = fieldValue.Dereference();
            }

            ICorDebugValue materializedField = await MaterializeForCallAsync(fieldValue, compiled, resolver, delegateAssemblyLoader, context, handles);
            if (materializedField.UnwrapDebugValue() is ICorDebugGenericValue
                {
                    Type: not (CorElementType.CLASS or CorElementType.OBJECT or CorElementType.STRING or CorElementType.SZARRAY or CorElementType.ARRAY)
                })
            {
                materializedField = (await BoxAsync(CilValue.FromCorValue(materializedField), resolver.GetCorDebugType(resolver.ResolveEvaluationFieldType(fieldHandle)), context, handles)).CorValue!;
            }
            ICorDebugValue fieldToken = await MaterializeAsync(CilValue.FromPrimitive(MetadataTokens.GetToken(fieldHandle)), context, handles);
            await context.Thread.CreateEval().CallParameterizedFunctionAsync(
                debugger.ProcessRuntimeEventsUntilEvalEvent,
                debugger.EvalStatus,
                setField,
                0,
                null,
                4,
                [sessionIdArgument, fieldToken, value, materializedField],
                throwOnException: true);
        }
        return value;
    }

    /// <summary>
    /// Normalizes a value before it is written into a debuggee location. Host values that have no debuggee
    /// representation yet (e.g. strings produced by <c>ldstr</c>) must be materialized into the debuggee first,
    /// because <see cref="CorDebugLocation.Write"/> can only persist values backed by an ICorDebugValue.
    /// </summary>
    private async Task<CilValue> MaterializeForStoreAsync(CilValue value, CompiledExpressionEvaluationContext context, EvaluationHandleScope handles)
    {
        if (value.Location is not null)
        {
            value = value.Dereference();
        }

        if (value.CorValue is not null || value.IsNull)
        {
            return value;
        }

        if (value.Value is string text)
        {
            ICorDebugEval eval = context.Thread.CreateEval();
            ICorDebugValue? materialized = handles.Track(await eval.NewStringAsync(
                debugger.ProcessRuntimeEventsUntilEvalEvent,
                debugger.EvalStatus,
                text,
                throwOnException: true));
            return CilValue.FromDebuggeeValue(materialized ?? throw new InvalidOperationException($"The debuggee made no string for \"{text}\""));
        }
        return value;
    }

    /// <summary>stloc, starg, stobj and cpobj: into a variable of the evaluation's own, a struct as a copy of its own.</summary>
    private async Task StoreAsync(ICilLocation location, CilValue value, CompiledExpressionEvaluationContext context, EvaluationHandleScope handles)
    {
        CilValue stored = await MaterializeForStoreAsync(value, context, handles);
        location.Write(location is TemporaryLocation ? await OwnAsync(stored, context, handles) : stored);
    }

    /// <summary>A value as a variable or argument of the evaluation's own holds it: a struct copied into a box of the
    /// evaluation's, so a call or a field's store through that variable changes the copy, never the program's variable,
    /// field or element the struct was read from.</summary>
    private async Task<CilValue> OwnAsync(CilValue value, CompiledExpressionEvaluationContext context, EvaluationHandleScope handles) =>
        value.Struct is { } read ? CilValue.InBox((await BoxAsync(value, read.ExactType, context, handles)).CorValue!) : value;

    private async Task<ICorDebugValue> MaterializeByRefArgumentAsync(
        CilValue value,
        EvaluationMetadataResolver resolver,
        CompiledExpressionEvaluationContext context,
        EvaluationHandleScope handles,
        List<(ICilLocation Location, ICilLocation Lent)> temporaryArguments)
    {
        if (value.Location is IDebuggeeLocation location)
        {
            return location.Value;
        }

        if (value.Location is SyntheticVariableLocation synthetic)
        {
            return synthetic.StorageValue;
        }

        if (value.Location is null)
        {
            throw new InvalidOperationException("A by-reference argument requires a managed location");
        }

        ResolvedCilType? declared = (value.Location as TemporaryLocation)?.DeclaredType;
        if (value.Location is TemporaryLocation { Written: false } && declared is not null)
        {
            value.Location.Write(await CreateDefaultValueAsync(declared, resolver, context, handles));
        }
        CilValue held = value.Location.Read();
        if (held.Struct is not null)
        {
            // The evaluation's box, changed by the call where it is.
            return await MaterializeForCallAsync(held, context, handles);
        }
        if ((declared ?? PrimitiveCilType(held.Value)) is { } type)
        {
            // Lent from an array of one, whose element the runtime writes the call's change back to.
            SyntheticVariableLocation home = new(await CreateArrayAsync(type, resolver.GetCorDebugType(type), 1, resolver, context, handles)
                ?? throw new InvalidOperationException("The program made no array for a by-ref argument"));
            home.Write(await MaterializeForStoreAsync(held, context, handles));
            temporaryArguments.Add((value.Location, home));
            return home.StorageValue;
        }
        ICorDebugValue materialized = await MaterializeForCallAsync(held, context, handles);
        temporaryArguments.Add((value.Location, new CorDebugLocation(materialized)));
        return materialized;
    }

    /// <summary>An array of the evaluation's own, filled and pinned until the evaluation ends: a home for values a span keeps
    /// a reference to, which no collection moves while the evaluation reads through the span.</summary>
    private async Task<CilValue> PinnedArrayAsync(ResolvedCilType elementType, int length, Action<ICorDebugArrayValue> fill, Interpretation run)
    {
        ICorDebugValue array = await CreateArrayAsync(elementType, run.Resolver.GetCorDebugType(elementType), checked((uint)length), run.Resolver, run.Context, run.Handles)
            ?? throw new InvalidOperationException("The program made no array for a span's values");
        ICorDebugValue held = array.UnwrapDebugValue();
        fill((ICorDebugArrayValue)held);
        run.Handles.Track(((ICorDebugHeapValue2)held).CreateHandle(CorDebugHandleType.HANDLE_PINNED));
        return CilValue.FromCorValue(array);
    }

    /// <summary>RuntimeHelpers' InitializeArray and CreateSpan over constants the expression's compilation laid out in its own
    /// image, which the program never loads: the array's elements written from them, or a span over a pinned array of them.</summary>
    private async Task ConstantsAsync(ResolvedRuntimeMethod method, CilValue[] arguments, EvaluationFieldToken constants, Stack<CilValue> stack, Interpretation run)
    {
        EvaluationMetadataResolver resolver = run.Resolver;
        string name = $"{resolver.GetRuntimeTypeName(method.DeclaringType)}.{method.Name}";
        byte[] data = resolver.GetEvaluationFieldData(constants.Field);
        switch (name)
        {
            case "System.Runtime.CompilerServices.RuntimeHelpers.InitializeArray" when arguments.Length == 2:
                Fill(GetArrayValue(arguments[0]), data);
                break;
            case "System.Runtime.CompilerServices.RuntimeHelpers.CreateSpan" when method.MethodTypeArguments is [var element]
                && EvaluationMetadataResolver.SizeOf(element) is > 0 and var size && data.Length % size == 0:
            {
                CilValue array = await PinnedArrayAsync(element, data.Length / size, made => Fill(made, data), run);
                ResolvedRuntimeMethod constructor = resolver.ResolveRuntimeMethod("System", "ReadOnlySpan`1", ".ctor", "!0[]");
                ICorDebugType[] typeArguments = [resolver.GetCorDebugType(element)];
                ICorDebugValue? span = run.Handles.Track(await run.Context.Thread.CreateEval().NewParameterizedObjectAsync(
                    debugger.ProcessRuntimeEventsUntilEvalEvent,
                    debugger.EvalStatus,
                    constructor.Function,
                    typeArguments.Length,
                    typeArguments,
                    1,
                    [array.CorValue!],
                    throwOnException: true));
                stack.Push(span is null ? CilValue.Null() : CilValue.FromCorValue(span));
                break;
            }
            default:
                throw new NotSupportedException($"{name} over the expression's own constants is not supported");
        }
    }

    /// <summary>An array's elements written from their values' bytes laid out one after another, as the runtime holds them.</summary>
    private static void Fill(ICorDebugArrayValue array, byte[] data)
    {
        int offset = 0;
        for (int position = 0; position < array.Count; position++)
        {
            ICorDebugGenericValue element = array.GetElementAtPosition(position) as ICorDebugGenericValue
                ?? throw new InvalidOperationException("The array's elements are not values constants can fill");
            int size = element.Size;
            if (offset + size > data.Length)
            {
                throw new InvalidOperationException($"The expression's constants fill {data.Length} bytes, fewer than the array's {array.Count} elements hold");
            }
            unsafe
            {
                fixed (byte* pointer = &data[offset])
                {
                    element.SetValue((nint)pointer);
                }
            }
            offset += size;
        }
    }

    /// <summary>Refuses a call that may keep a reference to a by-ref argument the runtime lends it as a copy of its own,
    /// gone once the call returns (a primitive, wherever it lives): what the call made would read whatever comes there next.</summary>
    private static void RefuseLentCopies(ResolvedRuntimeMethod method, ICorDebugValue[] arguments, EvaluationMetadataResolver resolver)
    {
        int first = method.IsStatic || method.Name == ".ctor" ? 0 : 1;
        for (int parameter = 0; parameter < method.Signature.ParameterTypes.Length; parameter++)
        {
            if (method.Signature.ParameterTypes[parameter].EndsWith('&') && IsLentACopy(arguments[first + parameter]))
            {
                string name = $"{resolver.GetRuntimeTypeName(method.DeclaringType)}.{method.Name}";
                throw new ManagedDebugger.EvalException(
                    $"Not evaluated: {name} may keep a reference to its argument {parameter + 1}, which the debugger can lend it only until it returns");
            }
        }
    }

    /// <summary>Whether the runtime passes a call this by-ref argument as a copy of its own: anything but a struct with a
    /// home in the program's memory, or the box the evaluation holds a struct in.</summary>
    private static bool IsLentACopy(ICorDebugValue argument) => argument is ICorDebugReferenceValue reference
        ? reference.IsNull || reference.Dereference() is not ICorDebugBoxValue
        : argument.UnwrapDebugValue() is not ICorDebugGenericValue { Type: CorElementType.VALUETYPE }
            || argument.TryGetAddress(out CORDB_ADDRESS address) != Cor.S_OK || address.Value == 0;

    /// <summary>Whether a value is a ref struct's, which may hold a reference to where another value lives.</summary>
    private static bool IsByRefLike(ICorDebugValue value)
    {
        try
        {
            if (value.ExactType is not { Type: CorElementType.VALUETYPE } type)
            {
                return false;
            }
            ICorDebugClass corClass = type.Class;
            return corClass.Module.GetMetaDataInterface<IMetaDataImport>()
                .TryGetCustomAttributeByName(corClass.Token, EvaluationMetadataResolver.ByRefLikeAttribute, out _, out _) is Cor.S_OK;
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return false;
        }
    }

    private static void WriteBackTemporaryByRefArguments(IEnumerable<(ICilLocation Location, ICilLocation Lent)> arguments, EvaluationHandleScope handles)
    {
        foreach ((ICilLocation? location, ICilLocation? lent) in arguments)
        {
            location.Write(handles.Root(lent.Read()));
        }
    }

    /// <summary>The primitive type a value the interpreter holds itself is of; null for any other value.</summary>
    private static ResolvedCilType? PrimitiveCilType(object? value) => value switch
    {
        bool => new(PrimitiveTypeCode.Boolean, null),
        char => new(PrimitiveTypeCode.Char, null),
        sbyte => new(PrimitiveTypeCode.SByte, null),
        byte => new(PrimitiveTypeCode.Byte, null),
        short => new(PrimitiveTypeCode.Int16, null),
        ushort => new(PrimitiveTypeCode.UInt16, null),
        int => new(PrimitiveTypeCode.Int32, null),
        uint => new(PrimitiveTypeCode.UInt32, null),
        long => new(PrimitiveTypeCode.Int64, null),
        ulong => new(PrimitiveTypeCode.UInt64, null),
        float => new(PrimitiveTypeCode.Single, null),
        double => new(PrimitiveTypeCode.Double, null),
        _ => null,
    };

    private async Task<ICorDebugValue> MaterializeReceiverAsync(
        CilValue value,
        CompiledExpressionEvaluationContext context,
        ResolvedCilType? constrainedType,
        EvaluationMetadataResolver resolver,
        EvaluationHandleScope handles)
    {
        ICorDebugThread thread = context.Thread;
        // A reference is the receiver itself: an object's, or a box the evaluation holds a struct in.
        if (constrainedType is not null && value.CorValue is not ICorDebugReferenceValue && value.CorValue?.UnwrapDebugValue() is ICorDebugGenericValue sourceGeneric)
        {
            ICorDebugType exactType = value.CorValue.ExactType;
            ICorDebugType[] typeArguments = exactType.TypeParameters;
            ICorDebugEval constrainedEval = thread.CreateEval();
            ICorDebugValue constrainedBox = handles.Track(await constrainedEval.NewParameterizedObjectNoConstructorAsync(
                debugger.ProcessRuntimeEventsUntilEvalEvent,
                debugger.EvalStatus,
                exactType.Class,
                typeArguments.Length,
                typeArguments.Length == 0 ? null : typeArguments,
                throwOnException: true))
                ?? throw new InvalidOperationException("Failed to box constrained CIL receiver");
            ICorDebugGenericValue constrainedGeneric = (ICorDebugGenericValue)constrainedBox.UnwrapDebugValue();
            byte[] constrainedData = sourceGeneric.GetValueAsBytes();
            unsafe
            {
                fixed (byte* pointer = constrainedData)
                {
                    constrainedGeneric.SetValue((nint)pointer);
                }
            }
            return constrainedBox;
        }
        if (value.CorValue is not null)
        {
            return value.CorValue;
        }

        // A string the expression wrote is made in the program, as an argument's is.
        if (value.Value is null or string)
        {
            return await MaterializeForCallAsync(value, context, handles);
        }

        CorElementType elementType = GetPrimitiveElementType(value.Value);
        if (!primitiveTypes.CorElementToValueClassMap.TryGetValue(elementType, out ICorDebugClass? boxedClass))
        {
            return await MaterializeForCallAsync(value, context, handles);
        }
        byte[] data = CilValueEncoding.GetBytes(value.Value, elementType);

        ICorDebugEval eval = thread.CreateEval();
        ICorDebugValue boxed = handles.Track(await eval.NewParameterizedObjectNoConstructorAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            boxedClass,
            0,
            null,
            throwOnException: true)) ?? throw new InvalidOperationException("Failed to box CIL receiver");
        ICorDebugGenericValue boxedGeneric = (ICorDebugGenericValue)boxed.UnwrapDebugValue();
        unsafe
        {
            fixed (byte* pointer = data)
            {
                boxedGeneric.SetValue((nint)pointer);
            }
        }
        return boxed;
    }

    private static CorElementType GetPrimitiveElementType(object value) => value switch
    {
        bool => CorElementType.BOOLEAN,
        char => CorElementType.CHAR,
        sbyte => CorElementType.I1,
        byte => CorElementType.U1,
        short => CorElementType.I2,
        ushort => CorElementType.U2,
        int => CorElementType.I4,
        uint => CorElementType.U4,
        long => CorElementType.I8,
        ulong => CorElementType.U8,
        float => CorElementType.R4,
        double => CorElementType.R8,
        _ => throw new NotSupportedException($"Value '{value.GetType().Name}' is not a primitive CIL value")
    };

    // The calls run here rather than in the program, which cannot run them: those of DefaultInterpolatedStringHandler
    // that interpolated strings are lowered to. Pointer and span arithmetic (Unsafe, MemoryMarshal, span-based
    // String.Join) is not modelled, so such an expression fails rather than give a wrong value.
    private async Task<(bool Handled, CilValue? Result)> TryExecuteIntrinsicCallAsync(
        ResolvedRuntimeMethod method,
        CilValue? receiver,
        CilValue[] arguments,
        EvaluationMetadataResolver resolver,
        CompiledExpressionEvaluationContext context,
        EvaluationHandleScope handles)
    {
        string declaringType = resolver.GetRuntimeTypeName(method.DeclaringType);
        if (declaringType != "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler")
        {
            return (false, null);
        }

        ICilLocation? receiverLocation = receiver?.Location;
        if (method.Name == ".ctor")
        {
            receiverLocation?.Write(CilValue.FromPrimitive(new StringBuilder()));
            return (true, null);
        }
        StringBuilder builder = receiverLocation?.Read().Value as StringBuilder
            ?? receiver?.Value as StringBuilder
            ?? throw new InvalidOperationException("Interpolated string handler receiver is unavailable");
        if (method.Name == "AppendLiteral")
        {
            builder.Append(arguments[0].Value as string);
            return (true, null);
        }
        if (method.Name == "AppendFormatted")
        {
            CilValue value = arguments[0];
            int alignment = arguments.Select(a => a.Value).OfType<int>().Skip(value.Value is int ? 1 : 0).FirstOrDefault();
            string? format = arguments.Select(a => a.Value).OfType<string>().FirstOrDefault();
            string text = await FormatInterpolatedValueAsync(value, format, resolver, context, handles);
            if (alignment != 0)
            {
                text = alignment > 0 ? text.PadLeft(alignment) : text.PadRight(-alignment);
            }

            builder.Append(text);
            return (true, null);
        }
        if (method.Name is "ToStringAndClear" or "ToString")
        {
            return (true, CilValue.FromPrimitive(builder.ToString()));
        }
        return (false, null);
    }

    private async Task<string> FormatInterpolatedValueAsync(
        CilValue value,
        string? format,
        EvaluationMetadataResolver resolver,
        CompiledExpressionEvaluationContext context,
        EvaluationHandleScope handles)
    {
        if (value.Location is not null)
        {
            value = value.Dereference();
        }

        if (value.IsNull)
        {
            return string.Empty;
        }

        if (value.Value is IFormattable formattable)
        {
            return formattable.ToString(format, null);
        }

        if (value.Value is not null)
        {
            return value.Value.ToString() ?? string.Empty;
        }

        if (value.GetStringText() is { } text)
        {
            return text;
        }

        ICorDebugValue receiver = value.CorValue ?? throw new InvalidOperationException("Interpolated value is unavailable");
        bool isDebuggeeValueType = receiver.ExactType.Type == CorElementType.VALUETYPE;
        if (isDebuggeeValueType)
        {
            receiver = (await BoxAsync(value, receiver.ExactType, context, handles)).CorValue!;
        }
        ResolvedRuntimeMethod concat = resolver.ResolveRuntimeMethod("System", "String", "Concat", MetadataTypeNames.Of(PrimitiveTypeCode.Object));
        ICorDebugEval eval = context.Thread.CreateEval();
        ICorDebugValue? result = handles.Track(await eval.CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            concat.Function,
            0,
            null,
            1,
            [receiver],
            throwOnException: true));
        return result?.UnwrapDebugValue() is ICorDebugStringValue stringValue ? stringValue.String : string.Empty;
    }
}
