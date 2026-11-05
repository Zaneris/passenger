namespace Passenger.Workspace.GDScript;

/// <summary>Where a name is declared: the script's res:// path and the offset its declaring name starts at; null path for
/// one of the engine's (its class and member then name it).</summary>
public sealed record GDScriptDeclaration(string? Script, int Start, string Name, GDScriptType Type, string? EngineClass = null)
{
    /// <summary>The script's member declared, whichever script that is, what kind it is telling how it reads; null for a local
    /// or an engine's.</summary>
    public GDScriptNode? Member { get; init; }
}

/// <summary>A member a type has, as completion lists it: its name, what it is ("var", "const", "func", "signal", "enum",
/// "class") and its type (a function's what it returns), and where it is declared.</summary>
public sealed record GDScriptMemberInfo(string Name, string Kind, GDScriptType Type, GDScriptDeclaration? Declaration);

/// <summary>The types Godot's analyzer gives a GDScript (a port of the typing of modules/gdscript/gdscript_analyzer.cpp,
/// MIT): each member's and local's type (declared, inferred by <c>:=</c>, or by an untyped one's value, soft), each
/// expression's, a class's members through its <c>extends</c> across the project's scripts and the engine's classes, and
/// what each name is declared as, with the warnings and errors its partial classes push where Godot's analyzer pushes them.
/// Pure; a script's trees are read once each.</summary>
public sealed partial class GDScriptAnalyzer
{
    private readonly GDScriptProject project;
    private readonly Dictionary<GDScriptNode, GDScriptType> memberTypes = [];
    private readonly HashSet<GDScriptNode> resolving = [];
    private readonly Dictionary<GDScriptEnum, (Dictionary<string, long> Values, bool Partial)> enumValues = [];

    /// <summary>Each inline setter's parameter's property, whose type it is given.</summary>
    private readonly Dictionary<GDScriptParameter, GDScriptVariable> setterProperties = [];

    public GDScriptAnalyzer(GDScriptProject project) => this.project = project;

    /// <summary>What the scripts are read against: the project's scripts, class names, autoloads and the engine's API.</summary>
    public GDScriptProject Project => project;

    /// <summary>The engine's API the types are resolved against; null where none was dumped.</summary>
    public GodotEngineApi? Engine => project.Engine;

    /// <summary>A script's analysis: every expression's and declaration's type, every name's declaration.</summary>
    public GDScriptAnalysis Analyze(GDScriptTree tree, string path)
    {
        GDScriptAnalysis analysis = new(tree, path) { Engine = Engine };
        analysed[path] = tree;
        GDScriptType self = ScriptClass(tree, path);
        List<GDScriptType> lineage = [];
        for (GDScriptType? at = self; at is not null && lineage.Count < 64; at = BaseOf(at))
        {
            lineage.Add(at);
        }
        analysis.Lineage = lineage;
        foreach (GDScriptMember member in tree.Members)
        {
            WrittenVariants(member.Node, member.Offset, analysis);
        }
        PaintExtends(tree.Class, new Context(analysis, self, 0, null, null));
        ExtendsDeclared(tree.Class, new Context(analysis, self, 0, null, null));
        DeclaresItself(tree.Class.Identifier, self with { Meta = true }, new Context(analysis, self, 0, null, null));
        MissingTool(tree.Class, tree, path, 0, new Context(analysis, self, 0, null, null));
        foreach (GDScriptMember member in tree.Members)
        {
            Walk(analysis, member.Node, new Context(analysis, self, member.Offset, null, null));
        }
        ClassWarnings(tree.Members, analysis, Uses(analysis));
        ClassLevelStrings(tree, new Context(analysis, self, 0, null, null));
        if (tree.Members.Count == 0 && tree.Class.Extends.Count == 0 && tree.Class.ExtendsPath is null && tree.Class.Identifier is null && !tree.Class.IsTool
            && tree.Class.Annotations.Count == 0 && tree.Errors.Count == 0)
        {
            analysis.Found.Add(new GDScriptFinding(0, 0, "EMPTY_FILE", Message("EMPTY_FILE", [])));
        }
        if (tree.Errors.Count == 0)
        {
            CycleErrors(tree, path, self, analysis);
            PropertyErrors(tree.Members.Select(m => (m.Node, m.Offset)), new Context(analysis, self, 0, null, null));
            foreach (GDScriptType inner in InnerClasses(tree.Members.Select(m => (m.Node, m.Offset)), path))
            {
                PropertyErrors(inner.Class!.Members.Select(m => (m, inner.Offset)), new Context(analysis, inner, inner.Offset, null, null));
            }
        }
        ConfusableWarnings(tree, analysis, self);
        if (!InheritanceErrors(tree, path, self, analysis))
        {
            StaticUnloadWarning(tree, analysis, self);
        }
        return analysis;
    }

