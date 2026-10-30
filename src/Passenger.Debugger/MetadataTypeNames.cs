using System.Collections.Immutable;
using System.Reflection.Metadata;
using Passenger.Core;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

/// <summary>The names the debugger knows metadata's types by: a type named alike from its definition and from a reference
/// to it in another module (its namespace, then each type it is nested in, joined by <c>nested</c>: '+' as reflection writes
/// it, '.' as C# does), and a primitive (<see cref="PrimitiveTypes"/>) by its System type's name and its runtime element
/// type.</summary>
internal static class MetadataTypeNames
{
    private static readonly Dictionary<CorElementType, PrimitiveType> ByElementType =
        PrimitiveTypes.All.Where(row => row.Code is not null).ToDictionary(row => ElementTypeOf(row.Code!.Value));

    public static string Of(MetadataReader reader, TypeDefinitionHandle handle, char nested = '+')
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        return declaring.IsNil ? Joined(reader.GetString(type.Namespace), name) : $"{Of(reader, declaring, nested)}{nested}{name}";
    }

    public static string Of(MetadataReader reader, TypeReferenceHandle handle, char nested = '+')
    {
        TypeReference type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? $"{Of(reader, (TypeReferenceHandle)type.ResolutionScope, nested)}{nested}{name}"
            : Joined(reader.GetString(type.Namespace), name);
    }

    /// <summary>A type definition named through the runtime's metadata import, as a module made while the program runs
    /// gives it too.</summary>
    public static string Of(IMetaDataImport import, mdTypeDef token, char nested = '+')
    {
        (string name, CorTypeAttr flags, _) = import.GetTypeDefProps(token);
        return flags.IsTdNested() ? $"{Of(import, import.GetNestedClassProps(token), nested)}{nested}{name}" : name;
    }

    public static string Of(PrimitiveTypeCode code) => PrimitiveTypes.Of(code).SystemName;

    /// <summary>The runtime's element type of a primitive, which ECMA-335 numbers as it numbers the primitive's code.</summary>
    public static CorElementType ElementTypeOf(PrimitiveTypeCode code) => (CorElementType)code;

    /// <summary>The primitive an element type is, null for any other (a class, an array, a pointer).</summary>
    public static PrimitiveType? PrimitiveOf(CorElementType element) => ByElementType.GetValueOrDefault(element);

    /// <summary>A metadata name without its generic arity: List`1 is List.</summary>
    public static string WithoutArity(string name) => name.IndexOf('`', StringComparison.Ordinal) is int arity and >= 0 ? name[..arity] : name;

    private static string Joined(string @namespace, string name) => @namespace.Length == 0 ? name : $"{@namespace}.{name}";
}

/// <summary>A method signature's types as text that a member reference and the definition it names write alike, whichever
/// modules they are in (<see cref="MetadataTypeNames"/>): the key the debugger matches methods by, and with nested types
/// joined by '.' (<see cref="Dotted"/>) the names a person's function breakpoint is matched against.</summary>
internal sealed class SignatureKeyProvider(char nested) : ISignatureTypeProvider<string, object?>
{
    public static SignatureKeyProvider Instance { get; } = new('+');

    public static SignatureKeyProvider Dotted { get; } = new('.');

    /// <summary>A whole signature as one key: its generic arity, return type and parameters.</summary>
    public static string Of(MethodSignature<string> signature) =>
        $"{signature.GenericParameterCount}|{signature.ReturnType}({string.Join(",", signature.ParameterTypes)})";

    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => $"{genericType}<{string.Join(",", typeArguments)}>";
    public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";
    public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => MetadataTypeNames.Of(typeCode);
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => MetadataTypeNames.Of(reader, handle, nested);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => MetadataTypeNames.Of(reader, handle, nested);
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
