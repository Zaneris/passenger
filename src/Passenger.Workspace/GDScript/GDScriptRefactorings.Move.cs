using System.Text;
using Passenger.Text;

namespace Passenger.Workspace.GDScript;

public static partial class GDScriptRefactorings
{
    /// <summary>Move: a function and its doc comments taken to another script, in its indentation, its uses made where it
    /// went: none to a script its class extends; <c>Other.f</c> for each call and value, by the other's class_name for a
    /// static one, by the autoload's name for one moved to an autoload's script. Refused where a use can't follow (a string,
    /// a scene or C#, <paramref name="elsewhere"/>; a call on more than a name; one unseen), where it reads a member the
    /// other lacks or self away from its class, or where another script's function has its name.</summary>
    public static GDScriptRefactored Move(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target, string destination, IReadOnlyDictionary<string, string> autoloads,
        IReadOnlyList<GDScriptPlace> elsewhere)
    {
        if (target.Node is not GDScriptFunction { IsLambda: false } function || !scripts.First(s => s.Script == target.Script).Tree.Members.Any(m => m.Node == function))
        {
            return GDScriptRefactored.No("put the caret on a function of a script's own class");
        }
        if (destination == target.Script || scripts.FirstOrDefault(s => s.Script == destination) is not { } to)
        {
            return GDScriptRefactored.No(destination == target.Script ? "choose another script" : $"{destination} is not a script of the project");
        }
        if (Virtuals.Contains(target.Named))
        {
            return GDScriptRefactored.No($"'{target.Named}' is Godot's own, which Godot calls on the node it is declared for");
        }
        if (to.Tree.Members.Any(m => NameOf(m.Node) == target.Named))
        {
            return GDScriptRefactored.No($"{destination} already has a member '{target.Named}'");
        }
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        bool inherited = Ancestors(scripts, target.Script).Contains(destination);
        string? receiver = inherited ? null
            : function.IsStatic ? to.Tree.Class.Identifier?.Name
            : autoloads.FirstOrDefault(a => a.Value == destination).Key;
        HashSet<string> theirs = [.. to.Tree.Members.Select(m => NameOf(m.Node)).OfType<string>(), .. Ancestors(scripts, destination)
            .SelectMany(a => scripts.FirstOrDefault(s => s.Script == a)?.Tree.Members.Select(m => NameOf(m.Node)).OfType<string>() ?? [])];
        // What it reads of its own script must be the other's too, and self means another object away from its class.
        HashSet<int> members = [.. own.Tree.Members.Select(m => DeclaringName(m.Node) is { } n ? m.Offset + n.Start : -1)];
        foreach (GDScriptNode node in function.Body is { } body ? Below(body) : [])
        {
            if (node is GDScriptSelf && !inherited)
            {
                return GDScriptRefactored.No("it reads self, which would be another object in its new script",
                    [new GDScriptPlace(target.Script, target.Offset + node.Start, target.Offset + node.End, "reads self")]);
            }
            if (node is GDScriptIdentifier read && own.Analysis.DeclarationOf(read) is { } declared && declared.Script == target.Script && declared.Start != target.Start
                && members.Contains(declared.Start) && (inherited || !theirs.Contains(read.Name)))
            {
                return GDScriptRefactored.No($"it reads '{read.Name}', which {destination} lacks",
                    [new GDScriptPlace(target.Script, target.Offset + read.Start, target.Offset + read.End, "reads it")]);
            }
        }
        GDScriptUses uses = UsesOf(scripts, target);
        List<GDScriptPlace> blocking = [.. Namesakes(scripts, target).Where(p => p.Script != destination), .. uses.Unsure];
        Dictionary<string, List<TextEdit>> edits = [];
        void Edit(string script, TextEdit edit) => (edits.TryGetValue(script, out List<TextEdit>? list) ? list : edits[script] = []).Add(edit);
        if (!inherited)
        {
            blocking.AddRange(elsewhere);
            blocking.AddRange(uses.Strings.Select(s => GDScriptUses.Place(s.Script, s.Offset, s.Call, "names it on an object of its own script")));
            foreach (GDScriptUses.Use use in uses.Names)
            {
                bool inside = use.Script.Script == target.Script && use.Offset == target.Offset && use.Name.Start >= function.Start && use.Name.End <= function.End;
                if (inside)
                {
                    continue;
                }
                if (receiver is null)
                {
                    blocking.Add(GDScriptUses.Place(use.Script, use.Offset, use.Name, $"uses it on an object of its own script, which {destination}'s are not"));
                    continue;
                }
                Dictionary<GDScriptNode, GDScriptNode?> parents = Parents(use.Script.Tree.Members.First(m => m.Offset == use.Offset).Node);
                if (parents.GetValueOrDefault(use.Name) is GDScriptSubscript { IsAttribute: true, Base: { } owner } attribute && attribute.Attribute == use.Name)
                {
                    if (owner is not (GDScriptSelf or GDScriptIdentifier))
                    {
                        blocking.Add(GDScriptUses.Place(use.Script, use.Offset, attribute, "calls it on an expression a move would leave unread"));
                        continue;
                    }
                    Edit(use.Script.Script, new TextEdit(use.Offset + attribute.Start, attribute.End - attribute.Start, $"{receiver}.{target.Named}"));
                }
                else
                {
                    Edit(use.Script.Script, new TextEdit(use.Offset + use.Name.Start, use.Name.End - use.Name.Start, $"{receiver}.{target.Named}"));
                }
            }
        }
        if (blocking.Count > 0)
        {
            return GDScriptRefactored.No($"Move can't follow every use ({Places(blocking)})", blocking);
        }
        // Taken out of its script with its doc comments, and put last in the other.
        TextRange lines = MemberLines(own.Text, target);
        string moved = own.Text[lines.Start..NextLine(own.Text, target.Offset + function.End)].TrimEnd('\n');
        string from = Unit(own.Text);
        string into = Unit(to.Text);
        if (from != into)
        {
            moved = string.Join('\n', moved.Split('\n').Select(line => Reunit(line, from, into)));
        }
        Edit(target.Script, new TextEdit(lines.Start, lines.Length, string.Empty));
        string gap = to.Text.Length == 0 ? string.Empty : to.Text.EndsWith('\n') ? "\n\n" : "\n\n\n";
        Edit(destination, new TextEdit(to.Text.Length, 0, $"{gap}{moved}\n"));
        return new GDScriptRefactored([.. edits.Select(e => new GDScriptEdits(e.Key, [.. e.Value.OrderBy(x => x.Start)]))], null, []);
    }