    /// <summary>The errors Godot gives as it reads a script's classes' names and bases, before any body: a name taken, and a
    /// base whose file the project lacks. Where a base can't be followed (one lacked, an inner class's this reading can't),
    /// Godot reads no body, so these errors alone stand, and whether that is so; where the script's own base is lacked, its
    /// inner classes are not read.</summary>
    private bool InheritanceErrors(GDScriptTree tree, string path, GDScriptType self, GDScriptAnalysis analysis)
    {
        int first = analysis.Found.Count;
        Context own = new(analysis, self, 0, null, null);
        ClassNameErrors(tree.Class, inner: false, own);
        bool stops = BaseLacked(tree.Class, path, own);
        foreach (GDScriptType inner in stops ? [] : InnerClasses(tree.Members.Select(m => (m.Node, m.Offset)), path))
        {
            Context within = new(analysis, inner, inner.Offset, null, null);
            ClassNameErrors(inner.Class!, inner: true, within);
            stops |= BaseLacked(inner.Class!, path, within) || !Knows(inner);
        }
        stops |= ExtendsErrors(tree, path, self, analysis);
        if (stops)
        {
            List<GDScriptFinding> read = analysis.Found.GetRange(first, analysis.Found.Count - first);
            analysis.Found.RemoveAll(f => f.Code is null);
            analysis.Found.AddRange(read);
        }
        return stops;
    }

    /// <summary>Whether a class extends by path a file the project lacks, or from Godot 4.8 one that is no GDScript, saying
    /// so in Godot's words.</summary>
    private bool BaseLacked(GDScriptClass node, string path, Context context)
    {
        if (node.ExtendsPath is { } script && Engine?.Version >= new Version(4, 8) && !script.StartsWith("uid://", StringComparison.Ordinal)
            && !script.EndsWith(".gd", StringComparison.OrdinalIgnoreCase))
        {
            Error(context, node, $"\"{Absolute(script, path)}\" is not a GDScript.");
            return true;
        }
        if (node.ExtendsPath is not { } written || Absolute(written, path) is not { } absolute || !Lacked(absolute))
        {
            return false;
        }
        Error(context, node, $"Could not resolve super class path \"{absolute}\".");
        return true;
    }

    /// <summary>Whether a res:// path written plainly (no uid, no <c>.</c> or <c>..</c> to resolve) names a file the project lacks.</summary>
    private bool Lacked(string absolute) =>
        absolute.StartsWith("res://", StringComparison.Ordinal) && !absolute["res://".Length..].Split('/').Any(p => p is "" or "." or "..") && project.Lacks(absolute);

