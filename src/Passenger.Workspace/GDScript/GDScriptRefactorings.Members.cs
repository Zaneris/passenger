using Passenger.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>A member of a script's own class a refactoring works on: its node, the offset its positions count from, and its
/// name; an inner class's member, or an enum's value (its enum's node, the value's name), below it.</summary>
public sealed record GDScriptTarget(string Script, GDScriptNode Node, int Offset, GDScriptIdentifier Name)
{
    /// <summary>Where its name is, as the analyzer's declarations say it.</summary>
    public int Start => Offset + Name.Start;

    public string Named => Name.Name;

    /// <summary>The inner class declaring it; null for the script's own class.</summary>
    public GDScriptClass? Within { get; init; }

    /// <summary>Whether it is an enum's value.</summary>
    public bool IsEnumValue => Node is GDScriptEnum enumerated && enumerated.Identifier != Name;

    /// <summary>Whether it is declared below the script's class's members: an inner class's member or an enum's value.</summary>
    public bool Nested => Within is not null || IsEnumValue;
}

/// <summary>A member's uses across the scripts: each name resolved to it (with the call it makes, where it is called), each
/// string a call names it by, and each place that may use it unseen.</summary>
public sealed record GDScriptUses(IReadOnlyList<GDScriptUses.Use> Names, IReadOnlyList<GDScriptUses.Named> Strings, IReadOnlyList<GDScriptPlace> Unsure)
{
    /// <summary>A name resolved to the member, the call it is called by (its callee), with the call's receiver, and the
    /// member of its script it lies in.</summary>
    public sealed record Use(GDScriptRead Script, GDScriptIdentifier Name, int Offset, GDScriptCall? Call, GDScriptExpression? Receiver, GDScriptNode Within);

    /// <summary>A string naming the member and where it does: as an argument of a call (<c>call("f")</c>), an index
    /// (<c>self["f"]</c>) or what <c>in</c> looks for.</summary>
    public sealed record Named(GDScriptRead Script, GDScriptLiteral Literal, int Offset, GDScriptExpression Site)
    {
        public GDScriptCall? Call => Site as GDScriptCall;
    }

    public IEnumerable<GDScriptPlace> Places =>
        Names.Select(u => Place(u.Script, u.Offset, u.Call is { } c ? c : u.Name, u.Call is null ? "takes it as a value" : "calls it"))
            .Concat(Strings.Select(s => Place(s.Script, s.Offset, s.Site, "names it in a string")));

    internal static GDScriptPlace Place(GDScriptRead script, int offset, GDScriptNode node, string what) =>
        new(script.Script, offset + node.Start, offset + node.End, what);
}

public static partial class GDScriptRefactorings
{
    /// <summary>The engine's functions a script's class overrides that Godot itself calls, whose signature Godot fixes.</summary>
    private static readonly HashSet<string> Virtuals = new(StringComparer.Ordinal)
    {
        "_init", "_ready", "_process", "_physics_process", "_enter_tree", "_exit_tree", "_input", "_unhandled_input", "_unhandled_key_input",
        "_shortcut_input", "_gui_input", "_draw", "_notification", "_get", "_set", "_get_property_list", "_to_string", "_static_init",
        "_integrate_forces", "_get_configuration_warnings", "_can_drop_data", "_drop_data", "_get_drag_data", "_has_point", "_validate_property",
        "_property_can_revert", "_property_get_revert", "_iter_init", "_iter_next", "_iter_get",
    };

    /// <summary>The functions that read or write a property by its name, and which argument names it; those that take the
    /// object first name it second.</summary>
    private static readonly Dictionary<string, int> PropertyCalls = new(StringComparer.Ordinal)
    {
        ["get"] = 0, ["set"] = 0, ["set_deferred"] = 0, ["set_thread_safe"] = 0, ["set_deferred_thread_group"] = 0, ["get_indexed"] = 0, ["set_indexed"] = 0,
        ["property_can_revert"] = 0, ["property_get_revert"] = 0, ["tween_property"] = 1, ["add_do_property"] = 1, ["add_undo_property"] = 1,
    };

    /// <summary>The functions that reach every node of a group by a member's name, and which argument names it.</summary>
    private static readonly Dictionary<string, int> GroupCalls = new(StringComparer.Ordinal)
    {
        ["call_group"] = 1, ["call_group_flags"] = 2, ["set_group"] = 1, ["set_group_flags"] = 2,
    };

    /// <summary>The member of a script's own class a name at an offset declares or refers to; null for none.</summary>
    public static GDScriptTarget? MemberAt(IReadOnlyList<GDScriptRead> scripts, GDScriptRead read, int offset)
    {
        GDScriptIdentifier? named = null;
        foreach (GDScriptMember member in read.Tree.Members)
        {
            if (offset >= member.Start && offset <= member.End)
            {
                named = Below(member.Node).OfType<GDScriptIdentifier>().FirstOrDefault(i => offset >= member.Offset + i.Start && offset <= member.Offset + i.End);
                if (named is not null && NameOf(member.Node) == named.Name && DeclaringName(member.Node) == named)
                {
                    return new GDScriptTarget(read.Script, member.Node, member.Offset, named);
                }
                break;
            }
        }
        if (named is null || read.Analysis.DeclarationOf(named) is not { Script: { } script } declared)
        {
            return null;
        }
        GDScriptRead? declaring = scripts.FirstOrDefault(s => s.Script == script);
        return declaring?.Tree.Members.Where(m => DeclaringName(m.Node) is { } n && m.Offset + n.Start == declared.Start)
            .Select(m => new GDScriptTarget(script, m.Node, m.Offset, DeclaringName(m.Node)!)).FirstOrDefault();
    }

