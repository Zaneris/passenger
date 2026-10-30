using System.Collections.Immutable;
using System.Reflection.Metadata;
using Passenger.Core;

namespace Passenger.Debugger;

internal sealed record FrameDisplayGenericContext(IReadOnlyList<string> TypeArguments, IReadOnlyList<string> MethodArguments);

/// <summary>Formats method signature types for stack frame names: a primitive by its keyword, a generic type closed as
/// <see cref="CSharpTypeNames.Closed"/> closes it, a nullable spelled out.</summary>
internal sealed class FrameDisplaySignatureTypeProvider : ISignatureTypeProvider<string, FrameDisplayGenericContext>
{
    public static FrameDisplaySignatureTypeProvider Instance { get; } = new();

    public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', shape.Rank - 1)}]";
    public string GetByReferenceType(string elementType) => "ref " + elementType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        CSharpTypeNames.Closed(genericType, typeArguments, spelledNullable: true) ?? $"{genericType}<{string.Join(", ", typeArguments)}>";
    public string GetGenericMethodParameter(FrameDisplayGenericContext genericContext, int index) => genericContext.MethodArguments[index];
    public string GetGenericTypeParameter(FrameDisplayGenericContext genericContext, int index) => genericContext.TypeArguments[index];
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetPinnedType(string elementType) => elementType;
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => MetadataTypeNames.Of(reader, handle, '.');
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => MetadataTypeNames.Of(reader, handle, '.');
    public string GetTypeFromSpecification(MetadataReader reader, FrameDisplayGenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => PrimitiveTypes.Of(typeCode).CSharpName;
}
