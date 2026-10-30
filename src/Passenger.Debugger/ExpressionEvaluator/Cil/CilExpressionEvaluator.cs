using System.Collections.Immutable;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.Debugger.ExpressionEvaluator.Compiler;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;
internal sealed class CilExpressionEvaluator(RuntimeAssemblyPrimitiveTypeClasses primitiveTypes, ManagedDebugger debugger)
{
    private readonly CilExpressionCompiler _compiler = new(debugger);
    private readonly CilInterpreter _interpreter = new(debugger, primitiveTypes);

    internal async Task<EvaluationResult> Evaluate(string expression, CompiledExpressionEvaluationContext context)
    {
        try
        {
            if (expression == "$exception" && debugger.GetCurrentException(context.ThreadId) is { } currentException)
            {
                if (currentException is not ICorDebugReferenceValue { IsNull: false } reference)
                {
                    return new EvaluationResult { Value = currentException };
                }
                using EvaluationHandleScope handles = new EvaluationHandleScope();
                ICorDebugHandleValue handle = handles.CreateOwnedHandle(reference);
                return new EvaluationResult { Value = handle, OwnedResultHandle = handles.DetachIfOwned(handle) };
            }
            if (ManagedDebugger.ReturnValueIndex(expression) is int index && debugger.TryGetReturnedValue(context.ThreadId, index, out ICorDebugHandleValue? returned) && returned is not null)
            {
                // What a call returned, named alone: the value itself, as $exception is, with no code compiled to reach it.
                using EvaluationHandleScope handles = new EvaluationHandleScope();
                ICorDebugHandleValue handle = handles.CreateOwnedHandle(returned);
                return new EvaluationResult { Value = handle, OwnedResultHandle = handles.DetachIfOwned(handle) };
            }
            CompiledEvaluationMethod? compiled = _compiler.TryCompile(expression, context, out string? error, out _, out ImmutableArray<string> missing);
            if (compiled is null && missing.Length > 0 && await LoadAsync(missing, context))
            {
                compiled = _compiler.TryCompile(expression, context, out error);
            }
            if (compiled is null)
            {
                return new EvaluationResult { Error = $"error: {error}" };
            }

            Lazy<DelegateMaterializerAssembly> delegateMaterializer = new Lazy<DelegateMaterializerAssembly>(() => _compiler.GetDelegateMaterializer(context));
            CilInterpretationResult interpretationResult = await _interpreter.InterpretAsync(compiled, context, delegateMaterializer);
            return new EvaluationResult { Value = interpretationResult.Value, OwnedResultHandle = interpretationResult.OwnedResultHandle, Declared = DeclaredOf(compiled) };
        }
        catch (Exception ex)
        {
            // An expression's every failure, its compile's, the interpreter's or the program's, is its answer.
            debugger.Log($"Evaluating {expression} failed: {ex}");
            return new EvaluationResult { Error = $"error: {ex.Message}" };
        }
    }

    /// <summary>The type of an expression's value as it compiles, none of it run, as
    /// <see cref="SignatureKeyProvider.Dotted"/> writes it (System.Single, System.Nullable`1&lt;System.Decimal&gt;); null
    /// where it does not compile.</summary>
    internal string? TypeOf(string expression, CompiledExpressionEvaluationContext context) =>
        _compiler.TryCompile(expression, context, out _) is { } compiled
            ? compiled.MetadataReader.GetMethodDefinition(compiled.EntryMethod).DecodeSignature(SignatureKeyProvider.Dotted, null).ReturnType
            : null;

