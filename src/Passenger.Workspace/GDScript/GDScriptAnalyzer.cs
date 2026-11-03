namespace Passenger.Workspace.GDScript;

/// <summary>Where a name is declared: the script's res:// path and the offset its declaring name starts at; null path for
/// one of the engine's (its class and member then name it).</summary>
public sealed record GDScriptDeclaration(string? Script, int Start, string Name, GDScriptType Type, string? EngineClass = null);

/// <summary>A member a type has, as completion lists it: its name, what it is ("var", "const", "func", "signal", "enum",
/// "class") and its type (a function's what it returns), and where it is declared.</summary>
public sealed record GDScriptMemberInfo(string Name, string Kind, GDScriptType Type, GDScriptDeclaration? Declaration);

/// <summary>The types Godot's analyzer gives a GDScript (a port of the typing of modules/gdscript/gdscript_analyzer.cpp,
/// MIT): each member's and local's type (declared, inferred by <c>:=</c>, or by an untyped one's value, soft), each
/// expression's, a class's members through its <c>extends</c> across the project's scripts and the engine's classes, and
/// what each name is declared as. Godot's errors and warnings are its own server's; none here. Pure; a script's trees are
/// read once each.</summary>
public sealed partial class GDScriptAnalyzer
{
    private readonly GDScriptProject project;
    private readonly Dictionary<GDScriptNode, GDScriptType> memberTypes = [];
    private readonly HashSet<GDScriptNode> resolving = [];

    public GDScriptAnalyzer(GDScriptProject project) => this.project = project;

    private GodotEngineApi? Engine => project.Engine;

    /// <summary>A script's analysis: every expression's and declaration's type, every name's declaration.</summary>
    public GDScriptAnalysis Analyze(GDScriptTree tree, string path)
    {
        GDScriptAnalysis analysis = new(tree, path);
        analysed[path] = tree;
        GDScriptType self = ScriptClass(tree, path);
        foreach (GDScriptMember member in tree.Members)
        {
            WrittenVariants(member.Node, member.Offset, analysis);
        }
        PaintExtends(tree.Class, new Context(analysis, self, 0, null, null));
        foreach (GDScriptMember member in tree.Members)
        {
            Walk(analysis, member.Node, new Context(analysis, self, member.Offset, null, null));
        }
        ClassWarnings(tree.Members, analysis, Uses(analysis));
        if (tree.Members.Count == 0 && tree.Class.Extends.Count == 0 && tree.Class.ExtendsPath is null && tree.Class.Identifier is null && !tree.Class.IsTool
            && tree.Class.Annotations.Count == 0 && tree.Errors.Count == 0)
        {
            analysis.Found.Add(new GDScriptFinding(0, 0, "EMPTY_FILE", Message("EMPTY_FILE", [])));
        }
        return analysis;
    }

    /// <summary>The members (an inner class's too) whose type or return type is written as <c>Variant</c>, known before any
    /// use of them is read.</summary>
    private static void WrittenVariants(GDScriptNode member, int offset, GDScriptAnalysis analysis)
    {
        switch (member)
        {
            case GDScriptAssignable { Identifier: { } name, Type.Chain: [{ Name: "Variant" }] }:
                analysis.WrittenVariant.Add(offset + name.Start);
                break;
            case GDScriptFunction { Identifier: { } name, ReturnType.Chain: [{ Name: "Variant" }] }:
                analysis.WrittenVariant.Add(offset + name.Start);
                break;
            case GDScriptClass inner:
                foreach (GDScriptNode innerMember in inner.Members)
                {
                    WrittenVariants(innerMember, offset, analysis);
                }
                break;
            default:
                break;
        }
    }

    /// <summary>The class a script declares, by its <c>class_name</c> or else its path.</summary>
    public static GDScriptType ScriptClass(GDScriptTree tree, string path) =>
        new(GDScriptTypeKind.Class, tree.Class.Identifier?.Name ?? path) { Class = tree.Class, Script = path };

    /// <summary>What a function, signal or variable is in its class, with the context it was read in.</summary>
    private sealed record Context(GDScriptAnalysis Analysis, GDScriptType Class, int Offset, Scope? Scope, GDScriptFunction? Function);

