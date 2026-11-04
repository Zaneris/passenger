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

    /// <summary>Rename of a member of a script's class: its declaration, each name resolved to it and each string a call
    /// names it by, in every script. Refused where the name is no GDScript name or the class has a member of it already,
    /// the member is Godot's own (<c>_ready</c>), a script it extends or that extends it declares a function of its name,
    /// or a place may use it where Passenger can't see whose member it reads (<see cref="GDScriptUses.Unsure"/>).</summary>
    public static GDScriptRefactored Rename(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target, string name)
    {
        if (WhyNotName(name) is { } bad)
        {
            return GDScriptRefactored.No(bad);
        }
        if (name == target.Named)
        {
            return GDScriptRefactored.No($"'{name}' is its name already");
        }
        if (target.Node is GDScriptFunction && Virtuals.Contains(target.Named))
        {
            return GDScriptRefactored.No($"'{target.Named}' is Godot's own, which Godot calls by its name");
        }
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        if (own.Tree.Members.Any(m => NameOf(m.Node) == name))
        {
            return GDScriptRefactored.No($"the script already has a member '{name}'");
        }
        GDScriptUses uses = UsesOf(scripts, target);
        List<GDScriptPlace> blocking = [.. target.Node is GDScriptFunction ? Overriding(scripts, own, target.Named) : [], .. uses.Unsure];
        if (blocking.Count > 0)
        {
            return GDScriptRefactored.No($"Rename can't see every use of '{target.Named}' ({Places(blocking)})", blocking);
        }
        Dictionary<string, List<TextEdit>> edits = new(StringComparer.Ordinal);
        void Edit(string script, int start, int length) => (edits.TryGetValue(script, out List<TextEdit>? list) ? list : edits[script] = []).Add(new TextEdit(start, length, name));
        Edit(target.Script, target.Start, target.Named.Length);
        foreach (GDScriptUses.Use use in uses.Names)
        {
            Edit(use.Script.Script, use.Offset + use.Name.Start, use.Name.End - use.Name.Start);
        }
        foreach (GDScriptUses.Named named in uses.Strings)
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
        return new GDScriptRefactored([.. edits.Select(e => new GDScriptEdits(e.Key, [.. e.Value.DistinctBy(t => t.Start).OrderBy(t => t.Start)]))], null, []);
    }

    /// <summary>The functions of a name in the scripts a script extends or that extend it, which override it or are
    /// overridden by it, so a rename of it would part them.</summary>
    private static IEnumerable<GDScriptPlace> Overriding(IReadOnlyList<GDScriptRead> scripts, GDScriptRead own, string name) =>
        scripts.Where(s => s.Script != own.Script && (Extends(scripts, s, own.Script) || Extends(scripts, own, s.Script)))
            .SelectMany(s => s.Tree.Members.Where(m => m.Node is GDScriptFunction f && f.Identifier?.Name == name)
                .Select(m => new GDScriptPlace(s.Script, m.Start, m.End, $"declares a '{name}' too, which overrides it or it overrides")));

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
    /// member it is in; null where the name is no local of the script's.</summary>
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