    /// <summary>The member a name at an offset declares or refers to at any depth: the script's class's
    /// (<see cref="MemberAt"/>), an inner class's, or an enum's value; null for none, a local among them.</summary>
    public static GDScriptTarget? DeclarationAt(IReadOnlyList<GDScriptRead> scripts, GDScriptRead read, int offset)
    {
        if ((MemberAt(scripts, read, offset) ?? Nested(read, (start, end) => start <= offset && offset <= end)) is { } found)
        {
            return found;
        }
        return read.Analysis.NodeAt(offset) is ({ } node, _) && node is GDScriptIdentifier name && read.Analysis.DeclarationOf(name) is { Script: { } script } declared
            && scripts.FirstOrDefault(s => s.Script == script) is { } declaring ? Nested(declaring, (start, _) => start == declared.Start) : null;
    }

    /// <summary>The first declaration below a script's class's members whose name's place is one asked: an inner class's
    /// member at any depth, or an enum's value.</summary>
    private static GDScriptTarget? Nested(GDScriptRead read, Func<int, int, bool> named) =>
        read.Tree.Members.Select(m => NestedIn(read.Script, m.Node, m.Offset, null, named)).FirstOrDefault(t => t is not null);

    private static GDScriptTarget? NestedIn(string script, GDScriptNode node, int offset, GDScriptClass? within, Func<int, int, bool> named)
    {
        if (node is GDScriptEnum enumerated)
        {
            return enumerated.Values.Where(v => named(offset + v.Name.Start, offset + v.Name.End))
                .Select(v => new GDScriptTarget(script, enumerated, offset, v.Name) { Within = within }).FirstOrDefault();
        }
        if (node is not GDScriptClass inner)
        {
            return null;
        }
        foreach (GDScriptNode member in inner.Members)
        {
            if (DeclaringName(member) is { } name && named(offset + name.Start, offset + name.End))
            {
                return new GDScriptTarget(script, member, offset, name) { Within = inner };
            }
            if (NestedIn(script, member, offset, inner, named) is { } deeper)
            {
                return deeper;
            }
        }
        return null;
    }

    private static GDScriptIdentifier? DeclaringName(GDScriptNode node) => node switch
    {
        GDScriptFunction function => function.Identifier,
        GDScriptAssignable assignable => assignable.Identifier,
        GDScriptSignal signal => signal.Identifier,
        GDScriptEnum enumerated => enumerated.Identifier,
        GDScriptClass inner => inner.Identifier,
        _ => null,
    };

    /// <summary>A member's uses in every script: the names the analyzer resolves to it, the strings naming it (a call's,
    /// <c>get</c>'s and <c>set</c>'s, <c>self["f"]</c>, <c>"f" in self</c>), and, as unsure, one built at run time, a group's
    /// nodes reached by its name, a member of its name the analyzer resolves to nothing (on a value of an engine class, or of
    /// no type written) and a property's name on a value of no type read, which may be a dictionary's key; a string or a call
    /// only on an object that may run the member's class (<see cref="MayRun"/>).</summary>
    public static GDScriptUses UsesOf(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target)
    {
        List<GDScriptUses.Use> names = [];
        List<GDScriptUses.Named> strings = [];
        List<GDScriptPlace> unsure = [];
        // A name as get() or an index reads it: every member of a class but the values of a named enum.
        void ByName(GDScriptRead script, int offset, GDScriptExpression site, GDScriptExpression named, GDScriptType on)
        {
            if (on.Kind == GDScriptTypeKind.Builtin || target is { IsEnumValue: true, Node: GDScriptEnum { Identifier: not null } } || !MayRun(scripts, on, target))
            {
                return;
            }
            if (named is GDScriptLiteral { Value: string text } literal)
            {
                if (text != target.Named && !text.StartsWith($"{target.Named}:", StringComparison.Ordinal))
                {
                    return;
                }
                if (on.IsVariant)
                {
                    unsure.Add(GDScriptUses.Place(script, offset, site, "may name it, on a value whose type is not read"));
                }
                else
                {
                    strings.Add(new GDScriptUses.Named(script, literal, offset, site));
                }
            }
            else if (target.Node is GDScriptVariable && !on.IsVariant && named is not GDScriptLiteral)
            {
                unsure.Add(GDScriptUses.Place(script, offset, site, "names a property by what it reads at run time"));
            }
        }
        foreach (GDScriptRead script in scripts)
        {
            foreach (GDScriptMember member in script.Tree.Members)
            {
                // What a call with no object runs on: the script's class; an inner class's, followed no further.
                GDScriptType self = member.Node is GDScriptClass ? GDScriptType.Variant : script.Analysis.Lineage.FirstOrDefault() ?? GDScriptType.Variant;
                Dictionary<GDScriptNode, GDScriptNode?> parents = Parents(member.Node);
                foreach ((GDScriptNode node, GDScriptNode? parent) in parents)
                {
                    switch (node)
                    {
                        case GDScriptIdentifier identifier when script.Analysis.DeclarationOf(identifier) is { } declared && declared.Script == target.Script
                            && declared.Start == target.Start && !(script.Script == target.Script && member.Offset + identifier.Start == target.Start):
                            (GDScriptCall? calling, GDScriptExpression? receiver) = parent switch
                            {
                                GDScriptCall direct when direct.Callee == identifier => (direct, null),
                                GDScriptSubscript { IsAttribute: true } attribute when attribute.Attribute == identifier && parents.GetValueOrDefault(attribute) is GDScriptCall through
                                    && through.Callee == attribute => (through, attribute.Base),
                                _ => ((GDScriptCall?)null, (GDScriptExpression?)null),
                            };
                            names.Add(new GDScriptUses.Use(script, identifier, member.Offset, calling, receiver, member.Node));
                            break;
                        case GDScriptSubscript { IsAttribute: true, Attribute: { } attribute, Base: { } on } when attribute.Name == target.Named
                            && script.Analysis.DeclarationOf(attribute) is null && MayRun(scripts, script.Analysis.TypeOf(on), target):
                            // A value typed as an engine class, or not at all, may hold a node running the script.
                            unsure.Add(GDScriptUses.Place(script, member.Offset, node, $"its '{target.Named}' is on a value whose script is not read"));
                            break;
                        case GDScriptCall { FunctionName: { } function, Arguments: var arguments } call when NamingCalls.TryGetValue(function, out int index) && arguments.Count > index
                            && !OnCallable(call, script) && MayRun(scripts, ReceiverOf(call, script, self), target):
                            GDScriptExpression argument = arguments[index];
                            if (argument is GDScriptLiteral { Value: string text } literal)
                            {
                                if (text == target.Named)
                                {
                                    strings.Add(new GDScriptUses.Named(script, literal, member.Offset, call));
                                }
                            }
                            else if (target.Node is GDScriptFunction && argument is not GDScriptLiteral)
                            {
                                unsure.Add(GDScriptUses.Place(script, member.Offset, call, "names a method by what it reads at run time"));
                            }
                            break;
                        case GDScriptCall { FunctionName: { } function, Arguments: var arguments } call when PropertyCalls.TryGetValue(function, out int property) && arguments.Count > property:
                            ByName(script, member.Offset, call, arguments[property], property == 1 ? script.Analysis.TypeOf(arguments[0]) : ReceiverOf(call, script, self));
                            break;
                        case GDScriptCall { FunctionName: { } function, Arguments: var arguments } call when GroupCalls.TryGetValue(function, out int group) && arguments.Count > group
                            && arguments[group] is GDScriptLiteral { Value: string reached } && reached == target.Named:
                            unsure.Add(GDScriptUses.Place(script, member.Offset, call, "names it for the nodes of a group"));
                            break;
                        case GDScriptBinary { Operator: GDScriptTokenKind.In, Left: GDScriptLiteral { Value: string } named, Right: { } on } test:
                            ByName(script, member.Offset, test, named, script.Analysis.TypeOf(on));
                            break;
                        case GDScriptSubscript { IsAttribute: false, Index: GDScriptLiteral { Value: string } named, Base: { } on } indexed:
                            ByName(script, member.Offset, indexed, named, script.Analysis.TypeOf(on));
                            break;
                        case GDScriptCall { FunctionName: "emit_signal" or "connect" or "disconnect" or "has_signal" or "is_connected" or "get_signal_connection_list",
                            Arguments: [GDScriptLiteral { Value: string signal } named, ..] } emitting
                            when signal == target.Named && target.Node is GDScriptSignal && MayRun(scripts, ReceiverOf(emitting, script, self), target):
                            strings.Add(new GDScriptUses.Named(script, named, member.Offset, emitting));
                            break;
                        case GDScriptCall { FunctionName: "Signal", Callee: GDScriptIdentifier, Arguments: [_, GDScriptLiteral { Value: string signal } named] } made
                            when signal == target.Named && target.Node is GDScriptSignal && MayRun(scripts, ReceiverOf(made, script, self), target):
                            strings.Add(new GDScriptUses.Named(script, named, member.Offset, made));
                            break;
                        case GDScriptCall { FunctionName: "emit_signal" or "has_signal", Arguments: [not GDScriptLiteral, ..] } emitting
                            when target.Node is GDScriptSignal && MayRun(scripts, ReceiverOf(emitting, script, self), target):
                            unsure.Add(GDScriptUses.Place(script, member.Offset, emitting, "names a signal by what it reads at run time"));
                            break;
                    }
                }
            }
        }
        return new GDScriptUses(names, strings, unsure);
    }

