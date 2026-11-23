using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Debugger.ExpressionEvaluator.Compiler;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;

/// <summary>Loads the code an expression's lambdas compile to into a collectible assembly load context in the program: the
/// debugger's own work, which the expression's time does not count (<see cref="MadeAsync{T}"/>).</summary>
internal sealed class DebuggeeDelegateAssemblyLoader(
    ManagedDebugger debugger,
    CompiledEvaluationMethod compiled,
    EvaluationMetadataResolver resolver,
    CompiledExpressionEvaluationContext context,
    EvaluationHandleScope handles,
    Lazy<DelegateMaterializerAssembly> materializer,
    ExpressionTime time) : IAsyncDisposable
{
    private int? _sessionId;

    public DelegateMaterializerAssembly Materializer => materializer.Value;

    /// <summary>Makes in the program what a lambda of the expression needs there, its code loaded and a delegate or
    /// closure made of it, set aside from the expression's time: the first of a session compiles and loads the debugger's
    /// own code, which a busy machine takes seconds over.</summary>
    public Task<T> MadeAsync<T>(Func<Task<T>> making) => time.AsideAsync(debugger.EvalStatus, async () =>
    {
        await debugger.HoldFirstLambdaAsync();
        return await making();
    });

    /// <summary>Loads the evaluation's own assembly into the program, as its first lambda does, for the types of its own the
    /// program makes (an anonymous type's), which the evaluation then finds there.</summary>
    public Task LoadOwnTypesAsync() => MadeAsync(GetSessionIdAsync);

    public async Task<ICorDebugFunction> GetMaterializerFunctionAsync(string methodName)
    {
        ModuleInfo module = await EnsureMaterializerLoadedAsync();
        MetadataReader reader = module.MetadataReader.PeMetadataReader;
        TypeDefinitionHandle type = reader.TypeDefinitions.First(handle => reader.GetString(reader.GetTypeDefinition(handle).Name) == Materializer.TypeName);
        MethodDefinitionHandle method = reader.GetTypeDefinition(type).GetMethods().First(handle => reader.GetString(reader.GetMethodDefinition(handle).Name) == methodName);
        return module.Module.GetFunctionFromToken(MetadataTokens.GetToken(method));
    }

    public async Task<int> GetSessionIdAsync()
    {
        if (_sessionId is { } existing)
        {
            return existing;
        }

        ICorDebugFunction begin = await GetMaterializerFunctionAsync("Begin");
        debugger.RegisterTransientEvaluationModule(compiled.ModuleVersionId);
        ICorDebugValue assembly = await CreateByteArrayAsync(compiled.Assembly);
        ICorDebugValue contextModule = await CreateStringAsync(resolver.CurrentFrameModuleVersionId.ToString("D"));
        ICorDebugValue result = handles.Track(await context.Thread.CreateEval().CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            begin,
            0,
            null,
            2,
            [assembly, contextModule],
            throwOnException: true)) ?? throw new InvalidOperationException("Failed to create the delegate assembly load session");
        int sessionId = CilValue.FromCorValue(result).AsInt32();
        _sessionId = sessionId;
        return sessionId;
    }

    public async ValueTask DisposeAsync()
    {
        if (_sessionId is not { } sessionId)
        {
            return;
        }

        _sessionId = null;
        ICorDebugFunction end = await GetMaterializerFunctionAsync("End");
        ICorDebugValue sessionIdArgument = CreateInt32(sessionId);
        await context.Thread.CreateEval().CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            end,
            0,
            null,
            1,
            [sessionIdArgument],
            throwOnException: true);
    }

    private async Task<ModuleInfo> EnsureMaterializerLoadedAsync()
    {
        if (FindModule(Materializer.ModuleVersionId) is { } loaded)
        {
            return loaded;
        }

        ICorDebugValue assembly = await CreateByteArrayAsync(Materializer.Assembly);
        ResolvedRuntimeMethod load = resolver.ResolveRuntimeMethod("System.Reflection", "Assembly", "Load", MetadataTypeNames.Of(PrimitiveTypeCode.Byte) + "[]");
        handles.Track(await context.Thread.CreateEval().CallParameterizedFunctionAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            load.Function,
            0,
            null,
            1,
            [assembly],
            throwOnException: true));
        return FindModule(Materializer.ModuleVersionId)
            ?? throw new InvalidOperationException($"Loaded delegate materializer module '{Materializer.ModuleVersionId}' was not reported by the runtime");
    }

    private async Task<ICorDebugValue> CreateByteArrayAsync(byte[] bytes)
    {
        ICorDebugType byteType = resolver.GetCorDebugType(new ResolvedCilType(PrimitiveTypeCode.Byte, null));
        ICorDebugValue arrayReference = handles.Track(await context.Thread.CreateEval().NewParameterizedArrayAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            byteType,
            checked((uint)bytes.Length),
            throwOnException: true)) ?? throw new InvalidOperationException("Failed to allocate generated assembly buffer");
        ICorDebugArrayValue array = arrayReference.UnwrapDebugValue() as ICorDebugArrayValue
            ?? throw new InvalidOperationException("Generated assembly buffer is not an array");
        if (bytes.Length > 0)
        {
            // One write at the first element, not a round trip per byte: the program is stopped, so its array stays put.
            context.Thread.Process.WriteMemory(array.GetElementAtPosition(0).Address, bytes.Length, bytes);
        }
        return arrayReference;
    }

    private async Task<ICorDebugValue> CreateStringAsync(string value) =>
        handles.Track(await context.Thread.CreateEval().NewStringAsync(
            debugger.ProcessRuntimeEventsUntilEvalEvent,
            debugger.EvalStatus,
            value,
            throwOnException: true))!;

    private ICorDebugValue CreateInt32(int value)
    {
        ICorDebugValue result = context.Thread.CreateEval().CreateValue(CorElementType.I4, null);
        new CorDebugLocation(result).Write(CilValue.FromPrimitive(value));
        return result;
    }

    private ModuleInfo? FindModule(Guid moduleVersionId) =>
        debugger.AllModules.FirstOrDefault(module => module.MetadataReader.Mvid == moduleVersionId);
}
