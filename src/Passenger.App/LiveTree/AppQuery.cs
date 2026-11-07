using System.Globalization;
using System.Text;
using Passenger.Inspector;
using Passenger.Core;

namespace Passenger.App.LiveTree;

/// <summary>An element of an app's tree as an agent finds it: the element, its parent, its window, and whether it shows
/// (its own and every ancestor's IsVisible).</summary>
public sealed record AppNode(Element Element, AppNode? Parent, Element Window, bool Shows)
{
    public bool IsInside(AppNode other)
    {
        for (AppNode? at = Parent; at is not null; at = at.Parent)
        {
            if (at == other)
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>How an agent names the elements of an app (<c>app_tree</c>'s filter, the other tools' control) and how the tree
/// is written for it. A query is steps joined by '/', each step finding elements inside the last one's: <c>@12</c> (an id
/// the tree shows), or a type, <c>#name</c>, <c>.class</c>es and <c>"text"</c> (what it shows) in any mix, with
/// <c>[n]</c> for the nth of its matches from 1; a bare word no type has is taken as a name. A step finds the outermost
/// elements matching it, so <c>"Save"</c> is the button, not the text inside it, and a template's parts only by #name or id.
/// Also where an element is in a tree, and whether two trees are the same. Every walk keeps a stack of its own
/// (<see cref="AppTree.Walk"/>), so no tree is too deep for the thread that walks it.</summary>
public static class AppQuery
{
    /// <summary>The lines an answer may carry before it says how many more there are.</summary>
    public const int MostLines = 1500;

    /// <summary>Every element of the windows given, in tree order, each window before what it holds.</summary>
    public static List<AppNode> Flatten(IReadOnlyList<Element> windows)
    {
        List<AppNode> all = [];
        // The nodes from a window down to the one made last.
        List<AppNode> line = [];
        foreach ((Element element, int depth, _) in AppTree.Walk(windows))
        {
            line.RemoveRange(depth, line.Count - depth);
            AppNode? parent = depth > 0 ? line[^1] : null;
            AppNode node = new(element, parent, parent?.Window ?? element, (parent?.Shows ?? true) && element.Visible);
            line.Add(node);
            all.Add(node);
        }
        return all;
    }

    /// <summary>The elements from a root down to the first with an id, in tree order; null where none has it.</summary>
    public static List<Element>? Path(IReadOnlyList<Element> roots, long id)
    {
        List<Element> line = [];
        foreach ((Element element, int depth, _) in AppTree.Walk(roots))
        {
            line.RemoveRange(depth, line.Count - depth);
            line.Add(element);
            if (element.Id == id)
            {
                return line;
            }
        }
        return null;
    }

    /// <summary>Whether two trees name the same elements, by id, type, name, whether each shows and whether it holds more
    /// than is read, in the same places.</summary>
    public static bool Same(IReadOnlyList<Element> first, IReadOnlyList<Element> second)
    {
        using IEnumerator<(Element Element, int Depth, bool Open)> others = AppTree.Walk(second).GetEnumerator();
        foreach ((Element a, int depth, _) in AppTree.Walk(first))
        {
            if (!others.MoveNext() || others.Current is not (Element b, int otherDepth, _) || depth != otherDepth || a.Id != b.Id || a.Type != b.Type
                || a.Name != b.Name || a.Visible != b.Visible || a.Deeper != b.Deeper || a.Children.Count != b.Children.Count)
            {
                return false;
            }
        }
        return !others.MoveNext();
    }

    /// <summary>The elements a query finds among <paramref name="nodes"/> (a whole tree, flattened), in tree order: a
    /// template's parts only by their #name or @id, as the tree leaves them out, unless <paramref name="templates"/>.</summary>
    public static List<AppNode> Find(IReadOnlyList<AppNode> nodes, string query, bool templates = false)
    {
        List<Step> steps = Parse(query);
        List<AppNode> found = [];
        for (int i = 0; i < steps.Count; i++)
        {
            Step step = steps[i];
            HashSet<AppNode> under = new(found, ReferenceEqualityComparer.Instance);
            IEnumerable<AppNode> scope = i == 0 ? nodes : nodes.Where(n => Inside(n, under));
            List<AppNode> matches = [.. scope.Where(n => step.Matches(n.Element, templates))];
            // A bare word that is no type of the tree's is a name.
            if (matches.Count == 0 && step is { Type: { } word, Name: null, Classes.Count: 0, Shown: null, Id: null })
            {
                Step named = step with { Type = null, Name = word };
                matches = [.. scope.Where(n => named.Matches(n.Element, templates))];
            }
            HashSet<AppNode> matched = new(matches, ReferenceEqualityComparer.Instance);
            List<AppNode> outermost = [.. matches.Where(m => !Inside(m, matched))];
            if (step.Index is int n)
            {
                if (n < 1 || n > outermost.Count)
                {
                    throw new ArgumentException($"{step.Written ?? query} has {Plural.Of(outermost.Count, "match", "matches")}, so no [{n}]");
                }
                outermost = [outermost[n - 1]];
            }
            found = outermost;
        }
        return found;
    }

    /// <summary>Whether a node is inside any of some others.</summary>
    private static bool Inside(AppNode node, HashSet<AppNode> others)
    {
        for (AppNode? at = node.Parent; at is not null; at = at.Parent)
        {
            if (others.Contains(at))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>The one element a query names, for input or a picture: of several, the one a person can see (shown, in view,
    /// not a template's part) when only one is, else an error listing them, those seen first, so the agent can name one by
    /// [n] or its id.</summary>
    public static AppNode One(IReadOnlyList<AppNode> nodes, string query)
    {
        List<AppNode> found = Find(nodes, query);
        List<AppNode> seen = [.. found.Where(n => n.Shows && n.Element.OutOfView != true && !n.Element.FromTemplate)];
        if (found.Count > 1 && seen.Count == 1)
        {
            found = seen;
        }
        List<AppNode> listed = [.. seen, .. found.Except(seen)];
        return found switch
        {
            [AppNode only] => only,
            [] => throw new ArgumentException($"Nothing in the app matches {query}; app_tree shows what there is"),
            _ => throw new ArgumentException($"{found.Count} elements match {query}: {string.Join("; ", listed.Take(10).Select(Where))}"
                + $"{(found.Count > 10 ? $"; and {found.Count - 10} more" : "")}. Name one by its id (@N) or by [n] after the query"),
        };
    }

    /// <summary>An element as a list names it: its line's label and the named elements it is inside, out to its window.</summary>
    public static string Where(AppNode node)
    {
        List<string> inside = [];
        for (AppNode? at = node.Parent; at is not null; at = at.Parent)
        {
            if (at.Parent is null || at.Element.Name is { Length: > 0 } && !at.Element.FromTemplate)
            {
                inside.Add(Short(at.Element));
            }
        }
        return inside.Count == 0 ? Label(node.Element) : $"{Label(node.Element)} in {string.Join(" in ", inside)}";
    }

    /// <summary>An element as the tree writes it: type, #name, .classes, "text", @id, its box in its window (x,y wxh), whether
    /// it is hidden, out of view, disabled, focused or a template's part, and the values asked for.</summary>
    public static string Label(Element element)
    {
        StringBuilder line = new(element.Type);
        if (element.Name is { Length: > 0 } name)
        {
            line.Append('#').Append(name);
        }
        foreach (string css in element.Classes)
        {
            line.Append('.').Append(css);
        }
        if (element.Text is { } text)
        {
            line.Append(" \"").Append(text.Replace("\n", "\\n", StringComparison.Ordinal)).Append('"');
        }
        line.Append(" @").Append(element.Id.ToString(CultureInfo.InvariantCulture));
        if (element.Bounds is { } box)
        {
            line.Append(' ').Append(Number(box.X)).Append(',').Append(Number(box.Y)).Append(' ').Append(Number(box.Width)).Append('x').Append(Number(box.Height));
        }
        if (!element.Visible)
        {
            line.Append(" [hidden]");
        }
        if (element.OutOfView == true)
        {
            line.Append(" [out of view]");
        }
        if (element.Disabled == true)
        {
            line.Append(" [disabled]");
        }
        if (element.Focused == true)
        {
            line.Append(" [focused]");
        }
        if (element.FromTemplate)
        {
            line.Append(" [template]");
        }
        if (element.Values is { Count: > 0 } values)
        {
            line.Append(" {").Append(string.Join("; ", values.Select(v => $"{v.Key}={v.Value}"))).Append('}');
        }
        return line.ToString();
    }

    /// <summary>An element as an answer names it in passing: its type, #name and @id.</summary>
    public static string Short(Element element) =>
        $"{element.Type}{(element.Name is { Length: > 0 } name ? "#" + name : "")} @{element.Id.ToString(CultureInfo.InvariantCulture)}";

    private static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The tree under some elements, indented two spaces a level: a template's parts left out unless asked for (what
    /// they hold coming up a level), and what is inside a hidden element counted rather than written unless asked for, as
    /// are Avalonia's empty layers over a window; where an element holds more than was read, it says so. At most
    /// <see cref="MostLines"/> lines, then how many more.</summary>
    public static string Write(IEnumerable<AppNode> roots, int? depth, bool templates, bool hidden)
    {
        StringBuilder text = new();
        int lines = 0;
        (int Count, bool Deeper) more = (0, false);
        // What is left to write, the next on top: an element, the level it would be written at, and whether it is a root.
        Stack<(Element Element, int Level, bool Root)> left = new();
        foreach (AppNode root in roots.Reverse())
        {
            left.Push((root.Element, 0, true));
        }
        while (left.TryPop(out (Element Element, int Level, bool Root) next))
        {
            (Element element, int level, bool root) = next;
            bool written = root || templates || !element.FromTemplate;
            bool holds = element.Children.Count > 0 || element.Deeper == true;
            if (!templates && !root && !holds && element.Name is null && element.Type.EndsWith("Layer", StringComparison.Ordinal))
            {
                continue;
            }
            if (!written && !element.Visible && !hidden)
            {
                continue;
            }
            if (written)
            {
                if (lines >= MostLines)
                {
                    (int count, bool deeper) = Inside(element);
                    more = (more.Count + count + 1, more.Deeper || deeper);
                    continue;
                }
                text.Append(' ', level * 2).Append(Label(element));
                lines++;
                if (!element.Visible && !hidden && holds)
                {
                    text.Append(CultureInfo.InvariantCulture, $" ({Counted(Inside(element))} inside)\n");
                    continue;
                }
                text.Append('\n');
                if (depth is int most && level >= most)
                {
                    if (holds)
                    {
                        text.Append(' ', (level + 1) * 2).Append(CultureInfo.InvariantCulture, $"... {Counted(Inside(element))} inside\n");
                        lines++;
                    }
                    continue;
                }
            }
            if (element.Deeper == true && lines < MostLines)
            {
                text.Append(' ', (written ? level + 1 : level) * 2)
                    .Append(CultureInfo.InvariantCulture, $"... more inside, not read: the tree is read {Element.DeepestNesting} levels deep\n");
                lines++;
            }
            for (int i = element.Children.Count - 1; i >= 0; i--)
            {
                left.Push((element.Children[i], written ? level + 1 : level, false));
            }
        }
        if (more.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"... and {Counted(more)} more elements: narrow it with control, filter or depth\n");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>How many elements are inside one, and whether it or any of them holds more than was read.</summary>
    private static (int Count, bool Deeper) Inside(Element element)
    {
        int count = 0;
        bool deeper = element.Deeper == true;
        foreach ((Element inside, _, _) in AppTree.Walk(element.Children))
        {
            count++;
            deeper |= inside.Deeper == true;
        }
        return (count, deeper);
    }

    /// <summary>A count of elements, "at least" where some hold more than was read.</summary>
    private static string Counted((int Count, bool Deeper) counted) =>
        counted.Deeper ? $"at least {counted.Count.ToString(CultureInfo.InvariantCulture)}" : counted.Count.ToString(CultureInfo.InvariantCulture);

    private static List<Step> Parse(string query)
    {
        List<Step> steps = [];
        int i = 0;
        string text = query.Trim();
        while (i < text.Length)
        {
            steps.Add(ParseStep(text, ref i, query));
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            if (i < text.Length)
            {
                if (text[i] != '/')
                {
                    throw new ArgumentException($"In {query}, '{text[i]}' at {i + 1} is not a step's part: steps are joined by /");
                }
                i++;
            }
        }
        return steps.Count > 0 ? steps : throw new ArgumentException("An empty query names nothing");
    }

    private static Step ParseStep(string text, ref int i, string query)
    {
        int start = i;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }
        if (i < text.Length && text[i] == '@')
        {
            i++;
            string digits = Word(text, ref i);
            return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long id)
                ? new Step(Id: id, Written: text[start..i].Trim())
                : throw new ArgumentException($"In {query}, @{digits} is no element's id");
        }
        string? type = null;
        string? name = null;
        List<string> classes = [];
        string? shown = null;
        int? index = null;
        if (i < text.Length && IsWordChar(text[i]))
        {
            type = Word(text, ref i);
        }
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '#')
            {
                i++;
                name = Word(text, ref i);
            }
            else if (c == '.')
            {
                i++;
                classes.Add(Word(text, ref i, extra: "-:"));
            }
            else if (c == '"')
            {
                shown = Quoted(text, ref i, query);
            }
            else if (c == '[')
            {
                int close = text.IndexOf(']', i);
                if (close < 0 || !int.TryParse(text.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int n))
                {
                    throw new ArgumentException($"In {query}, [n] takes a number from 1");
                }
                index = n;
                i = close + 1;
            }
            else if (char.IsWhiteSpace(c) && i + 1 < text.Length && text[(i + 1)..].TrimStart() is ['"', ..])
            {
                i++;
            }
            else
            {
                break;
            }
        }
        if (type is null && name is null && classes.Count == 0 && shown is null)
        {
            throw new ArgumentException($"In {query}, a step at {start + 1} names nothing: give a type, #name, .class, \"text\" or @id");
        }
        return new Step(type, name, classes, shown, index, null, text[start..i].Trim());
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '`';

    private static string Word(string text, ref int i, string extra = "")
    {
        int start = i;
        while (i < text.Length && (IsWordChar(text[i]) || extra.Contains(text[i], StringComparison.Ordinal)))
        {
            i++;
        }
        return text[start..i];
    }

    private static string Quoted(string text, ref int i, string query)
    {
        StringBuilder value = new();
        for (i++; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                value.Append(text[++i]);
            }
            else if (text[i] == '"')
            {
                i++;
                return value.ToString();
            }
            else
            {
                value.Append(text[i]);
            }
        }
        throw new ArgumentException($"In {query}, a \"text\" is not closed");
    }

    /// <summary>One step of a query, and the words it was written in.</summary>
    private sealed record Step(string? Type = null, string? Name = null, IReadOnlyList<string>? Classes = null, string? Shown = null,
        int? Index = null, long? Id = null, string? Written = null)
    {
        public bool Matches(Element element, bool templates) =>
            Id is long id
                ? element.Id == id
                : (templates || !element.FromTemplate || Name is not null)
                    && (Type is null || string.Equals(element.Type, Type, StringComparison.OrdinalIgnoreCase))
                    && (Name is null || element.Name == Name)
                    && (Classes is null || Classes.All(element.Classes.Contains))
                    && (Shown is null || string.Equals(element.Text?.Trim(), Shown.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
