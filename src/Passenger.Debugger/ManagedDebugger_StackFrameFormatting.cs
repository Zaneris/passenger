using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    /// <summary>'Module.dll!Namespace.Type.Method&lt;T&gt;(ParamType paramName, ...)'.</summary>
    private static string GetMethodFormattedName(ModuleInfo moduleInfo, int methodToken, IReadOnlyList<ICorDebugType> typeAndMethodTypeArguments)
    {
        try
        {
            ICorDebugModule module = moduleInfo.Module;
            MetadataReader reader = moduleInfo.MetadataReader.PeMetadataReader;
            MethodDefinition methodDefinition = reader.GetMethodDefinition((MethodDefinitionHandle)MetadataTokens.Handle(methodToken));
            TypeDefinitionHandle declaringTypeHandle = methodDefinition.GetDeclaringType();
            int typeArgumentCount = reader.GetTypeDefinition(declaringTypeHandle).GetGenericParameters().Count;
            int methodArgumentCount = methodDefinition.GetGenericParameters().Count;
            if (typeAndMethodTypeArguments.Count < typeArgumentCount + methodArgumentCount)
            {
                throw new InvalidOperationException("The frame did not provide all generic type arguments.");
            }
            List<string> typeArgumentNames = typeAndMethodTypeArguments
                .Take(typeArgumentCount)
                .Select(GetCorDebugTypeFriendlyName)
                .ToList();
            List<string> methodArgumentNames = typeAndMethodTypeArguments
                .Skip(typeArgumentCount)
                .Take(methodArgumentCount)
                .Select(GetCorDebugTypeFriendlyName)
                .ToList();

            FrameDisplayGenericContext genericContext = new FrameDisplayGenericContext(typeArgumentNames, methodArgumentNames);
            string className = GetFormattedTypeName(reader, declaringTypeHandle, typeArgumentNames);
            string methodName = reader.GetString(methodDefinition.Name);
            if (methodArgumentNames.Count > 0)
            {
                methodName += $"<{string.Join(", ", methodArgumentNames)}>";
            }

            string parameters = GetFormattedParameters(reader, methodDefinition, genericContext);
            return $"{Path.GetFileName(module.Name)}!{className}.{methodName}({parameters})";
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return "Unknown";
        }
    }

    private static string GetFormattedParameters(MetadataReader reader, MethodDefinition methodDefinition, FrameDisplayGenericContext genericContext)
    {
        ImmutableArray<string> parameterTypes = methodDefinition.DecodeSignature(FrameDisplaySignatureTypeProvider.Instance, genericContext).ParameterTypes;
        Dictionary<int, string> parameterNames = methodDefinition.GetParameters()
            .Select(reader.GetParameter)
            .Where(parameter => parameter.SequenceNumber > 0)
            .ToDictionary(parameter => parameter.SequenceNumber, parameter => reader.GetString(parameter.Name));
        return string.Join(", ", parameterTypes.Select((type, index) =>
            parameterNames.TryGetValue(index + 1, out string? name) && name.Length > 0 ? $"{type} {name}" : type));
    }

    private static string GetFormattedTypeName(MetadataReader reader, TypeDefinitionHandle handle, IReadOnlyList<string> typeArguments)
    {
        int argumentIndex = 0;
        return GetFormattedTypeName(reader, handle, typeArguments, ref argumentIndex);
    }

    private static string GetFormattedTypeName(MetadataReader reader, TypeDefinitionHandle handle, IReadOnlyList<string> typeArguments, ref int argumentIndex)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        string prefix = declaringType.IsNil
            ? reader.GetString(type.Namespace)
            : GetFormattedTypeName(reader, declaringType, typeArguments, ref argumentIndex);
        string name = reader.GetString(type.Name);
        int arityIndex = name.LastIndexOf('`');
        if (arityIndex >= 0 && int.TryParse(name.AsSpan(arityIndex + 1), out int arity))
        {
            name = $"{name[..arityIndex]}<{string.Join(", ", typeArguments.Skip(argumentIndex).Take(arity))}>";
            argumentIndex += arity;
        }
        return string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
    }
}
