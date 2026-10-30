using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Passenger.Core;

namespace Passenger.Xaml.Metadata;

/// <summary>The public types of one assembly, read with System.Reflection.Metadata: their names at once, the rest of each
/// type the first time it is asked for. Only the metadata is kept, in memory, so the file is never held open (a project's
/// own build can write it again). Read once per file and version for the whole process (<see cref="Get"/>): the packages
/// and the framework's reference assemblies every project and window shares are read once.</summary>
public sealed class AssemblyTypes
{
    private static readonly FileMemo<AssemblyTypes> Cache = new();

    /// <summary>The attributes a XAML dialect reads, by their short names; every other is left unread.</summary>
    private static readonly HashSet<string> Wanted = new(StringComparer.Ordinal)
    {
        "XmlnsDefinitionAttribute", "XmlnsPrefixAttribute", "ContentPropertyAttribute", "ContentAttribute", "TemplatePartAttribute",
        "PseudoClassesAttribute", "RuntimeNamePropertyAttribute", "DictionaryKeyPropertyAttribute", "MarkupExtensionReturnTypeAttribute",
        "ObsoleteAttribute", "EditorBrowsableAttribute", "ConstructorArgumentAttribute", "TypeConverterAttribute", "AssignBindingAttribute",
        "InheritDataTypeFromItemsAttribute", "TemplateContentAttribute", "DataTypeAttribute", "ControlTemplateAttribute",
        "TrimSurroundingWhitespaceAttribute", "WhitespaceSignificantCollectionAttribute", "UsableDuringInitializationAttribute",
    };

    private readonly PEReader pe;
    private readonly MetadataReader reader;
    private readonly object gate = new();

    private AssemblyTypes(string path, PEReader pe, Stream file)
    {
        Path = path;
        this.pe = pe;
        reader = pe.GetMetadataReader();
        Name = reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : System.IO.Path.GetFileNameWithoutExtension(path);
        Types = ReadTypes();
        Attributes = reader.IsAssembly ? ReadAttributes(reader.GetAssemblyDefinition().GetCustomAttributes()) : [];
        ClassXamlFiles = ReadClassXamlFiles(file);
    }

    public string Path { get; }

    public string Name { get; }

    public IReadOnlyList<ClrType> Types { get; }

    /// <summary>The assembly's own attributes a dialect reads (<c>XmlnsDefinition</c>, <c>XmlnsPrefix</c>).</summary>
    public IReadOnlyList<ClrAttribute> Attributes { get; }

    /// <summary>The XAML files compiled into it on their own, by their paths in its project
    /// (<c>/Controls/FluentControls.xaml</c>), as the methods Avalonia's compiler makes for each name them
    /// (<c>Build:/Controls/FluentControls.xaml</c>); a dictionary one of them merges in is compiled into it.</summary>
    public IReadOnlyList<string> XamlFiles
    {
        get
        {
            lock (gate)
            {
                if (xamlFiles is not null)
                {
                    return xamlFiles;
                }
                HashSet<string> found = new(StringComparer.Ordinal);
                foreach (MethodDefinitionHandle handle in reader.MethodDefinitions)
                {
                    string name = reader.GetString(reader.GetMethodDefinition(handle).Name);
                    if (name.StartsWith("Build:/", StringComparison.Ordinal))
                    {
                        found.Add(name["Build:".Length..]);
                    }
                }
                return xamlFiles = [.. found.Order(StringComparer.Ordinal)];
            }
        }
    }

    private List<string>? xamlFiles;

    /// <summary>The XAML files compiled into one of its classes (<c>x:Class</c>), by their paths in its project
    /// (<c>/FluentTheme.xaml</c>), as the index of classes Avalonia's compiler keeps among its resources names them: a
    /// theme's root, which merges in the rest, among them.</summary>
    public IReadOnlyList<string> ClassXamlFiles { get; }

    /// <summary>The string constants its code holds: what XAML compiled into it keeps of its resources' keys.</summary>
    public IReadOnlyList<string> Strings
    {
        get
        {
            lock (gate)
            {
                if (strings is not null)
                {
                    return strings;
                }
                List<string> found = [];
                if (reader.GetHeapSize(HeapIndex.UserString) <= 1)
                {
                    return strings = found;
                }
                UserStringHandle handle = MetadataTokens.UserStringHandle(1);
                for (int i = 0; i < 1_000_000 && !handle.IsNil; i++)
                {
                    string value = reader.GetUserString(handle);
                    if (value.Length > 0)
                    {
                        found.Add(value);
                    }
                    handle = reader.GetNextHandle(handle);
                }
                return strings = found;
            }
        }
    }

