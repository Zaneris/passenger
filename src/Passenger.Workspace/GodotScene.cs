using System.Text;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>A value of a Godot text scene or resource as Godot's variant text writes it, by where it lies in the file: for
/// a string, bare or the one argument of a call (<c>NodePath("Hud/Score")</c>, <c>ExtResource("1_x")</c>, a StringName's
/// <c>&amp;"x"</c>), what it says and where its characters lie between the quotes (an old file's <c>ExtResource( 1 )</c>
/// counts its digits so); for a bare word (<c>true</c>, <c>null</c>), the word.</summary>
public sealed record GodotValue(int Start, int End, string? Call, string? Text, int TextStart, int TextEnd, string? Word)
{
    /// <summary>How many values an array holds at its top; zero for any other value.</summary>
    public int Items { get; init; }

    /// <summary>Whether the string is written as its characters, no escape in it, so a place in it is a place in the file.</summary>
    public bool Plain => Text is not null && TextEnd - TextStart == Text.Length;
}

/// <summary>A key and its value: an attribute of a tag (<c>name="Score"</c>) or a property after it (<c>text = "0"</c>),
/// with where its key starts.</summary>
public sealed record GodotEntry(string Key, int KeyStart, GodotValue Value)
{
    /// <summary>Whether its key is written as a string, as Godot writes one holding a character past ASCII.</summary>
    public bool Quoted { get; init; }

    /// <summary>Where its key's characters start, past a string's quote.</summary>
    public int NameStart => Quoted ? KeyStart + 1 : KeyStart;
}

/// <summary>One tag of a Godot text file and the properties written after it until the next: its name
/// (<c>node</c>, <c>ext_resource</c>, <c>connection</c>), where it lies and on which line (from zero).</summary>
public sealed record GodotSection(string Tag, int Start, int End, int Line, IReadOnlyList<GodotEntry> Attributes, IReadOnlyList<GodotEntry> Properties)
{
    public GodotEntry? Attribute(string key) => Attributes.FirstOrDefault(a => a.Key == key);

    public GodotEntry? Property(string key) => Properties.LastOrDefault(p => p.Key == key);

    /// <summary>The string an attribute gives, bare or as a call's argument, else the word it is (an old file's number);
    /// null where it gives neither.</summary>
    public string? Text(string key) => Attribute(key)?.Value is { } value ? value.Text ?? value.Word : null;
}

/// <summary>A node a scene writes (<c>[node name="Score" type="Label" parent="Hud"]</c>): its name, its engine class, the path
/// of its parent from the scene's root ("." for the root's children, none for the root), the external resource it instances
/// and its script's, and whether it is unique in its owner (<c>%Score</c>).</summary>
public sealed record GodotNode(GodotSection Section)
{
    public string Name => Section.Text("name") ?? string.Empty;

    public string? Type => Section.Text("type");

    public string? Parent => Section.Text("parent");

    public string? Instance => Section.Attribute("instance")?.Value is { Call: "ExtResource", Text: { } id } ? id : null;

    public string? Placeholder => Section.Text("instance_placeholder");

    public string? Script => Section.Property("script")?.Value is { Call: "ExtResource", Text: { } id } ? id : null;

    public bool Unique => Section.Property("unique_name_in_owner")?.Value.Word == "true";

    /// <summary>Its path from the scene's root as the file writes it: "." for the root, else its parent's then its name.</summary>
    public string PathInScene => Parent is not { } parent ? "." : parent == "." ? Name : $"{parent}/{Name}";
}

/// <summary>A signal connection a scene writes (<c>[connection signal="pressed" from="Hud/Start" to="." method="OnStart"]</c>):
/// the signal, the paths of the node sending it and the one receiving it from the scene's root, the method called, how many
/// values it binds after the signal's own (the sending node first where its flags say so) and how many of those it drops
/// (<c>unbinds</c>).</summary>
public sealed record GodotConnection(GodotSection Section)
{
    public string Signal => Section.Text("signal") ?? string.Empty;

    public string From => Section.Text("from") ?? ".";

    public string To => Section.Text("to") ?? ".";

    public string Method => Section.Text("method") ?? string.Empty;

    /// <summary>The values <c>binds= [1, "a"]</c> gives.</summary>
    public int Binds => Section.Attribute("binds")?.Value.Items ?? 0;

    public int Unbinds => Section.Attribute("unbinds")?.Value.Word is { } word && int.TryParse(word, out int count) ? count : 0;