    private sealed class Scope(Scope? parent)
    {
        private readonly Dictionary<string, (GDScriptType Type, GDScriptDeclaration Declaration, Local? Local)> names = new(StringComparer.Ordinal);

        public Scope? Parent { get; } = parent;

        /// <summary>The locals declared in it, in the order declared, which its end checks for use.</summary>
        public List<Local> Declared { get; } = [];

        public void Add(string name, GDScriptType type, GDScriptDeclaration declaration, Local? local = null)
        {
            names[name] = (type, declaration, local);
            if (local is not null)
            {
                Declared.Add(local);
            }
        }

        public (GDScriptType Type, GDScriptDeclaration Declaration, Local? Local)? Find(string name) =>
            names.TryGetValue(name, out (GDScriptType, GDScriptDeclaration, Local?) found) ? found : Parent?.Find(name);

        /// <summary>Whether a name is declared in it itself, not in a scope it is inside.</summary>
        public bool Holds(string name) => names.ContainsKey(name);
    }

    /// <summary>A local's uses and assignments as Godot counts them, for its warnings: what declares it (a variable, constant,
    /// parameter, a pattern's bind or a loop's variable) and its name.</summary>
    private sealed class Local(GDScriptNode node, GDScriptIdentifier name, LocalKind kind)
    {
        public GDScriptNode Node { get; } = node;

        public GDScriptIdentifier Name { get; } = name;

        public LocalKind Kind { get; } = kind;

        public int Usages { get; set; }

        public int Assignments { get; set; }
    }

    private enum LocalKind
    {
        Variable,
        Constant,
        Parameter,
        Bind,
        Iterator,
        Setter,
    }

    // Classes and their members.

    /// <summary>The members a class declares itself, each with the offset its positions count from.</summary>
    private IEnumerable<(GDScriptNode Node, int Offset)> OwnMembers(GDScriptType type)
    {
        if (type.Class is not { } node || type.Script is not { } path)
        {
            return [];
        }
        if (Tree(path) is { } tree && tree.Class == node)
        {
            return tree.Members.Select(m => (m.Node, m.Offset));
        }
        return node.Members.Select(m => (m, type.Offset));
    }

    private readonly Dictionary<string, GDScriptTree> analysed = new(StringComparer.Ordinal);

    /// <summary>A script's tree: the one being analysed where it is, else the project's.</summary>
    private GDScriptTree? Tree(string path) => analysed.TryGetValue(path, out GDScriptTree? tree) ? tree : project.Script(path);

    /// <summary>The class a class extends: a script's class, an engine class, or RefCounted where nothing is written.</summary>
    public GDScriptType? BaseOf(GDScriptType type)
    {
        if (type.Kind == GDScriptTypeKind.Native)
        {
            return Engine?.Classes.GetValueOrDefault(type.Name)?.Inherits is { } inherits ? GDScriptType.Native(inherits) : null;
        }
        if (type.Class is not { } node)
        {
            return null;
        }
        if (node.ExtendsPath is { } written)
        {
            string path = Absolute(written, type.Script);
            GDScriptType? based = Tree(path) is { } tree ? ScriptClass(tree, path) : null;
            foreach (GDScriptIdentifier inner in node.Extends)
            {
                based = based is null ? null : InnerClass(based, inner.Name);
            }
            return based ?? GDScriptType.Native("RefCounted");
        }
        if (node.Extends.Count == 0)
        {
            return GDScriptType.Native("RefCounted");
        }
        GDScriptType? found = ExtendedType(node.Extends[0].Name, type);
        foreach (GDScriptIdentifier inner in node.Extends.Skip(1))
        {
            found = found is null ? null : InnerClass(found, inner.Name);
        }
        return found?.Instance;
    }