    /// <summary>What a call naming a member runs on: a Callable's or Signal's object (<c>Callable(o, "f")</c>), the object it
    /// is called on, else <paramref name="self"/>.</summary>
    private static GDScriptType ReceiverOf(GDScriptCall call, GDScriptRead script, GDScriptType self) => call switch
    {
        { FunctionName: "Callable" or "Signal", Callee: GDScriptIdentifier, Arguments: [{ } on, ..] } => script.Analysis.TypeOf(on),
        { IsSuper: false, Callee: GDScriptSubscript { IsAttribute: true, Base: { } on } } => script.Analysis.TypeOf(on),
        _ => self,
    };

    /// <summary>Whether a value of a type may be an object running the target's class, so a member it reads or calls by
    /// name may be the target: one of no type held, the class or a class it is built on or built on it, an engine class it
    /// is built on or built on that; never a builtin value. A class whose bases are not all read may be any.</summary>
    private static bool MayRun(IReadOnlyList<GDScriptRead> scripts, GDScriptType type, GDScriptTarget target)
    {
        if (type.IsVariant || !type.Hard || type.Meta)
        {
            return true;
        }
        if (type.Kind is GDScriptTypeKind.Builtin or GDScriptTypeKind.Enum)
        {
            return false;
        }
        // Who builds on an inner class is not followed.
        if (target.Within is not null || scripts.FirstOrDefault(s => s.Script == target.Script)?.Analysis is not { Lineage: [{ } own, ..] lineage } declaring || lineage.Any(t => SameClass(t, type)))
        {
            return true;
        }
        if (type.Kind == GDScriptTypeKind.Native)
        {
            // An engine class built on the class's own may hold a node running it.
            return lineage is not [.., { Kind: GDScriptTypeKind.Native, Name: "Object" }] || declaring.Engine is not { } engine || !engine.Classes.ContainsKey(type.Name)
                || engine.Extends(type.Name, lineage.First(t => t.Kind == GDScriptTypeKind.Native).Name);
        }
        // A script's class built on the target's; an inner class's, or one of a script not read, may be any.
        if (scripts.FirstOrDefault(s => s.Script == type.Script)?.Analysis.Lineage is not [{ } its, ..] built || !SameClass(its, type))
        {
            return true;
        }
        return !lineage.Any(t => t.Kind == GDScriptTypeKind.Native) || !built.Any(t => t.Kind == GDScriptTypeKind.Native) || built.Any(t => SameClass(t, own));
    }

