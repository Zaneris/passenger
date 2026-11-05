using Passenger.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>A node path a GDScript writes for the node it runs on: what it says, where the expression or string lies, and where
/// its characters lie, as they are written or (<see cref="Plain"/> false) through an escape; the quote around them, none
/// for a <c>$Path</c> or <c>%Unique</c> written bare, and whether the string is raw.</summary>
public sealed record GDScriptNodePath(string Path, int Start, int End, int CharsStart, int CharsEnd, bool Plain, char? Quote, bool Raw)
{
    /// <summary>The edits writing a name over some of its path's names (each its place in <see cref="Path"/>), as what holds them
    /// writes it: a bare path taking only a word, one that can't hold the name written anew as a quoted <c>$"…"</c>; a string
    /// escaped for its quote; null for a string that can't say it (<see cref="GodotScriptStrings.Written"/>).</summary>
    public IReadOnlyList<TextEdit>? Renamed(IReadOnlyList<(int Start, int End)> names, string name)
    {
        string whole = Enumerable.Reverse(names).Aggregate(Path, (said, at) => said[..at.Start] + name + said[at.End..]);
        if (Quote is not { } quote)
        {
            return IsWord(name) && Plain ? [.. names.Select(at => new TextEdit(CharsStart + at.Start, at.End - at.Start, name))]
                : [new TextEdit(Start, End - Start, $"$\"{GodotScriptStrings.Written(whole, '"', false)}\"")];
        }
        if (GodotScriptStrings.Written(name, quote, Raw) is not { } written)
        {
            return null;
        }
        return Plain ? [.. names.Select(at => new TextEdit(CharsStart + at.Start, at.End - at.Start, written))]
            : GodotScriptStrings.Written(whole, quote, Raw) is { } anew ? [new TextEdit(CharsStart, CharsEnd - CharsStart, anew)] : null;
    }

    private static bool IsWord(string name) => name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c == '_');
}

/// <summary>The node paths a GDScript writes, as Godot's parser reads them: a <c>$Path</c> or <c>%Unique</c>, or a string or
/// NodePath literal given to get_node, get_node_or_null or has_node: the one at an offset, and every one its own node leads. Pure.</summary>
public static class GDScriptNodePaths
{
    /// <summary>What the script's own node is asked of a path, the path its first argument.</summary>
    private static readonly HashSet<string> Calls = new(StringComparer.Ordinal) { "get_node", "get_node_or_null", "has_node", "get_node_and_resource", "has_node_and_resource" };

    /// <summary>Every path the script's own node leads, in its own code at any depth: a <c>$</c> or <c>%</c>, and a string or
    /// <c>NodePath(…)</c> given to one of its node calls, which on another object leads from that object instead.</summary>
    public static IReadOnlyList<GDScriptNodePath> In(GDScriptTree tree)
    {
        List<GDScriptNodePath> paths = [];
        foreach (GDScriptMember member in tree.Members)
        {
            Stack<GDScriptNode> left = new([member.Node]);
            while (left.Count > 0)
            {
                GDScriptNode node = left.Pop();
                if (Written(tree.Text, node, member.Offset) is { } written)
                {
                    paths.Add(written);
                }
                foreach (GDScriptNode child in node.Children)
                {
                    left.Push(child);
                }
            }
        }
        return [.. paths.OrderBy(p => p.Start)];
    }

    private static GDScriptNodePath? Written(string text, GDScriptNode node, int offset) => node switch
    {
        GDScriptGetNode get => Bare(text, get, offset) ?? (Quoted(text, get.Path, offset + get.Start + 1, offset + get.End) is { } quoted ? quoted with { Start = offset + get.Start } : null),
        GDScriptCall { FunctionName: { } function, Callee: GDScriptIdentifier or GDScriptSubscript { Base: GDScriptSelf }, Arguments: [{ } given, ..] } when Calls.Contains(function) => given switch
        {
            GDScriptLiteral { Value: string path } literal => Quoted(text, path, offset + literal.Start, offset + literal.End),
            GDScriptCall { FunctionName: "NodePath", Arguments: [GDScriptLiteral { Value: string path } literal] } => Quoted(text, path, offset + literal.Start, offset + literal.End),
            _ => null,
        },
        _ => null,
    };

    /// <summary>A path written bare (<c>$A/B</c>, <c>%A</c>), told by its characters saying it after any <c>$</c>; null for a quoted one.</summary>
    private static GDScriptNodePath? Bare(string text, GDScriptGetNode get, int offset)
    {
        int start = offset + get.Start;
        int end = offset + get.End;
        int chars = start + (get.UsesDollar ? 1 : 0);
        if (chars < end && text[chars] is '"' or '\'')
        {
            return null;
        }
        return new GDScriptNodePath(get.Path, start, end, chars, end, text.AsSpan(chars, end - chars).SequenceEqual(get.Path), null, false);
    }

    /// <summary>A path written as a string from its prefix to its closing quote.</summary>
    private static GDScriptNodePath? Quoted(string text, string path, int start, int end)
    {
        int quote = text.IndexOfAny(['"', '\''], start, end - start);
        if (quote < 0)
        {
            return null;
        }
        char mark = text[quote];
        int edge = quote + 2 < end && text[quote + 1] == mark && text[quote + 2] == mark ? 3 : 1;
        int chars = quote + edge;
        return new GDScriptNodePath(path, start, end, chars, end - edge, text.AsSpan(chars, end - edge - chars).SequenceEqual(path), mark, quote > start && text[quote - 1] == 'r');
    }

    /// <summary>The path written around an offset of the script's text, or null where none is.</summary>
    public static string? At(GDScriptTree tree, int offset)
    {
        foreach (GDScriptMember member in tree.Members)
        {
            if (member.Start <= offset && offset <= member.End && In(member.Node, offset - member.Offset) is { } path)
            {
                return path;
            }
        }
        return null;
    }

    private static string? In(GDScriptNode node, int at)
    {
        switch (node)
        {
            case GDScriptGetNode get when get.Start <= at && at <= get.End:
                return get.Path;
            case GDScriptCall { FunctionName: "get_node" or "get_node_or_null" or "has_node", Arguments: [GDScriptLiteral { Value: string path } given, ..] }
                when given.Start <= at && at <= given.End:
                return path;
        }
        foreach (GDScriptNode child in node.Children)
        {
            if (In(child, at) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