    /// <summary>A name an <c>extends</c> writes first: an inner class of the script it is in (for an inner class), a
    /// <c>class_name</c>, an engine class; never the class's own members, which come from what it extends.</summary>
    private GDScriptType? ExtendedType(string name, GDScriptType type)
    {
        if (type.Script is { } path && Tree(path) is { } tree && type.Class != tree.Class && InnerClass(ScriptClass(tree, path), name) is { } sibling)
        {
            return sibling;
        }
        if (project.ClassPath(name) is { } global && Tree(global) is { } globalTree)
        {
            return ScriptClass(globalTree, global);
        }
        return Engine?.Classes.ContainsKey(name) == true || Engine is null ? GDScriptType.Native(name) : null;
    }

    /// <summary>A path an <c>extends</c> or <c>preload</c> writes, relative to the script's folder where not res://.</summary>
    public static string Absolute(string written, string? from)
    {
        if (written.StartsWith("res://", StringComparison.Ordinal) || from is null || written.StartsWith("uid://", StringComparison.Ordinal))
        {
            return written;
        }
        string folder = from[..(from.LastIndexOf('/') + 1)];
        List<string> parts = [.. folder["res://".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (string part in written.Split('/'))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part is not ("." or ""))
            {
                parts.Add(part);
            }
        }
        return "res://" + string.Join('/', parts);
    }

    private GDScriptType? InnerClass(GDScriptType owner, string name) => OwnNames(owner).TryGetValue(name, out (GDScriptNode Node, int Offset) named) ? named.Node switch
    {
        GDScriptClass { Identifier: not null } inner => new GDScriptType(GDScriptTypeKind.Class, name) { Class = inner, Script = owner.Script, Offset = named.Offset, Meta = true },
        GDScriptEnum { Identifier: not null } enumerated => EnumType(enumerated, owner, meta: true),
        _ => null,
    } : null;

    private static GDScriptType EnumType(GDScriptEnum node, GDScriptType owner, bool meta)
    {
        Dictionary<string, long> values = new(StringComparer.Ordinal);
        long next = 0;
        foreach ((GDScriptIdentifier name, GDScriptExpression? value) in node.Values)
        {
            next = value is GDScriptLiteral { Value: long given } ? given : next;
            values[name.Name] = next++;
        }
        return new GDScriptType(GDScriptTypeKind.Enum, node.Identifier?.Name ?? string.Empty)
        {
            Values = values, Owner = owner.Name, Script = owner.Script, Meta = meta, Class = owner.Class, Offset = owner.Offset,
        };
    }

