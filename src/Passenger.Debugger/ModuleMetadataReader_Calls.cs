using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Core;
using Passenger.Debugger.ExpressionEvaluator.Cil;

namespace Passenger.Debugger;

/// <summary>What a method's IL calls between two offsets, for the values a step's calls return and for Smart Step Into:
/// each call, object creation and delegate made of a method (a lambda's body, a method group), with what it names.</summary>
public partial class ModuleMetadataReader
{
    internal enum CallKind
    {
        Call,
        NewObject,

        /// <summary>A delegate made of a method (ldftn, ldvirtftn): what runs when the delegate is invoked.</summary>
        Delegate,
    }

    /// <summary>A call at an IL offset and the token (a MethodDef, MemberRef or MethodSpec) it names.</summary>
    internal readonly record struct CallSite(int Offset, CallKind Kind, int Token);

    /// <summary>A called method as its token names it: its name, its declaring type's (simple) name, and its parameters'
    /// and return type's, as a C# reader writes them.</summary>
    internal sealed record CalledMethod(string Name, string? DeclaringType, IReadOnlyList<string> Parameters, string ReturnType);

    /// <summary>The calls in [start, end) of a method's IL, in IL order: the order they run, within a statement. None for
    /// a module with no image (one emitted as the program runs).</summary>
    internal IReadOnlyList<CallSite> CallSites(int methodToken, int start, int end, int? version = null)
    {
        if (MethodIL(methodToken, version) is not { } il)
        {
            return [];
        }
        List<CallSite> calls = [];
        foreach (CilInstruction instruction in CilInstructionDecoder.Decode(il))
        {
            if (instruction.Offset < start || instruction.Offset >= end)
            {
                continue;
            }
            CallKind? kind = instruction.Code switch
            {
                ILOpCode.Call or ILOpCode.Callvirt => CallKind.Call,
                ILOpCode.Newobj => CallKind.NewObject,
                ILOpCode.Ldftn or ILOpCode.Ldvirtftn => CallKind.Delegate,
                _ => null,
            };
            if (kind is { } found)
            {
                calls.Add(new CallSite(instruction.Offset, found, instruction.Token));
            }
        }
        return calls;
    }

    /// <summary>What a call's token names; null for a token of another kind.</summary>
    internal CalledMethod? DescribeCall(int token)
    {
        EntityHandle handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.MethodSpecification)
        {
            handle = _peMetadataReader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
        }
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                MethodDefinition method = _peMetadataReader.GetMethodDefinition((MethodDefinitionHandle)handle);
                MethodSignature<string> signature = method.DecodeSignature(CallLabelProvider.Instance, null);
                return new CalledMethod(_peMetadataReader.GetString(method.Name), MetadataTypeNames.WithoutArity(GetTypeName(method.GetDeclaringType()).Name), signature.ParameterTypes, signature.ReturnType);
            }
            case HandleKind.MemberReference:
            {
                MemberReference member = _peMetadataReader.GetMemberReference((MemberReferenceHandle)handle);
                if (member.GetKind() != MemberReferenceKind.Method)
                {
                    return null;
                }
                MethodSignature<string> signature = member.DecodeMethodSignature(CallLabelProvider.Instance, null);
                string? declaring = member.Parent.Kind switch
                {
                    HandleKind.TypeDefinition => MetadataTypeNames.WithoutArity(GetTypeName((TypeDefinitionHandle)member.Parent).Name),
                    HandleKind.TypeReference => MetadataTypeNames.WithoutArity(GetTypeName((TypeReferenceHandle)member.Parent).Name),
                    HandleKind.TypeSpecification => _peMetadataReader.GetTypeSpecification((TypeSpecificationHandle)member.Parent).DecodeSignature(CallLabelProvider.Instance, null),
                    _ => null,
                };
                return new CalledMethod(_peMetadataReader.GetString(member.Name), declaring, signature.ParameterTypes, signature.ReturnType);
            }
            default:
                return null;
        }
    }

    /// <summary>A method's sequence points that show a line, in IL order.</summary>
    internal IReadOnlyList<Point> VisibleSequencePoints(int methodToken, int? version = null) =>
        [.. Points(methodToken, version).Where(point => !point.IsHidden).OrderBy(point => point.Offset)];

    /// <summary>The file a sequence point of a method is in.</summary>
    internal static string? DocumentOf(Point point) => point.Document;

    /// <summary>The MoveNext an async or iterator method's state machine runs its body in, found by the attribute the
    /// compiler puts on the method; null for a method without one.</summary>
    internal int? GetStateMachineMoveNextToken(int kickoffMethodToken)
    {
        MethodDefinition kickoff = _peMetadataReader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(kickoffMethodToken));
        TypeDefinition declaringType = _peMetadataReader.GetTypeDefinition(kickoff.GetDeclaringType());
        foreach (TypeDefinitionHandle stateMachineHandle in declaringType.GetNestedTypes())
        {
            if (!kickoff.GetCustomAttributes().Any(attribute => StateMachineAttributeMatches(attribute, stateMachineHandle)))
            {
                continue;
            }
            foreach (MethodDefinitionHandle methodHandle in _peMetadataReader.GetTypeDefinition(stateMachineHandle).GetMethods())
            {
                if (_peMetadataReader.GetString(_peMetadataReader.GetMethodDefinition(methodHandle).Name) == "MoveNext")
                {
                    return MetadataTokens.GetToken(methodHandle);
                }
            }
        }
        return null;
    }

    /// <summary>Types as a call's label shows them: C#'s keywords, other types by their simple name.</summary>
    private sealed class CallLabelProvider : ISignatureTypeProvider<string, object?>
    {
        public static CallLabelProvider Instance { get; } = new();

        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
        public string GetByReferenceType(string elementType) => "ref " + elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(", ", typeArguments)}>";
        public string GetGenericMethodParameter(object? genericContext, int index) => index == 0 ? "T" : $"T{index + 1}";
        public string GetGenericTypeParameter(object? genericContext, int index) => index == 0 ? "T" : $"T{index + 1}";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => PrimitiveTypes.Of(typeCode).CSharpName;
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => MetadataTypeNames.WithoutArity(reader.GetString(reader.GetTypeDefinition(handle).Name));
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => MetadataTypeNames.WithoutArity(reader.GetString(reader.GetTypeReference(handle).Name));
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
