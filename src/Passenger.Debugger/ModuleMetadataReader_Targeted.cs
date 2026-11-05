using System.Reflection.Metadata;

namespace Passenger.Debugger;

/// <summary>A debugger attribute an assembly declares for a type it names ([assembly: DebuggerDisplay(…, Target = …)] or
/// TargetTypeName): the type's full name, as reflection writes a definition's ("Ns.Outer+Inner`1"), and the display it
/// gives the type, or the view a [DebuggerTypeProxy] does, its name qualified by the declaring assembly where it names
/// none, as the runtime resolves it.</summary>
internal sealed record TargetedAttribute(string Target, DebuggerDisplay? Display, string? Proxy);

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
                if ((named.GetValueOrDefault("Target") ?? named.GetValueOrDefault("TargetTypeName")) is not { Length: > 0 } target)
                {
                    continue;
                }
                targeted.Add(kind is DisplayAttribute
                    ? new TargetedAttribute(DefinitionName(target), new DebuggerDisplay(first, named.GetValueOrDefault("Name"), named.GetValueOrDefault("Type")), null)
                    : new TargetedAttribute(DefinitionName(target), null, Qualified(first, assembly)));
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

    /// <summary>The definition a type's name names, without its type arguments or assembly.</summary>
    private static string DefinitionName(string serialized)
    {
        int end = serialized.IndexOfAny(['[', ',']);
        return (end < 0 ? serialized : serialized[..end]).Trim();
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