    /// <summary>A member a type has by a name: its own, then its base classes' (script classes, then the engine's); null for none.</summary>
    public GDScriptMemberInfo? Member(GDScriptType type, string name)
    {
        int depth = 0;
        for (GDScriptType? at = type.Instance; at is not null && depth++ < 64; at = BaseOf(at))
        {
            if (at.Kind == GDScriptTypeKind.Native)
            {
                return NativeMember(at.Name, name, type.Meta);
            }
            if (at.Kind != GDScriptTypeKind.Class)
            {
                break;
            }
            if (OwnNames(at).TryGetValue(name, out (GDScriptNode Node, int Offset) named) && ScriptMember(named.Node, named.Offset, name, at) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Every member a type has, its own first, then its bases'.</summary>
    public IReadOnlyList<GDScriptMemberInfo> Members(GDScriptType type)
    {
        List<GDScriptMemberInfo> all = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (type.Kind == GDScriptTypeKind.Builtin && Engine?.Builtins.GetValueOrDefault(type.Name) is { } builtin)
        {
            foreach ((string name, string member) in type.Meta ? [] : builtin.Properties)
            {
                Add(new GDScriptMemberInfo(name, "var", FromApi(member), null));
            }
            foreach (GodotApiMethod method in builtin.Methods.Values.Where(m => !type.Meta || m.IsStatic))
            {
                Add(new GDScriptMemberInfo(method.Name, "func", FromApi(method.Returns), null));
            }
            foreach ((string name, string constant) in type.Meta ? builtin.Constants : [])
            {
                Add(new GDScriptMemberInfo(name, "const", FromApi(constant), null));
            }
            return all;
        }
        if (type.Kind == GDScriptTypeKind.Enum)
        {
            foreach (string value in type.Meta ? type.Values?.Keys ?? [] : [])
            {
                Add(new GDScriptMemberInfo(value, "const", type.Instance, null));
            }
            return all;
        }
        int depth = 0;
        for (GDScriptType? at = type.Instance; at is not null && depth++ < 64; at = BaseOf(at))
        {
            if (at.Kind == GDScriptTypeKind.Native)
            {
                foreach (GodotApiClass engine in Engine?.Chain(at.Name) ?? [])
                {
                    foreach (string name in engine.Properties.Keys.Concat(engine.Methods.Keys).Concat(engine.Signals.Keys).Concat(engine.Constants.Keys).Concat(engine.Enums.Keys))
                    {
                        if (!seen.Contains(name) && NativeMember(engine.Name, name, type.Meta) is { } found)
                        {
                            Add(found);
                        }
                    }
                }
                break;
            }
            foreach ((GDScriptNode node, int offset) in OwnMembers(at))
            {
                foreach (string name in Names(node))
                {
                    if (!seen.Contains(name) && ScriptMember(node, offset, name, at) is { } found)
                    {
                        Add(found);
                    }
                }
            }
        }
        return all;

        void Add(GDScriptMemberInfo member)
        {
            if (seen.Add(member.Name))
            {
                all.Add(member);
            }
        }
    }

    private static IEnumerable<string> Names(GDScriptNode node) => node switch
    {
        GDScriptVariable { Identifier: { } name } => [name.Name],
        GDScriptConstant { Identifier: { } name } => [name.Name],
        GDScriptFunction { Identifier: { } name } => [name.Name],
        GDScriptSignal { Identifier: { } name } => [name.Name],
        GDScriptClass { Identifier: { } name } => [name.Name],
        GDScriptEnum { Identifier: { } name } => [name.Name],
        GDScriptEnum unnamed => unnamed.Values.Select(v => v.Name.Name),
        _ => [],
    };

    private GDScriptMemberInfo? ScriptMember(GDScriptNode node, int offset, string name, GDScriptType owner)
    {
        GDScriptDeclaration At(GDScriptIdentifier identifier, GDScriptType type) => new(owner.Script, offset + identifier.Start, name, type);
        switch (node)
        {
            case GDScriptVariable { Identifier: { } identifier } variable when identifier.Name == name:
                GDScriptType variableType = MemberType(variable, owner, offset);
                return new GDScriptMemberInfo(name, "var", variableType, At(identifier, variableType));
            case GDScriptConstant { Identifier: { } identifier } constant when identifier.Name == name:
                GDScriptType constantType = MemberType(constant, owner, offset);
                return new GDScriptMemberInfo(name, "const", constantType, At(identifier, constantType));
            case GDScriptFunction { Identifier: { } identifier } function when identifier.Name == name:
                GDScriptType returns = ReturnType(function, owner, offset);
                return new GDScriptMemberInfo(name, "func", returns, At(identifier, returns));
            case GDScriptSignal { Identifier: { } identifier } when identifier.Name == name:
                return new GDScriptMemberInfo(name, "signal", GDScriptType.Builtin("Signal"), At(identifier, GDScriptType.Builtin("Signal")));
            case GDScriptClass { Identifier: { } identifier } inner when identifier.Name == name:
                GDScriptType innerType = new(GDScriptTypeKind.Class, name) { Class = inner, Script = owner.Script, Offset = offset, Meta = true };
                return new GDScriptMemberInfo(name, "class", innerType, At(identifier, innerType));
            case GDScriptEnum { Identifier: { } identifier } named when identifier.Name == name:
                GDScriptType enumType = EnumType(named, owner, meta: true);
                return new GDScriptMemberInfo(name, "enum", enumType, At(identifier, enumType));
            case GDScriptEnum { Identifier: null } unnamed:
                foreach ((GDScriptIdentifier value, _) in unnamed.Values)
                {
                    if (value.Name == name)
                    {
                        return new GDScriptMemberInfo(name, "const", GDScriptType.Builtin("int"), At(value, GDScriptType.Builtin("int")));
                    }
                }
                return null;
            default:
                return null;
        }
    }

    private GDScriptMemberInfo? NativeMember(string className, string name, bool meta)
    {
        foreach (GodotApiClass engine in Engine?.Chain(className) ?? [])
        {
            GDScriptDeclaration Of(GDScriptType type) => new(null, 0, name, type, engine.Name);
            if (!meta && engine.Properties.TryGetValue(name, out string? property))
            {
                GDScriptType type = FromApi(property);
                return new GDScriptMemberInfo(name, "var", type, Of(type));
            }
            if (engine.Methods.TryGetValue(name, out GodotApiMethod? method) && (!meta || method.IsStatic))
            {
                GDScriptType type = FromApi(method.Returns);
                return new GDScriptMemberInfo(name, "func", type, Of(type));
            }
            if (!meta && engine.Signals.ContainsKey(name))
            {
                return new GDScriptMemberInfo(name, "signal", GDScriptType.Builtin("Signal"), Of(GDScriptType.Builtin("Signal")));
            }
            if (engine.Enums.TryGetValue(name, out GodotApiEnum? named))
            {
                GDScriptType type = NativeEnum(engine.Name, named, meta: true);
                return new GDScriptMemberInfo(name, "enum", type, Of(type));
            }
            foreach (GodotApiEnum owning in engine.Enums.Values)
            {
                if (owning.Values.ContainsKey(name))
                {
                    GDScriptType type = NativeEnum(engine.Name, owning, meta: false);
                    return new GDScriptMemberInfo(name, "const", type, Of(type));
                }
            }
            if (engine.Constants.ContainsKey(name))
            {
                return new GDScriptMemberInfo(name, "const", GDScriptType.Builtin("int"), Of(GDScriptType.Builtin("int")));
            }
        }
        return null;
    }

    private static GDScriptType NativeEnum(string owner, GodotApiEnum named, bool meta) =>
        new(GDScriptTypeKind.Enum, named.Name) { Owner = owner, Values = named.Values, Meta = meta };

    /// <summary>A type as the engine's API writes one: <c>int</c>, <c>Node</c>, <c>enum::Node.ProcessMode</c>,
    /// <c>bitfield::…</c> (an int), <c>typedarray::Node</c>, <c>typeddictionary::String;int</c>, several classes (their nearest
    /// common class), <c>void</c>, <c>Variant</c>.</summary>
    public GDScriptType FromApi(string written)
    {
        if (written is "Variant" or "")
        {
            return GDScriptType.Variant;
        }
        if (written == "void")
        {
            return GDScriptType.Builtin("Nil") with { IsVoid = true };
        }
        if (written.StartsWith("bitfield::", StringComparison.Ordinal))
        {
            return GDScriptType.Builtin("int");
        }
        if (written.StartsWith("enum::", StringComparison.Ordinal))
        {
            string name = written["enum::".Length..];
            int dot = name.LastIndexOf('.');
            string? owner = dot < 0 ? null : name[..dot];
            string enumName = name[(dot + 1)..];
            GodotApiEnum? found = owner is null ? Engine?.GlobalEnums.GetValueOrDefault(enumName)
                : owner.Contains('.', StringComparison.Ordinal) ? null : Engine?.Classes.GetValueOrDefault(owner)?.Enums.GetValueOrDefault(enumName)
                    ?? Engine?.Builtins.GetValueOrDefault(owner)?.Enums.GetValueOrDefault(enumName);
            return new GDScriptType(GDScriptTypeKind.Enum, enumName) { Owner = owner, Values = found?.Values };
        }
        if (written.StartsWith("typedarray::", StringComparison.Ordinal))
        {
            string element = written["typedarray::".Length..];
            return GDScriptType.Builtin("Array") with { Elements = [FromApi(element[(element.LastIndexOf(':') + 1)..])] };
        }
        if (written.StartsWith("typeddictionary::", StringComparison.Ordinal))
        {
            string[] pair = written["typeddictionary::".Length..].Split(';');
            return GDScriptType.Builtin("Dictionary") with { Elements = [.. pair.Select(p => FromApi(p[(p.LastIndexOf(':') + 1)..]))] };
        }
        if (written.Contains(',', StringComparison.Ordinal))
        {
            string[] classes = written.Split(',');
            string? common = Engine?.Chain(classes[0]).Select(c => c.Name).FirstOrDefault(c => classes.All(other => Engine.Extends(other, c)));
            return GDScriptType.Native(common ?? "Object");
        }
        if (Engine?.Builtins.ContainsKey(written) == true || IsBuiltinName(written))
        {
            return GDScriptType.Builtin(written);
        }
        return GDScriptType.Native(written);
    }

    private bool IsBuiltinName(string name) => Engine is null && name is "bool" or "int" or "float" or "String" or "StringName" or "NodePath" or "Array"
        or "Dictionary" or "Vector2" or "Vector2i" or "Vector3" or "Vector3i" or "Vector4" or "Vector4i" or "Color" or "Rect2" or "Rect2i" or "Transform2D"
        or "Transform3D" or "Basis" or "Quaternion" or "Plane" or "AABB" or "Projection" or "RID" or "Callable" or "Signal" or "PackedByteArray"
        or "PackedInt32Array" or "PackedInt64Array" or "PackedFloat32Array" or "PackedFloat64Array" or "PackedStringArray" or "PackedVector2Array"
        or "PackedVector3Array" or "PackedColorArray" or "PackedVector4Array";

    /// <summary>A name a type written may begin with, in a class's scope: one of its own or its outer classes' inner classes
    /// and enums, a <c>class_name</c>, a built-in type, an engine class; null for none.</summary>
    private GDScriptType? GlobalOrScopedType(string name, GDScriptType scope)
    {
        if (name == "Variant")
        {
            return GDScriptType.Variant;
        }
        int depth = 0;
        for (GDScriptType? at = scope.Instance; at is { Kind: GDScriptTypeKind.Class } && depth++ < 64; at = BaseOf(at))
        {
            if (InnerClass(at, name) is { } inner)
            {
                return inner;
            }
        }
        if (scope.Script is { } path && Tree(path) is { } tree && scope.Class != tree.Class && InnerClass(ScriptClass(tree, path), name) is { } outer)
        {
            return outer;
        }
        if (project.ClassPath(name) is { } global && Tree(global) is { } globalTree)
        {
            return ScriptClass(globalTree, global) with { Meta = true };
        }
        if (Engine?.Builtins.ContainsKey(name) == true && name != "Nil" || IsBuiltinName(name))
        {
            return GDScriptType.Builtin(name) with { Meta = true };
        }
        if (Engine?.Classes.ContainsKey(name) == true)
        {
            return GDScriptType.Native(name) with { Meta = true };
        }
        if (Engine?.GlobalEnums.GetValueOrDefault(name) is { } global2)
        {
            return new GDScriptType(GDScriptTypeKind.Enum, name) { Values = global2.Values, Meta = true };
        }
        if (project.Autoload(name) is { } autoload && Tree(autoload) is { } autoloaded)
        {
            return ScriptClass(autoloaded, autoload) with { Meta = true };
        }
        return null;
    }

    /// <summary>The type a type specifier names (<c>int</c>, <c>Array[Node]</c>, <c>Inner.State</c>, <c>Node.ProcessMode</c>),
    /// as a value of it; Variant for one not found.</summary>
    private GDScriptType ResolveType(GDScriptTypeNode node, Context context)
    {
        if (node.IsVoid)
        {
            return GDScriptType.Builtin("Nil") with { IsVoid = true };
        }
        if (node.Chain.Count == 0)
        {
            return GDScriptType.Variant;
        }
        if (node.Chain is [{ Name: "Variant" }, { Name: var variantEnum }] && Engine?.GlobalEnums.GetValueOrDefault($"Variant.{variantEnum}") is { } variantType)
        {
            return new GDScriptType(GDScriptTypeKind.Enum, variantType.Name) { Values = variantType.Values };
        }
        GDScriptType? found = ConstantType(node.Chain[0].Name, context) ?? GlobalOrScopedType(node.Chain[0].Name, context.Class);
        foreach (GDScriptIdentifier next in node.Chain.Skip(1))
        {
            found = found switch
            {
                { Kind: GDScriptTypeKind.Class } owner => InnerClass(owner.Instance, next.Name),
                { Kind: GDScriptTypeKind.Native } engine => Engine?.Chain(engine.Name).Select(c => c.Enums.GetValueOrDefault(next.Name) is { } e ? NativeEnum(c.Name, e, meta: true) : null)
                    .FirstOrDefault(e => e is not null),
                { Kind: GDScriptTypeKind.Builtin } builtin => Engine?.Builtins.GetValueOrDefault(builtin.Name)?.Enums.GetValueOrDefault(next.Name) is { } e
                    ? NativeEnum(builtin.Name, e, meta: true) : null,
                _ => null,
            };
        }
        if (found is null)
        {
            // A type this reading cannot find is no Variant Godot would say: what it holds is unknown.
            return GDScriptType.Variant.Soft;
        }
        GDScriptType type = found.Instance with { Hard = true };
        PaintType(node, found, context);
        return node.Elements.Count > 0 ? type with { Elements = [.. node.Elements.Select(e => ResolveType(e, context))] } : type;
    }

    /// <summary>A constant of the class's scope that stands for a type (<c>const Enemy = preload("enemy.gd")</c>); null for none.</summary>
    private GDScriptType? ConstantType(string name, Context context) =>
        Member(context.Class, name) is { Kind: "const", Type: { Meta: true } meta } ? meta : null;

    private GDScriptType MemberType(GDScriptAssignable node, GDScriptType owner, int offset)
    {
        if (memberTypes.TryGetValue(node, out GDScriptType? kept))
        {
            return kept;
        }
        if (!resolving.Add(node))
        {
            return GDScriptType.Variant;
        }
        Context context = new(new GDScriptAnalysis(null, owner.Script ?? string.Empty), owner.Instance, offset, null, null);
        GDScriptType type = AssignableType(node, context, constant: node is GDScriptConstant);
        resolving.Remove(node);
        memberTypes[node] = type;
        return type;
    }

    /// <summary>What a function returns as Godot's resolve_function_signature gives it: declared; a constructor its class;
    /// an override of a method written without one, what the method it overrides returns; else Variant.</summary>
    private GDScriptType ReturnType(GDScriptFunction function, GDScriptType owner, int offset)
    {
        if (function.Identifier?.Name == "_init" && !function.IsLambda)
        {
            return owner.Instance with { Hard = true };
        }
        if (function.Identifier?.Name == "_static_init" && !function.IsLambda)
        {
            return GDScriptType.Variant.Soft;
        }
        if (function.ReturnType is { } declared)
        {
            return ResolveType(declared, new Context(new GDScriptAnalysis(null, owner.Script ?? string.Empty), owner.Instance, offset, null, function));
        }
        if (!function.IsLambda && function.Identifier is { } name && !resolving.Contains(function) && BaseOf(owner.Instance) is { } based)
        {
            resolving.Add(function);
            GDScriptMemberInfo? overridden = Member(based, name.Name);
            resolving.Remove(function);
            if (overridden is { Kind: "func" })
            {
                return overridden.Type;
            }
        }
        return GDScriptType.Variant.Soft;
    }

    /// <summary>A variable's, constant's or parameter's type as Godot's resolve_assignable gives it: declared, else its
    /// value's (hard where written <c>:=</c> or a constant, else only inferred), never null's.</summary>
    private GDScriptType AssignableType(GDScriptAssignable node, Context context, bool constant)
    {
        if (node.Type is { } declared)
        {
            if (node.Initializer is { } value)
            {
                Reduce(value, context);
            }
            return ResolveType(declared, context);
        }
        if (node.Initializer is not { } initializer)
        {
            return GDScriptType.Variant.Soft;
        }
        GDScriptType reduced = Reduce(initializer, context);
        if (reduced is { Kind: GDScriptTypeKind.Builtin, Name: "Nil" } && !constant || !reduced.Hard && (node.Infers || constant))
        {
            reduced = GDScriptType.Variant;
        }
        return node.Infers || constant ? reduced with { Hard = !reduced.IsVariant || reduced.Hard } : reduced.Soft;
    }
}
