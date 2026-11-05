using Passenger.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>A name's places as Find Usages lists them: by script, each its start and end, its declaration first; and the
/// places that may use it unseen.</summary>
public sealed record GDScriptNamePlaces(IReadOnlyList<(string Script, int Start, int End)> Places, IReadOnlyList<GDScriptPlace> Unsure);

public static partial class GDScriptRefactorings
{
    /// <summary>A member's places: its declaration, each name resolved to it and each string a call names it by, across the
    /// scripts (<see cref="UsesOf"/>), with the places that may use it unseen.</summary>
    public static GDScriptNamePlaces PlacesOf(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target)
    {
        GDScriptUses uses = UsesOf(scripts, target);
        List<(string, int, int)> places = [(target.Script, target.Start, target.Start + target.Named.Length)];
        places.AddRange(uses.Names.Select(u => (u.Script.Script, u.Offset + u.Name.Start, u.Offset + u.Name.End)));
        places.AddRange(uses.Strings.Select(s => (s.Script.Script, s.Offset + s.Literal.Start, s.Offset + s.Literal.End)));
        return new GDScriptNamePlaces(places, uses.Unsure);
    }

    /// <summary>Whether a name stands where a local of its name would be read in its place: alone, not after a dot, as what a
    /// call calls (which looks only at the class) or in a type.</summary>
    private static bool ReadsAsAName(GDScriptIdentifier name, Dictionary<GDScriptNode, GDScriptNode?> parents) => parents.GetValueOrDefault(name) switch
    {
        GDScriptSubscript { IsAttribute: true } attribute => attribute.Attribute != name,
        GDScriptCall call => call.Callee != name,
        GDScriptTypeNode => false,
        _ => true,
    };

