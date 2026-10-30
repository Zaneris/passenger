using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public static class Extensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsLiteral(this mdFieldDef mdFieldDef, IMetaDataImport metadataImport)
    {
        (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) fieldProps = metadataImport.GetFieldProps(mdFieldDef);
        bool isStatic = fieldProps.pdwAttr.IsFdLiteral();
        return isStatic;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStatic(this mdFieldDef mdFieldDef, IMetaDataImport metadataImport)
    {
        (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) fieldProps = metadataImport.GetFieldProps(mdFieldDef);
        bool isStatic = fieldProps.pdwAttr.IsFdStatic();
        return isStatic;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPublic(this mdFieldDef mdFieldDef, IMetaDataImport metadataImport)
    {
        (string szField, mdTypeDef pClass, CorFieldAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, CorElementType pdwCPlusTypeFlag, nint ppValue, int pcchValue) fieldProps = metadataImport.GetFieldProps(mdFieldDef);
        bool isPublic = fieldProps.pdwAttr.IsFdPublic();
        return isPublic;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPublic(this mdProperty mdProperty, IMetaDataImport metadataImport)
    {
        (string szProperty, mdTypeDef pClass, CorPropertyAttr pdwPropFlags, nint ppvSig, int pbSig, CorElementType pdwCPlusTypeFlag, nint ppDefaultValue, int pcchDefaultValue, mdMethodDef pmdSetter, mdMethodDef pmdGetter, mdMethodDef rmdOtherMethod, int pcOtherMethod) propertyProps = metadataImport.GetPropertyProps(mdProperty);
        (string szMethod, mdTypeDef pClass, CorMethodAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, int pulCodeRVA, int pdwImplFlags) getterMethodProps = metadataImport.GetMethodProps(propertyProps.pmdGetter);
        bool isPublic = getterMethodProps.pdwAttr.IsMdPublic();
        return isPublic;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStatic(this mdProperty mdProperty, IMetaDataImport metadataImport)
    {
        (string szProperty, mdTypeDef pClass, CorPropertyAttr pdwPropFlags, nint ppvSig, int pbSig, CorElementType pdwCPlusTypeFlag, nint ppDefaultValue, int pcchDefaultValue, mdMethodDef pmdSetter, mdMethodDef pmdGetter, mdMethodDef rmdOtherMethod, int pcOtherMethod) propertyProps = metadataImport.GetPropertyProps(mdProperty);
        (string szMethod, mdTypeDef pClass, CorMethodAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, int pulCodeRVA, int pdwImplFlags) getterMethodProps = metadataImport.GetMethodProps(propertyProps.pmdGetter);
        bool isStatic = getterMethodProps.pdwAttr.IsMdStatic();
        return isStatic;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsStatic(this mdMethodDef methodToken, IMetaDataImport metaDataImport)
    {
        (string szMethod, mdTypeDef pClass, CorMethodAttr pdwAttr, nint ppvSigBlob, int pcbSigBlob, int pulCodeRVA, int pdwImplFlags) methodProps = metaDataImport.GetMethodProps(methodToken);
        bool isStatic = methodProps.pdwAttr.IsMdStatic();
        return isStatic;
    }

    // The '+' form of a nested type's name, which a custom attribute's Type argument writes.
    public static mdTypeDef? FindMaybeNestedTypeDefByNameOrNull(this IMetaDataImport metadataImport, string typeName)
    {
        string[] nestedClasses = typeName.Split('+');
        mdTypeDef? enclosingClass = null;
        foreach (string nestedClass in nestedClasses)
        {
            mdTypeDef? mdTypeDef = metadataImport.FindTypeDefByNameOrNull(nestedClass, enclosingClass ?? mdToken.Nil);
            if (mdTypeDef is null)
            {
                return null;
            }

            enclosingClass = mdTypeDef;
        }
        return enclosingClass;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static mdTypeDef? FindTypeDefByNameOrNull(this IMetaDataImport metadataImport, string typeName, mdToken enclosingClass)
    {
        int result = metadataImport.TryFindTypeDefByName(typeName, enclosingClass, out mdTypeDef mdTypeDef);
        if (result is Cor.S_OK)
        {
            return mdTypeDef;
        }

        return null;
    }

    public static mdTypeDef? FindTypeDefByNameOrNullInCandidateNamespaces(this IMetaDataImport metadataImport, string typeName, mdToken enclosingClass, ImmutableArray<string> candidateNamespaces)
    {
        foreach (string candidateNamespace in candidateNamespaces)
        {
            string fullTypeName = string.IsNullOrEmpty(candidateNamespace) ? typeName : $"{candidateNamespace}.{typeName}";
            int result = metadataImport.TryFindTypeDefByName(fullTypeName, enclosingClass, out mdTypeDef mdTypeDef);
            if (result is Cor.S_OK)
            {
                return mdTypeDef;
            }
        }
        return null;
    }

    // https://github.com/Samsung/netcoredbg/blob/8b8b22200fecdb1aec5f47af63215462d8c79a4b/src/debugger/evaluator.cpp#L695
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsCompilerGeneratedFieldName(string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        if (fieldName.Length > 1 && fieldName.StartsWith('<'))
        {
            return true;
        }

        if (fieldName.Length > 4 && fieldName.StartsWith("CS$<", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static mdProperty? GetPropertyWithName(this IMetaDataImport metaDataImport, mdTypeDef mdTypeDef, string propertyName)
    {
        IEnumerable<mdProperty> properties = metaDataImport.EnumProperties(mdTypeDef);

        foreach (mdProperty property in properties)
        {
            if (property.IsNil)
            {
                continue;
            }

            (string szProperty, mdTypeDef pClass, CorPropertyAttr pdwPropFlags, nint ppvSig, int pbSig, CorElementType pdwCPlusTypeFlag, nint ppDefaultValue, int pcchDefaultValue, mdMethodDef pmdSetter, mdMethodDef pmdGetter, mdMethodDef rmdOtherMethod, int pcOtherMethod) propertyProps = metaDataImport.GetPropertyProps(property);
            if (propertyProps.szProperty == propertyName)
            {
                return property;
            }
        }

        return null;
    }

    public static bool HasAnyAttribute(this IMetaDataImport metadataImport, mdToken token, string[] attributeNames)
    {
        foreach (string attributeName in attributeNames)
        {
            if (metadataImport.TryGetCustomAttributeByName(token, attributeName, out _, out _) is Cor.S_OK)
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsExtensionMethod(this IMetaDataImport metadataImport, mdToken token)
    {
        return metadataImport.HasAnyAttribute(token, [AttributeConstants.ExtensionMethodAttributeName]);
    }

    internal static async Task<ICorDebugValue?> CallParameterlessInstanceMethodAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugFunction corDebugFunction, ICorDebugValue corDebugValue)
    {
        const bool isStatic = false;

        ICorDebugType[] typeParameterArgs = corDebugValue.ExactType.TypeParameters;

        // An instance property is given the original reference value, never the dereferenced one.
        ICorDebugValue[] corDebugValues = isStatic ? [] : [corDebugValue];
        ICorDebugValue? result = await eval.CallParameterizedFunctionAsync(processEventsUntilEvalEventFunc, evalStatus, corDebugFunction, typeParameterArgs.Length, typeParameterArgs, corDebugValues.Length, corDebugValues);
        return result;
    }

    internal static async Task<ICorDebugValue?> CallParameterizedFunctionAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugFunction corDebugFunction, int typeParamCount, ICorDebugType[]? typeParameterArgs, int paramCount, ICorDebugValue[] corDebugValues, bool throwOnException = false)
    {
        return await RunEvalAsync(eval, processEventsUntilEvalEventFunc, evalStatus, throwOnException,
            () => eval.CallParameterizedFunction(corDebugFunction, typeParamCount, typeParameterArgs, paramCount, corDebugValues),
            e =>
            {
                int getResultResult = e.Eval.TryGetResult(out ICorDebugValue? result);
                if (getResultResult is not Cor.CORDBG_S_FUNC_EVAL_HAS_NO_RESULT && result is null)
                {
                    Marshal.ThrowExceptionForHR(getResultResult);
                }

                return result;
            });
    }

    internal static async Task<ICorDebugValue?> NewParameterizedObjectNoConstructorAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugClass pClass, int nTypeArgs, ICorDebugType[]? ppTypeArgs, bool throwOnException = false)
    {
        return await RunEvalAsync(eval, processEventsUntilEvalEventFunc, evalStatus, throwOnException,
            () => eval.NewParameterizedObjectNoConstructor(pClass, nTypeArgs, ppTypeArgs),
            e => e.Eval.Result);
    }

    internal static async Task<ICorDebugValue?> NewParameterizedObjectAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugFunction corDebugFunction, int nTypeArgs, ICorDebugType[]? ppTypeArgs, int argCount, ICorDebugValue[] argValues, bool throwOnException = false)
    {
        return await RunEvalAsync(eval, processEventsUntilEvalEventFunc, evalStatus, throwOnException,
            () => eval.NewParameterizedObject(corDebugFunction, nTypeArgs, ppTypeArgs, argCount, argValues),
            e => e.Eval.Result);
    }

    internal static async Task<ICorDebugValue?> NewParameterizedArrayAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, ICorDebugType elementType, uint length, bool throwOnException = false)
    {
        return await RunEvalAsync(eval, processEventsUntilEvalEventFunc, evalStatus, throwOnException,
            () => eval.NewParameterizedArray(elementType, 1, [length], [0]),
            e => e.Eval.Result);
    }

    /// <summary>A string made in the program, every character of it: its length is given, so a NUL inside it is kept.</summary>
    internal static async Task<ICorDebugValue> NewStringAsync(this ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, string str, bool throwOnException = false)
    {
        return (await RunEvalAsync(eval, processEventsUntilEvalEventFunc, evalStatus, throwOnException,
            () => eval.NewStringWithLength(str),
            e => e.Eval.Result))!;
    }

    /// <summary>How long code the debugger runs in the program (a getter, a watch's call) may take before it is
    /// stopped, so one that never returns cannot hang the debugger. A static of the adapter's process, which sets it
    /// from <see cref="Dap.AdapterEnvironment.EvaluationTimeout"/>; the waits for an abort to take scale with
    /// it.</summary>
    public static TimeSpan EvaluationTimeout { get; set; } = Dap.AdapterEnvironment.DefaultEvaluationTimeout;

    /// <summary>How long an evaluation past its time is looked at for a moment it runs managed code, and how long an abort
    /// asked then may take, before the evaluation is left to run on to its own end: 2 s of the 5 s, longer on a slower
    /// machine by <see cref="Core.Waits.Scale"/>, as both wait on the program.</summary>
    internal static TimeSpan AbortWait => Core.Waits.Of(EvaluationTimeout * 0.4);

    /// <summary>How often an evaluation past its time is looked at, the program held a moment each time.</summary>
    internal static readonly TimeSpan LookEvery = TimeSpan.FromMilliseconds(25);

    /// <summary>Why a read's evaluation did not run, or was left to end in the program: a continue, a step or a stop came for
    /// the program, which does not wait for it (<see cref="ManagedDebugger.Leaving"/>).</summary>
    internal const string NotEvaluated = "Not evaluated: the program went on";
    private const string GaveWay = "The program went on before the evaluation ended";

    private static async Task<ICorDebugValue?> RunEvalAsync(ICorDebugEval eval, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus, bool throwOnException, Action startEval, Func<EvalCompleteCorDebugManagedCallbackEventArgs, ICorDebugValue?> onComplete)
    {
        ICorDebugValue? returnValue = null;
        ICorDebugValue? thrown = null;

        ICorDebugThread thread = eval.Thread;
        if (evalStatus.ImplicitOff is { } off && !evalStatus.Explicit)
        {
            throw new ManagedDebugger.EvalException(off);
        }
        CancellationToken leaving = evalStatus.Leaving;
        if (leaving.IsCancellationRequested)
        {
            throw new ManagedDebugger.EvalException(NotEvaluated);
        }
        if (evalStatus.Abandoned.ContainsKey(thread.Id))
        {
            throw new ManagedDebugger.EvalException("An evaluation that could not be stopped is still running on this thread, so no other can run on it until it returns");
        }

        try
        {
            startEval();
        }
        catch (COMException refused)
        {
            // Nothing ran: the runtime runs no code on a thread a pause caught in optimized or native code.
            throw new ManagedDebugger.EvalRefusedException(refused);
        }

        ICorDebugProcess process = thread.Process;
        evalStatus.ThreadId = thread.Id;
        CancellationTokenSource reading = new();
        Task<CorDebugManagedCallbackEventArgs> waiting = Task.FromCanceled<CorDebugManagedCallbackEventArgs>(new CancellationToken(true));
        try
        {
            // Only the evaluation's thread runs: the others' values hold still and none meets a breakpoint unseen.
            process.SetAllThreadsDebugState(CorDebugThreadState.THREAD_SUSPEND, thread);
            process.Continue(false);
            waiting = Read();
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            bool ended = await Task.WhenAny(waiting, Task.Delay(EvaluationTimeout, leaving)) == waiting;
            bool lookedAt = !ended && !leaving.IsCancellationRequested;
            bool asked = false;
            string? lastWhy = null;
            if (lookedAt)
            {
                // Asked only of a thread caught where the abort takes as it goes on, never left pending (ManagedDebugger_Aborts.cs).
                long lookUntil = System.Diagnostics.Stopwatch.GetTimestamp() + Ticks(AbortWait);
                while (!ended && !leaving.IsCancellationRequested && System.Diagnostics.Stopwatch.GetTimestamp() < lookUntil)
                {
                    if (await GiveUp() is not null)
                    {
                        ended = true;
                        break;
                    }
                    if (process.TryStop(0) is var held and not Cor.S_OK)
                    {
                        evalStatus.Log?.Invoke($"The program could not be held to look at the evaluation on thread {thread.Id}: 0x{held:X8}");
                        break;
                    }
                    string? why = asked ? null : evalStatus.WhyNoAbortTakes(thread);
                    if (!asked && why is null)
                    {
                        evalStatus.Log?.Invoke($"The evaluation on thread {thread.Id} runs managed code past its time, and is aborted");
                        eval.TryAbort();
                        asked = true;
                        lookUntil = System.Diagnostics.Stopwatch.GetTimestamp() + Ticks(AbortWait);
                    }
                    else if (why is not null && why != lastWhy)
                    {
                        evalStatus.Log?.Invoke($"The evaluation on thread {thread.Id} is past its time and not aborted: {why}");
                        lastWhy = why;
                    }
                    process.Continue(false);
                    waiting = Read();
                    ended = await Task.WhenAny(waiting, Task.Delay(LookEvery, leaving)) == waiting;
                }
            }
            if (!ended && leaving.IsCancellationRequested)
            {
                // Left to the request that leaves the stop, aborted only once due: an abort holds the runtime 0.5 s.
                ended = await GiveUp() is not null;
                if (!ended)
                {
                    process.Stop(0);
                    evalStatus.Abandoned[thread.Id] = eval;
                    evalStatus.DueAt[thread.Id] = began + Ticks(EvaluationTimeout);
                    evalStatus.AbortWhenDue?.Invoke(thread.Id, eval, EvaluationTimeout - System.Diagnostics.Stopwatch.GetElapsedTime(began));
                    throw new ManagedDebugger.EvalException(GaveWay);
                }
            }
            if (lookedAt)
            {
                ended = ended || await GiveUp() is not null;
                bool aborted = ended && eval.TryGetResult(out _) is Cor.CORDBG_S_FUNC_EVAL_ABORTED;
                if (!ended)
                {
                    // Left to run on in the program, paused as the IDE shows it; its end is let go whenever it comes.
                    process.Stop(0);
                    evalStatus.Abandoned[thread.Id] = eval;
                    evalStatus.DueAt.Remove(thread.Id);
                    evalStatus.Log?.Invoke(asked ? $"The abort asked of the evaluation on thread {thread.Id} has not taken; it is left to run on"
                        : $"The evaluation on thread {thread.Id} is left to run on, unaborted");
                }
                evalStatus.ImplicitOff = $"Implicit evaluation is disabled: an evaluation took longer than {EvaluationTimeout.TotalSeconds:0.#} s. It is enabled again once the program runs on";
                if (!ended || aborted)
                {
                    throw new ManagedDebugger.EvalException(aborted
                        ? $"The evaluation took longer than {EvaluationTimeout.TotalSeconds:0.#} s and was stopped: it may wait on another thread, which stays paused while it runs"
                        : $"The evaluation took longer than {EvaluationTimeout.TotalSeconds:0.#} s and could not be stopped, as {(asked ? "the abort asked of it has not taken" : lastWhy ?? "the program could not be held to look at it")}: it runs on in the program until it returns, the program paused meanwhile");
                }
                // It ended of itself as it was looked at: its value is read as any other's.
            }
            CorDebugManagedCallbackEventArgs evalEvent = await waiting;
            if (evalEvent is EvalCompleteCorDebugManagedCallbackEventArgs completeEvent)
            {
                if (completeEvent.Eval != eval)
                {
                    throw new ManagedDebugger.EvalException("EvalComplete callback error - Eval does not match");
                }

                returnValue = onComplete(completeEvent);
            }
            else if (evalEvent is EvalExceptionCorDebugManagedCallbackEventArgs exceptionEvent)
            {
                if (exceptionEvent.Eval != eval)
                {
                    throw new ManagedDebugger.EvalException("EvalException callback error - Eval does not match");
                }

                ICorDebugValue exceptionValue = exceptionEvent.Eval.Result ?? throw new ManagedDebugger.EvalException("EvalException callback error - Result is null");
                if (throwOnException)
                {
                    thrown = exceptionValue;
                }
                else
                {
                    returnValue = exceptionValue;
                }
            }
            if (thrown is null)
            {
                return returnValue;
            }
        }
        finally
        {
            evalStatus.ThreadId = null;
            evalStatus.Reading = default;
            reading.Dispose();
            // Taken at the next Continue; a process that ended meanwhile has no threads to set.
            process.TrySetAllThreadsDebugState(CorDebugThreadState.THREAD_RUN, thread);
        }

        try
        {
            string type = ManagedDebugger.GetCorDebugTypeFriendlyName(thrown.ExactType);
            throw new ManagedDebugger.EvalException(await MessageOfAsync(thread, thrown, processEventsUntilEvalEventFunc, evalStatus) is { } message
                ? $"Evaluation threw {type}: {message.ReplaceLineEndings(" ")}"
                : $"Evaluation threw {type}");
        }
        finally
        {
            if (thrown is ICorDebugHandleValue handle)
            {
                handle.TryDispose();
            }
        }

        // Reads the runtime's events, under the request's hold of the engine's lock, until the evaluation's end.
        Task<CorDebugManagedCallbackEventArgs> Read()
        {
            reading.Dispose();
            reading = new CancellationTokenSource();
            evalStatus.Reading = reading.Token;
            return processEventsUntilEvalEventFunc();
        }

        // Ends the wait for the evaluation's event, which reads the runtime's events while the request holds the engine's
        // lock and so must not outlive it; the event, if it came at the last moment.
        async Task<CorDebugManagedCallbackEventArgs?> GiveUp()
        {
            reading.Cancel();
            try
            {
                return await waiting;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    /// <summary>What an exception the program threw in an evaluation says, as its Message reads, run on the thread it was
    /// thrown on; null where that cannot be read.</summary>
    private static async Task<string?> MessageOfAsync(ICorDebugThread thread, ICorDebugValue exception, Func<Task<CorDebugManagedCallbackEventArgs>> processEventsUntilEvalEventFunc, EvalStatus evalStatus)
    {
        ICorDebugType? type = exception.ExactType;
        while (type is not null && ManagedDebugger.GetCorDebugTypeFriendlyName(type) != "System.Exception")
        {
            type = type.Base;
        }
        if (type is null)
        {
            return null;
        }
        ICorDebugValue? message;
        try
        {
            ICorDebugClass exceptionClass = type.Class;
            mdMethodDef getter = exceptionClass.Module.GetMetaDataInterface<IMetaDataImport>().FindMethod(exceptionClass.Token, "get_Message", 0, 0);
            // A virtual call, so an exception's own Message, its parameter's name or value added, is what it says.
            message = await thread.CreateEval().CallParameterizedFunctionAsync(processEventsUntilEvalEventFunc, evalStatus, exceptionClass.Module.GetFunctionFromToken(getter), 0, null, 1, [exception]);
        }
        catch (Exception error) when (error is ManagedDebugger.EvalException || Failures.OfRuntime(error))
        {
            return null;
        }
        try
        {
            return message?.UnwrapDebugValue() is ICorDebugStringValue text ? text.String : null;
        }
        finally
        {
            if (message is ICorDebugHandleValue handle)
            {
                handle.TryDispose();
            }
        }
    }

    /// <summary>A time as <see cref="System.Diagnostics.Stopwatch"/> timestamps count it.</summary>
    internal static long Ticks(TimeSpan time) => (long)(time.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);

    public static ICorDebugValue NewBooleanValue(this ICorDebugEval eval, bool value)
    {
        ICorDebugValue corValue = eval.CreateValue(CorElementType.BOOLEAN, null);

        if (value is true && corValue is ICorDebugGenericValue genValue)
        {
            int size = genValue.Size;
            byte[] valueData = new byte[size];
            valueData[0] = 1;
            unsafe
            {
                fixed (byte* p = valueData)
                {
                    nint ptr = (IntPtr)p;
                    genValue.SetValue(ptr);
                }
            }
        }

        return corValue;
    }

    public static string ToDisplayName(this CorDebugInternalFrameType frameType)
    {
        return frameType switch
        {
            CorDebugInternalFrameType.STUBFRAME_M2U => "[Managed to Native Transition]",
            CorDebugInternalFrameType.STUBFRAME_U2M => "[Native to Managed Transition]",
            CorDebugInternalFrameType.STUBFRAME_APPDOMAIN_TRANSITION => "[Appdomain Transition]",
            CorDebugInternalFrameType.STUBFRAME_LIGHTWEIGHT_FUNCTION => "[Lightweight function]",
            CorDebugInternalFrameType.STUBFRAME_FUNC_EVAL => "[Func Eval]",
            CorDebugInternalFrameType.STUBFRAME_INTERNALCALL => "[Internal Call]",
            CorDebugInternalFrameType.STUBFRAME_CLASS_INIT => "[Class Init]",
            CorDebugInternalFrameType.STUBFRAME_EXCEPTION => "[Exception]",
            CorDebugInternalFrameType.STUBFRAME_SECURITY => "[Security]",
            CorDebugInternalFrameType.STUBFRAME_JIT_COMPILATION => "[JIT Compilation]",
            _ => "[Unknown]"
        };
    }
}