    /// <summary>A line's indentation from one unit to another, level for level.</summary>
    private static string Reunit(string line, string from, string into)
    {
        int levels = 0;
        int at = 0;
        while (line.AsSpan(at).StartsWith(from, StringComparison.Ordinal))
        {
            levels++;
            at += from.Length;
        }
        StringBuilder made = new();
        for (int i = 0; i < levels; i++)
        {
            made.Append(into);
        }
        return made.Append(line, at, line.Length - at).ToString();
    }

    /// <summary>The scripts a script extends, nearest first, by path or by class_name.</summary>
    private static List<string> Ancestors(IReadOnlyList<GDScriptRead> scripts, string script)
    {
        List<string> found = [];
        for (GDScriptRead? at = scripts.FirstOrDefault(s => s.Script == script); at is not null && found.Count < 32;)
        {
            GDScriptClass own = at.Tree.Class;
            string? next = own.ExtendsPath is { } path ? (path.StartsWith("res://", StringComparison.Ordinal) ? path : GDScriptAnalyzer.Absolute(path, at.Script))
                : own.Extends is [{ } first, ..] ? scripts.FirstOrDefault(s => s.Tree.Class.Identifier?.Name == first.Name)?.Script : null;
            if (next is null || found.Contains(next))
            {
                break;
            }
            found.Add(next);
            at = scripts.FirstOrDefault(s => s.Script == next);
        }
        return found;
    }
}