    /// <summary>Whether two types are one class: an engine class by name, a script's by its script and its place there.</summary>
    private static bool SameClass(GDScriptType a, GDScriptType b) => a.Kind == b.Kind && (a.Kind == GDScriptTypeKind.Native ? a.Name == b.Name
        : a.Script == b.Script && (a.Class == b.Class || a.Offset == b.Offset && a.Class?.Identifier?.Name == b.Class?.Identifier?.Name));

    /// <summary>Whether a call is a Callable's own (<c>clear.call_deferred(0.2)</c>, a lambda's <c>call()</c>), whose
    /// arguments are the function's, not a method's name.</summary>
    private static bool OnCallable(GDScriptCall call, GDScriptRead script) =>
        call.Callee is GDScriptSubscript { IsAttribute: true, Base: { } on } && script.Analysis.TypeOf(on) is { Kind: GDScriptTypeKind.Builtin, Name: "Callable", Meta: false };

    /// <summary>The functions of other scripts with the member's name, which may override it or be overridden by it.</summary>
    private static IEnumerable<GDScriptPlace> Namesakes(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target) =>
        scripts.Where(s => s.Script != target.Script).SelectMany(s => s.Tree.Members.Where(m => m.Node is GDScriptFunction f && f.Identifier?.Name == target.Named)
            .Select(m => new GDScriptPlace(s.Script, m.Start, m.End, $"declares a '{target.Named}' too, which may override it")));

    /// <summary>A member's lines and the blank lines after it (its doc comments and annotations before it with it), as the
    /// range a deletion takes out; for the last member, the blank lines before it instead.</summary>
    private static TextRange MemberLines(string text, GDScriptTarget target)
    {
        int first = target.Offset + (target.Node.Annotations.Count > 0 ? Math.Min(target.Node.Start, target.Node.Annotations.Min(a => a.Start)) : target.Node.Start);
        int start = LineStart(text, first);
        // Its doc comments, the ## lines just above it.
        while (start > 0)
        {
            int above = LineStart(text, start - 1);
            if (!text[above..start].TrimStart().StartsWith("##", StringComparison.Ordinal))
            {
                break;
            }
            start = above;
        }
        int end = NextLine(text, target.Offset + target.Node.End);
        int next = end;
        while (next < text.Length && text[next..NextLine(text, next)].Trim().Length == 0)
        {
            next = NextLine(text, next);
        }
        if (next < text.Length)
        {
            return TextRange.FromBounds(start, next);
        }
        while (start > 0 && text[LineStart(text, start - 1)..start].Trim().Length == 0)
        {
            start = LineStart(text, start - 1);
        }
        return TextRange.FromBounds(start, text.Length);
    }

    /// <summary>Why Godot itself calls a function by its name, which no use shows; null where it doesn't.</summary>
    private static string? GodotCalls(GDScriptRead own, GDScriptTarget target) =>
        target.Node is GDScriptFunction && Virtuals.Contains(target.Named) ? $"'{target.Named}' is Godot's own, which Godot calls by its name"
        : target is { Node: GDScriptFunction, Within: null } && EngineDeclaring(own, target.Named) is ({ } engineClass, bool isVirtual)
            ? $"'{target.Named}' overrides {engineClass}'s own{(isVirtual ? ", which Godot calls by its name" : string.Empty)}"
        : null;

    /// <summary>Safe Delete: a member taken out of its script, its lines and doc comments with it, where nothing names it: no
    /// script, no string, no place <paramref name="elsewhere"/> (a scene's connection, C#), none unsure, no function of another
    /// script it overrides or that overrides it; else refused, naming every use. Refused too for a function Godot calls by
    /// its name, and a variable whose value calls, which would no longer run.</summary>
    public static GDScriptRefactored SafeDelete(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target, IReadOnlyList<GDScriptPlace> elsewhere)
    {
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        if (GodotCalls(own, target) is { } called)
        {
            return GDScriptRefactored.No(called);
        }
        if (target.Node is GDScriptVariable { Initializer: { } value } && own.Analysis.Effects.Of(value) == GDScriptEffect.Acts)
        {
            return GDScriptRefactored.No($"'{target.Named}''s value calls, which would no longer run: {Written(own.Text, target.Offset, value)}",
                [new GDScriptPlace(target.Script, target.Offset + value.Start, target.Offset + value.End, "runs as its class is made")]);
        }
        GDScriptUses uses = UsesOf(scripts, target);
        List<GDScriptPlace> blocking = [.. uses.Places, .. uses.Unsure, .. elsewhere, .. FamilyOf(scripts, target).Where(m => m.Script != target.Script)
            .Select(m => new GDScriptPlace(m.Script, m.Start, m.Start + m.Named.Length, "declares it too, which it overrides or that overrides it"))];
        if (blocking.Count > 0)
        {
            return GDScriptRefactored.No($"'{target.Named}' is still used ({Places(blocking)})", blocking);
        }
        TextRange lines = MemberLines(own.Text, target);
        return GDScriptRefactored.Of(target.Script, [new TextEdit(lines.Start, lines.Length, string.Empty)]);
    }

    /// <summary>A short list of places, by script and line.</summary>
    public static string Places(IReadOnlyList<GDScriptPlace> places) =>
        string.Join("; ", places.Take(4).Select(p => $"{p.Script}: {p.What}")) + (places.Count > 4 ? $"; and {places.Count - 4} more" : string.Empty);

