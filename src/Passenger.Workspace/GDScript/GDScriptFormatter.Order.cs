using System.Text;
using Passenger.Text;

namespace Passenger.Workspace.GDScript;

public static partial class GDScriptFormatter
{
    /// <summary>Godot's own virtuals a script's class overrides, in the order the guide's Code order puts the first five.</summary>
    private static readonly string[] FirstVirtuals = ["_init", "_enter_tree", "_ready", "_process", "_physics_process"];

    private static readonly HashSet<string> OtherVirtuals = new(StringComparer.Ordinal)
    {
        "_exit_tree", "_input", "_unhandled_input", "_unhandled_key_input", "_shortcut_input", "_gui_input", "_notification", "_draw", "_get", "_set",
        "_get_property_list", "_property_can_revert", "_property_get_revert", "_validate_property", "_to_string", "_integrate_forces",
        "_get_configuration_warnings", "_can_drop_data", "_drop_data", "_get_drag_data", "_has_point", "_make_custom_tooltip",
    };

    /// <summary>Order Script's Parts: a script's top-level members, each with its comments, in the style guide's Code order
    /// (<see cref="GroupOf"/>), public before private in each; two blank lines around a function or class, one between other
    /// groups. Refused where a value would read a member coming after it, where export groups or regions hold members by
    /// place, and unless the result holds the same members and comments and parses.</summary>
    public static GDScriptFormatted OrderParts(string text)
    {
        GDScriptTree tree = GDScriptTree.Parse(text);
        if (tree.Errors.Count > 0)
        {
            return new GDScriptFormatted([], text, $"the script does not parse: {tree.Errors[0].Message}");
        }
        if (tree.Stopped is not null)
        {
            return new GDScriptFormatted([], text, ReadInPart);
        }
        if (tree.Members.Count < 2)
        {
            return new GDScriptFormatted([], text, null);
        }
        if (tree.Members.Any(m => m.Node is GDScriptAnnotation))
        {
            return new GDScriptFormatted([], text, "its export groups hold the exports after them by place");
        }
        if (text.Split('\n').Any(l => l.TrimStart().StartsWith("#region", StringComparison.Ordinal)))
        {
            return new GDScriptFormatted([], text, "its regions hold the members inside them by place");
        }
        // Each member's block: its lines and the comment lines just above it.
        List<(GDScriptMember Member, int Start, int End, int Group, int Index)> blocks = [];
        int floor = LineStartOf(text, tree.Members[0].Offset + FirstOf(tree.Members[0].Node));
        for (int i = 0; i < tree.Members.Count; i++)
        {
            GDScriptMember member = tree.Members[i];
            int start = LineStartOf(text, member.Offset + FirstOf(member.Node));
            int floorHere = i == 0 ? FloorOfPreamble(text, tree) : blocks[^1].End;
            while (start > floorHere && LineStartOf(text, start - 1) is int above && above >= floorHere && text[above..start].TrimStart().StartsWith('#'))
            {
                start = above;
            }
            int end = NextLineOf(text, Math.Max(member.Start, member.End - 1));
            if (i == 0)
            {
                floor = start;
            }
            if (i > 0 && start < blocks[^1].End)
            {
                return new GDScriptFormatted([], text, "two of its members share a line");
            }
            string gap = text[(i == 0 ? start : blocks[^1].End)..start];
            if (gap.Split('\n').Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')))
            {
                return new GDScriptFormatted([], text, "something stands between its members that belongs to none");
            }
            // Comments a blank line parts from the member after them go with it too.
            if (i > 0 && gap.Trim().Length > 0)
            {
                start = LineStartOf(text, blocks[^1].End + (gap.Length - gap.TrimStart().Length));
            }
            blocks.Add((member, start, end, GroupOf(member.Node), i));
        }
        string preamble = text[..floor].TrimEnd('\n');
        string after = text[blocks[^1].End..];
        if (after.Trim().Length > 0 && !after.Split('\n').All(l => l.Trim().Length == 0 || l.TrimStart().StartsWith('#')))
        {
            return new GDScriptFormatted([], text, "something after its last member belongs to none");
        }
        List<(GDScriptMember Member, int Start, int End, int Group, int Index)> ordered = [.. blocks.OrderBy(b => b.Group)
            .ThenBy(b => NameOfMember(b.Member.Node)?.StartsWith('_') == true ? 1 : 0).ThenBy(b => b.Index)];
        if (Reads(tree, blocks, ordered) is { } refused)
        {
            return new GDScriptFormatted([], text, refused);
        }
        StringBuilder made = new(preamble);
        for (int i = 0; i < ordered.Count; i++)
        {
            bool block = ordered[i].Member.Node is GDScriptFunction or GDScriptClass;
            int blank = i == 0 ? (preamble.Length == 0 ? -1 : block ? 2 : 1)
                : block || ordered[i - 1].Member.Node is GDScriptFunction or GDScriptClass ? 2
                : ordered[i - 1].Group != ordered[i].Group ? 1 : 0;
            if (blank >= 0)
            {
                made.Append('\n', blank + 1);
            }
            made.Append(text[ordered[i].Start..ordered[i].End].TrimEnd('\n'));
        }
        made.Append('\n');
        string trailing = after.Trim('\n');
        if (trailing.Length > 0)
        {
            made.Append('\n').Append(trailing).Append('\n');
        }
        string laid = made.ToString();
        if (laid == text)
        {
            return new GDScriptFormatted([], text, null);
        }
        if (Unlike(text, tree, laid) is { } why)
        {
            return new GDScriptFormatted([], text, $"ordering it would change what it says ({why})") { Attempted = laid };
        }
        return new GDScriptFormatted([new TextEdit(0, text.Length, laid)], laid, null);
    }

    /// <summary>Where the preamble (annotations, class_name, extends, the class's doc comment) ends: the end of its last line.</summary>
    private static int FloorOfPreamble(string text, GDScriptTree tree) => tree.Class.End > 0 ? NextLineOf(text, Math.Min(text.Length, tree.Class.End)) : 0;

    /// <summary>The guide's Code order's place of a member: signals, enums, constants, static variables, exported, regular
    /// and @onready variables, _static_init, static functions, Godot's virtuals from _init to _physics_process then the
    /// rest, other functions, inner classes.</summary>
    private static int GroupOf(GDScriptNode node) => node switch
    {
        GDScriptSignal => 5,
        GDScriptEnum => 6,
        GDScriptConstant => 7,
        GDScriptVariable { IsStatic: true } => 8,
        GDScriptVariable exported when exported.Annotations.Any(a => a.Name.StartsWith("@export", StringComparison.Ordinal)) => 9,
        GDScriptVariable ready when ready.Annotations.Any(a => a.Name == "@onready") => 11,
        GDScriptVariable => 10,
        GDScriptFunction { Identifier.Name: "_static_init" } => 12,
        GDScriptFunction { IsStatic: true } => 13,
        GDScriptFunction { Identifier.Name: var name } when Array.IndexOf(FirstVirtuals, name) is >= 0 and int at => 140 + at,
        GDScriptFunction { Identifier.Name: var name } when OtherVirtuals.Contains(name) => 146,
        GDScriptFunction => 160,
        GDScriptClass => 170,
        _ => 160,
    };

    private static string? NameOfMember(GDScriptNode node) => node switch
    {
        GDScriptFunction function => function.Identifier?.Name,
        GDScriptAssignable assignable => assignable.Identifier?.Name,
        GDScriptSignal signal => signal.Identifier?.Name,
        GDScriptEnum enumerated => enumerated.Identifier?.Name,
        GDScriptClass inner => inner.Identifier?.Name,
        _ => null,
    };

    /// <summary>Why a variable's or constant's value, or an enum's, would read a member coming after it in the new order,
    /// where it came before it; null where none would.</summary>
    private static string? Reads(GDScriptTree tree, List<(GDScriptMember Member, int Start, int End, int Group, int Index)> blocks,
        List<(GDScriptMember Member, int Start, int End, int Group, int Index)> ordered)
    {
        GDScriptAnalysis analysis = new GDScriptAnalyzer(GDScriptProject.Alone(null)).Analyze(tree, "res://ordered.gd");
        Dictionary<int, int> places = [];
        for (int i = 0; i < ordered.Count; i++)
        {
            places[ordered[i].Index] = i;
        }
        Dictionary<int, int> byName = [];
        Dictionary<string, int> byWord = new(StringComparer.Ordinal);
        foreach ((GDScriptMember member, _, _, _, int index) in blocks)
        {
            if (DeclaringNameOf(member.Node) is { } name)
            {
                byName[member.Offset + name.Start] = index;
                byWord.TryAdd(name.Name, index);
            }
        }
        foreach ((GDScriptMember member, _, _, _, int index) in blocks)
        {
            // What a variable's or constant's value, or an enum's values, read.
            (string? name, IEnumerable<GDScriptExpression> values) = member.Node switch
            {
                GDScriptAssignable { Initializer: { } value } reader => (reader.Identifier?.Name, (IEnumerable<GDScriptExpression>)[value]),
                GDScriptEnum enumerated => (enumerated.Identifier?.Name ?? "the enum", enumerated.Values.Select(v => v.Value).OfType<GDScriptExpression>()),
                _ => (null, []),
            };
            foreach (GDScriptIdentifier read in values.SelectMany(Descendants).OfType<GDScriptIdentifier>())
            {
                // A name the analyzer resolves to no member (one in an enum's value) is taken as the member of its name.
                int other = analysis.DeclarationOf(read) is { } declared ? byName.GetValueOrDefault(declared.Start, -1) : byWord.GetValueOrDefault(read.Name, -1);
                if (other >= 0 && other != index && other < index && places[other] > places[index])
                {
                    return $"'{name}''s value reads '{read.Name}', which would come after it";
                }
            }
        }
        return null;
    }

    private static GDScriptIdentifier? DeclaringNameOf(GDScriptNode node) => node switch
    {
        GDScriptFunction function => function.Identifier,
        GDScriptAssignable assignable => assignable.Identifier,
        GDScriptSignal signal => signal.Identifier,
        GDScriptEnum enumerated => enumerated.Identifier,
        GDScriptClass inner => inner.Identifier,
        _ => null,
    };

    private static IEnumerable<GDScriptNode> Descendants(GDScriptNode root)
    {
        Stack<GDScriptNode> left = new([root]);
        while (left.Count > 0)
        {
            GDScriptNode node = left.Pop();
            yield return node;
            foreach (GDScriptNode child in node.Children)
            {
                left.Push(child);
            }
        }
    }

    /// <summary>Why a script ordered anew is not the same script: it no longer parses, or its members, each as written, or
    /// its comments are not those it had; null where they are.</summary>
    private static string? Unlike(string was, GDScriptTree before, string now)
    {
        GDScriptTree after = GDScriptTree.Parse(now);
        if (after.Errors.Count > 0)
        {
            return "it would not parse";
        }
        List<string> Members(GDScriptTree tree) => [.. tree.Members.Select(m => tree.Text[m.Start..m.End]).Order(StringComparer.Ordinal)];
        if (!Members(before).SequenceEqual(Members(after)))
        {
            return "its members";
        }
        List<string> CommentsOf(string text) => [.. text.Split('\n').Select(l => l.IndexOf('#', StringComparison.Ordinal) is >= 0 and int hash ? l[hash..].TrimEnd() : null)
            .OfType<string>().Order(StringComparer.Ordinal)];
        return CommentsOf(was).SequenceEqual(CommentsOf(now)) ? null : "its comments";
    }

    private static int LineStartOf(string text, int offset) => offset <= 0 ? 0 : text.LastIndexOf('\n', Math.Min(offset, text.Length) - 1) + 1;

    private static int NextLineOf(string text, int offset)
    {
        int newline = text.IndexOf('\n', Math.Min(offset, text.Length));
        return newline < 0 ? text.Length : newline + 1;
    }
}
