using System.Runtime.InteropServices;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    internal async ValueTask<ICorDebugValue> GetStaticFieldValueAsync(ICorDebugType type, mdFieldDef fieldDef, ThreadId threadId, FrameStackDepth stackDepth)
    {
        await EnsureClassConstructorHasRun(type, threadId, stackDepth);

        ICorDebugILFrame currentFrame = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth);
        int result = type.TryGetStaticFieldValue(fieldDef, currentFrame, out ICorDebugValue? value);
        Marshal.ThrowExceptionForHR(result);
        return value;
    }

    private async ValueTask EnsureClassConstructorHasRun(ICorDebugType type, ThreadId threadId, FrameStackDepth stackDepth)
    {
        bool hasTypeId = type.TryGetTypeID(out COR_TYPEID typeId) is Cor.S_OK; // GetTypeID can/will throw for generic static types
        if (hasTypeId && _initializedStaticTypes.Contains(typeId))
        {
            return;
        }

        ICorDebugFunction suppressFinalize = GetSuppressFinalizeFunction();
        ICorDebugType[] typeParameters = type.TypeParameters;
        ICorDebugThread thread = GetIlFrameForThreadIdAndStackDepth(threadId, stackDepth).Chain.Thread;
        ICorDebugEval eval = thread.CreateEval();
        ICorDebugValue initializationValue = await eval.NewParameterizedObjectNoConstructorAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, type.Class, typeParameters.Length, typeParameters, throwOnException: true) ?? throw new EvalException("Type initialization returned no value");

        try
        {
            ICorDebugEval suppressEval = thread.CreateEval();
            ICorDebugValue? suppressResult = await suppressEval.CallParameterizedFunctionAsync(ProcessRuntimeEventsUntilEvalEvent, EvalStatus, suppressFinalize, 0, null, 1, [initializationValue], throwOnException: true);
            if (suppressResult is ICorDebugHandleValue suppressHandle)
            {
                suppressHandle.TryDispose();
            }

            if (hasTypeId || type.TryGetTypeID(out typeId) is Cor.S_OK)
            {
                _initializedStaticTypes.Add(typeId);
            }
        }
        finally
        {
            if (initializationValue is ICorDebugHandleValue initializationHandle)
            {
                initializationHandle.TryDispose();
            }
        }
    }

    private ICorDebugFunction GetSuppressFinalizeFunction()
    {
        if (_suppressFinalizeFunction is not null)
        {
            return _suppressFinalizeFunction;
        }

        ModuleInfo coreLib = CoreLib ?? throw new InvalidOperationException(CoreLibNotLoaded);
        IMetaDataImport metadata = coreLib.Module.GetMetaDataInterface<IMetaDataImport>();
        mdTypeDef gcType = metadata.FindTypeDefByNameOrNull("System.GC", mdToken.Nil) ?? throw new InvalidOperationException("Could not find System.GC");
        mdMethodDef suppressFinalize = metadata.EnumMethodsWithName(gcType, "SuppressFinalize").SingleOrDefault();
        if (suppressFinalize.IsNil)
        {
            throw new InvalidOperationException("Could not find System.GC.SuppressFinalize");
        }

        return _suppressFinalizeFunction = coreLib.Module.GetFunctionFromToken(suppressFinalize);
    }
}