    /// <summary>Inline Function: each call of a function whose body is one statement given that statement, its parameters
    /// read as the arguments passed (a default where none is), the function then taken out. Refused where it is used other
    /// than by a call on itself, named by a string or <paramref name="elsewhere"/>, awaits, where an argument that is more
    /// than a name or a literal would be read other than once, or one that calls only sometimes, where the arguments would
    /// no longer run in their order, ahead of the body, or where a name the body reads is a local at the call.</summary>
    public static GDScriptRefactored InlineFunction(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target, IReadOnlyList<GDScriptPlace> elsewhere)
    {
        if (target.Node is not GDScriptFunction { Body.Statements: [var only] } function || function.Rest is not null)
        {
            return GDScriptRefactored.No(target.Node is GDScriptFunction ? "only a function of one statement inlines" : "put the caret on a function");
        }
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        GDScriptExpression? body = only switch
        {
            GDScriptReturn { Value: { } value } => value,
            GDScriptExpression statement => statement,
            _ => null,
        };
        if (body is null || Below(only).Any(n => n is GDScriptAwait or GDScriptLambda or GDScriptAssignment))
        {
            return GDScriptRefactored.No("only a function returning an expression, or of one call, inlines");
        }
        bool returns = only is GDScriptReturn;
        GDScriptUses uses = UsesOf(scripts, target);
        List<GDScriptPlace> blocking = [.. uses.Strings.Select(s => GDScriptUses.Place(s.Script, s.Offset, s.Site, "names it in a string")), .. uses.Unsure, .. elsewhere,
            .. uses.Names.Where(u => u.Call is null || u.Receiver is not (null or GDScriptSelf)).Select(u => GDScriptUses.Place(u.Script, u.Offset, u.Name, u.Call is null ? "takes it as a value" : "calls it on another object"))];
        if (blocking.Count > 0)
        {
            return GDScriptRefactored.No($"'{target.Named}' is used where it can't be inlined ({Places(blocking)})", blocking);
        }
        // Each parameter's reads in the body.
        Dictionary<int, List<GDScriptIdentifier>> reads = [];
        foreach (GDScriptIdentifier name in Below(body).OfType<GDScriptIdentifier>())
        {
            if (own.Analysis.DeclarationOf(name) is { } declared && declared.Script == target.Script
                && function.Parameters.FindIndex(p => p.Identifier is { } n && target.Offset + n.Start == declared.Start) is >= 0 and int index)
            {
                (reads.TryGetValue(index, out List<GDScriptIdentifier>? list) ? list : reads[index] = []).Add(name);
            }
        }
        Dictionary<GDScriptNode, GDScriptNode?> bodyParents = Parents(only);
        Dictionary<string, List<TextEdit>> edits = [];
        GDScriptEffects inBody = own.Analysis.Effects;
        foreach (GDScriptUses.Use use in uses.Names)
        {
            GDScriptCall call = use.Call!;
            string text = use.Script.Text;
            if (uses.Names.Any(other => other != use && other.Script == use.Script && other.Offset == use.Offset && call.Start < other.Call!.Start && other.Call.End <= call.End))
            {
                return GDScriptRefactored.No("its calls nest", [GDScriptUses.Place(use.Script, use.Offset, call, "calls it inside a call of it")]);
            }
            Dictionary<GDScriptNode, GDScriptNode?> parents = Parents(use.Within);
            if (!returns && parents.GetValueOrDefault(call) is not GDScriptSuite)
            {
                return GDScriptRefactored.No("it returns nothing, and a call reads its value", [GDScriptUses.Place(use.Script, use.Offset, call, "reads its value")]);
            }
            // The body's text with each parameter read as its argument, from its end back.
            int bodyStart = target.Offset + body.Start;
            string inlined = own.Text[bodyStart..(target.Offset + body.End)];
            List<(int Start, int End, string Text)> swaps = [];
            List<(string Text, GDScriptEffect Effect, int Ran)> passed = [];
            HashSet<GDScriptExpression> late = [];
            for (int i = 0; i < function.Parameters.Count; i++)
            {
                GDScriptParameter parameter = function.Parameters[i];
                GDScriptExpression? argument = i < call.Arguments.Count ? call.Arguments[i] : null;
                string? given = argument is not null ? text[(use.Offset + argument.Start)..(use.Offset + argument.End)]
                    : parameter.Initializer is { } fallback ? own.Text[(target.Offset + fallback.Start)..(target.Offset + fallback.End)] : null;
                if (given is null)
                {
                    return GDScriptRefactored.No($"a call passes no '{parameter.Identifier?.Name}'", [GDScriptUses.Place(use.Script, use.Offset, call, "calls it")]);
                }
                List<GDScriptIdentifier> read = reads.GetValueOrDefault(i) ?? [];
                GDScriptEffects at = argument is not null ? use.Script.Analysis.Effects : inBody;
                GDScriptExpression? value = argument ?? parameter.Initializer;
                GDScriptEffect effect = value is not null ? at.Of(value) : GDScriptEffect.None;
                bool simple = argument is GDScriptIdentifier or GDScriptLiteral or GDScriptSelf || argument is null && effect != GDScriptEffect.Acts && !inBody.Makes(value!);
                if (!simple && read.Count != 1)
                {
                    return GDScriptRefactored.No($"'{given}' would be read {read.Count} times", [GDScriptUses.Place(use.Script, use.Offset, call, "calls it")]);
                }
                if (effect == GDScriptEffect.Acts && read.FirstOrDefault(r => Conditional(r, only, bodyParents) is not null) is { } sometimes)
                {
                    return GDScriptRefactored.No($"'{given}' would run only as the function's body says: {Conditional(sometimes, only, bodyParents)}", [GDScriptUses.Place(use.Script, use.Offset, call, "calls it")]);
                }
                // Godot reads a plain variable passed as the call runs, after every other argument.
                bool lateRead = value is not null && at.MayBeLate(value);
                passed.Add((given, effect, lateRead ? function.Parameters.Count + i : i));
                if (lateRead)
                {
                    late.UnionWith(read);
                }
                bool compound = argument is not null ? Compound(argument) : parameter.Initializer is { } d && Compound(d);
                foreach (GDScriptIdentifier name in read)
                {
                    swaps.Add((target.Offset + name.Start - bodyStart, target.Offset + name.End - bodyStart, Placed(given, name, compound, bodyParents)));
                }
            }
            if (Below(body).OfType<GDScriptIdentifier>().FirstOrDefault(n => !reads.Values.Any(r => r.Contains(n)) && ReadsAsAName(n, bodyParents) && LocalOver(call, n.Name, parents)) is { } captured)
            {
                return GDScriptRefactored.No($"'{captured.Name}' would read the local where it is called, not the member", [GDScriptUses.Place(use.Script, use.Offset, call, "calls it")]);
            }
            if (Disordered(inBody, only, reads, passed, late, own.Text, target.Offset) is { } disordered)
            {
                return GDScriptRefactored.No(disordered, [GDScriptUses.Place(use.Script, use.Offset, call, "calls it")]);
            }
            foreach ((int start, int end, string swapped) in swaps.OrderByDescending(s => s.Start))
            {
                inlined = inlined[..start] + swapped + inlined[end..];
            }
            string placed = returns ? Placed(inlined, call, Compound(body), parents) : inlined;
            (edits.TryGetValue(use.Script.Script, out List<TextEdit>? list) ? list : edits[use.Script.Script] = [])
                .Add(new TextEdit(use.Offset + call.Start, call.End - call.Start, placed));
        }
        TextRange lines = MemberLines(own.Text, target);
        (edits.TryGetValue(target.Script, out List<TextEdit>? ownEdits) ? ownEdits : edits[target.Script] = []).Add(new TextEdit(lines.Start, lines.Length, string.Empty));
        return new GDScriptRefactored([.. edits.Select(e => new GDScriptEdits(e.Key, [.. e.Value.OrderBy(x => x.Start)]))], null, []);
    }