    /// <summary>Whether a local or parameter of a name stands over a place: declared before it in a block around it, or a
    /// parameter before the one whose default it is in.</summary>
    private static bool LocalOver(GDScriptNode place, string name, Dictionary<GDScriptNode, GDScriptNode?> parents)
    {
        for (GDScriptNode? at = parents.GetValueOrDefault(place); at is not null; at = parents.GetValueOrDefault(at))
        {
            if (at is GDScriptSuite { Locals: { } locals } && locals.TryGetValue(name, out (string Kind, GDScriptNode Node) local) && local.Node.End <= place.Start
                || at is GDScriptFunction function && function.Parameters.Any(p => p.Identifier?.Name == name && p.End <= place.Start))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Rename of a member of a script's class: its declaration, each name resolved to it and each string a call
    /// names it by, in every script, a function's overrides with it (<see cref="FamilyOf"/>). Refused where the name is no
    /// GDScript name or a class it renames in, or one built on them or they on it, has a member of it, a use would read a
    /// local of the name instead, the member is Godot's own (<c>_ready</c>, an engine class's), or a place may use it where
    /// Passenger can't see whose member it reads (<see cref="GDScriptUses.Unsure"/>), unless <paramref name="anyway"/>, which
    /// leaves them, naming them.</summary>
    public static GDScriptRefactored Rename(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target, string name, bool anyway = false)
    {
        if (WhyNotName(name) is { } bad)
        {
            return GDScriptRefactored.No(bad);
        }
        if (name == target.Named)
        {
            return GDScriptRefactored.No($"'{name}' is its name already");
        }
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        if (GodotCalls(own, target) is { } called)
        {
            return GDScriptRefactored.No(called);
        }
        if (target is { Node: GDScriptFunction, Within: { } inner } && ClassesOf(own)
            .FirstOrDefault(c => c != inner && (BuiltOn(own, c, inner) || BuiltOn(own, inner, c)) && Scope(c, own).Contains(target.Named)) is { } beside)
        {
            return GDScriptRefactored.No($"the inner class {beside.Identifier?.Name} declares a '{target.Named}' too, which overrides it or it overrides");
        }
        IReadOnlyList<GDScriptTarget> family = FamilyOf(scripts, target);
        foreach (GDScriptTarget member in family)
        {
            GDScriptRead declaring = scripts.First(s => s.Script == member.Script);
            // An enum's values are its own names, and an unnamed enum's its class's too.
            IEnumerable<string> names = member.IsEnumValue && member.Node is GDScriptEnum enumerated
                ? enumerated.Values.Select(v => v.Name.Name).Concat(enumerated.Identifier is null ? Scope(member.Within ?? declaring.Tree.Class, declaring) : [])
                : Scope(member.Within ?? declaring.Tree.Class, declaring);
            if (names.Contains(name))
            {
                return GDScriptRefactored.No(member.Script == target.Script ? $"{(member.Within is { } within ? $"the inner class {within.Identifier?.Name}" : "the script")} already has a member '{name}'"
                    : $"{member.Script} already has a member '{name}'");
            }
        }
        bool classWide = target.Within is null && !(target.Node is GDScriptEnum { Identifier: not null } && target.IsEnumValue);
        if (classWide && Related(scripts, scripts.First(s => s.Script == family[0].Script)).FirstOrDefault(s => Scope(s.Tree.Class, s).Contains(name)) is { } taken)
        {
            return GDScriptRefactored.No($"{taken.Script} declares a '{name}' too, in a class built on this one or that this one is built on");
        }
        List<GDScriptUses> found = [.. family.Select(member => UsesOf(scripts, member))];
        List<GDScriptPlace> captured = [.. found.SelectMany(u => u.Names).GroupBy(u => u.Within).SelectMany(within =>
        {
            Dictionary<GDScriptNode, GDScriptNode?> parents = Parents(within.Key);
            return within.Where(u => ReadsAsAName(u.Name, parents) && LocalOver(u.Name, name, parents))
                .Select(u => GDScriptUses.Place(u.Script, u.Offset, u.Name, $"reads it where a local '{name}' stands"));
        })];
        if (captured.Count > 0)
        {
            return GDScriptRefactored.No($"'{name}' would read the local, not the member ({Places(captured)})", captured);
        }
        List<GDScriptPlace> blocking = [.. found.SelectMany(u => u.Unsure).Distinct()];
        if (blocking.Count > 0 && !anyway)
        {
            return GDScriptRefactored.No($"Rename can't see every use of '{target.Named}' ({Places(blocking)})", blocking) with { Unseen = true };
        }
        Dictionary<string, List<TextEdit>> edits = new(StringComparer.Ordinal);
        void Edit(string script, int start, int length) => (edits.TryGetValue(script, out List<TextEdit>? list) ? list : edits[script] = []).Add(new TextEdit(start, length, name));
        foreach (GDScriptTarget member in family)
        {
            Edit(member.Script, member.Start, member.Named.Length);
        }
        foreach (GDScriptUses.Use use in found.SelectMany(u => u.Names))
        {
            Edit(use.Script.Script, use.Offset + use.Name.Start, use.Name.End - use.Name.Start);
        }
        foreach (GDScriptUses.Named named in found.SelectMany(u => u.Strings))
        {
            // The string's characters between its quotes, past a StringName's or NodePath's mark.
            string text = named.Script.Text;
            int start = named.Offset + named.Literal.Start;
            int quote = text.IndexOfAny(['"', '\''], start);
            if (quote >= 0 && quote < named.Offset + named.Literal.End && text.AsSpan(quote + 1).StartsWith(target.Named, StringComparison.Ordinal))
            {
                Edit(named.Script.Script, quote + 1, target.Named.Length);
            }
        }
        return new GDScriptRefactored([.. edits.Select(e => new GDScriptEdits(e.Key, [.. e.Value.DistinctBy(t => t.Start).OrderBy(t => t.Start)]))], null, blocking);
    }

    /// <summary>A function and the functions of its name it overrides or that override it: the farthest of the scripts it is
    /// built on that declares it, then every script built on that one declaring it too, as each overrides the one above;
    /// any other member alone.</summary>
    public static IReadOnlyList<GDScriptTarget> FamilyOf(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target)
    {
        if (target is not { Node: GDScriptFunction, Within: null })
        {
            return [target];
        }
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        GDScriptRead root = own;
        foreach (GDScriptType built in own.Analysis.Lineage.Skip(1))
        {
            if (scripts.FirstOrDefault(s => s.Script == built.Script) is { Analysis.Lineage: [{ } its, ..] } above && SameClass(its, built) && FunctionNamed(above, target.Named) is not null)
            {
                root = above;
            }
        }
        List<GDScriptTarget> family = [FunctionNamed(root, target.Named)!];
        foreach (GDScriptRead below in scripts)
        {
            if (below != root && Extends(scripts, below, root.Script) && FunctionNamed(below, target.Named) is { } overriding)
            {
                family.Add(overriding);
            }
        }
        return family;
    }

    /// <summary>A script's own function of a name, as a target; null where its class declares none.</summary>
    private static GDScriptTarget? FunctionNamed(GDScriptRead script, string name) =>
        script.Tree.Members.Where(m => m.Node is GDScriptFunction { Identifier: { } n } && n.Name == name)
            .Select(m => new GDScriptTarget(script.Script, m.Node, m.Offset, ((GDScriptFunction)m.Node).Identifier!)).FirstOrDefault();

    /// <summary>The names a class declares: its members' and its unnamed enums' values.</summary>
    private static IEnumerable<string> Scope(GDScriptClass declared, GDScriptRead script) =>
        (declared == script.Tree.Class ? script.Tree.Members.Select(m => m.Node) : declared.Members)
            .SelectMany(n => n is GDScriptEnum { Identifier: null } unnamed ? unnamed.Values.Select(v => v.Name.Name) : NameOf(n) is { } named ? [named] : []);

    /// <summary>A script's inner classes at any depth.</summary>
    private static IEnumerable<GDScriptClass> ClassesOf(GDScriptRead script)
    {
        static IEnumerable<GDScriptClass> Inner(IEnumerable<GDScriptNode> members) => members.OfType<GDScriptClass>().SelectMany(c => Inner(c.Members).Prepend(c));
        return Inner(script.Tree.Members.Select(m => m.Node));
    }

    /// <summary>Whether one of a script's inner classes is built on another, each naming the one below as its base.</summary>
    private static bool BuiltOn(GDScriptRead script, GDScriptClass from, GDScriptClass on)
    {
        GDScriptClass? at = from;
        for (int depth = 0; at is { ExtendsPath: null, Extends: [.., { } based] } && depth < 16; depth++)
        {
            at = ClassesOf(script).FirstOrDefault(c => c != at && c.Identifier?.Name == based.Name);
            if (at == on)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The scripts a script is built on and those built on it.</summary>
    private static IEnumerable<GDScriptRead> Related(IReadOnlyList<GDScriptRead> scripts, GDScriptRead root) =>
        scripts.Where(s => s != root && (Extends(scripts, s, root.Script) || Extends(scripts, root, s.Script)));

    /// <summary>The engine class a script is built on that declares a method of a name, which a function of it overrides,
    /// and whether Godot calls it (a virtual); null for none.</summary>
    private static (string Class, bool Virtual)? EngineDeclaring(GDScriptRead script, string name) =>
        script.Analysis.Engine is { } engine && script.Analysis.Lineage.FirstOrDefault(t => t.Kind == GDScriptTypeKind.Native) is { } native
            && engine.Chain(native.Name).FirstOrDefault(c => c.Methods.ContainsKey(name)) is { } declaring ? (declaring.Name, declaring.Methods[name].IsVirtual) : null;

    /// <summary>Whether a script's class extends another script, by its path or its class_name, through the scripts between.</summary>
    private static bool Extends(IReadOnlyList<GDScriptRead> scripts, GDScriptRead from, string script)
    {
        GDScriptRead? at = from;
        for (int depth = 0; at is not null && depth < 64; depth++)
        {
            GDScriptClass declared = at.Tree.Class;
            string? path = declared.ExtendsPath is { } written ? GDScriptAnalyzer.Absolute(written, at.Script)
                : declared.Extends.Count > 0 ? scripts.FirstOrDefault(s => s.Tree.Class.Identifier?.Name == declared.Extends[0].Name)?.Script : null;
            if (path is null)
            {
                return false;
            }
            if (path == script)
            {
                return true;
            }
            at = scripts.FirstOrDefault(s => s.Script == path);
        }
        return false;
    }

    /// <summary>The local or parameter a name at an offset declares or reads: its declaration's offset, its name, and the
    /// member it is in; null where the name is no local of the script's (an inner class's member among them).</summary>
    private static (int Declared, string Name, GDScriptMember Member)? LocalAt(GDScriptRead read, int offset)
    {
        if (read.Analysis.NodeAt(offset) is not ({ } node, int from) || node is not GDScriptIdentifier identifier
            || read.Tree.Members.FirstOrDefault(m => offset >= m.Start && offset <= m.End) is not { Node: not null } member)
        {
            return null;
        }
        int declared = read.Analysis.DeclarationOf(identifier) is { } resolved && resolved.Script == read.Script ? resolved.Start
            : read.Analysis.Declaring.Contains(identifier) ? from + identifier.Start : -1;
        if (declared < member.Start || declared > member.End || DeclaringName(member.Node) is { } memberName && member.Offset + memberName.Start == declared)
        {
            return null;
        }
        // A local is declared inside a function, its name aside: an inner class's members are no locals.
        if (!Below(member.Node).OfType<GDScriptFunction>().Any(f => member.Offset + f.Start <= declared && declared <= member.Offset + f.End
            && !(f.Identifier is { } own && member.Offset + own.Start == declared)))
        {
            return null;
        }
        return (declared, identifier.Name, member);
    }

    /// <summary>A local's places within its member: its declaration and each name resolved to it; null where the name at an
    /// offset is no local.</summary>
    public static GDScriptNamePlaces? LocalPlacesOf(GDScriptRead read, int offset) =>
        LocalAt(read, offset) is ({ } declared, { } name, var member)
            ? new GDScriptNamePlaces([.. LocalNames(read, member, declared).Select(i => (read.Script, member.Offset + i.Start, member.Offset + i.End))], []) : null;

    private static IEnumerable<GDScriptIdentifier> LocalNames(GDScriptRead read, GDScriptMember member, int declared) =>
        Below(member.Node).OfType<GDScriptIdentifier>().Where(i => member.Offset + i.Start == declared
            || read.Analysis.DeclarationOf(i) is { } resolved && resolved.Script == read.Script && resolved.Start == declared).DistinctBy(i => i.Start);

    /// <summary>Rename of a local or parameter: its declaration and each name resolved to it in its member; refused where
    /// the member already reads another name of the new one; null where the name at an offset is no local.</summary>
    public static GDScriptRefactored? RenameLocal(GDScriptRead read, int offset, string name)
    {
        if (LocalAt(read, offset) is not ({ } declared, { } old, var member))
        {
            return null;
        }
        if (WhyNotName(name) is { } bad)
        {
            return GDScriptRefactored.No(bad);
        }
        if (name == old)
        {
            return GDScriptRefactored.No($"'{name}' is its name already");
        }
        List<GDScriptIdentifier> renamed = [.. LocalNames(read, member, declared)];
        if (Below(member.Node).OfType<GDScriptIdentifier>().FirstOrDefault(i => i.Name == name && !renamed.Contains(i)) is { } taken)
        {
            return GDScriptRefactored.No($"the function already uses a '{name}'", [GDScriptUses.Place(read, member.Offset, taken, "names it")]);
        }
        return GDScriptRefactored.Of(read.Script, [.. renamed.Select(i => new TextEdit(member.Offset + i.Start, i.End - i.Start, name))]);
    }
}
