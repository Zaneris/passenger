using System.Collections.Immutable;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis.CSharp.Symbols;

namespace Passenger.Debugger;

/// <summary>A frame's method as its code names it, with the type arguments it runs with: 'Namespace.Type.Method&lt;T&gt;(
/// ParamType paramName, ...)'. A lambda the compiler made a method of is named by the method it is written in, then
/// "lambda" (Type.Method.lambda(int a)), and a local function by that method, then its own name (Type.Method.Add(int a)),
/// out of the types the compiler keeps their closures in.</summary>
internal static class FrameNames
{
    /// <summary>What a lambda's frame is called after the method it is written in.</summary>
    private const string Lambda = "lambda";

    public static string Of(MetadataReader reader, MethodDefinitionHandle handle, IReadOnlyList<string> typeArguments, IReadOnlyList<string> methodArguments)
    {
        MethodDefinition method = reader.GetMethodDefinition(handle);
        TypeDefinitionHandle declaring = method.GetDeclaringType();
        string parameters = Parameters(reader, method, new FrameDisplayGenericContext(typeArguments, methodArguments));
        string name = reader.GetString(method.Name);
        if (Written(reader, declaring, name, [.. typeArguments], [.. methodArguments]) is { } written)
        {
            return $"{written}({parameters})";
        }
        return $"{TypeName(reader, declaring, typeArguments)}.{name}{Arguments(methodArguments)}({parameters})";
    }

    /// <summary>A lambda's or a local function's name as its code writes it, by the type and method it is written in; null
    /// for a method of any other name.</summary>
    private static string? Written(MetadataReader reader, TypeDefinitionHandle declaring, string name, string[] typeArguments, string[] methodArguments)
    {
        GeneratedNameKind kind = GeneratedNameParser.GetKind(name);
        string? local = null;
        if (kind is not (GeneratedNameKind.LambdaMethod or GeneratedNameKind.LocalFunction)
            || !GeneratedNameParser.TryParseSourceMethodNameFromGeneratedName(name, kind, out string? enclosing)
            || (kind is GeneratedNameKind.LocalFunction && !GeneratedNameParser.TryParseLocalFunctionName(name, out local)))
        {
            return null;
        }
        TypeDefinitionHandle written = declaring;
        while (!written.IsNil && GeneratedNameParser.GetKind(reader.GetString(reader.GetTypeDefinition(written).Name)) is GeneratedNameKind.LambdaDisplayClass)
        {
            written = reader.GetTypeDefinition(written).GetDeclaringType();
        }
        if (written.IsNil)
        {
            return null;
        }
        int own = reader.GetTypeDefinition(written).GetGenericParameters().Count;
        if (own > typeArguments.Length)
        {
            return null;
        }
        // Past the written type's type arguments come its method's, then a local function's: split only where that is sure.
        string[] rest = [.. typeArguments[own..], .. methodArguments];
        int? arity = Arity(reader, written, enclosing) is { } count && (local is null ? count == rest.Length : count <= rest.Length) ? count : null;
        string type = TypeName(reader, written, typeArguments[..own]);
        string method = arity is { } taken ? $"{enclosing}{Arguments(rest[..taken])}" : enclosing;
        return local is null ? $"{type}.{method}.{Lambda}" : $"{type}.{method}.{local}{(arity is { } split ? Arguments(rest[split..]) : string.Empty)}";
    }

    /// <summary>How many type parameters a type's methods of a name declare, where they all declare as many; null where none
    /// is of that name or they differ.</summary>
    private static int? Arity(MetadataReader reader, TypeDefinitionHandle type, string name)
    {
        int? found = null;
        foreach (MethodDefinitionHandle handle in reader.GetTypeDefinition(type).GetMethods())
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            if (!reader.StringComparer.Equals(method.Name, name))
            {
                continue;
            }
            int count = method.GetGenericParameters().Count;
            if (found is { } before && before != count)
            {
                return null;
            }
            found = count;
        }
        return found;
    }

    private static string Arguments(IReadOnlyList<string> arguments) => arguments.Count > 0 ? $"<{string.Join(", ", arguments)}>" : string.Empty;

    /// <summary>A method's parameters as its source declares them, so a local function's closure, a parameter the compiler
    /// adds with no name, is left out.</summary>
    private static string Parameters(MetadataReader reader, MethodDefinition method, FrameDisplayGenericContext genericContext)
    {
        ImmutableArray<string> parameterTypes = method.DecodeSignature(FrameDisplaySignatureTypeProvider.Instance, genericContext).ParameterTypes;
        ImmutableArray<EntityHandle> parameterHandles = method.DecodeSignature(ManagedDebugger.FieldTypeHandleProvider.Instance, null).ParameterTypes;
        Dictionary<int, string> parameterNames = method.GetParameters()
            .Select(reader.GetParameter)
            .Where(parameter => parameter.SequenceNumber > 0)
            .ToDictionary(parameter => parameter.SequenceNumber, parameter => reader.GetString(parameter.Name));
        return string.Join(", ", parameterTypes
            .Select((type, index) => parameterNames.TryGetValue(index + 1, out string? name) && name.Length > 0 ? $"{type} {name}"
                : ModuleMetadataReader.IsClosure(reader, parameterHandles[index]) ? null
                : type)
            .OfType<string>());
    }

    /// <summary>A frame's type with the type arguments it runs with, its nested types joined by '.', a nullable spelled out.</summary>
    private static string TypeName(MetadataReader reader, TypeDefinitionHandle handle, IReadOnlyList<string> typeArguments)
    {
        string name = MetadataTypeNames.Of(reader, handle, '.');
        return CSharpTypeNames.Closed(name, typeArguments, spelledNullable: true) ?? name;
    }
}
