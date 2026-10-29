using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Passenger.Workspace;

/// <summary>A JSON file changed as text where its values stand (launchSettings.json's profiles, an AppHost's appsettings.json
/// and user secrets, a solution filter's projects): a value replaced where it is, a member added after its object's last
/// and an item after its array's last, indented as they are, one taken out with its line, the comments and trailing commas
/// .NET's readers take kept, every other character as it was.</summary>
public static class JsonText
{
    internal static readonly JsonSerializerOptions Written = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>JSON as .NET's readers of these files take it: comments passed over and trailing commas allowed.</summary>
    internal static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The text with an entry of an object member of its root set (appsettings.json's <c>Parameters</c>'
    /// <c>password</c>), the member made when the root has none, or with a member of the root set when no entry is given;
    /// null with why when the text does not read as JSON or its root is no object.</summary>
    public static (string? Text, string? Problem) SetInRoot(string text, string property, string? entry, JsonNode? value)
    {
        Node root;
        try
        {
            root = new Reader(text).Document();
        }
        catch (FormatException unreadable)
        {
            return (null, $"it does not read as JSON: {unreadable.Message}");
        }
        if (root.Kind != Kind.Object)
        {
            return (null, "its root is not an object");
        }
        Layout layout = new(text, Unit(text, root));
        return (entry is null ? layout.Set(root, property, value) : layout.SetEntry(root, property, entry, value), null);
    }

    /// <summary>The text with a configuration key set (appsettings.json's <c>Payments</c> → <c>ApiKey</c>): each section the
    /// text holds as an object followed (its name's letter case aside, as configuration reads keys), a key written whole at a
    /// level (<c>"Payments:ApiKey"</c>, as user secrets keep them) set where it stands, the first section it lacks made with
    /// the rest under it, a level in each, as the file indents; null with why when the text does not read as JSON, its root
    /// is no object or a section is a value.</summary>
    public static (string? Text, string? Problem) SetKey(string text, IReadOnlyList<string> path, JsonNode? value)
    {
        Node root;
        try
        {
            root = new Reader(text).Document();
        }
        catch (FormatException unreadable)
        {
            return (null, $"it does not read as JSON: {unreadable.Message}");
        }
        if (root.Kind != Kind.Object)
        {
            return (null, "its root is not an object");
        }
        Layout layout = new(text, Unit(text, root));
        Node holder = root;
        for (int i = 0; i < path.Count; i++)
        {
            string rest = string.Join(':', path.Skip(i));
            if (holder.Members.LastOrDefault(m => m.Name.Equals(rest, StringComparison.OrdinalIgnoreCase)) is { } whole)
            {
                return (layout.Set(holder, whole.Name, value), null);
            }
            if (i == path.Count - 1 || holder.Members.LastOrDefault(m => m.Name.Equals(path[i], StringComparison.OrdinalIgnoreCase)) is not { } section)
            {
                return (layout.SetNested(holder, [.. path.Skip(i)], value), null);
            }
            if (section.Value.Kind != Kind.Object)
            {
                return (null, $"{string.Join(':', path.Take(i + 1))} is a value there, not a section");
            }
            holder = section.Value;
        }
        return (text, null);
    }

    /// <summary>The text with an item put after the last of an array a path of members from its root names (a solution
    /// filter's <c>solution</c>, <c>projects</c>), as its items are laid out; a member the path lacks made, with the rest under
    /// it, as the file indents. Null with why when the text does not read as JSON, its root or a member on the path is no
    /// object, or the last no array.</summary>
    public static (string? Text, string? Problem) AddToArray(string text, IReadOnlyList<string> path, JsonNode item)
    {
        (Node? root, Node? holder, int reached, string? problem) = ArrayAt(text, path);
        if (root is null || holder is null)
        {
            return (null, problem);
        }
        Layout layout = new(text, Unit(text, root));
        if (reached == path.Count)
        {
            return (layout.Append(holder, item), null);
        }
        JsonNode made = new JsonArray(item.DeepClone());
        for (int j = path.Count - 1; j > reached; j--)
        {
            made = new JsonObject { [path[j]] = made };
        }
        return (layout.Add(holder, path[reached], made), null);
    }

    /// <summary>The text with each item of an array a path of members from its root names (a solution filter's
    /// <c>solution</c>, <c>projects</c>) as <paramref name="change"/> answers for it: the item it was given kept as written,
    /// another written where it stands, null taken out with its line. A null item, or one .NET does not read, is kept
    /// unasked, and an array the path does not reach changes nothing. Null with why when the text does not read as JSON,
    /// its root or a member on the path is no object, or the last no array.</summary>
    public static (string? Text, string? Problem) ChangeItems(string text, IReadOnlyList<string> path, Func<JsonNode, JsonNode?> change)
    {
        (Node? root, Node? array, int reached, string? problem) = ArrayAt(text, path);
        if (root is null || array is null)
        {
            return (null, problem);
        }
        if (reached < path.Count)
        {
            return (text, null);
        }
        List<(int Index, JsonNode? Value)> changes = [];
        for (int i = 0; i < array.Items.Count; i++)
        {
            Node item = array.Items[i];
            if (Read(text[item.Start..item.End]) is { } read && change(read) is var now && !ReferenceEquals(now, read))
            {
                changes.Add((i, now));
            }
        }
        string unit = Unit(text, root);
        // From the last to the first, each read again where it stands now: those before it are where they were.
        for (int c = changes.Count - 1; c >= 0; c--)
        {
            Node items = ArrayAt(text, path).Holder!;
            Layout layout = new(text, unit);
            text = changes[c].Value is { } value ? layout.Rewrite(items.Items[changes[c].Index], value) : layout.Remove(items, changes[c].Index);
        }
        return (text, null);

        static JsonNode? Read(string item)
        {
            try
            {
                return JsonNode.Parse(item, documentOptions: Lenient);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>The text's root and the array a path of members from it names, or the object holding the first member the
    /// path lacks, with how many of the path's members it reached; no root, with why, when the text does not read as JSON,
    /// its root or a member on the path is no object, or the last no array.</summary>
    private static (Node? Root, Node? Holder, int Reached, string? Problem) ArrayAt(string text, IReadOnlyList<string> path)
    {
        Node root;
        try
        {
            root = new Reader(text).Document();
        }
        catch (FormatException unreadable)
        {
            return (null, null, 0, $"it does not read as JSON: {unreadable.Message}");
        }
        if (root.Kind != Kind.Object)
        {
            return (null, null, 0, "its root is not an object");
        }
        Node holder = root;
        for (int i = 0; i < path.Count; i++)
        {
            if (holder.Member(path[i]) is not { } member)
            {
                return (root, holder, i, null);
            }
            if (i == path.Count - 1)
            {
                return member.Value.Kind == Kind.Array ? (root, member.Value, path.Count, null) : (null, null, i, $"its {path[i]} is not a list");
            }
            if (member.Value.Kind != Kind.Object)
            {
                return (null, null, i, $"its {path[i]} is not an object");
            }
            holder = member.Value;
        }
        return (null, null, 0, "its root is not a list");
    }

    /// <summary>A value written as dotnet new lays out launchSettings.json: each member and item of an object or array on a
    /// line of its own, a level of <paramref name="unit"/> in from the line holding it (which stands at <paramref name="indent"/>).</summary>
    public static string Laid(JsonNode? value, string unit = "  ", string eol = "\n", string indent = "") => value switch
    {
        JsonObject { Count: > 0 } members => "{" + string.Concat(members.Select((member, i) =>
            $"{eol}{indent}{unit}{JsonSerializer.Serialize(member.Key, Written)}: {Laid(member.Value, unit, eol, indent + unit)}{(i < members.Count - 1 ? "," : string.Empty)}"))
            + $"{eol}{indent}}}",
        JsonArray { Count: > 0 } items => "[" + string.Concat(items.Select((item, i) =>
            $"{eol}{indent}{unit}{Laid(item, unit, eol, indent + unit)}{(i < items.Count - 1 ? "," : string.Empty)}")) + $"{eol}{indent}]",
        _ => value?.ToJsonString(Written) ?? "null",
    };

    /// <summary>One level of the file's indentation: what its root's first property stands in from the root's own line,
    /// else two spaces, as dotnet new writes launchSettings.json.</summary>
    internal static string Unit(string text, Node root)
    {
        if (root.Members.FirstOrDefault() is { } first && !Layout.SameLine(text, root.Start, first.Start))
        {
            string inner = Layout.IndentOf(text, first.Start);
            string outer = Layout.IndentOf(text, root.Start);
            if (inner.Length > outer.Length && inner.StartsWith(outer, StringComparison.Ordinal))
            {
                return inner[outer.Length..];
            }
        }
        return "  ";
    }

    /// <summary>The changes made in the text as it is laid out: its line break, its unit of indentation, and each object's
    /// own way of holding its members (each on a line of its own, or on the object's line).</summary>
    internal sealed class Layout
    {
        private readonly string eol;
        private readonly string unit;
        private string text;

        public Layout(string text, string unit)
        {
            this.text = text;
            this.unit = unit;
            eol = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        }

        public string Set(Node holder, string name, JsonNode? value)
        {
            Member? member = holder.Member(name);
            if (value is null)
            {
                return member is null ? text : Remove(holder, member);
            }
            string written = value.ToJsonString(Written);
            return member is null ? Insert(holder, name, written) : Replace(member.Value.Start, member.Value.End, written);
        }

        /// <summary>A member set whose value is the rest of a key's sections, each an object a level in from the one holding it.</summary>
        public string SetNested(Node holder, IReadOnlyList<string> names, JsonNode? value)
        {
            if (names.Count == 1)
            {
                return Set(holder, names[0], value);
            }
            string indent = MemberIndent(holder);
            string written = value?.ToJsonString(Written) ?? "null";
            for (int i = names.Count - 1; i >= 1; i--)
            {
                string inner = indent + string.Concat(Enumerable.Repeat(unit, i));
                written = $"{{{eol}{inner}{Quoted(names[i])}: {written}{eol}{indent}{string.Concat(Enumerable.Repeat(unit, i - 1))}}}";
            }
            return Insert(holder, names[0], written);
        }

        public string SetEntry(Node holder, string name, string entry, JsonNode? value)
        {
            Member? member = holder.Member(name);
            if (member?.Value is { Kind: Kind.Object } entries)
            {
                return Set(entries, entry, value);
            }
            if (value is null)
            {
                return text;
            }
            string one = $"{Quoted(entry)}: {value.ToJsonString(Written)}";
            string written = Lined(holder)
                ? $"{{{eol}{MemberIndent(holder)}{unit}{one}{eol}{MemberIndent(holder)}}}"
                : $"{{ {one} }}";
            return member is null ? Insert(holder, name, written) : Replace(member.Value.Start, member.Value.End, written);
        }

        /// <summary>A member added after the object's last, its value laid out (<see cref="Laid"/>) a level in from it where
        /// the object holds its members on lines of their own or holds none, else written on the object's line as the
        /// others are.</summary>
        public string Add(Node holder, string name, JsonNode value)
        {
            if (holder.Members.Count == 0 && SameLine(text, holder.Start, holder.Close))
            {
                string outer = IndentOf(text, holder.Start);
                return Replace(holder.Start + 1, holder.Close, $"{eol}{outer}{unit}{Quoted(name)}: {Laid(value, unit, eol, outer + unit)}{eol}{outer}");
            }
            return Insert(holder, name, Lined(holder) ? Laid(value, unit, eol, MemberIndent(holder)) : value.ToJsonString(Written));
        }

        /// <summary>An item put after the array's last, on a line of its own indented as the others where they have lines
        /// of their own, else on the array's line; the last's trailing comma, if it has one, kept as the new one's. An empty
        /// array on one line holds it on a line of its own, a level in from the array's line.</summary>
        public string Append(Node array, JsonNode item)
        {
            string written = item.ToJsonString(Written);
            if (array.Items.Count == 0)
            {
                string outer = IndentOf(text, array.Start);
                return SameLine(text, array.Start, array.Close)
                    ? Replace(array.Start + 1, array.Close, $"{eol}{outer}{unit}{written}{eol}{outer}")
                    : Replace(array.Start + 1, array.Start + 1, $"{eol}{outer}{unit}{written}");
            }
            Node last = array.Items[^1];
            int after = last.Comma >= 0 ? last.Comma + 1 : last.End;
            string comma = last.Comma >= 0 ? string.Empty : ",";
            string trailing = last.Comma >= 0 ? "," : string.Empty;
            return SameLine(text, array.Start, array.Items[0].Start)
                ? Replace(after, after, $"{comma} {written}{trailing}")
                : Replace(after, after, $"{comma}{eol}{IndentOf(text, last.Start)}{written}{trailing}");
        }

        /// <summary>A member put after the object's last, on a line of its own indented as the others where they have
        /// lines of their own, else on the object's line; the last's trailing comma, if it has one, kept as the new one's.</summary>
        private string Insert(Node holder, string name, string written)
        {
            string member = $"{Quoted(name)}: {written}";
            if (holder.Members.Count == 0)
            {
                return SameLine(text, holder.Start, holder.Close)
                    ? Replace(holder.Start + 1, holder.Close, $" {member} ")
                    : Replace(holder.Start + 1, holder.Start + 1, $"{eol}{IndentOf(text, holder.Start)}{unit}{member}");
            }
            Member last = holder.Members[^1];
            int after = last.Comma >= 0 ? last.Comma + 1 : last.Value.End;
            string comma = last.Comma >= 0 ? string.Empty : ",";
            string trailing = last.Comma >= 0 ? "," : string.Empty;
            return Lined(holder)
                ? Replace(after, after, $"{comma}{eol}{IndentOf(text, last.Start)}{member}{trailing}")
                : Replace(after, after, $"{comma} {member}{trailing}");
        }

        /// <summary>An array's item written in its place.</summary>
        public string Rewrite(Node item, JsonNode value) => Replace(item.Start, item.End, value.ToJsonString(Written));

        /// <summary>An array's item taken out as <see cref="Cut"/> takes one.</summary>
        public string Remove(Node array, int index)
        {
            Node item = array.Items[index];
            return Cut(array, array.Items.Count, item.Start, item.End, item.Comma, index > 0 ? array.Items[index - 1].Comma : -1);
        }

        private string Remove(Node holder, Member member)
        {
            int index = holder.Members.IndexOf(member);
            return Cut(holder, holder.Members.Count, member.Start, member.Value.End, member.Comma, index > 0 ? holder.Members[index - 1].Comma : -1);
        }

        /// <summary>A member or item (from <paramref name="start"/> to <paramref name="end"/>) taken out with its comma, and where
        /// it has a line of its own, with that line; the last without a comma of its own takes the one before it
        /// (<paramref name="before"/>); the only one leaves its object or array empty.</summary>
        private string Cut(Node holder, int count, int start, int end, int comma, int before)
        {
            if (count == 1)
            {
                return Replace(holder.Start + 1, holder.Close, string.Empty);
            }
            if (comma < 0)
            {
                return Replace(before, end, string.Empty);
            }
            end = comma + 1;
            int lineStart = LineStart(text, start);
            if (text.AsSpan(lineStart, start - lineStart).IsWhiteSpace())
            {
                int rest = end;
                while (rest < text.Length && text[rest] is ' ' or '\t')
                {
                    rest++;
                }
                if (rest == text.Length || text[rest] is '\r' or '\n')
                {
                    // Alone on its line: the line goes, its break with it.
                    start = lineStart;
                    end = rest < text.Length && text[rest] == '\r' && rest + 1 < text.Length && text[rest + 1] == '\n' ? rest + 2 : Math.Min(rest + 1, text.Length);
                    return Replace(start, end, string.Empty);
                }
            }
            while (end < text.Length && text[end] is ' ' or '\t')
            {
                end++;
            }
            return Replace(start, end, string.Empty);
        }

        private string Replace(int start, int end, string with) => text = string.Concat(text.AsSpan(0, start), with, text.AsSpan(end));

        /// <summary>Whether an object holds its members on lines of their own (its first is not on its brace's line).</summary>
        private bool Lined(Node holder) => holder.Members.Count > 0 ? !SameLine(text, holder.Start, holder.Members[0].Start) : !SameLine(text, holder.Start, holder.Close);

        /// <summary>Where a member of an object stands: as its others do, else a level in from the object's line.</summary>
        private string MemberIndent(Node holder) => holder.Members.Count > 0 ? IndentOf(text, holder.Members[^1].Start) : IndentOf(text, holder.Start) + unit;

        public static bool SameLine(string text, int from, int to) => text.AsSpan(from, to - from).IndexOfAny('\r', '\n') < 0;

        public static int LineStart(string text, int at)
        {
            int before = text.LastIndexOfAny(['\r', '\n'], Math.Max(0, at - 1));
            return at == 0 || before < 0 ? 0 : before + 1;
        }

        /// <summary>The spaces and tabs a line begins with, the line of the character at an index.</summary>
        public static string IndentOf(string text, int at)
        {
            int start = LineStart(text, at);
            int end = start;
            while (end < text.Length && text[end] is ' ' or '\t')
            {
                end++;
            }
            return text[start..end];
        }

        private static string Quoted(string name) => JsonSerializer.Serialize(name, Written);
    }

    internal enum Kind
    {
        Object,
        Array,
        Other,
    }

    /// <summary>A value where the text holds it: from its first character to just past its last, an object's members and
    /// closing brace.</summary>
    internal sealed class Node(Kind kind, int start)
    {
        public Kind Kind { get; } = kind;

        public int Start { get; } = start;

        public int End { get; set; }

        public int Close { get; set; }

        public List<Member> Members { get; } = [];

        /// <summary>An array's items, in order.</summary>
        public List<Node> Items { get; } = [];

        /// <summary>Where the comma after it stands, as an array's item; -1 for none.</summary>
        public int Comma { get; set; } = -1;

        /// <summary>The last member of that name, the one a reader takes.</summary>
        public Member? Member(string name) => Members.LastOrDefault(m => m.Name == name);
    }

    /// <summary>An object's member: its name, where its name's quote stands, its value and the comma after it (-1 for none).</summary>
    internal sealed class Member(string name, int start, Node value)
    {
        public string Name { get; } = name;

        public int Start { get; } = start;

        public Node Value { get; } = value;

        public int Comma { get; set; } = -1;
    }

    /// <summary>JSON as dotnet run reads launchSettings.json (comments and trailing commas allowed), into where each value
    /// stands; a text that does not read is a <see cref="FormatException"/>.</summary>
    internal sealed class Reader(string text)
    {
        private int at;

        public Node Document()
        {
            if (text.Length > 0 && text[0] == '﻿')
            {
                at = 1;
            }
            Node root = Value();
            Trivia();
            return at < text.Length ? throw new FormatException($"more follows its value at {at}") : root;
        }

        private Node Value()
        {
            Trivia();
            if (at >= text.Length)
            {
                throw new FormatException("it ends where a value should be");
            }
            return text[at] switch
            {
                '{' => Object(),
                '[' => Array(),
                '"' => String(),
                _ => Literal(),
            };
        }

        private Node Object()
        {
            Node node = new(Kind.Object, at++);
            while (true)
            {
                Trivia();
                if (at >= text.Length)
                {
                    throw new FormatException("an object is not closed");
                }
                if (text[at] == '}')
                {
                    node.Close = at++;
                    node.End = at;
                    return node;
                }
                if (text[at] != '"')
                {
                    throw new FormatException($"a property's name is not quoted at {at}");
                }
                int start = at;
                Node key = String();
                string name;
                try
                {
                    name = JsonSerializer.Deserialize<string>(text.AsSpan(key.Start, key.End - key.Start)) ?? string.Empty;
                }
                catch (JsonException unreadable)
                {
                    throw new FormatException($"a property's name does not read at {start}", unreadable);
                }
                Trivia();
                if (at >= text.Length || text[at] != ':')
                {
                    throw new FormatException($"a property's name is not followed by ':' at {at}");
                }
                at++;
                Member member = new(name, start, Value());
                node.Members.Add(member);
                Trivia();
                if (at < text.Length && text[at] == ',')
                {
                    member.Comma = at++;
                }
                else if (at >= text.Length || text[at] != '}')
                {
                    throw new FormatException($"a property is followed by neither ',' nor '}}' at {at}");
                }
            }
        }

        private Node Array()
        {
            Node node = new(Kind.Array, at++);
            while (true)
            {
                Trivia();
                if (at >= text.Length)
                {
                    throw new FormatException("an array is not closed");
                }
                if (text[at] == ']')
                {
                    node.Close = at++;
                    node.End = at;
                    return node;
                }
                Node item = Value();
                node.Items.Add(item);
                Trivia();
                if (at < text.Length && text[at] == ',')
                {
                    item.Comma = at++;
                }
                else if (at >= text.Length || text[at] != ']')
                {
                    throw new FormatException($"an item is followed by neither ',' nor ']' at {at}");
                }
            }
        }

        private Node String()
        {
            Node node = new(Kind.Other, at++);
            while (at < text.Length && text[at] != '"')
            {
                at += text[at] == '\\' ? 2 : 1;
            }
            if (at >= text.Length)
            {
                throw new FormatException("a string is not closed");
            }
            node.End = ++at;
            return node;
        }

        private Node Literal()
        {
            Node node = new(Kind.Other, at);
            while (at < text.Length && !char.IsWhiteSpace(text[at]) && text[at] is not (',' or '}' or ']' or '/'))
            {
                at++;
            }
            if (at == node.Start)
            {
                throw new FormatException($"no value at {at}");
            }
            node.End = at;
            return node;
        }

        /// <summary>Passes over whitespace and comments.</summary>
        private void Trivia()
        {
            while (at < text.Length)
            {
                if (char.IsWhiteSpace(text[at]))
                {
                    at++;
                }
                else if (text[at] == '/' && at + 1 < text.Length && text[at + 1] == '/')
                {
                    while (at < text.Length && text[at] is not ('\r' or '\n'))
                    {
                        at++;
                    }
                }
                else if (text[at] == '/' && at + 1 < text.Length && text[at + 1] == '*')
                {
                    int end = text.IndexOf("*/", at + 2, StringComparison.Ordinal);
                    at = end < 0 ? throw new FormatException("a comment is not closed") : end + 2;
                }
                else
                {
                    return;
                }
            }
        }
    }
}
