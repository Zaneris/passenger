using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    /// <summary>'Module.dll!Namespace.Type.Method&lt;T&gt;(ParamType paramName, ...)', named as <see cref="FrameNames"/> names it.</summary>
    private static string GetMethodFormattedName(ModuleInfo moduleInfo, int methodToken, IReadOnlyList<ICorDebugType> typeAndMethodTypeArguments)
    {
        try
        {
            ICorDebugModule module = moduleInfo.Module;
            MetadataReader reader = moduleInfo.MetadataReader.PeMetadataReader;
            MethodDefinitionHandle handle = (MethodDefinitionHandle)MetadataTokens.Handle(methodToken);
            MethodDefinition methodDefinition = reader.GetMethodDefinition(handle);
            int typeArgumentCount = reader.GetTypeDefinition(methodDefinition.GetDeclaringType()).GetGenericParameters().Count;
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
            return $"{Path.GetFileName(module.Name)}!{FrameNames.Of(reader, handle, typeArgumentNames, methodArgumentNames)}";
        }
        catch (Exception error) when (Failures.OfRuntime(error))
        {
            return "Unknown";
        }
    }
}
