using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using Passenger.Core;

namespace Passenger.Debugger;

/// <summary>One frame of an exception's stored stack: its method's module's metadata and the method's token, the file and
/// line its code was at (none where unknown, or where its module has no symbols of its own), and whether it is the last
/// frame of a stack trace an earlier throw left (rethrown, as an await rethrows).</summary>
internal readonly record struct TracedFrame(MetadataReader Reader, int MethodToken, (string File, int Line)? Source, bool LastOfEarlierThrow);

/// <summary>An exception's stored stack written as its StackTrace property writes it, from metadata and symbols alone: each
/// shown frame "   at Type.Method[T](ParameterType name) in file:line N", a state machine's method named as the method
/// that made it (an iterator's with +MoveNext() after), frames marked [StackTraceHidden] or inlined aggressively left out
/// but the last, and where an earlier throw's trace ends, a line saying so.</summary>
internal static class StackTraceText
{
    private const string EndOfEarlierThrow = "--- End of stack trace from previous location ---";

    private const MethodImplAttributes RuntimeAsync = (MethodImplAttributes)0x2000;

    public static string Of(IReadOnlyList<TracedFrame> frames)
    {
        StringBuilder text = new();
        for (int at = 0; at < frames.Count; at++)
        {
            TracedFrame frame = frames[at];
            MetadataReader reader = frame.Reader;
            MethodDefinitionHandle handle = MetadataTokens.MethodDefinitionHandle(frame.MethodToken);
            MethodDefinition method = reader.GetMethodDefinition(handle);
            if (!Shown(reader, method) && at != frames.Count - 1)
            {
                continue;
            }
            if (text.Length > 0)
            {
                text.AppendLine();
            }
            text.Append("   at ");
            bool async = (method.ImplAttributes & RuntimeAsync) != 0;
            TypeDefinitionHandle? declaring = DeclaringTypeOf(method);
            string name = reader.GetString(method.Name);
            MethodDefinitionHandle named = handle;
            bool iterator = false;
            if (!async && declaring is { } machine && Has(reader, reader.GetTypeDefinition(machine).GetCustomAttributes(), "System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
            {
                async = Implements(reader, machine, "System.Runtime.CompilerServices.IAsyncStateMachine");
                if ((async || Implements(reader, machine, "System.Collections.IEnumerator")) && MadeBy(reader, machine) is { } made)
                {
                    (named, iterator) = made;
                    declaring = DeclaringTypeOf(reader.GetMethodDefinition(named));
                }
            }
            MethodDefinition shown = reader.GetMethodDefinition(named);
            if (declaring is { } type)
            {
                text.Append(MetadataTypeNames.Of(reader, type, '.')).Append('.');
            }
            text.Append(reader.GetString(shown.Name));
            ImmutableArray<string> methodParameters = [.. shown.GetGenericParameters().Select(p => reader.GetString(reader.GetGenericParameter(p).Name))];
            if (methodParameters.Length > 0)
            {
                text.Append('[').AppendJoin(',', methodParameters).Append(']');
            }
            text.Append('(').AppendJoin(", ", Parameters(reader, shown, methodParameters)).Append(')');
            if (iterator)
            {
                text.Append('+').Append(name).Append("()");
            }
            if (frame.Source is { } source)
            {
                text.Append(CultureInfo.InvariantCulture, $" in {source.File}:line {source.Line}");
            }
            if (frame.LastOfEarlierThrow && !async)
            {
                text.AppendLine().Append(EndOfEarlierThrow);
            }
        }
        return text.ToString();
    }

    /// <summary>Whether a method shows in a stack trace: not marked [StackTraceHidden], nor its type, nor inlined aggressively.</summary>
    private static bool Shown(MetadataReader reader, MethodDefinition method) =>
        (method.ImplAttributes & MethodImplAttributes.AggressiveInlining) == 0
        && !Has(reader, method.GetCustomAttributes(), StackTraceHidden)
        && (DeclaringTypeOf(method) is not { } type || !Has(reader, reader.GetTypeDefinition(type).GetCustomAttributes(), StackTraceHidden));

    private const string StackTraceHidden = "System.Diagnostics.StackTraceHiddenAttribute";

    /// <summary>A method's type, none for a method of the module itself, as reflection has it.</summary>
    private static TypeDefinitionHandle? DeclaringTypeOf(MethodDefinition method) =>
        method.GetDeclaringType() is { IsNil: false } type && MetadataTokens.GetRowNumber(type) != 1 ? type : null;

    private static bool Has(MetadataReader reader, CustomAttributeHandleCollection attributes, string name) =>
        ModuleMetadataReader.HasAttribute(reader, attributes, [name]);

    /// <summary>Whether a type, or a type it derives from in its module, names an interface of this full name.</summary>
    private static bool Implements(MetadataReader reader, TypeDefinitionHandle handle, string name)
    {
        for (TypeDefinitionHandle? at = handle; at is { } each;)
        {
            TypeDefinition type = reader.GetTypeDefinition(each);
            foreach (InterfaceImplementationHandle implemented in type.GetInterfaceImplementations())
            {
                EntityHandle named = reader.GetInterfaceImplementation(implemented).Interface;
                if (named.Kind switch
                {
                    HandleKind.TypeReference => MetadataTypeNames.Of(reader, (TypeReferenceHandle)named),
                    HandleKind.TypeDefinition => MetadataTypeNames.Of(reader, (TypeDefinitionHandle)named),
                    _ => null,
                } == name)
                {
                    return true;
                }
            }
            at = type.BaseType.Kind is HandleKind.TypeDefinition ? (TypeDefinitionHandle)type.BaseType : null;
        }
        return false;
    }

    /// <summary>The method a state machine was made for: the one of the type it is nested in whose [AsyncStateMachine],
    /// [IteratorStateMachine] or [AsyncIteratorStateMachine] names it, and whether that makes an iterator.</summary>
    private static (MethodDefinitionHandle Method, bool Iterator)? MadeBy(MetadataReader reader, TypeDefinitionHandle machine)
    {
        TypeDefinitionHandle parent = reader.GetTypeDefinition(machine).GetDeclaringType();
        if (parent.IsNil)
        {
            return null;
        }
        string machineName = MetadataTypeNames.Of(reader, machine);
        foreach (MethodDefinitionHandle candidate in reader.GetTypeDefinition(parent).GetMethods())
        {
            foreach (CustomAttributeHandle handle in reader.GetMethodDefinition(candidate).GetCustomAttributes())
            {
                CustomAttribute attribute = reader.GetCustomAttribute(handle);
                string? attributeName = ModuleMetadataReader.AttributeTypeName(reader, attribute);
                if (attributeName is not (AsyncMachine or IteratorMachine or AsyncIteratorMachine))
                {
                    continue;
                }
                BlobReader blob = reader.GetBlobReader(attribute.Value);
                if (blob.Length < 2 || blob.ReadUInt16() != 1 || blob.ReadSerializedString() is not { } typeName || TypeNamed(typeName) != machineName)
                {
                    continue;
                }
                return (candidate, attributeName is IteratorMachine or AsyncIteratorMachine);
            }
        }
        return null;
    }

    private const string AsyncMachine = "System.Runtime.CompilerServices.AsyncStateMachineAttribute";

    private const string IteratorMachine = "System.Runtime.CompilerServices.IteratorStateMachineAttribute";

    private const string AsyncIteratorMachine = "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute";

    /// <summary>The full name of the type an attribute's Type argument names, without its type arguments or assembly.</summary>
    private static string TypeNamed(string serialized)
    {
        int end = serialized.IndexOfAny(['[', ',']);
        return (end < 0 ? serialized : serialized[..end]).Trim();
    }

    /// <summary>A method's parameters as reflection names them: each type's own short name, then its name where it has one.</summary>
    private static IEnumerable<string> Parameters(MetadataReader reader, MethodDefinition method, ImmutableArray<string> methodParameters)
    {
        ImmutableArray<string> typeParameters = DeclaringTypeOf(method) is { } type
            ? [.. reader.GetTypeDefinition(type).GetGenericParameters().Select(p => reader.GetString(reader.GetGenericParameter(p).Name))]
            : [];
        ImmutableArray<string> types = method.DecodeSignature(ShortTypeNames.Instance, new ShortTypeNames.Context(typeParameters, methodParameters)).ParameterTypes;
        string?[] names = new string?[types.Length];
        foreach (ParameterHandle handle in method.GetParameters())
        {
            Parameter parameter = reader.GetParameter(handle);
            if (parameter.SequenceNumber is > 0 and int sequence && sequence <= names.Length)
            {
                names[sequence - 1] = reader.GetString(parameter.Name);
            }
        }
        return types.Select((parameterType, at) => names[at] is { } name ? $"{parameterType} {name}" : parameterType);
    }

    /// <summary>Signature types by the short names reflection's Type.Name gives: Int32, List`1, String[], Int32&amp;.</summary>
    private sealed class ShortTypeNames : ISignatureTypeProvider<string, ShortTypeNames.Context>
    {
        public static ShortTypeNames Instance { get; } = new();

        public sealed record Context(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters);

        public string GetArrayType(string elementType, ArrayShape shape) => shape.Rank == 1 ? $"{elementType}[*]" : $"{elementType}[{new string(',', shape.Rank - 1)}]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "IntPtr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;
        public string GetGenericMethodParameter(Context genericContext, int index) => index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : $"!!{index}";
        public string GetGenericTypeParameter(Context genericContext, int index) => index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : $"!{index}";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => PrimitiveTypes.Of(typeCode).Name;
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSpecification(MetadataReader reader, Context genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
