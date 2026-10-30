using Passenger.Core;

namespace Passenger.Xaml.Metadata;

/// <summary>What kind of type a <see cref="ClrType"/> is.</summary>
public enum ClrTypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
}

/// <summary>A type as a member's signature or a base list names it: from metadata by its full metadata name
/// (<c>System.Collections.Generic.List`1</c>), from source as written (<c>List</c>, with the <see cref="Scope"/> it is read
/// in) until a type system resolves it. Its arguments are its generic type's.</summary>
public sealed record TypeRef(string Name, IReadOnlyList<TypeRef> Arguments, bool IsArray = false, SourceScope? Scope = null, bool IsGenericParameter = false)
{
    public static TypeRef Named(string fullName) => new(fullName, []);

    /// <summary>The name a person reads: without namespace or arity, its arguments in angle brackets.</summary>
    public string Display
    {
        get
        {
            string name = Name;
            int dot = name.LastIndexOfAny(['.', '+']);
            if (dot >= 0 && Scope is null)
            {
                name = name[(dot + 1)..];
            }
            int tick = name.IndexOf('`');
            if (tick >= 0)
            {
                name = name[..tick];
            }
            string shown = TypeNames.Keyword(Name) ?? name;
            if (Arguments.Count > 0)
            {
                shown += $"<{string.Join(", ", Arguments.Select(a => a.Display))}>";
            }
            return IsArray ? shown + "[]" : shown;
        }
    }

    public override string ToString() => Display;
}

/// <summary>Where a name in source is read: its file's namespace, its usings and aliases (the project's global ones too),
/// and the types it is declared inside, so <c>IBrush</c> resolves as the compiler would resolve it there.</summary>
public sealed record SourceScope(string? Namespace, IReadOnlyList<string> Usings, IReadOnlyDictionary<string, string> Aliases, IReadOnlyList<string> Enclosing)
{
    public static readonly SourceScope Empty = new(null, [], new Dictionary<string, string>(), []);
}

/// <summary>A place in a source file, its line and column from zero, its lines as Roslyn counts them: a lone '\r', U+0085,
/// U+2028 and U+2029 end one too, as the language server's do, so a place is read in its file as the server's are.</summary>
public sealed record SourcePlace(string Path, int Line, int Column);

public enum MemberKind
{
    Property,
    Field,
    Method,
    Event,
    Constructor,
}

/// <summary>An attribute a type, member or assembly carries, with its arguments as constants (a <c>typeof</c> as the
/// type's full name).</summary>
public sealed record ClrAttribute(string TypeName, IReadOnlyList<object?> Arguments, IReadOnlyList<KeyValuePair<string, object?>> Named)
{
    /// <summary>The attribute's name without namespace or its <c>Attribute</c> ending.</summary>
    public string ShortName
    {
        get
        {
            string name = TypeName[(TypeName.LastIndexOf('.') + 1)..];
            return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
        }
    }

    public string? StringArgument(int index) => index < Arguments.Count ? Arguments[index] as string : null;
}

/// <summary>A member of a type that XAML can reach: a property, a field (an Avalonia or WPF property's static field, an
/// enum's member), a method (an attached property's getter or setter, a constructor's parameters), or an event.</summary>
public sealed class ClrMember
{
    public ClrMember(ClrType declaringType, string name, MemberKind kind, TypeRef type, bool isStatic, bool isPublic, bool canWrite,
        IReadOnlyList<TypeRef>? parameters = null, IReadOnlyList<string>? parameterNames = null, IReadOnlyList<ClrAttribute>? attributes = null,
        SourcePlace? place = null, object? constant = null)
    {
        DeclaringType = declaringType;
        Name = name;
        Kind = kind;
        Type = type;
        IsStatic = isStatic;
        IsPublic = isPublic;
        CanWrite = canWrite;
        Parameters = parameters ?? [];
        ParameterNames = parameterNames ?? [];
        Attributes = attributes ?? [];
        Place = place;
        Constant = constant;
    }

    public ClrType DeclaringType { get; }

    public string Name { get; }

    public MemberKind Kind { get; }

    /// <summary>A property's or field's type, a method's return type, an event's handler type; void for a constructor.</summary>
    public TypeRef Type { get; }

    public bool IsStatic { get; }

    /// <summary>Public, or for the project's own source, anything but private (XAML compiles into its assembly).</summary>
    public bool IsPublic { get; }

    /// <summary>A property with a setter its XAML can call, or a field that is not read-only.</summary>
    public bool CanWrite { get; }

    public IReadOnlyList<TypeRef> Parameters { get; }

    public IReadOnlyList<string> ParameterNames { get; }

    public IReadOnlyList<ClrAttribute> Attributes { get; }

    /// <summary>Where source declares it; null for metadata.</summary>
    public SourcePlace? Place { get; }

