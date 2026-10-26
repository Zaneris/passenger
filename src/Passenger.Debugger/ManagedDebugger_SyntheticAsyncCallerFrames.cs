using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

// After a continuation, async methods are resumed on a different thread, so the caller of the async method is not visible in the physical stack trace
// This class provides a way to synthesize the caller frames of async methods by inspecting the state machine and its associated task continuation chain
internal sealed record SyntheticAsyncCallerFrame(
    ModuleInfo Module,
    int KickoffMethodToken,
    int MoveNextToken,
    ICorDebugValue StateMachine,
    string Name);

public partial class ManagedDebugger
{
    private const int SyntheticAsyncCallerDepthLimit = 100;
    private const int MaxStateMachineSearchDepth = 100;

    private IEnumerable<SyntheticAsyncCallerFrame> GetSyntheticAsyncCallerFrames(ICorDebugILFrame physicalFrame)
    {
        if (FindModule(physicalFrame.Function.Module) is not { } physicalModule ||
            physicalModule.MetadataReader.GetStateMachineKickoffMethodToken(physicalFrame.Function.Token) is null)
        {
            yield break;
        }

        if (physicalFrame.TryGetArgument(0, out ICorDebugValue? stateMachine) < 0 || stateMachine is null)
        {
            yield break;
        }

        HashSet<CORDB_ADDRESS> visitedTasks = new HashSet<CORDB_ADDRESS>();
        HashSet<CORDB_ADDRESS> visitedStateMachines = new HashSet<CORDB_ADDRESS>();
        if (stateMachine is ICorDebugReferenceValue initialStateMachineReference)
        {
            visitedStateMachines.Add(initialStateMachineReference.Value);
        }

        for (int syntheticFrameIndex = 0; syntheticFrameIndex < SyntheticAsyncCallerDepthLimit; syntheticFrameIndex++)
        {
            ICorDebugValue? task = GetStateMachineTask(stateMachine);
            if (task is null)
            {
                yield break;
            }

            if (task is ICorDebugReferenceValue taskReference && !visitedTasks.Add(taskReference.Value))
            {
                yield break;
            }

            ICorDebugObjectValue? taskObject = task.UnwrapDebugValueToObjectOrNull();
            if (taskObject is null)
            {
                yield break;
            }

            ICorDebugValue? continuation = GetInstanceFieldValue(taskObject, "m_continuationObject");
            if (continuation is null)
            {
                yield break;
            }

            stateMachine = FindStateMachine(continuation, 0)!;
            if (stateMachine is null)
            {
                yield break;
            }

            // A repeated state machine is a cyclic continuation chain (two methods awaiting each other): stop.
            if (stateMachine is ICorDebugReferenceValue stateMachineReference && !visitedStateMachines.Add(stateMachineReference.Value))
            {
                yield break;
            }

            SyntheticAsyncCallerFrame? frame = CreateSyntheticAsyncCallerFrame(stateMachine);
            if (frame is null)
            {
                yield break;
            }

            yield return frame;
        }
    }

    private static ICorDebugValue? GetStateMachineTask(ICorDebugValue stateMachine)
    {
        ICorDebugObjectValue? stateMachineObject = stateMachine.UnwrapDebugValueToObjectOrNull();
        ICorDebugValue? builder = stateMachineObject is null ? null : GetInstanceFieldValue(stateMachineObject, "<>t__builder") ??
            GetInstanceFieldValue(stateMachineObject, "$Builder");
        ICorDebugObjectValue? builderObject = builder is null ? null : builder.UnwrapDebugValueToObjectOrNull();
        return builderObject is null ? null : GetInstanceFieldValue(builderObject, "m_task") ??
            GetInstanceFieldValue(builderObject, "_task");
    }

    private static readonly string[] StateMachineFieldNameCandidates = ["StateMachine", "m_stateMachine", "m_action"];
    private static ICorDebugValue? FindStateMachine(ICorDebugValue value, int depth)
    {
        if (depth >= MaxStateMachineSearchDepth)
        {
            return null;
        }

        ICorDebugObjectValue? objectValue = value.UnwrapDebugValueToObjectOrNull();
        if (objectValue is null)
        {
            return null;
        }

        foreach (string fieldName in StateMachineFieldNameCandidates)
        {
            ICorDebugValue? nested = GetInstanceFieldValue(objectValue, fieldName);
            if (nested is null)
            {
                continue;
            }

            ICorDebugValue? stateMachine = FindStateMachine(nested, depth + 1);
            if (stateMachine is not null)
            {
                return stateMachine;
            }
        }

        IMetaDataImport metadata = objectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
        if (metadata.EnumMethodsWithName(objectValue.Class.Token, "MoveNext").Any())
        {
            return value;
        }

        if (objectValue is ICorDebugDelegateObjectValue delegateValue && delegateValue.Target.IsNull is false)
        {
            return FindStateMachine(delegateValue.Target, depth + 1);
        }

        return null;
    }