    /// <summary>The classes a script declares inside its own, an inner class's too, each by its offset.</summary>
    private static IEnumerable<GDScriptType> InnerClasses(IEnumerable<(GDScriptNode Node, int Offset)> members, string path)
    {
        foreach ((GDScriptNode node, int offset) in members)
        {
            if (node is GDScriptClass { Identifier: { } name } inner)
            {
                yield return new GDScriptType(GDScriptTypeKind.Class, name.Name) { Class = inner, Script = path, Offset = offset };
                foreach (GDScriptType nested in InnerClasses(inner.Members.Select(m => (m, offset)), path))
                {
                    yield return nested;
                }
            }
        }
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
    private sealed record Context(GDScriptAnalysis Analysis, GDScriptType Class, int Offset, Scope? Scope, GDScriptFunction? Function)
    {
        /// <summary>What a return in the function must give, where its type is written: its declared return type, a
        /// getter's its property's; null for none.</summary>
        public GDScriptType? Returns { get; init; }

        /// <summary>Where the walk is static, as Godot names it: the static function it is in above any lambda (a static
        /// variable's setter or getter by Godot's name for it), empty in a static variable's value; null where it is not.</summary>
        public string? Static { get; init; }

        /// <summary>Whether a return in the function may give no value: its return type written void, or an override of a
        /// void function with none written.</summary>
        public bool Voids { get; init; }
    }

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

        /// <summary>Whether it holds a function's parameters, the outermost scope of its own.</summary>
        public bool Function { get; init; }

        /// <summary>A local of an enclosing function the scope's own function reads by name, as a lambda captures it.</summary>
        public (GDScriptType Type, GDScriptDeclaration Declaration, Local? Local)? Captured(string name)
        {
            bool crossed = false;
            for (Scope? at = this; at is not null; at = at.Parent)
            {
                if (at.names.TryGetValue(name, out (GDScriptType, GDScriptDeclaration, Local?) found))
                {
                    return crossed && found.Item3 is not null ? found : null;
                }
                crossed |= at.Function;
            }
            return null;
        }

        /// <summary>A suite's own variables and constants, wherever in it each is declared, by name with its kind.</summary>
        public IReadOnlyDictionary<string, string>? Suite { get; init; }

        /// <summary>What a name is declared as anywhere in this suite or one it is inside, as Godot's has_local reads a suite
        /// once its parser is done with it; null where none declares it.</summary>
        public string? InSuites(string name) => Suite?.GetValueOrDefault(name) ?? Parent?.InSuites(name);
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
    internal IEnumerable<(GDScriptNode Node, int Offset)> OwnMembers(GDScriptType type)
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

    /// <summary>The project's trees as first read, so a class read twice is one class whatever the project gives each time.</summary>
    private readonly Dictionary<string, GDScriptTree?> read = new(StringComparer.Ordinal);

    /// <summary>A script's tree: the one being analysed where it is, else the project's as first read.</summary>
    private GDScriptTree? Tree(string path)
    {
        if (analysed.TryGetValue(path, out GDScriptTree? tree))
        {
            return tree;
        }
        if (!read.TryGetValue(path, out tree))
        {
            read[path] = tree = project.Script(path);
        }
        return tree;
    }

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

    /// <summary>A name an <c>extends</c> writes first: for an inner class, an inner class of a class it is written in or
    /// of one they extend; a <c>class_name</c>; an engine class; never the class's own members, which come from what it extends.</summary>
    private GDScriptType? ExtendedType(string name, GDScriptType type)
    {
        if (type.Script is { } path && Tree(path) is { } tree && type.Class is { } inner && inner != tree.Class)
        {
            // An inner class's base is found among the classes it is written in and the script classes they extend.
            List<GDScriptType> enclosing = Enclosing(ScriptClass(tree, path), tree.Members.Select(m => (m.Node, m.Offset)), inner);
            for (int i = enclosing.Count - 1; i >= 0; i--)
            {
                int depth = 0;
                for (GDScriptType? at = enclosing[i]; at is { Kind: GDScriptTypeKind.Class } && depth++ < 64; at = BaseOf(at))
                {
                    if (InnerClass(at, name) is { Kind: GDScriptTypeKind.Class } sibling && sibling.Class != inner)
                    {
                        return sibling;
                    }
                }
            }
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

    /// <summary>Godot's name for an enum written without one, whose values are of it.</summary>
    private const string AnonymousEnum = "<anonymous enum>";

    private GDScriptType EnumType(GDScriptEnum node, GDScriptType owner, bool meta)
    {
        (Dictionary<string, long> values, bool partial) = EnumValues(node, owner);
        return new GDScriptType(GDScriptTypeKind.Enum, node.Identifier?.Name ?? AnonymousEnum)
        {
            Values = values, ValuesPartial = partial, Owner = Fqcn(owner), Script = owner.Script, Meta = meta, Class = owner.Class, Offset = owner.Offset,
        };
    }

    /// <summary>An enum's values as Godot gives them, each what it writes (<see cref="IntegerValue"/>) else one past the last,
    /// and whether one is only partly known: written as what only Godot can fold, or reaching its own enum.</summary>
    private (Dictionary<string, long> Values, bool Partial) EnumValues(GDScriptEnum node, GDScriptType owner)
    {
        if (enumValues.TryGetValue(node, out (Dictionary<string, long> Values, bool Partial) kept))
        {
            return kept;
        }
        bool entered = resolving.Add(node);
        Dictionary<string, long> values = new(StringComparer.Ordinal);
        long next = 0;
        bool partial = !entered;
        foreach ((GDScriptIdentifier name, GDScriptExpression? value) in node.Values)
        {
            long? written = value is null ? next : entered ? IntegerValue(value, owner, values) : null;
            partial |= written is null;
            next = written ?? next;
            values[name.Name] = next++;
        }
        if (entered)
        {
            resolving.Remove(node);
            enumValues[node] = (values, partial);
        }
        return (values, partial);
    }

    /// <summary>A class's fully qualified name as Godot's parser gives it (its fqcn): its script's path, then each class it
    /// is written in and its own after <c>::</c>, which an enum of it is named by.</summary>
    private string Fqcn(GDScriptType owner)
    {
        if (owner.Script is not { } path || Tree(path) is not { } tree || owner.Class is not { } node || node == tree.Class)
        {
            return owner.Script ?? owner.Name;
        }
        List<GDScriptType> enclosing = Enclosing(ScriptClass(tree, path), tree.Members.Select(m => (m.Node, m.Offset)), node);
        return string.Concat([path, .. enclosing.Skip(1).Select(c => "::" + c.Name), "::", owner.Name]);
    }

    /// <summary>The type a class's member writes, read in the class's scope.</summary>
    internal GDScriptType TypeWritten(GDScriptTypeNode written, GDScriptType owner, int offset) =>
        ResolveType(written, new Context(new GDScriptAnalysis(null, owner.Script ?? string.Empty), owner.Instance, offset, null, null));

    /// <summary>The enum a name stands for in a class's scope, its own, one it inherits or an outer class's; null for none.</summary>
    internal GDScriptType? EnumNamed(string name, GDScriptType scope) =>
        ScopeClasses(scope).Select(c => OwnEnum(c, name)).FirstOrDefault(e => e is not null) ?? (Member(scope, name) is { Kind: "enum" } inherited ? inherited.Type : null);

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
        GDScriptDeclaration At(GDScriptIdentifier identifier, GDScriptType type) => new(owner.Script, offset + identifier.Start, name, type) { Member = node };
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
                        GDScriptType anonymous = EnumType(unnamed, owner, meta: false) with { Hard = true };
                        return new GDScriptMemberInfo(name, "const", anonymous, At(value, anonymous));
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

    /// <summary>A name a type written may begin with, in a class's scope: a class of the scope by its own name (a script's
    /// <c>class_name</c> whether or not the project lists it), one of its own or its outer classes' inner classes and enums,
    /// a <c>class_name</c>, a built-in type, an engine class; null for none.</summary>
    private GDScriptType? GlobalOrScopedType(string name, GDScriptType scope)
    {
        if (name == "Variant")
        {
            return GDScriptType.Variant;
        }
        foreach (GDScriptType at in ScopeClasses(scope))
        {
            if (at.Class?.Identifier?.Name == name)
            {
                return at with { Meta = true };
            }
            if (InnerClass(at, name) is { } inner)
            {
                return inner;
            }
            if (ScopedConstantType(at, name) is { } constant)
            {
                return constant;
            }
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
        if (project.Autoload(name) is { } autoload && project.Resolved(autoload) is { } loaded && Tree(loaded) is { } autoloaded)
        {
            return ScriptClass(autoloaded, loaded) with { Meta = true };
        }
        return null;
    }

    /// <summary>The type a type specifier names (<c>int</c>, <c>Array[Node]</c>, <c>Inner.State</c>, <c>Node.ProcessMode</c>),
    /// as a value of it; Variant for one not found.</summary>
    /// <summary>A name in a type written or a base that names a script's class by that class's own name, recorded as
    /// declared where the class is, so Go to Declaration, Find Usages and Rename take it as they take a use in code.</summary>
    private static void NamesClass(GDScriptIdentifier name, GDScriptType? type, Context context)
    {
        if (context.Analysis.Tree is not null && type is { Kind: GDScriptTypeKind.Class, Script: { } script, Class.Identifier: { } own } && own.Name == name.Name)
        {
            context.Analysis.Declared[name] = new GDScriptDeclaration(script, type.Offset + own.Start, name.Name, type with { Meta = true });
        }
    }

    /// <summary>A script's enum named as a type (<c>var m: Mood</c>, <c>Helper.Mood</c>) recorded as declared where the enum is.</summary>
    private void NamesEnum(GDScriptIdentifier name, GDScriptType? type, Context context)
    {
        if (context.Analysis.Tree is null || type is not { Kind: GDScriptTypeKind.Enum, Script: { } script, Class: { } holder } || type.Name != name.Name)
        {
            return;
        }
        GDScriptType at = new(GDScriptTypeKind.Class, holder.Identifier?.Name ?? script) { Class = holder, Script = script, Offset = type.Offset };
        if (OwnNames(at).TryGetValue(name.Name, out (GDScriptNode Node, int Offset) declared) && declared.Node is GDScriptEnum { Identifier: { } own })
        {
            context.Analysis.Declared[name] = new GDScriptDeclaration(script, declared.Offset + own.Start, name.Name, type with { Meta = true });
        }
    }

    /// <summary>A class's base written by name (<c>extends Inner</c>, <c>extends Outer.Inner</c>), each name of a script's
    /// class recorded as declared where the class is.</summary>
    private void ExtendsDeclared(GDScriptClass @class, Context context)
    {
        GDScriptType? at = null;
        for (int i = 0; i < @class.Extends.Count; i++)
        {
            at = i == 0 ? GlobalOrScopedType(@class.Extends[i].Name, context.Class) : at is { Kind: GDScriptTypeKind.Class } ? InnerClass(at.Instance, @class.Extends[i].Name) : null;
            NamesClass(@class.Extends[i], at, context);
        }
    }

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
        GDScriptIdentifier first = node.Chain[0];
        bool tells = context.Analysis.Tree is not null && Knows(context.Class);
        GDScriptType? localType = null;
        if (context.Scope?.Find(first.Name) is { Local: { } local } scoped)
        {
            // A local constant may name a type; any other local never does.
            if (local.Kind != LocalKind.Constant)
            {
                return Unfound(tells, first, $"Local {LocalWords(local.Kind)} \"{first.Name}\" cannot be used as a type.");
            }
            // A constant written as Variant holding a class is still that class (Godot reads its value).
            GDScriptType? held = scoped.Type.Meta ? scoped.Type
                : local.Node is GDScriptConstant { Initializer: { } value } && context.Analysis.Types.GetValueOrDefault(value) is { Meta: true } valued ? valued : null;
            if (held is null)
            {
                return Unfound(tells, first, $"Local constant \"{first.Name}\" is not a valid type.");
            }
            localType = held;
        }
        else if (context.Scope?.InSuites(first.Name) is { } later)
        {
            // Declared further on in its block: Godot's suite holds it already, its type not yet read.
            return Unfound(tells, first, later == "constant" ? $"Local constant \"{first.Name}\" is not resolved at this point."
                : $"Local {later} \"{first.Name}\" cannot be used as a type.");
        }
        if (first.Name == "Variant" && node.Chain.Count > 1)
        {
            return node.Chain.Count == 2 ? Unfound(tells, node.Chain[1], $"Name \"{node.Chain[1].Name}\" is not a nested type of \"Variant\".")
                : Unfound(tells, node.Chain[2], "Variant only contains enum types, which do not have nested types.");
        }
        GDScriptType? found = localType ?? ConstantType(first.Name, context) ?? GlobalOrScopedType(first.Name, context.Class);
        // An enum or inner class of a class it extends (an engine class's Node.ProcessMode, written ProcessMode) is a type too.
        if (found is null && Member(context.Class, first.Name) is { Kind: "enum" or "class", Type.Meta: true } inherited)
        {
            found = inherited.Type;
        }
        NamesClass(first, found, context);
        NamesEnum(first, found, context);
        if (found is null && project.Autoload(first.Name) is { } autoload && Tree(project.Resolved(autoload)) is null)
        {
            // An autoload of a scene: what Godot types it as comes from the scene's instance, which this reading has not.
            return GDScriptType.Variant.Soft;
        }
        if (found is null && Member(context.Class, first.Name) is { } notType)
        {
            return Unfound(tells, node, Engine?.Version >= new Version(4, 8) ? $"\"{first.Name}\" is a {MemberWords(notType)}, so it can't be used as a type."
                : $"\"{first.Name}\" is a {MemberWords(notType)} but does not contain a type.");
        }
        if (found is null)
        {
            // A type this reading cannot find is no Variant Godot would say: what it holds is unknown.
            return Unfound(tells, node, $"Could not find type \"{first.Name}\" in the current scope.");
        }
        for (int i = 1; i < node.Chain.Count; i++)
        {
            GDScriptIdentifier next = node.Chain[i];
            GDScriptType owner = found!;
            switch (owner.Kind)
            {
                case GDScriptTypeKind.Class:
                    found = InnerClass(owner.Instance, next.Name) ?? (Member(owner.Instance, next.Name) is { Kind: "const", Type: { Meta: true } preloaded } ? preloaded : null);
                    if (found is null)
                    {
                        return Unfound(tells, node.Chain[1], Member(owner.Instance, next.Name) is not null
                            ? $"Member \"{next.Name}\" under base \"{owner}\" is not a valid type." : $"Could not find type \"{next.Name}\" under base \"{owner}\".");
                    }
                    NamesClass(next, found, context);
                    NamesEnum(next, found, context);
                    break;
                case GDScriptTypeKind.Native:
                    found = i == 1 ? Engine?.Chain(owner.Name).Select(c => c.Enums.GetValueOrDefault(next.Name) is { } e ? NativeEnum(c.Name, e, meta: true) : null).FirstOrDefault(e => e is not null) : null;
                    if (found is null)
                    {
                        return i == 1 ? Unfound(tells, next, $"Could not find type \"{next.Name}\" in \"{first.Name}\".") : Unfound(tells, next, "Enums cannot contain nested types.");
                    }
                    break;
                case GDScriptTypeKind.Builtin:
                    found = i == 1 && Engine?.Builtins.GetValueOrDefault(owner.Name)?.Enums.GetValueOrDefault(next.Name) is { } e ? NativeEnum(owner.Name, e, meta: true) : null;
                    if (found is null)
                    {
                        return i == 1 ? Unfound(tells && Engine is not null, next, $"Name \"{next.Name}\" is not a nested type of \"{first.Name}\".")
                            : Unfound(tells, next, "Built-in types only contain enum types, which do not have nested types.");
                    }
                    break;
                case GDScriptTypeKind.Enum when owner.Script is null && owner.Owner is null:
                    return Unfound(tells, next, "Enums cannot contain nested types.");
                default:
                    return Unfound(tells, node.Chain[1], $"Could not find nested type \"{node.Chain[1].Name}\" under base \"{owner.Instance}\".");
            }
        }
        GDScriptType type = found!.Instance with { Hard = true };
        PaintType(node, found, context);
        if (node.Elements.Count > 0)
        {
            string? wrong = type switch
            {
                { Kind: GDScriptTypeKind.Builtin, Name: "Array" } when node.Elements.Count != 1 => "Typed arrays require exactly one collection element type.",
                { Kind: GDScriptTypeKind.Builtin, Name: "Dictionary" } when node.Elements.Count != 2 => "Typed dictionaries require exactly two collection element types.",
                { Kind: GDScriptTypeKind.Builtin, Name: "Array" or "Dictionary" } => null,
                _ => "Only arrays and dictionaries can specify collection element types.",
            };
            if (wrong is not null)
            {
                return Unfound(tells, node, wrong);
            }
        }
        return node.Elements.Count > 0 ? type with { Elements = [.. node.Elements.Select(e => ResolveType(e, context))] } : type;

        GDScriptType Unfound(bool said, GDScriptNode at, string message)
        {
            if (said)
            {
                Error(context, at, message);
            }
            return GDScriptType.Variant.Soft;
        }
    }

    /// <summary>Whether every class a class extends is read, through to an engine class the API holds: what an error of a
    /// name Godot would not find stands on, a member of a class Passenger cannot read being no name it may say is missing.</summary>
    private bool Knows(GDScriptType type)
    {
        if (Engine is not { } engine)
        {
            return false;
        }
        int depth = 0;
        for (GDScriptType? at = type.Instance; at is not null && depth++ < 64; at = BaseOf(at))
        {
            if (at.Kind == GDScriptTypeKind.Native)
            {
                return engine.Classes.ContainsKey(at.Name);
            }
            if (at.Kind != GDScriptTypeKind.Class || at.Class is not { } node || at.Script is null)
            {
                return false;
            }
            if (node.ExtendsPath is { } written && Tree(Absolute(project.Resolved(written), at.Script)) is null
                || node.ExtendsPath is null && node.Extends.Count > 0 && ExtendedType(node.Extends[0].Name, at) is null)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>A local's kind as Godot's SuiteNode::Local::get_name says it.</summary>
    private static string LocalWords(LocalKind kind) => kind switch
    {
        LocalKind.Constant => "constant",
        LocalKind.Parameter or LocalKind.Setter => "parameter",
        LocalKind.Iterator => "for loop iterator",
        LocalKind.Bind => "pattern bind",
        _ => "variable",
    };

    /// <summary>A member's kind as Godot's ClassNode::Member::get_type_name says it.</summary>
    private static string MemberWords(GDScriptMemberInfo member) => member.Kind switch
    {
        "func" => "function",
        "signal" => "signal",
        "const" when member.Type.Kind == GDScriptTypeKind.Enum && !member.Type.Meta => "enum value",
        "const" => "constant",
        "enum" => "enum",
        "class" => "class",
        _ => "variable",
    };

    /// <summary>A constant of the class's scope that stands for a type (<c>const Enemy = preload("enemy.gd")</c>); null for none.</summary>
    private GDScriptType? ConstantType(string name, Context context) => ScopeClasses(context.Class).Select(c => ScopedConstantType(c, name)).FirstOrDefault(t => t is not null);

    /// <summary>The classes a class's code finds names in, as Godot's get_class_node_current_scope_classes lists them: the
    /// class and the script classes it extends, then each class it is written in, nearest first, with theirs.</summary>
    private IEnumerable<GDScriptType> ScopeClasses(GDScriptType scope)
    {
        List<GDScriptType> outers = [scope.Instance];
        if (scope.Script is { } path && Tree(path) is { } tree && scope.Class is { } inner && inner != tree.Class)
        {
            List<GDScriptType> enclosing = Enclosing(ScriptClass(tree, path), tree.Members.Select(m => (m.Node, m.Offset)), inner);
            enclosing.Reverse();
            outers.AddRange(enclosing);
        }
        foreach (GDScriptType outer in outers)
        {
            int depth = 0;
            for (GDScriptType? at = outer; at is { Kind: GDScriptTypeKind.Class } && depth++ < 64; at = BaseOf(at))
            {
                yield return at;
            }
        }
    }

    /// <summary>A constant a class declares itself that stands for a type: its own meta type, or a script or class its value
    /// is though its type is written otherwise (<c>const Other: GDScript = preload(…)</c>); null for none.</summary>
    private GDScriptType? ScopedConstantType(GDScriptType owner, string name)
    {
        if (!OwnNames(owner).TryGetValue(name, out (GDScriptNode Node, int Offset) named) || named.Node is not GDScriptConstant { Initializer: { } value } constant)
        {
            return null;
        }
        GDScriptType type = MemberType(constant, owner, named.Offset);
        if (type.Meta)
        {
            return type;
        }
        if (!resolving.Add(value))
        {
            return null;
        }
        Context reading = new(new GDScriptAnalysis(null, owner.Script ?? string.Empty), owner.Instance, named.Offset, null, null);
        GDScriptType read = Reduce(value, reading);
        resolving.Remove(value);
        return read is { Meta: true, Kind: GDScriptTypeKind.Class or GDScriptTypeKind.Native } ? read : null;
    }

    /// <summary>The classes that hold an inner class, from the script's own down to its nearest; none where it is not found.</summary>
    private static List<GDScriptType> Enclosing(GDScriptType at, IEnumerable<(GDScriptNode Node, int Offset)> members, GDScriptClass inner)
    {
        foreach ((GDScriptNode node, int offset) in members)
        {
            if (node is not GDScriptClass { Identifier: { } name } nested)
            {
                continue;
            }
            if (nested == inner)
            {
                return [at];
            }
            GDScriptType held = new(GDScriptTypeKind.Class, name.Name) { Class = nested, Script = at.Script, Offset = offset };
            if (Enclosing(held, nested.Members.Select(m => (m, offset)), inner) is { Count: > 0 } deeper)
            {
                return [at, .. deeper];
            }
        }
        return [];
    }

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
            GDScriptType? valued = node.Initializer is { } value ? Reduce(value, context) : null;
            GDScriptType resolved = ResolveType(declared, context);
            if (node is not GDScriptParameter && resolved is { Kind: GDScriptTypeKind.Enum, Meta: false } && valued is { Kind: GDScriptTypeKind.Builtin, Name: "int" } && node.Initializer is { } integer)
            {
                Warn(context, integer, "INT_AS_ENUM_WITHOUT_CAST");
            }
            return resolved;
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
