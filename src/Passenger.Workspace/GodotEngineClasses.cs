using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Passenger.Workspace;

/// <summary>A signal of Godot's engine by Godot's own name, with its parameters' names and types as GodotSharp declares them.</summary>
public sealed record GodotEngineSignal(string Name, IReadOnlyList<(string Name, string Type)> Parameters);

/// <summary>A method of Godot's engine by Godot's own name: how many parameters it takes, and how many a call must give.</summary>
public sealed record GodotEngineMethod(string Name, int Parameters, int Required);

/// <summary>Godot's engine classes as a .NET build's GodotSharp.dll declares them, read by Godot's own names without loading
/// it: each class by its name (<c>[GodotClassName]</c>, else its C# name; GodotObject is Object) with the class it extends,
/// and its methods and signals by the names its nested <c>MethodName</c> and <c>SignalName</c> give them, which their static
/// constructors set (<c>"get_tree"</c>), a signal's parameters from its event's delegate. Read once for each file; none
/// for a file that cannot be read.</summary>
public sealed class GodotEngineClasses
{
    private static readonly ConcurrentDictionary<string, Lazy<GodotEngineClasses>> Kept = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string?> bases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, GodotEngineMethod>> methods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, GodotEngineSignal>> signals = new(StringComparer.Ordinal);

    private GodotEngineClasses()
    {
    }

    /// <summary>The engine classes a GodotSharp.dll declares, read the first time its path is asked for.</summary>
    public static GodotEngineClasses Of(string path) => Kept.GetOrAdd(path, p => new Lazy<GodotEngineClasses>(() => Read(p))).Value;

    /// <summary>Whether it read any class.</summary>
    public bool Any => bases.Count > 0;

    /// <summary>Whether it declares an engine class by its Godot name.</summary>
    public bool Knows(string type) => bases.ContainsKey(type);

    /// <summary>An engine class and the classes it extends, by Godot's names, itself first; empty for one not known.</summary>
    public IReadOnlyList<string> Chain(string type)
    {
        List<string> chain = [];
        for (string? at = type; at is not null && bases.TryGetValue(at, out string? next) && chain.Count < 64; at = next)
        {
            chain.Add(at);
        }
        return chain;
    }

    /// <summary>Whether an engine class, or one it extends, has a method by its Godot name; null for a class not known.</summary>
    public bool? HasMethod(string type, string name) => Knows(type) ? Method(type, name) is not null : null;

    /// <summary>An engine class's method by its Godot name, its own or one of a class it extends; null where it has none.</summary>
    public GodotEngineMethod? Method(string type, string name) =>
        Chain(type).Select(c => methods.TryGetValue(c, out Dictionary<string, GodotEngineMethod>? own) ? own.GetValueOrDefault(name) : null).FirstOrDefault(m => m is not null);

    /// <summary>An engine class's signal by its Godot name, its own or one of a class it extends; null where it has none.</summary>
    public GodotEngineSignal? Signal(string type, string name) =>
        Chain(type).Select(c => signals.TryGetValue(c, out Dictionary<string, GodotEngineSignal>? own) ? own.GetValueOrDefault(name) : null).FirstOrDefault(s => s is not null);

    /// <summary>Every method and signal name of an engine class and the classes it extends, by Godot's names.</summary>
    public IReadOnlyList<string> Names(string type, bool signal) =>
        [.. Chain(type).SelectMany(c => signal ? signals.TryGetValue(c, out Dictionary<string, GodotEngineSignal>? s) ? s.Keys : Enumerable.Empty<string>()
            : methods.TryGetValue(c, out Dictionary<string, GodotEngineMethod>? m) ? m.Keys : Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal)];