    /// <summary>Whether Godot binds the sending node right after the signal's own values (<c>flags</c> holding
    /// CONNECT_APPEND_SOURCE_OBJECT, 16).</summary>
    public bool AppendsSource => Section.Attribute("flags")?.Value.Word is { } word && int.TryParse(word, out int flags) && (flags & AppendSource) != 0;

    /// <summary>The values bound after the signal's own: the sending node where <see cref="AppendsSource"/>, then <see cref="Binds"/>.</summary>
    public int Bound => (AppendsSource ? 1 : 0) + Binds;

    private const int AppendSource = 16;
}

/// <summary>An external resource a file names (<c>[ext_resource type="Script" path="res://Main.cs" id="1_x"]</c>).</summary>
public sealed record GodotExternal(GodotSection Section)
{
    public string Id => Section.Text("id") ?? string.Empty;

    public string Type => Section.Text("type") ?? string.Empty;

    public string? Path => Section.Text("path");

    public string? Uid => Section.Text("uid");
}

/// <summary>A Godot text scene (<c>.tscn</c>) or resource (<c>.tres</c>) as Godot's own format writes it: a header tag
/// (<c>gd_scene</c>, <c>gd_resource</c>), then tags each followed by its properties, a property's value Godot's variant text
/// over as many lines as its brackets and strings hold. Read whole, kept by where each part lies so a change can be made in
/// place; a tag or value Passenger does not know is kept by its place alone.</summary>
public sealed class GodotFile
{
    private readonly int[] lineStarts;

    private GodotFile(string text, IReadOnlyList<GodotSection> sections, int[] lineStarts)
    {
        Text = text;
        Sections = sections;
        this.lineStarts = lineStarts;
        Externals = [.. sections.Where(s => s.Tag == "ext_resource").Select(s => new GodotExternal(s))];
        Nodes = [.. sections.Where(s => s.Tag == "node").Select(s => new GodotNode(s))];
        Connections = [.. sections.Where(s => s.Tag == "connection").Select(s => new GodotConnection(s))];
    }

    public IReadOnlyList<GodotSection> Sections { get; }

    /// <summary>The text it was read from.</summary>
    public string Text { get; }

    /// <summary>A value as the file writes it.</summary>
    public string Written(GodotValue value) => Text[value.Start..value.End];

    public IReadOnlyList<GodotExternal> Externals { get; }

    public IReadOnlyList<GodotNode> Nodes { get; }

    public IReadOnlyList<GodotConnection> Connections { get; }

    /// <summary>The header: <c>gd_scene</c> or <c>gd_resource</c>; null for a file with none.</summary>
    public GodotSection? Header => Sections.Count > 0 && Sections[0].Tag.StartsWith("gd_", StringComparison.Ordinal) ? Sections[0] : null;

    /// <summary>A resource file's own section (<c>[resource]</c>), whose properties are the resource's; null in a scene.</summary>
    public GodotSection? Resource => Sections.FirstOrDefault(s => s.Tag == "resource");

    /// <summary>The paths of the folders a scene's nodes are opened in for editing, though they are another scene's
    /// (<c>[editable path="Player"]</c>).</summary>
    public IReadOnlyList<GodotSection> Editables => [.. Sections.Where(s => s.Tag == "editable")];

    /// <summary>The external resource of an id; null for none.</summary>
    public GodotExternal? External(string? id) => id is null ? null : Externals.FirstOrDefault(e => e.Id == id);

    /// <summary>Every call of a name a value holds, at its top or inside its arrays, dictionaries and other calls but never
    /// inside its strings (<c>SubResource("Box_x")</c> in <c>[SubResource("Box_x")]</c>), each with what its one argument
    /// says (a string's characters, or an old file's number) and where that lies; a NodePath's <c>^"…"</c> among NodePath's.</summary>
    public IEnumerable<(string Argument, int Start, int End)> CallsIn(GodotValue value, string call)
    {
        int at = value.Start;
        while (at < value.End)
        {
            if (Text[at] == '^' && call == "NodePath" && at + 1 < value.End && Text[at + 1] == '"')
            {
                (string path, int closing, int after) = Quoted(Text, at + 1);
                yield return (path, at + 2, closing);
                at = after;
                continue;
            }
            if (Text[at] == '"')
            {
                at = Quoted(Text, at).Next;
                continue;
            }
            bool named = string.CompareOrdinal(Text, at, call, 0, call.Length) == 0 && (at == 0 || !(char.IsLetterOrDigit(Text[at - 1]) || Text[at - 1] == '_'));
            int open = SkipSpaces(Text, at + call.Length);
            if (!named || open >= value.End || Text[open] != '(')
            {
                at++;
                continue;
            }
            int argument = SkipSpaces(Text, open + 1);
            if (argument < value.End && Text[argument] == '"')
            {
                (string said, int close, int next) = Quoted(Text, argument);
                yield return (said, argument + 1, close);
                at = next;
                continue;
            }
            int digits = argument;
            while (digits < value.End && char.IsDigit(Text[digits]))
            {
                digits++;
            }
            if (digits > argument)
            {
                yield return (Text[argument..digits], argument, digits);
            }
            at = Math.Max(digits, open + 1);
        }
    }

