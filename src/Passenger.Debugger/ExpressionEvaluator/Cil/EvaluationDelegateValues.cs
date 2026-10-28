using System.Reflection.Metadata;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger.ExpressionEvaluator.Cil;
internal sealed class EvaluationClosure(TypeDefinitionHandle type, MethodDefinitionHandle constructor)
{
    public TypeDefinitionHandle Type { get; } = type;
    public MethodDefinitionHandle Constructor { get; } = constructor;
    public Dictionary<FieldDefinitionHandle, ICilLocation> Fields { get; } = new();
    public Dictionary<FieldDefinitionHandle, ICilLocation> FieldBindings { get; } = new();
    public ICorDebugValue? MaterializedValue { get; set; }
}

internal sealed record EvaluationFunctionPointer(int MethodToken, ResolvedRuntimeMethod? RuntimeMethod = null);
internal sealed record EvaluationDelegate(EvaluationFunctionPointer Function, CilValue Target, ResolvedRuntimeType DelegateType);

/// <summary>A field of the evaluation's own that ldtoken names: the constants the compilation laid out for an array
/// initializer or a span, which the program never loads.</summary>
internal sealed record EvaluationFieldToken(FieldDefinitionHandle Field);