    private List<string>? strings;

    /// <summary>An assembly's types, read once per file and the time it was last written; null for a file that is not
    /// one (a native library, or one being written).</summary>
    public static AssemblyTypes? Get(string path) => Cache.Get(path, TryRead);

    private static AssemblyTypes? TryRead(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            PEReader pe = new(stream, PEStreamOptions.PrefetchMetadata | PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata)
            {
                pe.Dispose();
                return null;
            }
            return new AssemblyTypes(path, pe, stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The XAML files of its classes, read from the file while it is open: only the index of Avalonia's resources
    /// and the entry naming them, never the rest (a font library's fonts are among them).</summary>
    private IReadOnlyList<string> ReadClassXamlFiles(Stream file)
    {
        if (!reader.IsAssembly || pe.PEHeaders.CorHeader is not { } cor || !pe.PEHeaders.TryGetDirectoryOffset(cor.ResourcesDirectory, out int resources))
        {
            return [];
        }
        foreach (ManifestResourceHandle handle in reader.ManifestResources)
        {
            ManifestResource resource = reader.GetManifestResource(handle);
            if (resource.Implementation.IsNil && reader.StringComparer.Equals(resource.Name, "!AvaloniaResources"))
            {
                file.Position = resources + resource.Offset;
                return XamlClassFiles(file);
            }
        }
        return [];
    }

    /// <summary>The paths of the XAML compiled into classes, from Avalonia's resources (the length the assembly gives
    /// them, then their index and entries) read from where a stream stands: none where they hold no such entry, or are
    /// not as Avalonia 11 and later write them.</summary>
    public static IReadOnlyList<string> XamlClassFiles(Stream resources)
    {
        try
        {
            using BinaryReader read = new(resources, System.Text.Encoding.UTF8, leaveOpen: true);
            long length = read.ReadUInt32();
            long start = resources.Position;
            int indexSize = read.ReadInt32();
            if (indexSize < 8 || indexSize > length - 4 || read.ReadInt32() != 2)
            {
                return [];
            }
            int count = read.ReadInt32();
            for (int i = 0; i < count && resources.Position < start + 4 + indexSize; i++)
            {
                string path = read.ReadString();
                int offset = read.ReadInt32();
                int size = read.ReadInt32();
                if (path != "/!AvaloniaResourceXamlInfo")
                {
                    continue;
                }
                if (offset < 0 || size is < 0 or > 16 << 20 || 4L + indexSize + offset + size > length)
                {
                    return [];
                }
                resources.Position = start + 4 + indexSize + offset;
                using MemoryStream info = new(read.ReadBytes(size));
                using System.Xml.XmlReader xml = System.Xml.XmlReader.Create(info, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null });
                return [.. System.Xml.Linq.XDocument.Load(xml).Descendants().Where(e => e.Name.LocalName == "Value" && e.Parent?.Name.LocalName == "KeyValueOfstringstring")
                    .Select(e => e.Value).Where(v => v.StartsWith('/'))];
            }
            return [];
        }
        catch (Exception error) when (error is IOException or System.Xml.XmlException)
        {
            return [];
        }
    }

    private List<ClrType> ReadTypes()
    {
        List<ClrType> types = [];
        Dictionary<TypeDefinitionHandle, string> names = [];
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            if (!Visible(definition))
            {
                continue;
            }
            string name = reader.GetString(definition.Name);
            if (name.Length == 0 || name[0] == '<')
            {
                continue;
            }
            TypeDefinitionHandle declaring = definition.GetDeclaringType();
            string? declaringName = declaring.IsNil ? null : FullName(declaring, names);
            if (!declaring.IsNil && declaringName is null)
            {
                continue;
            }
            string @namespace = declaring.IsNil ? reader.GetString(definition.Namespace) : reader.GetString(reader.GetTypeDefinition(Outermost(declaring)).Namespace);
            ClrType type = new(@namespace, name, KindOf(definition), new TypeOrigin(Name, Path, null), t => Read(t, handle), declaringName);
            names[handle] = type.FullName;
            types.Add(type);
        }
        return types;
    }

    private TypeDefinitionHandle Outermost(TypeDefinitionHandle handle)
    {
        while (true)
        {
            TypeDefinitionHandle declaring = reader.GetTypeDefinition(handle).GetDeclaringType();
            if (declaring.IsNil)
            {
                return handle;
            }
            handle = declaring;
        }
    }