    /// <summary>A constant field's value (an enum member's); for a static property of source, the string its getter first gives
    /// a call or a constructor (a generated resource class's key, its ResourceManager's name).</summary>
    public object? Constant { get; }

    public bool Has(string attribute) => Attributes.Any(a => a.ShortName == attribute);

    public override string ToString() => $"{DeclaringType.Name}.{Name}";
}

/// <summary>A type a XAML file can name, read from an assembly's metadata or the project's own C#. Its base, attributes
/// and members are read the first time they are asked for, so a type system of every type a project references costs
/// only their names until a file reaches one. Safe on any thread.</summary>
public sealed class ClrType
{
    private readonly Lazy<ClrTypeDetails> details;

    public ClrType(string @namespace, string metadataName, ClrTypeKind kind, TypeOrigin origin, Func<ClrType, ClrTypeDetails> read,
        string? declaringTypeName = null)
    {
        Namespace = @namespace;
        MetadataName = metadataName;
        Kind = kind;
        Origin = origin;
        DeclaringTypeName = declaringTypeName;
        string own = declaringTypeName is null ? metadataName : $"{declaringTypeName}+{metadataName}";
        FullName = declaringTypeName is not null ? own : @namespace.Length > 0 ? $"{@namespace}.{metadataName}" : metadataName;
        int tick = metadataName.IndexOf('`');
        Name = tick < 0 ? metadataName : metadataName[..tick];
        Arity = tick < 0 ? 0 : int.Parse(metadataName.AsSpan(tick + 1), System.Globalization.CultureInfo.InvariantCulture);
        details = new Lazy<ClrTypeDetails>(() => read(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Namespace { get; }

    /// <summary>The name with its arity (<c>List`1</c>).</summary>
    public string MetadataName { get; }

    /// <summary>The name a person writes (<c>List</c>).</summary>
    public string Name { get; }

    public int Arity { get; }

    /// <summary>Namespace and metadata name, a nested type's after its declaring type's and a <c>+</c>.</summary>
    public string FullName { get; }

    /// <summary>The full name of the type it is nested in, null for a top-level one.</summary>
    public string? DeclaringTypeName { get; }

    public bool IsNested => DeclaringTypeName is not null;

    public ClrTypeKind Kind { get; }

    public TypeOrigin Origin { get; }

    public bool IsFromSource => Origin.Place is not null;

    private ClrTypeDetails Details => details.Value;

    public TypeRef? Base => Details.Base;

    public IReadOnlyList<TypeRef> Interfaces => Details.Interfaces;

    public IReadOnlyList<ClrAttribute> Attributes => Details.Attributes;

    /// <summary>Its own members, not its bases'.</summary>
    public IReadOnlyList<ClrMember> Members => Details.Members;

    public bool IsAbstract => Details.IsAbstract;

    public bool IsStatic => Details.IsStatic;

    public bool IsPublic => Details.IsPublic;

    public IReadOnlyList<string> GenericParameters => Details.GenericParameters;

    /// <summary>Whether XAML can make one as an element: a class or struct with a public constructor taking nothing.</summary>
    public bool HasDefaultConstructor => Kind == ClrTypeKind.Struct
        || (Kind == ClrTypeKind.Class && !IsAbstract && !IsStatic && Details.HasDefaultConstructor);

    public bool Has(string attribute) => Attributes.Any(a => a.ShortName == attribute);

    public ClrAttribute? Attribute(string attribute) => Attributes.FirstOrDefault(a => a.ShortName == attribute);

    public IEnumerable<ClrAttribute> AttributesNamed(string attribute) => Attributes.Where(a => a.ShortName == attribute);

    public override string ToString() => FullName;
}

/// <summary>What is read of a type only when it is first asked for.</summary>
public sealed record ClrTypeDetails(TypeRef? Base, IReadOnlyList<TypeRef> Interfaces, IReadOnlyList<ClrAttribute> Attributes, IReadOnlyList<ClrMember> Members,
    bool IsAbstract, bool IsStatic, bool IsPublic, bool HasDefaultConstructor, IReadOnlyList<string> GenericParameters)
{
    public static readonly ClrTypeDetails None = new(null, [], [], [], false, false, true, false, []);
}

/// <summary>Where a type comes from: an assembly by its name and file, and for the project's own source the place it is
/// declared.</summary>
public sealed record TypeOrigin(string AssemblyName, string? AssemblyPath, SourcePlace? Place);

/// <summary>C#'s keywords for the types they name (<see cref="PrimitiveTypes"/>).</summary>
public static class TypeNames
{
    /// <summary>The full name a C# keyword names, or null.</summary>
    public static string? FromKeyword(string keyword) => PrimitiveTypes.OfKeyword(keyword)?.SystemName;

    /// <summary>The C# keyword for a full name, or null.</summary>
    public static string? Keyword(string fullName) => PrimitiveTypes.Named(fullName)?.Keyword;
}
