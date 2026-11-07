using System.Reflection.Metadata;

namespace Passenger.Debugger;

/// <summary>A debugger attribute an assembly declares for a type it names ([assembly: DebuggerDisplay(…, Target = …)] or
/// TargetTypeName): the type's definition, as reflection writes its full name ("Ns.Outer+Inner`1"), the one construction of
/// it the attribute names, where it names one (<see cref="TargetNames"/>), and the display it gives the type, or the view a
/// [DebuggerTypeProxy] does, its name qualified by the declaring assembly where it names none, as the runtime resolves it.</summary>
internal sealed record TargetedAttribute(string Target, string? Constructed, DebuggerDisplay? Display, string? Proxy)
{
    /// <summary>Those of a definition's attributes that reach a type constructed so: each for the definition, then each for
    /// that construction, which a reader taking the last prefers.</summary>
    public static IReadOnlyList<TargetedAttribute> Fitting(IEnumerable<TargetedAttribute> attributes, string constructed) =>
        [.. attributes.Where(t => t.Constructed is null || t.Constructed == constructed).OrderBy(t => t.Constructed is not null)];
}

/// <summary>Types' names compared without their assemblies, each argument of a constructed type in brackets after its
/// definition ("Ns.Pair`2[System.Int32,System.String[]]"): a type's home is rarely the assembly a name was compiled against,
/// which only forwards to it.</summary>
internal static class TargetNames
{
    /// <summary>The definition a serialized name names and its whole name where it constructs one; null for text that names
    /// no type.</summary>
    public static (string Definition, string? Constructed)? Read(string serialized) =>
        !TypeName.TryParse(serialized.Trim(), out TypeName? named) ? null
        : named.IsConstructedGenericType ? (named.GetGenericTypeDefinition().FullName, Written(named))
        : (named.FullName, null);

    /// <summary>A definition's name with these arguments' names, if it has any.</summary>
    public static string Constructed(string definition, IEnumerable<string> arguments) =>
        string.Join(',', arguments) is { Length: > 0 } listed ? $"{definition}[{listed}]" : definition;

    /// <summary>An array's name, in the form both a serialized name and the runtime's type give it.</summary>
    public static string Array(string element, int rank) => $"{element}[{new string(',', rank - 1)}]";

    private static string Written(TypeName type) =>
        type.IsConstructedGenericType ? Constructed(type.GetGenericTypeDefinition().FullName, type.GetGenericArguments().Select(Written))
        : type.IsArray ? Array(Written(type.GetElementType()), type.GetArrayRank())
        : type.IsPointer ? $"{Written(type.GetElementType())}*"
        : type.FullName;
}

public partial class ModuleMetadataReader
{
    private const string DisplayAttribute = "System.Diagnostics.DebuggerDisplayAttribute";

    private const string ProxyAttribute = "System.Diagnostics.DebuggerTypeProxyAttribute";

    private IReadOnlyList<TargetedAttribute>? _targeted;

    /// <summary>The [DebuggerDisplay] and [DebuggerTypeProxy] attributes the module's assembly declares for types it targets,
    /// read once.</summary>
    internal IReadOnlyList<TargetedAttribute> Targeted => _targeted ??= ReadTargeted();

    private IReadOnlyList<TargetedAttribute> ReadTargeted()
    {
        MetadataReader reader = _peMetadataReader;
        if (!reader.IsAssembly)
        {
            return [];
        }
        string assembly = reader.GetString(reader.GetAssemblyDefinition().Name);
        List<TargetedAttribute> targeted = [];
        foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (AttributeTypeName(reader, attribute) is not ((DisplayAttribute or ProxyAttribute) and { } kind))
            {
                continue;
            }
            try
            {
                BlobReader blob = reader.GetBlobReader(attribute.Value);
                (string first, Dictionary<string, string?> named) = ReadStringAttribute(ref blob);
                if ((named.GetValueOrDefault("Target") ?? named.GetValueOrDefault("TargetTypeName")) is not { Length: > 0 } target
                    || TargetNames.Read(target) is not { } type)
                {
                    continue;
                }
                targeted.Add(kind is DisplayAttribute
                    ? new TargetedAttribute(type.Definition, type.Constructed, new DebuggerDisplay(first, named.GetValueOrDefault("Name"), named.GetValueOrDefault("Type")), null)
                    : new TargetedAttribute(type.Definition, type.Constructed, null, Qualified(first, assembly)));
            }
            catch (BadImageFormatException)
            {
                // An attribute's blob of another shape is not one the debugger reads.
            }
        }
        return targeted;
    }

    /// <summary>An attribute's first argument, a string or a type's name, and its named arguments of either kind.</summary>
    internal static (string First, Dictionary<string, string?> Named) ReadStringAttribute(ref BlobReader blob)
    {
        if (blob.ReadUInt16() != 0x0001)
        {
            throw new BadImageFormatException("Invalid custom attribute prolog");
        }
        string first = blob.ReadSerializedString() ?? string.Empty;
        Dictionary<string, string?> named = new(StringComparer.Ordinal);
        ushort count = blob.ReadUInt16();
        for (int at = 0; at < count; at++)
        {
            blob.ReadByte();
            if (blob.ReadSerializationTypeCode() is not (SerializationTypeCode.String or SerializationTypeCode.Type))
            {
                throw new BadImageFormatException("A named argument that is neither a string nor a type");
            }
            string name = blob.ReadSerializedString() ?? string.Empty;
            named[name] = blob.ReadSerializedString();
        }
        return (first, named);
    }

    /// <summary>A type's name qualified by an assembly where it names none at its top level.</summary>
    private static string Qualified(string typeName, string assembly)
    {
        int depth = 0;
        foreach (char c in typeName)
        {
            depth += c switch { '[' => 1, ']' => -1, _ => 0 };
            if (c == ',' && depth == 0)
            {
                return typeName;
            }
        }
        return $"{typeName}, {assembly}";
    }
}