    /// <summary>A type's full name, a nested one's only when every type it is in is public.</summary>
    private string? FullName(TypeDefinitionHandle handle, Dictionary<TypeDefinitionHandle, string> named)
    {
        if (named.TryGetValue(handle, out string? known))
        {
            return known;
        }
        TypeDefinition definition = reader.GetTypeDefinition(handle);
        if (!Visible(definition))
        {
            return null;
        }
        TypeDefinitionHandle declaring = definition.GetDeclaringType();
        string name = reader.GetString(definition.Name);
        if (declaring.IsNil)
        {
            string @namespace = reader.GetString(definition.Namespace);
            return @namespace.Length > 0 ? $"{@namespace}.{name}" : name;
        }
        return FullName(declaring, named) is { } outer ? $"{outer}+{name}" : null;
    }

    private static bool Visible(TypeDefinition definition) =>
        (definition.Attributes & TypeAttributes.VisibilityMask) is TypeAttributes.Public or TypeAttributes.NestedPublic;

    private ClrTypeKind KindOf(TypeDefinition definition)
    {
        if ((definition.Attributes & TypeAttributes.Interface) != 0)
        {
            return ClrTypeKind.Interface;
        }
        string? baseName = definition.BaseType.IsNil ? null : HandleName(definition.BaseType);
        return baseName switch
        {
            "System.Enum" => ClrTypeKind.Enum,
            "System.ValueType" => ClrTypeKind.Struct,
            "System.MulticastDelegate" => ClrTypeKind.Delegate,
            _ => ClrTypeKind.Class,
        };
    }

    /// <summary>The full name a type handle names, without decoding a specification's arguments.</summary>
    private string? HandleName(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => DefinitionName((TypeDefinitionHandle)handle),
        HandleKind.TypeReference => ReferenceName(reader, (TypeReferenceHandle)handle),
        _ => null,
    };