    private static GodotEngineClasses Read(string path)
    {
        GodotEngineClasses read = new();
        try
        {
            using FileStream stream = File.OpenRead(path);
            using PEReader pe = new(stream);
            MetadataReader metadata = pe.GetMetadataReader();
            Dictionary<TypeDefinitionHandle, string> named = [];
            foreach (TypeDefinitionHandle handle in metadata.TypeDefinitions)
            {
                TypeDefinition type = metadata.GetTypeDefinition(handle);
                if (type.GetDeclaringType().IsNil && metadata.GetString(type.Namespace) == "Godot" && (type.Attributes & System.Reflection.TypeAttributes.ClassSemanticsMask) == 0)
                {
                    string own = metadata.GetString(type.Name);
                    named[handle] = ClassName(metadata, type) ?? (own == "GodotObject" ? "Object" : own);
                }
            }
            Dictionary<(string Owner, string Nested, string Member), string> names = GodotSharpIl.NamesOf(pe, metadata, [GodotSharpIl.MethodName, GodotSharpIl.SignalName]);
            // Each class's method names apart, in the order read, so a class looks through its own alone.
            Dictionary<string, List<(string Member, string Godot)>> methodNames = new(StringComparer.Ordinal);
            foreach (((string owner, string nested, string member), string godot) in names)
            {
                if (nested == GodotSharpIl.MethodName)
                {
                    (methodNames.TryGetValue(owner, out List<(string Member, string Godot)>? owned) ? owned : methodNames[owner] = []).Add((member, godot));
                }
            }
            foreach ((TypeDefinitionHandle handle, string name) in named)
            {
                TypeDefinition type = metadata.GetTypeDefinition(handle);
                string? extended = type.BaseType.Kind == HandleKind.TypeDefinition && named.TryGetValue((TypeDefinitionHandle)type.BaseType, out string? over) ? over : null;
                if (name != "Object" && extended is null)
                {
                    continue;
                }
                string own = metadata.GetString(type.Name);
                read.bases[name] = extended;
                Dictionary<string, GodotEngineMethod> callable = new(StringComparer.Ordinal);
                ILookup<string, MethodDefinition> declaredMethods = type.GetMethods().Select(metadata.GetMethodDefinition).ToLookup(m => metadata.GetString(m.Name), StringComparer.Ordinal);
                foreach ((string member, string godot) in methodNames.GetValueOrDefault(own) ?? [])
                {
                    // Its overloads' widest and narrowest calls, by the C# method of the name the field is named after.
                    List<(int Count, int Required)> overloads = [.. declaredMethods[member].Select(m => ArityOf(metadata, m))];
                    callable[godot] = overloads.Count == 0 ? new GodotEngineMethod(godot, int.MaxValue, 0)
                        : new GodotEngineMethod(godot, overloads.Max(o => o.Count), overloads.Min(o => o.Required));
                }
                read.methods[name] = callable;
                Dictionary<string, GodotEngineSignal> declared = new(StringComparer.Ordinal);
                foreach (EventDefinition signal in type.GetEvents().Select(metadata.GetEventDefinition))
                {
                    if (names.GetValueOrDefault((own, GodotSharpIl.SignalName, metadata.GetString(signal.Name))) is { } godot)
                    {
                        declared[godot] = new GodotEngineSignal(godot, ParametersOf(metadata, signal.Type, named));
                    }
                }
                read.signals[name] = declared;
            }
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            // A file that cannot be read declares no class, so nothing is known of the engine's members.
        }
        return read;
    }

    /// <summary>How many parameters a method takes and how many have no default.</summary>
    private static (int Count, int Required) ArityOf(MetadataReader metadata, MethodDefinition method)
    {
        List<Parameter> parameters = [.. method.GetParameters().Select(metadata.GetParameter).Where(p => p.SequenceNumber > 0)];
        int count = method.DecodeSignature(new TypeNames([]), null).ParameterTypes.Length;
        return (count, count - parameters.Count(p => (p.Attributes & (System.Reflection.ParameterAttributes.Optional | System.Reflection.ParameterAttributes.HasDefault)) != 0));
    }