    /// <summary>The line (from zero) an offset is on.</summary>
    public int LineOf(int offset) => Math.Max(0, Sorted.FirstAfter<int>(lineStarts, offset) - 1);

    /// <summary>Whether a file is one Godot writes as text: a scene or a resource.</summary>
    public static bool IsText(string path) =>
        path.EndsWith(".tscn", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".tres", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a file is a resource Godot writes as bytes, as it may an Animation or an AnimationLibrary.</summary>
    public static bool IsBinary(string path) =>
        path.EndsWith(".res", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase);

    public static GodotFile Parse(string text)
    {
        List<int> starts = [0];
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            starts.Add(i + 1);
        }
        int[] lineStarts = [.. starts];
        List<GodotSection> sections = [];
        int at = 0;
        while ((at = SkipBlank(text, at)) < text.Length)
        {
            if (text[at] == ';')
            {
                at = LineEnd(text, at);
                continue;
            }
            if (text[at] != '[')
            {
                // A property before any tag: passed over with its value.
                at = Entry(text, at, inTag: false, out _);
                continue;
            }
            int start = at;
            (string tag, List<GodotEntry> attributes, int end) = Tag(text, at);
            List<GodotEntry> properties = [];
            at = end;
            while ((at = SkipBlank(text, at)) < text.Length && text[at] != '[')
            {
                if (text[at] == ';')
                {
                    at = LineEnd(text, at);
                    continue;
                }
                at = Entry(text, at, inTag: false, out GodotEntry? property);
                if (property is not null)
                {
                    properties.Add(property);
                }
            }
            int line = Math.Max(0, Sorted.FirstAfter<int>(lineStarts, start) - 1);
            sections.Add(new GodotSection(tag, start, end, line, attributes, properties));
        }
        return new GodotFile(text, sections, lineStarts);
    }

    /// <summary>A tag from its '[': its name, its attributes, and where the text goes on after its ']'.</summary>
    private static (string Tag, List<GodotEntry> Attributes, int End) Tag(string text, int open)
    {
        int at = open + 1;
        int nameStart = at;
        while (at < text.Length && text[at] is not (' ' or '\t' or ']' or '\n' or '\r'))
        {
            at++;
        }
        string name = text[nameStart..at];
        List<GodotEntry> attributes = [];
        while (true)
        {
            at = SkipBlank(text, at);
            if (at >= text.Length)
            {
                return (name, attributes, at);
            }
            if (text[at] == ']')
            {
                return (name, attributes, at + 1);
            }
            int before = at;
            at = Entry(text, at, inTag: true, out GodotEntry? attribute);
            if (attribute is not null)
            {
                attributes.Add(attribute);
            }
            if (at == before)
            {
                at++;
            }
        }
    }

    /// <summary>A key, '=' and its value from the key's start; where the text goes on after it, and the entry, null where no
    /// '=' follows the key (the rest of its line passed over). A key may be written as a string.</summary>
    private static int Entry(string text, int at, bool inTag, out GodotEntry? entry)
    {
        entry = null;
        int keyStart = at;
        string key;
        if (text[at] == '"')
        {
            (key, _, at) = Quoted(text, at);
        }
        else
        {
            while (at < text.Length && text[at] is not ('=' or '\n' or ']') && !(inTag && text[at] is ' ' or '\t'))
            {
                at++;
            }
            key = text[keyStart..at].TrimEnd();
        }
        at = SkipSpaces(text, at);
        if (at >= text.Length || text[at] != '=')
        {
            return inTag ? at : LineEnd(text, at);
        }
        int valueStart = SkipSpaces(text, at + 1);
        int end = ValueEnd(text, valueStart, inTag);
        entry = new GodotEntry(key, keyStart, ValueAt(text, valueStart, end)) { Quoted = text[keyStart] == '"' };
        return end;
    }