    /// <summary>Where an expression's value is typed by its frame's type parameters, alone or in a larger type (T, V? of a
    /// struct V, List&lt;T&gt;, T[]): that type as a cast names it with the parameters, and as C# names it with the types the
    /// frame runs them as (null where one of those is shared or has no such name); null for a type using none.</summary>
    internal (string Cast, string? RunAs)? TypeParametersOf(string expression, CompiledExpressionEvaluationContext context)
    {
        if (context.RootValue is not null || _compiler.TryCompile(expression, context, out _) is not { } compiled)
        {
            return null;
        }
        MetadataReader reader = compiled.MetadataReader;
        MethodDefinition method = reader.GetMethodDefinition(compiled.EntryMethod);
        if (!method.DecodeSignature(SignatureKeyProvider.Dotted, null).ReturnType.Contains('!', StringComparison.Ordinal))
        {
            return null;
        }
        TypeParameterNames named = new(NamesOf(reader, reader.GetTypeDefinition(method.GetDeclaringType()).GetGenericParameters()), NamesOf(reader, method.GetGenericParameters()));
        (ICorDebugType[] ofClass, ICorDebugType[] ofMethod) = CilInterpreter.SplitFrameTypeParameters(debugger, debugger.GetIlFrameForThreadIdAndStackDepth(context.ThreadId, context.StackDepth));
        return Nameable(method.DecodeSignature(CSharpTypeNames.NullableSpelled, named).ReturnType) is { } cast
            ? (cast, Nameable(method.DecodeSignature(CSharpTypeNames.Instance, new TypeParameterNames(RunAs(ofClass), RunAs(ofMethod))).ReturnType))
            : null;

        static string?[] NamesOf(MetadataReader reader, GenericParameterHandleCollection parameters) =>
            [.. parameters.Select(parameter => reader.GetString(reader.GetGenericParameter(parameter).Name))];

        static string?[] RunAs(ICorDebugType[] arguments) => [.. arguments.Select(argument =>
        {
            try
            {
                return ManagedDebugger.GetCorDebugTypeFriendlyName(argument) is string name && name != "System.__Canon" ? name : null;
            }
            catch (Exception error) when (Failures.OfRuntime(error))
            {
                return null;
            }
        })];

        static string? Nameable(string? type) => type is not null && !SyntaxFactory.ParseTypeName(type).ContainsDiagnostics ? type : null;
    }

    /// <summary>Whether an expression fails to compile with one of the errors <paramref name="ids"/> (CS8331, a readonly
    /// variable assigned) among its errors, none of it run.</summary>
    internal bool FailsWith(string expression, CompiledExpressionEvaluationContext context, IReadOnlyCollection<string> ids) =>
        _compiler.TryCompile(expression, context, out _, out ImmutableArray<string> errors, out _) is null && errors.Any(ids.Contains);

    /// <summary>Whether an expression compiles, none of it run.</summary>
    internal bool Compiles(string expression, CompiledExpressionEvaluationContext context) => _compiler.TryCompile(expression, context, out _) is not null;

    /// <summary>The type an expression's value is as it compiles, which its members' expressions reach them through.</summary>
    private static DeclaredType? DeclaredOf(CompiledEvaluationMethod compiled) =>
        compiled.MetadataReader.GetMethodDefinition(compiled.EntryMethod).DecodeSignature(DeclaredTypeProvider.Instance, null).ReturnType;

    /// <summary>Loads into the program the assemblies an expression needs and it has not loaded (a program that has
    /// called no LINQ method has no System.Linq). Whether each loaded, the runtime's module load having added it to
    /// what the next compile reads.</summary>
    private async Task<bool> LoadAsync(ImmutableArray<string> assemblies, CompiledExpressionEvaluationContext context)
    {
        foreach (string assembly in assemblies.Distinct(StringComparer.Ordinal))
        {
            using EvaluationResult loaded = await Evaluate($"System.Reflection.Assembly.Load(\"{assembly.Replace("\"", "\\\"", StringComparison.Ordinal)}\")", context);
            if (loaded.Error is not null)
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed class EvaluationResult : IDisposable
{
    private ICorDebugHandleValue? _ownedResultHandle;

    public ICorDebugValue? Value { get; init; }

    /// <summary>The type the expression's value is as it compiles; null where none is known (the debugger's own names).</summary>
    public DeclaredType? Declared { get; init; }
    public string? Error { get; init; }
    public ICorDebugHandleValue? OwnedResultHandle
    {
        init => _ownedResultHandle = value;
    }

    public void RelinquishResultHandleOwnership()
    {
        _ownedResultHandle = null;
    }

    public void Dispose()
    {
        _ownedResultHandle?.TryDispose();
        _ownedResultHandle = null;
    }
}