    private SyntheticAsyncCallerFrame? CreateSyntheticAsyncCallerFrame(ICorDebugValue stateMachine)
    {
        ICorDebugObjectValue? objectValue = stateMachine.UnwrapDebugValueToObjectOrNull();
        if (objectValue is null || FindModule(objectValue.Class.Module) is not { } module)
        {
            return null;
        }

        IMetaDataImport metadata = objectValue.Class.Module.GetMetaDataInterface<IMetaDataImport>();
        mdMethodDef moveNext = metadata.EnumMethodsWithName(objectValue.Class.Token, "MoveNext").FirstOrDefault();
        if (moveNext.IsNil)
        {
            return null;
        }

        int moveNextToken = (int)moveNext;
        int? kickoffToken = module.MetadataReader.GetStateMachineKickoffMethodToken(moveNextToken);
        if (kickoffToken is null)
        {
            return null;
        }

        string name = GetMethodFormattedName(module, kickoffToken.Value, objectValue.ExactType.TypeParameters);
        return new SyntheticAsyncCallerFrame(module, kickoffToken.Value, moveNextToken, stateMachine, name);
    }

    /// Source info for a synthetic frame is the location of the await the caller is currently suspended on. It is
    /// resolved on demand so that a stack trace request never reads decompiled PDBs; pass decompiled when resolving an
    /// individual frame, to take the module's decompiled symbols from the cache when it has none of its own.
    private SourceInfo? GetSyntheticAsyncFrameSourceInfo(SyntheticAsyncCallerFrame frame, ThreadId threadId, FrameStackDepth physicalFrameStackDepth, bool decompiled)
    {
        ModuleInfo module = frame.Module;
        if (decompiled)
        {
            LoadDecompiledSymbols(module);
        }
        if (module.MetadataReader.HasSymbols is false)
        {
            return null;
        }

        ICorDebugObjectValue? objectValue = frame.StateMachine.UnwrapDebugValueToObjectOrNull();
        if (objectValue is null)
        {
            return null;
        }

        int? state = ReadInt32Field(objectValue, "<>1__state") ?? ReadInt32Field(objectValue, "$State");
        ModuleMetadataReader.AsyncMethodSteppingInfo? steppingInfo = module.MetadataReader.GetAsyncMethodSteppingInfo(frame.MoveNextToken);
        if (state is null || state < 0 || steppingInfo is null || state >= steppingInfo.AwaitInfos.Count)
        {
            return null;
        }

        ModuleMetadataReader.AsyncAwaitInfo awaitInfo = steppingInfo.AwaitInfos[state.Value];
        (string sourceFilePath, int startLine, int endLine, int startColumn, int endColumn)? source = module.MetadataReader.GetSourceLocationForOffset(frame.MoveNextToken, checked((int)awaitInfo.YieldOffset));
        if (source is null)
        {
            return null;
        }

        DecompiledSourceInfo? decompiledSourceInfo = null;
        if (module.SymbolsFromDecompiled)
        {
            // A synthetic frame has no physical callers: the search starts at the frame its chain was built from.
            ICorDebugFrame originatingFrame = GetFrameForThreadIdAndStackDepth(threadId, physicalFrameStackDepth);
            string? callingUserCodeAssemblyPath = FindCallingUserCodeAssemblyPath(originatingFrame);
            decompiledSourceInfo = CreateDecompiledSourceInfo(module, frame.MoveNextToken, callingUserCodeAssemblyPath);
        }

        return new SourceInfo(source.Value.sourceFilePath, source.Value.startLine, source.Value.endLine, source.Value.startColumn, source.Value.endColumn, decompiledSourceInfo);
    }

    private static int? ReadInt32Field(ICorDebugObjectValue objectValue, string name)
    {
        try
        {
            ICorDebugGenericValue? field = GetInstanceFieldValue(objectValue, name)?.UnwrapDebugValue() as ICorDebugGenericValue;
            return field is null ? null : BitConverter.ToInt32(field.GetValueAsBytes());
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return null;
        }
    }

    private static ICorDebugValue? GetInstanceFieldValue(ICorDebugObjectValue objectValue, string fieldName)
    {
        for (ICorDebugType? type = objectValue.ExactType; type is not null; type = type.Base)
        {
            IMetaDataImport metadata = type.Class.Module.GetMetaDataInterface<IMetaDataImport>();
            mdFieldDef field = metadata.EnumFieldsWithName(type.Class.Token, fieldName).FirstOrDefault();
            if (!field.IsNil)
            {
                return objectValue.GetFieldValue(type.Class, field);
            }
        }
        return null;
    }
}