    /// <summary>Where a value ends: at its line's end, or in a tag at a space or the tag's ']', outside its brackets and strings.</summary>
    private static int ValueEnd(string text, int start, bool inTag)
    {
        int depth = 0;
        int at = start;
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '"')
            {
                at = Quoted(text, at).Next;
                continue;
            }
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    break;
                }
                depth--;
            }
            else if (depth == 0 && (c == '\n' || inTag && c is ' ' or '\t' or '\r'))
            {
                break;
            }
            at++;
        }
        while (at > start && text[at - 1] is ' ' or '\t' or '\r')
        {
            at--;
        }
        return at;
    }

    /// <summary>A value's meaning where Passenger reads one: a string's, a call's one argument's, or a bare word's.</summary>
    internal static GodotValue ValueAt(string text, int start, int end)
    {
        if (start >= end)
        {
            return new GodotValue(start, end, null, null, start, start, null);
        }
        char first = text[start];
        if (first == '"')
        {
            (string said, int close, _) = Quoted(text, start);
            return new GodotValue(start, end, null, said, start + 1, close, null);
        }
        if (first is '&' or '^' && start + 1 < end && text[start + 1] == '"')
        {
            (string said, int close, _) = Quoted(text, start + 1);
            return new GodotValue(start, end, first == '&' ? "StringName" : "NodePath", said, start + 2, close, null);
        }
        if (first == '[')
        {
            return new GodotValue(start, end, null, null, start, start, null) { Items = Items(text, start, end) };
        }
        int at = start;
        while (at < end && (char.IsLetterOrDigit(text[at]) || text[at] is '_' or '-' or '.' or '+'))
        {
            at++;
        }
        if (at == end)
        {
            return new GodotValue(start, end, null, null, start, start, text[start..end]);
        }
        if (at > start && text[at] == '(')
        {
            string call = text[start..at];
            int argument = SkipSpaces(text, at + 1);
            if (argument < end && text[argument] == '"')
            {
                (string said, int close, _) = Quoted(text, argument);
                return new GodotValue(start, end, call, said, argument + 1, close, null);
            }
            int digits = argument;
            while (digits < end && char.IsDigit(text[digits]))
            {
                digits++;
            }
            if (digits > argument)
            {
                return new GodotValue(start, end, call, text[argument..digits], argument, digits, null);
            }
            return new GodotValue(start, end, call, null, start, start, null);
        }
        return new GodotValue(start, end, null, null, start, start, null);
    }

    /// <summary>How many values an array from its '[' to its end holds at its top.</summary>
    private static int Items(string text, int open, int end)
    {
        int depth = 0;
        int items = 0;
        bool any = false;
        for (int at = open + 1; at < end; at++)
        {
            char c = text[at];
            if (c == '"')
            {
                at = Quoted(text, at).Next - 1;
                any = true;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
                any = true;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth-- == 0)
                {
                    break;
                }
            }
            else if (c == ',' && depth == 0)
            {
                items += any ? 1 : 0;
                any = false;
            }
            else if (!char.IsWhiteSpace(c))
            {
                any = true;
            }
        }
        return items + (any ? 1 : 0);
    }

    /// <summary>A quoted string from its opening quote, Godot's escapes read: what it says, where its characters end (its
    /// closing quote) and where the text goes on after it; one never closed runs to the text's end.</summary>
    internal static (string Value, int End, int Next) Quoted(string text, int quote)
    {
        StringBuilder value = new();
        int at = quote + 1;
        while (at < text.Length && text[at] != '"')
        {
            if (text[at] == '\\' && at + 1 < text.Length)
            {
                at++;
                char escaped = text[at];
                int digits = escaped == 'u' ? 4 : escaped == 'U' ? 6 : 0;
                if (digits > 0 && at + digits < text.Length
                    && int.TryParse(text.AsSpan(at + 1, digits), System.Globalization.NumberStyles.AllowHexSpecifier, null, out int code))
                {
                    // A \u's half of a surrogate pair joins the other half as it is appended.
                    value.Append(digits == 4 ? ((char)code).ToString() : code <= 0x10FFFF && code is < 0xD800 or > 0xDFFF ? char.ConvertFromUtf32(code) : "\uFFFD");
                    at += digits + 1;
                    continue;
                }
                value.Append(escaped switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', _ => escaped });
            }
            else
            {
                value.Append(text[at]);
            }
            at++;
        }
        return (value.ToString(), at, Math.Min(at + 1, text.Length));
    }

    private static int LineEnd(string text, int at)
    {
        int end = text.IndexOf('\n', at);
        return end < 0 ? text.Length : end + 1;
    }

    private static int SkipBlank(string text, int at)
    {
        while (at < text.Length && char.IsWhiteSpace(text[at]))
        {
            at++;
        }
        return at;
    }

    private static int SkipSpaces(string text, int at)
    {
        while (at < text.Length && text[at] is ' ' or '\t')
        {
            at++;
        }
        return at;
    }
}