    /// <summary>Why a call's arguments can't stand where a body reads its parameters: one would run after an argument that
    /// ran after it (<c>Ran</c> their order at the call), or after what the body runs first, where the two can't change
    /// places; null where each can.</summary>
    private static string? Disordered(GDScriptEffects inBody, GDScriptNode body, Dictionary<int, List<GDScriptIdentifier>> reads, List<(string Text, GDScriptEffect Effect, int Ran)> passed,
        HashSet<GDScriptExpression> late, string text, int offset)
    {
        Dictionary<GDScriptNode, int> parameterOf = [];
        foreach ((int index, List<GDScriptIdentifier> names) in reads)
        {
            foreach (GDScriptIdentifier name in names)
            {
                parameterOf[name] = index;
            }
        }
        List<GDScriptNode> ran = [];
        foreach (GDScriptNode node in inBody.InOrder(body, late))
        {
            if (parameterOf.TryGetValue(node, out int reading))
            {
                foreach (GDScriptNode earlier in ran)
                {
                    bool other = parameterOf.TryGetValue(earlier, out int read);
                    if (other && passed[read].Ran > passed[reading].Ran && !GDScriptEffects.Commute(passed[read].Effect, passed[reading].Effect))
                    {
                        return $"'{passed[reading].Text}' would run after '{passed[read].Text}', which ran after it";
                    }
                    if (!other && !GDScriptEffects.Commute(inBody.Own(earlier), passed[reading].Effect))
                    {
                        return $"'{passed[reading].Text}' would run after '{Written(text, offset, earlier)}', which the function runs after it";
                    }
                }
            }
            ran.Add(node);
        }
        return null;
    }

    /// <summary>Whether a text is one type as GDScript's parser reads one: a name, an inner class's by dots, a typed collection
    /// or <c>void</c>.</summary>
    public static bool IsType(string text)
    {
        const string Before = "func f() -> ";
        return GDScriptTree.Parse($"{Before}{text}:\n\tpass\n") is { Errors.Count: 0, Members: [{ Node: GDScriptFunction { ReturnType: { } type }, Offset: var offset }] }
            && offset + type.End == Before.Length + text.Length;
    }

    /// <summary>A function's signature as Change Signature shows it: each parameter's name, type and default as written.</summary>
    public static IReadOnlyList<GDScriptParameterChange> ParametersOf(GDScriptRead own, GDScriptTarget target) =>
        target.Node is GDScriptFunction function
            ? [.. function.Parameters.Select((p, i) => new GDScriptParameterChange(i, p.Identifier?.Name ?? string.Empty, Written(own.Text, target.Offset, p.Type), Written(own.Text, target.Offset, p.Initializer), string.Empty))]
            : [];

    private static string Written(string text, int offset, GDScriptNode? node) => node is null ? string.Empty : text[(offset + node.Start)..(offset + node.End)];