    private string DefinitionName(TypeDefinitionHandle handle)
    {
        TypeDefinition definition = reader.GetTypeDefinition(handle);
        string name = reader.GetString(definition.Name);
        TypeDefinitionHandle declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return $"{DefinitionName(declaring)}+{name}";
        }
        string @namespace = reader.GetString(definition.Namespace);
        return @namespace.Length > 0 ? $"{@namespace}.{name}" : name;
    }

    internal static string ReferenceName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference reference = reader.GetTypeReference(handle);
        string name = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{ReferenceName(reader, (TypeReferenceHandle)reference.ResolutionScope)}+{name}";
        }
        string @namespace = reader.GetString(reference.Namespace);
        return @namespace.Length > 0 ? $"{@namespace}.{name}" : name;
    }

    /// <summary>The rest of a type, read under the assembly's lock: a metadata reader is not safe on two threads at once.</summary>
    private ClrTypeDetails Read(ClrType type, TypeDefinitionHandle handle)
    {
        lock (gate)
        {
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            string[] generics = [.. definition.GetGenericParameters().Select(p => reader.GetString(reader.GetGenericParameter(p).Name))];
            GenericContext context = new(generics, []);
            SignatureTypes types = new(this);
            TypeRef? baseType = definition.BaseType.IsNil ? null : types.Decode(definition.BaseType, context);
            List<TypeRef> interfaces = [];
            foreach (InterfaceImplementationHandle implemented in definition.GetInterfaceImplementations())
            {
                interfaces.Add(types.Decode(reader.GetInterfaceImplementation(implemented).Interface, context));
            }
            List<ClrMember> members = [];
            bool defaultConstructor = false;
            foreach (MethodDefinitionHandle methodHandle in definition.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                {
                    continue;
                }
                string name = reader.GetString(method.Name);
                bool isStatic = (method.Attributes & MethodAttributes.Static) != 0;
                bool constructor = name == ".ctor";
                // Of the other methods, only an attached property's getter and setter and an attached event's adder are XAML's.
                if (!constructor && !(isStatic && (name.StartsWith("Get", StringComparison.Ordinal) || name.StartsWith("Set", StringComparison.Ordinal)
                    || (name.StartsWith("Add", StringComparison.Ordinal) && name.EndsWith("Handler", StringComparison.Ordinal)))))
                {
                    continue;
                }
                string[] methodGenerics = [.. method.GetGenericParameters().Select(p => reader.GetString(reader.GetGenericParameter(p).Name))];
                MethodSignature<TypeRef> signature = method.DecodeSignature(types, new GenericContext(generics, methodGenerics));
                string[] parameterNames = new string[signature.ParameterTypes.Length];
                foreach (ParameterHandle parameterHandle in method.GetParameters())
                {
                    Parameter parameter = reader.GetParameter(parameterHandle);
                    if (parameter.SequenceNumber > 0 && parameter.SequenceNumber <= parameterNames.Length)
                    {
                        parameterNames[parameter.SequenceNumber - 1] = reader.GetString(parameter.Name);
                    }
                }
                if (constructor)
                {
                    if (isStatic)
                    {
                        continue;
                    }
                    defaultConstructor |= signature.ParameterTypes.Length == 0;
                    members.Add(new ClrMember(type, name, MemberKind.Constructor, TypeRef.Named("System.Void"), false, true, false,
                        signature.ParameterTypes, parameterNames, ReadAttributes(method.GetCustomAttributes())));
                    continue;
                }
                members.Add(new ClrMember(type, name, MemberKind.Method, signature.ReturnType, true, true, false, signature.ParameterTypes, parameterNames));
            }
            foreach (PropertyDefinitionHandle propertyHandle in definition.GetProperties())
            {
                PropertyDefinition property = reader.GetPropertyDefinition(propertyHandle);
                PropertyAccessors accessors = property.GetAccessors();
                MethodDefinition? getter = accessors.Getter.IsNil ? null : reader.GetMethodDefinition(accessors.Getter);
                MethodDefinition? setter = accessors.Setter.IsNil ? null : reader.GetMethodDefinition(accessors.Setter);
                bool publicGet = getter is { } g && (g.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                bool publicSet = setter is { } s && (s.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
                if (!publicGet && !publicSet)
                {
                    continue;
                }
                MethodDefinition accessor = (publicGet ? getter : setter)!.Value;
                bool isStatic = (accessor.Attributes & MethodAttributes.Static) != 0;
                MethodSignature<TypeRef> signature = property.DecodeSignature(types, context);
                // An indexer (Item[...]) is no property XAML sets.
                if (signature.ParameterTypes.Length > 0)
                {
                    continue;
                }
                members.Add(new ClrMember(type, reader.GetString(property.Name), MemberKind.Property, signature.ReturnType, isStatic, true, publicSet,
                    attributes: ReadAttributes(property.GetCustomAttributes())));
            }
            foreach (FieldDefinitionHandle fieldHandle in definition.GetFields())
            {
                FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.FieldAccessMask) != FieldAttributes.Public)
                {
                    continue;
                }
                string name = reader.GetString(field.Name);
                bool isStatic = (field.Attributes & FieldAttributes.Static) != 0;
                bool literal = (field.Attributes & FieldAttributes.Literal) != 0;
                TypeRef fieldType = field.DecodeSignature(types, context);
                bool readOnly = (field.Attributes & FieldAttributes.InitOnly) != 0 || literal;
                members.Add(new ClrMember(type, name, MemberKind.Field, fieldType, isStatic, true, !readOnly, attributes: ReadAttributes(field.GetCustomAttributes())));
            }
            foreach (EventDefinitionHandle eventHandle in definition.GetEvents())
            {
                EventDefinition @event = reader.GetEventDefinition(eventHandle);
                EventAccessors accessors = @event.GetAccessors();
                if (accessors.Adder.IsNil)
                {
                    continue;
                }
                MethodDefinition adder = reader.GetMethodDefinition(accessors.Adder);
                if ((adder.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                {
                    continue;
                }
                members.Add(new ClrMember(type, reader.GetString(@event.Name), MemberKind.Event, types.Decode(@event.Type, context),
                    (adder.Attributes & MethodAttributes.Static) != 0, true, true));
            }
            TypeAttributes flags = definition.Attributes;
            bool isAbstract = (flags & TypeAttributes.Abstract) != 0;
            bool sealedType = (flags & TypeAttributes.Sealed) != 0;
            return new ClrTypeDetails(baseType, interfaces, ReadAttributes(definition.GetCustomAttributes()), members,
                isAbstract && !sealedType, isAbstract && sealedType, true, defaultConstructor || type.Kind == ClrTypeKind.Struct, generics);
        }
    }

    private IReadOnlyList<ClrAttribute> ReadAttributes(CustomAttributeHandleCollection handles)
    {
        List<ClrAttribute>? read = null;
        foreach (CustomAttributeHandle handle in handles)
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            string? typeName = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent is var parent && parent.Kind is HandleKind.TypeReference or HandleKind.TypeDefinition
                    ? HandleName(parent)
                    : null,
                HandleKind.MethodDefinition => DefinitionName(reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType()),
                _ => null,
            };
            if (typeName is null || !Wanted.Contains(typeName[(typeName.LastIndexOfAny(['.', '+']) + 1)..]))
            {
                continue;
            }
            try
            {
                CustomAttributeValue<TypeRef> value = attribute.DecodeValue(new AttributeTypes(this));
                (read ??= []).Add(new ClrAttribute(typeName, [.. value.FixedArguments.Select(a => Constant(a.Value))],
                    [.. value.NamedArguments.Select(a => new KeyValuePair<string, object?>(a.Name ?? string.Empty, Constant(a.Value)))]));
            }
            catch (Exception error) when (error is BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                // An attribute whose arguments name an enum of another assembly cannot be read without it: left unread.
            }
        }
        return read ?? (IReadOnlyList<ClrAttribute>)[];
    }

    private static object? Constant(object? value) => value switch
    {
        TypeRef type => type.Name,
        ImmutableArray<CustomAttributeTypedArgument<TypeRef>> array => array.Select(a => Constant(a.Value)).ToArray(),
        _ => value,
    };

    private sealed record GenericContext(IReadOnlyList<string> Type, IReadOnlyList<string> Method);

    private sealed class SignatureTypes(AssemblyTypes assembly) : ISignatureTypeProvider<TypeRef, GenericContext>
    {
        public TypeRef Decode(EntityHandle handle, GenericContext context) => handle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFromDefinition(assembly.reader, (TypeDefinitionHandle)handle, 0),
            HandleKind.TypeReference => GetTypeFromReference(assembly.reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => GetTypeFromSpecification(assembly.reader, context, (TypeSpecificationHandle)handle, 0),
            _ => TypeRef.Named("System.Object"),
        };

        public TypeRef GetArrayType(TypeRef elementType, ArrayShape shape) => elementType with { IsArray = true };

        public TypeRef GetByReferenceType(TypeRef elementType) => elementType;

        public TypeRef GetFunctionPointerType(MethodSignature<TypeRef> signature) => TypeRef.Named("System.IntPtr");

        public TypeRef GetGenericInstantiation(TypeRef genericType, ImmutableArray<TypeRef> typeArguments) => genericType with { Arguments = typeArguments };

        public TypeRef GetGenericMethodParameter(GenericContext genericContext, int index) =>
            new(index < genericContext.Method.Count ? genericContext.Method[index] : $"!!{index}", [], IsGenericParameter: true);

        public TypeRef GetGenericTypeParameter(GenericContext genericContext, int index) =>
            new(index < genericContext.Type.Count ? genericContext.Type[index] : $"!{index}", [], IsGenericParameter: true);

        public TypeRef GetModifiedType(TypeRef modifier, TypeRef unmodifiedType, bool isRequired) => unmodifiedType;

        public TypeRef GetPinnedType(TypeRef elementType) => elementType;

        public TypeRef GetPointerType(TypeRef elementType) => elementType;

        public TypeRef GetPrimitiveType(PrimitiveTypeCode typeCode) => TypeRef.Named(PrimitiveTypes.Of(typeCode).SystemName);

        public TypeRef GetSZArrayType(TypeRef elementType) => elementType with { IsArray = true };

        public TypeRef GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeRef.Named(assembly.DefinitionName(handle));

        public TypeRef GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeRef.Named(ReferenceName(reader, handle));

        public TypeRef GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    private sealed class AttributeTypes(AssemblyTypes assembly) : ICustomAttributeTypeProvider<TypeRef>
    {
        public TypeRef GetPrimitiveType(PrimitiveTypeCode typeCode) => TypeRef.Named(PrimitiveTypes.Of(typeCode).SystemName);

        public TypeRef GetSystemType() => TypeRef.Named("System.Type");

        public TypeRef GetSZArrayType(TypeRef elementType) => elementType with { IsArray = true };

        public TypeRef GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeRef.Named(assembly.DefinitionName(handle));

        public TypeRef GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeRef.Named(ReferenceName(reader, handle));

        public TypeRef GetTypeFromSerializedName(string name)
        {
            int comma = name.IndexOf(',');
            return TypeRef.Named(comma < 0 ? name : name[..comma].Trim());
        }

        // Enums of other assemblies cannot be looked into here; the attributes a dialect reads take int-sized ones.
        public PrimitiveTypeCode GetUnderlyingEnumType(TypeRef type) => PrimitiveTypeCode.Int32;

        public bool IsSystemType(TypeRef type) => type.Name == "System.Type";
    }
}