    /// <summary>The name a class's <c>[GodotClassName]</c> gives it; null for none.</summary>
    private static string? ClassName(MetadataReader metadata, TypeDefinition type)
    {
        foreach (CustomAttribute attribute in type.GetCustomAttributes().Select(metadata.GetCustomAttribute))
        {
            if (attribute.Constructor.Kind == HandleKind.MemberReference
                && metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is { Kind: HandleKind.TypeReference } parent
                && metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)parent).Name) == "GodotClassNameAttribute"
                || attribute.Constructor.Kind == HandleKind.MethodDefinition
                && metadata.GetString(metadata.GetTypeDefinition(metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()).Name) == "GodotClassNameAttribute")
            {
                BlobReader blob = metadata.GetBlobReader(attribute.Value);
                return blob.ReadUInt16() == 1 ? blob.ReadSerializedString() : null;
            }
        }
        return null;
    }

    /// <summary>A signal's parameters, from its event's delegate's Invoke: each one's name and type as GDScript writes it;
    /// none for an event of a delegate not declared here (an Action).</summary>
    private static IReadOnlyList<(string Name, string Type)> ParametersOf(MetadataReader metadata, EntityHandle delegateType, Dictionary<TypeDefinitionHandle, string> named)
    {
        if (delegateType.Kind != HandleKind.TypeDefinition)
        {
            return [];
        }
        TypeDefinition handler = metadata.GetTypeDefinition((TypeDefinitionHandle)delegateType);
        if (handler.GetMethods().Select(metadata.GetMethodDefinition).FirstOrDefault(m => metadata.GetString(m.Name) == "Invoke") is not { Signature.IsNil: false } invoke)
        {
            return [];
        }
        MethodSignature<string> signature = invoke.DecodeSignature(new TypeNames(named), null);
        List<string> names = [.. invoke.GetParameters().Select(metadata.GetParameter).Where(p => p.SequenceNumber > 0).OrderBy(p => p.SequenceNumber).Select(p => metadata.GetString(p.Name))];
        return [.. signature.ParameterTypes.Select((type, i) => (i < names.Count ? names[i] : $"arg{i}", type))];
    }

    /// <summary>A parameter's type as GDScript writes it: an engine class by its Godot name, C#'s numbers, strings and truths
    /// by GDScript's (<c>int</c>, <c>float</c>, <c>String</c>, <c>bool</c>), a variant untyped (empty), else its C# name.</summary>
    private sealed class TypeNames(Dictionary<TypeDefinitionHandle, string> named) : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode code) => code switch
        {
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.String => "String",
            PrimitiveTypeCode.Single or PrimitiveTypeCode.Double => "float",
            PrimitiveTypeCode.Object => string.Empty,
            _ => "int",
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            named.TryGetValue(handle, out string? engine) ? engine : Shown(reader.GetString(reader.GetTypeDefinition(handle).Name));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Shown(reader.GetString(reader.GetTypeReference(handle).Name));

        public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType.Split('`')[0] is "Array" && typeArguments.Length == 1 && typeArguments[0].Length > 0 ? $"Array[{typeArguments[0]}]" : genericType.Split('`')[0];

        public string GetSZArrayType(string elementType) => elementType switch
        {
            "String" => "PackedStringArray",
            "int" => "PackedInt64Array",
            "float" => "PackedFloat64Array",
            _ => "Array",
        };

        public string GetArrayType(string elementType, ArrayShape shape) => "Array";

        public string GetByReferenceType(string elementType) => elementType;

        public string GetPointerType(string elementType) => string.Empty;

        public string GetPinnedType(string elementType) => elementType;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => string.Empty;

        public string GetGenericMethodParameter(object? context, int index) => string.Empty;

        public string GetGenericTypeParameter(object? context, int index) => string.Empty;

        private static string Shown(string name) => name switch
        {
            "Variant" => string.Empty,
            "GodotObject" => "Object",
            "Rid" => "RID",
            "Aabb" => "AABB",
            _ => name,
        };
    }
}