    /// <summary>Change Signature of a function: its name and parameters as given, each call and string naming it updated,
    /// a renamed parameter's reads renamed. Refused where a removed parameter is read, another script declares its name, it
    /// is Godot's own, or, its parameters changing, it is a value (but one <paramref name="followed"/> names: a signal's
    /// connection changing with it), named at run time, read unseen, called from <paramref name="elsewhere"/>, or a call's
    /// arguments would not run as they did (<see cref="Disarranged"/>).</summary>
    public static GDScriptRefactored ChangeSignature(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target, string name, IReadOnlyList<GDScriptParameterChange> parameters,
        IReadOnlyList<GDScriptPlace> elsewhere, IReadOnlySet<GDScriptNode>? followed = null)
    {
        if (target.Node is not GDScriptFunction { ParametersStart: >= 0, ParametersEnd: > 0 } function || function.IsLambda)
        {
            return GDScriptRefactored.No("put the caret on a function");
        }
        if (function.Rest is not null)
        {
            return GDScriptRefactored.No("the function takes the rest of its arguments");
        }
        if ((WhyNotName(name) ?? parameters.Select(p => WhyNotName(p.Name)).FirstOrDefault(w => w is not null)) is { } bad)
        {
            return GDScriptRefactored.No(bad);
        }
        if (parameters.SkipWhile(p => p.Default.Length == 0).FirstOrDefault(p => p.Default.Length == 0) is { } undefaulted)
        {
            return GDScriptRefactored.No($"'{undefaulted.Name}' has no default after a parameter with one, which GDScript refuses");
        }
        if (parameters.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return GDScriptRefactored.No($"two parameters are named '{twice.Key}'");
        }
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        IReadOnlyList<GDScriptParameterChange> was = ParametersOf(own, target);
        // What calls pass changes: an order, a parameter added or taken out, or a default a call leans on taken away.
        bool reshaped = parameters.Count != was.Count || parameters.Select((p, i) => p.From != i || p.Default.Length == 0 && was[p.From].Default.Length > 0).Any(x => x);
        bool renamed = name != target.Named;
        if (reshaped && Virtuals.Contains(target.Named) || renamed && Virtuals.Contains(target.Named))
        {
            return GDScriptRefactored.No($"'{target.Named}' is Godot's own, which Godot calls as it declares it");
        }
        if (renamed && own.Tree.Members.Any(m => NameOf(m.Node) == name))
        {
            return GDScriptRefactored.No($"the script already has a member '{name}'");
        }
        GDScriptUses uses = UsesOf(scripts, target);
        List<GDScriptPlace> blocking = [.. Namesakes(scripts, target), .. uses.Unsure];
        if (reshaped)
        {
            blocking.AddRange(elsewhere);
            blocking.AddRange(uses.Names.Where(u => u.Call is null && followed?.Contains(u.Name) != true)
                .Select(u => GDScriptUses.Place(u.Script, u.Offset, u.Name, "takes it as a value, whose callers can't be followed")));
            blocking.AddRange(uses.Strings.Where(s => s.Call?.FunctionName is not ("call" or "call_deferred" or "rpc" or "rpc_id" or "has_method") && followed?.Contains(s.Literal) != true)
                .Select(s => GDScriptUses.Place(s.Script, s.Offset, s.Site, "names it for a later call")));
            foreach (GDScriptUses.Use use in uses.Names)
            {
                if (use.Call is { } call && Disarranged(use.Script, use.Offset, call, 0, parameters) is { } why)
                {
                    blocking.Add(GDScriptUses.Place(use.Script, use.Offset, call, why));
                }
            }
            foreach (GDScriptUses.Named named in uses.Strings)
            {
                if (named.Call is { FunctionName: "call" or "call_deferred" or "rpc" or "rpc_id" } passing
                    && Disarranged(named.Script, named.Offset, passing, passing.FunctionName == "rpc_id" ? 2 : 1, parameters) is { } why)
                {
                    blocking.Add(GDScriptUses.Place(named.Script, named.Offset, passing, why));
                }
            }
        }
        if (blocking.Count > 0)
        {
            return GDScriptRefactored.No($"Change Signature can't follow every use ({Places(blocking)})", blocking);
        }
        // A parameter removed must be read no more.
        Dictionary<int, List<GDScriptIdentifier>> reads = [];
        foreach (GDScriptIdentifier read in function.Body is { } body ? Below(body).OfType<GDScriptIdentifier>() : [])
        {
            if (own.Analysis.DeclarationOf(read) is { } declared && declared.Script == target.Script
                && function.Parameters.FindIndex(p => p.Identifier is { } n && target.Offset + n.Start == declared.Start) is >= 0 and int index)
            {
                (reads.TryGetValue(index, out List<GDScriptIdentifier>? list) ? list : reads[index] = []).Add(read);
            }
        }
        foreach ((int index, List<GDScriptIdentifier> read) in reads)
        {
            if (!parameters.Any(p => p.From == index))
            {
                return GDScriptRefactored.No($"'{was[index].Name}' is still read in the function", [GDScriptUses.Place(own, target.Offset, read[0], "reads it")]);
            }
        }
        if (parameters.FirstOrDefault(p => p.From < 0 && p.CallValue.Length == 0 && p.Default.Length == 0) is { } unpassed)
        {
            return GDScriptRefactored.No($"the new parameter '{unpassed.Name}' needs what each call passes, or a default");
        }
        Dictionary<string, List<TextEdit>> edits = [];
        void Edit(string script, TextEdit edit) => (edits.TryGetValue(script, out List<TextEdit>? list) ? list : edits[script] = []).Add(edit);
        // The declaration.
        string text = own.Text;
        string written = string.Join(", ", parameters.Select(p =>
        {
            if (p.From >= 0 && was[p.From] is var old && old.Name == p.Name && old.Type == p.Type && old.Default == p.Default)
            {
                return Written(text, target.Offset, function.Parameters[p.From]);
            }
            string typed = p.Type.Length > 0 ? $": {p.Type}" : string.Empty;
            return $"{p.Name}{typed}{(p.Default.Length > 0 ? $" = {p.Default}" : string.Empty)}";
        }));
        Edit(target.Script, new TextEdit(target.Offset + function.ParametersStart + 1, function.ParametersEnd - function.ParametersStart - 2, written));
        if (renamed)
        {
            Edit(target.Script, new TextEdit(target.Start, target.Named.Length, name));
        }
        foreach ((int index, List<GDScriptIdentifier> read) in reads)
        {
            if (parameters.First(p => p.From == index).Name is var now && now != was[index].Name)
            {
                foreach (GDScriptIdentifier at in read)
                {
                    Edit(target.Script, new TextEdit(target.Offset + at.Start, at.End - at.Start, now));
                }
            }
        }
        // Each call: its arguments put in the new order, a default written where one is left out before one passed.
        foreach (GDScriptUses.Use use in uses.Names)
        {
            if (renamed)
            {
                Edit(use.Script.Script, new TextEdit(use.Offset + use.Name.Start, use.Name.End - use.Name.Start, name));
            }
            if (reshaped && use.Call is { } call && Reargued(use.Script.Text, use.Offset, call, 0, was, parameters) is { } argued)
            {
                Edit(use.Script.Script, argued);
            }
        }
        foreach (GDScriptUses.Named named in uses.Strings)
        {
            string? literal = renamed ? Requoted(named.Script.Text[(named.Offset + named.Literal.Start)..(named.Offset + named.Literal.End)], name) : null;
            if (reshaped && named.Call is { FunctionName: "call" or "call_deferred" or "rpc" or "rpc_id" } passing
                && Reargued(named.Script.Text, named.Offset, passing, passing.FunctionName == "rpc_id" ? 2 : 1, was, parameters, (named.Literal, literal)) is { } argued)
            {
                Edit(named.Script.Script, argued);
            }
            else if (literal is not null)
            {
                Edit(named.Script.Script, new TextEdit(named.Offset + named.Literal.Start, named.Literal.End - named.Literal.Start, literal));
            }
        }
        foreach ((string script, List<TextEdit> list) in edits)
        {
            List<TextEdit> sorted = [.. list.OrderBy(e => e.Start)];
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Start < sorted[i - 1].Start + sorted[i - 1].Length)
                {
                    return GDScriptRefactored.No("its calls nest", [new GDScriptPlace(script, sorted[i].Start, sorted[i].Start + sorted[i].Length, "calls it inside a call of it")]);
                }
            }
        }
        return new GDScriptRefactored([.. edits.Select(e => new GDScriptEdits(e.Key, [.. e.Value.OrderBy(x => x.Start)]))], null, []);
    }

    /// <summary>Why a call's arguments after its first <paramref name="kept"/> can't be put as a signature's change says:
    /// two would run the other way round, or one that calls would go with its parameter. Godot reads a plain variable as the
    /// call runs, so wherever it stands; null where they can.</summary>
    private static string? Disarranged(GDScriptRead script, int offset, GDScriptCall call, int kept, IReadOnlyList<GDScriptParameterChange> parameters)
    {
        GDScriptEffects effects = script.Analysis.Effects;
        return Disarranged([.. call.Arguments.Skip(kept).Select(a => (Written(script.Text, offset, a), effects.Late(a) ? GDScriptEffect.None : effects.Of(a)))], parameters);
    }

    private static string? Disarranged(IReadOnlyList<(string Text, GDScriptEffect Effect)> given, IReadOnlyList<GDScriptParameterChange> parameters)
    {
        int PlaceOf(int from) => parameters.ToList().FindIndex(p => p.From == from);
        for (int i = 0; i < given.Count; i++)
        {
            if (PlaceOf(i) < 0 && given[i].Effect == GDScriptEffect.Acts)
            {
                return $"passes '{given[i].Text}', which would no longer run";
            }
            for (int j = i + 1; j < given.Count; j++)
            {
                if (PlaceOf(j) >= 0 && PlaceOf(j) < PlaceOf(i) && !GDScriptEffects.Commute(given[i].Effect, given[j].Effect))
                {
                    return $"passes '{given[i].Text}' and '{given[j].Text}', which would run the other way round";
                }
            }
        }
        return null;
    }

    /// <summary>Why a C# call's arguments, by their text, can't be put as a signature's change says, as for a script's call:
    /// C# runs them as written.</summary>
    public static string? WhyNotRearranged(IReadOnlyList<string> given, IReadOnlyList<GDScriptParameterChange> parameters) =>
        Disarranged([.. given.Select(a => (a, OfCSharp(a.Trim())))], parameters);

    private static GDScriptEffect OfCSharp(string argument) =>
        argument.Length == 0 || argument[0] is '"' or '\'' or '$' or '@' && !argument.Contains('{') || char.IsDigit(argument[0]) || argument is "true" or "false" or "null" ? GDScriptEffect.None
        : argument.Contains('(') || argument.Contains("++", StringComparison.Ordinal) || argument.Contains("--", StringComparison.Ordinal) || argument.StartsWith("await ", StringComparison.Ordinal)
            ? GDScriptEffect.Acts : GDScriptEffect.Reads;

    /// <summary>A string literal as written with another text inside its quotes.</summary>
    private static string Requoted(string written, string name)
    {
        int open = written.IndexOfAny(['"', '\'']);
        int close = open < 0 ? -1 : written.LastIndexOf(written[open]);
        return close > open ? written[..(open + 1)] + name + written[close..] : written;
    }

    /// <summary>A call's arguments after its first <paramref name="kept"/> (a string call's name), put as a signature's
    /// change says: each kept parameter's argument where it was passed, its old default where it was left out before one
    /// passed, a new one's value; null where nothing changes.</summary>
    private static TextEdit? Reargued(string text, int offset, GDScriptCall call, int kept, IReadOnlyList<GDScriptParameterChange> was, IReadOnlyList<GDScriptParameterChange> parameters,
        (GDScriptLiteral Literal, string? Text) renaming = default)
    {
        int open = text.IndexOf('(', offset + (call.Callee?.End ?? call.Start));
        int close = offset + call.End - 1;
        if (open < 0 || close <= open || text[close] != ')')
        {
            return null;
        }
        List<string> given = [.. call.Arguments.Skip(kept).Select(a => text[(offset + a.Start)..(offset + a.End)])];
        IEnumerable<string> before = call.Arguments.Take(kept).Select(a => a == renaming.Literal && renaming.Text is { } renamed ? renamed : text[(offset + a.Start)..(offset + a.End)]);
        string arguments = string.Join(", ", before.Concat(Rearranged(given, was, parameters)));
        return new TextEdit(open + 1, close - open - 1, arguments);
    }

    /// <summary>The arguments a call passes after a signature's change, from those it passed: each kept parameter's where
    /// it was passed, its old default where it was left out before one passed, a new one's value; none left out at the end
    /// that a default now covers. A C# call's too.</summary>
    public static IReadOnlyList<string> Rearranged(IReadOnlyList<string> given, IReadOnlyList<GDScriptParameterChange> was, IReadOnlyList<GDScriptParameterChange> parameters)
    {
        List<(string Text, bool Left)> made = [];
        foreach (GDScriptParameterChange parameter in parameters)
        {
            made.Add(parameter.From >= 0
                ? parameter.From < given.Count ? (given[parameter.From], false) : (was[parameter.From].Default, parameter.Default.Length > 0)
                : parameter.CallValue.Length > 0 ? (parameter.CallValue, false) : (parameter.Default, true));
        }
        while (made.Count > 0 && made[^1].Left)
        {
            made.RemoveAt(made.Count - 1);
        }
        return [.. made.Select(m => m.Text)];
    }
}
