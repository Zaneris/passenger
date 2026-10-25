using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace Passenger.Workspace;

/// <summary>An XML file's text (a nuget.config, a project, a .slnx) and the elements in it, changed in place: each change
/// is a splice of the text, after which the elements are read from it again, so an element is looked up afresh for each
/// change. Only the elements changed are written, indented as their neighbours are; every other character stays as it
/// was. Comments are no elements, so what one holds is never found or changed.</summary>
internal sealed class XmlText
{
    private XElement root = null!;
    private int[] lines = [];

    public XmlText(string text)
    {
        Text = text;
        NewLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        Parse();
        // The step one level of the file is indented by, as its root's first child has it.
        string step = root.Elements().FirstOrDefault() is { } first ? Indent(first) : string.Empty;
        Unit = step.Length > Indent(root).Length ? step[Indent(root).Length..] : "  ";
    }

    public string Text { get; private set; }

    public string NewLine { get; }

    public string Unit { get; }

    /// <summary>The root element as the text now reads.</summary>
    public XElement Root => root;

    /// <summary>The root's child of a name, whatever namespace the file gives its elements.</summary>
    public XElement? Section(string name) => root.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    /// <summary>A value as an attribute's quotes hold it.</summary>
    public static string Escape(string value) => SecurityElement.Escape(value);

    /// <summary>The indent a section's children have: its last child's, else one step in from the section.</summary>
    public string ChildIndent(string section) => Section(section) is not { } found
        ? Indent(root) + Unit + Unit
        : found.Elements().LastOrDefault() is { } last && Indent(last) is { Length: > 0 } own ? own : Indent(found) + Unit;

    /// <summary>Adds an element at the end of a section, the section made at the root's end if there is none.</summary>
    public void AddChild(string section, string element)
    {
        if (Section(section) is not { } found)
        {
            string indent = Indent(root) + Unit;
            Insert(root, $"<{section}>{NewLine}{indent}{Unit}{element}{NewLine}{indent}</{section}>", indent);
            return;
        }
        Insert(found, element, ChildIndent(section));
    }

    /// <summary>The indent a parent's children have: its last child's, else a step in from the parent's line.</summary>
    public string IndentIn(XElement parent) =>
        parent.Elements().LastOrDefault() is { } last && Indent(last) is { Length: > 0 } own ? own : LineIndent(Span(parent).Start) + Unit;

    /// <summary>Puts an element after another: on the same line when more follows it there, else on a line of its own
    /// indented as it is, a step further in when its line begins with what holds it.</summary>
    public void After(XElement element, string with)
    {
        (int start, int end) = Span(element);
        int lineEnd = Text.IndexOfAny(['\r', '\n'], end);
        if (Text[end..(lineEnd < 0 ? Text.Length : lineEnd)].Trim().Length > 0)
        {
            Splice(end, 0, with);
            return;
        }
        string indent = StartsLine(start) ? Indent(element) : LineIndent(start) + Unit;
        Splice(end, 0, NewLine + indent + with);
    }

    /// <summary>Puts an element before another: on a line of its own indented as it is when it starts its line, else just
    /// before it.</summary>
    public void Before(XElement element, string with)
    {
        int start = Span(element).Start;
        Splice(start, 0, StartsLine(start) ? with + NewLine + Indent(element) : with);
    }

    /// <summary>Writes an element as a parent's last child: just before its end tag when that shares its line (on a line
    /// of its own there for <paramref name="ownLine"/>), else on a line of its own before the end tag's, indented as the
    /// parent's children (a blank line after it where one stood before the end tag); an empty element written short is
    /// written out.</summary>
    public void Append(XElement parent, string element, bool ownLine = false)
    {
        (int start, int end) = Span(parent);
        int open = TagEnd(start);
        if (Text[open - 2] == '/')
        {
            string head = Text[start..(open - 2)].TrimEnd();
            Splice(start, end - start, $"{head}>{NewLine}{LineIndent(start)}{Unit}{element}{NewLine}{LineIndent(start)}</{TagName(start)}>");
            return;
        }
        string indent = IndentIn(parent);
        int close = Text.LastIndexOf("</", end - 1, StringComparison.Ordinal);
        int closeLine = Text.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
        if (closeLine <= open || Text[closeLine..close].Trim().Length > 0)
        {
            Splice(close, 0, ownLine ? NewLine + indent + element + NewLine : element);
            return;
        }
        int previous = closeLine >= 2 ? Text.LastIndexOf('\n', closeLine - 2) + 1 : 0;
        bool spaced = previous > open && Text[previous..(closeLine - 1)].Trim().Length == 0;
        Splice(closeLine, 0, indent + element + NewLine + (spaced ? NewLine : string.Empty));
    }

    public void Replace(XElement element, string with)
    {
        (int start, int end) = Span(element);
        Splice(start, end - start, with);
    }

    /// <summary>Sets one attribute in an element's start tag, the rest of the tag as it was; one it lacks goes after its
    /// last.</summary>
    public void SetAttribute(XElement element, string name, string value)
    {
        if (ValueOf(element, name) is { } written)
        {
            Splice(written.Start, written.Length, Quoted(value, Text[written.Start - 1]));
            return;
        }
        int start = Span(element).Start;
        int end = TagEnd(start) - 1;
        end -= Text[end - 1] == '/' ? 1 : 0;
        while (char.IsWhiteSpace(Text[end - 1]))
        {
            end--;
        }
        Splice(end, 0, $" {name}=\"{Quoted(value, '"')}\"");
    }

    /// <summary>Where an attribute's value is in an element's start tag, inside its quotes, as written; null when the tag
    /// has no attribute of that name.</summary>
    public (int Start, int Length)? ValueOf(XElement element, string name)
    {
        int i = Span(element).Start + 1;
        while (!char.IsWhiteSpace(Text[i]) && Text[i] is not ('/' or '>'))
        {
            i++;
        }
        while (true)
        {
            while (char.IsWhiteSpace(Text[i]))
            {
                i++;
            }
            if (Text[i] is '/' or '>')
            {
                return null;
            }
            int named = i;
            while (!char.IsWhiteSpace(Text[i]) && Text[i] != '=')
            {
                i++;
            }
            bool wanted = Text.AsSpan(named, i - named).SequenceEqual(name);
            i = Text.IndexOf('=', i) + 1;
            while (char.IsWhiteSpace(Text[i]))
            {
                i++;
            }
            int value = i + 1;
            int shut = Text.IndexOf(Text[i], value);
            if (wanted)
            {
                return (value, shut - value);
            }
            i = shut + 1;
        }
    }

    /// <summary>Where an element's content is, between its tags, as written; null for one written short.</summary>
    public (int Start, int Length)? InnerOf(XElement element)
    {
        (int start, int end) = Span(element);
        int open = TagEnd(start);
        if (Text[open - 2] == '/')
        {
            return null;
        }
        int close = Text.LastIndexOf("</", end - 1, StringComparison.Ordinal);
        return (open, close - open);
    }

    /// <summary>An element's start tag as written, its &gt; or /&gt; aside.</summary>
    public string StartTag(XElement element)
    {
        int start = Span(element).Start;
        int open = TagEnd(start);
        return Text[start..(open - (Text[open - 2] == '/' ? 2 : 1))].TrimEnd();
    }

    public string Written((int Start, int Length) span) => Text.Substring(span.Start, span.Length);

    /// <summary>Makes several changes of the text as it reads now at once, each a span and what replaces it, none
    /// overlapping another.</summary>
    public void Apply(IEnumerable<(int Start, int Length, string With)> edits)
    {
        System.Text.StringBuilder changed = new(Text);
        foreach ((int start, int length, string with) in edits.OrderByDescending(e => e.Start))
        {
            changed.Remove(start, length).Insert(start, with);
        }
        Text = changed.ToString();
        Parse();
    }

    /// <summary>A value as quotes of a kind hold it, or text between tags (no quote): only what XML needs escaped is.</summary>
    public static string Quoted(string value, char quote = '\0')
    {
        string escaped = value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
        return quote switch
        {
            '"' => escaped.Replace("\"", "&quot;", StringComparison.Ordinal),
            '\'' => escaped.Replace("'", "&apos;", StringComparison.Ordinal),
            _ => escaped,
        };
    }

    /// <summary>Takes an element out, with its line when it stands on one of its own, and then with a blank line after it
    /// too for <paramref name="spaced"/> where one stands before it as well.</summary>
    public void Remove(XElement element, bool spaced = false)
    {
        (int start, int end) = Span(element);
        int from = start;
        while (from > 0 && Text[from - 1] is ' ' or '\t')
        {
            from--;
        }
        int to = end;
        while (to < Text.Length && Text[to] is ' ' or '\t')
        {
            to++;
        }
        if ((from == 0 || Text[from - 1] == '\n') && (to == Text.Length || Text[to] is '\r' or '\n'))
        {
            to += to < Text.Length && Text[to] == '\r' ? 1 : 0;
            to += to < Text.Length && Text[to] == '\n' ? 1 : 0;
            int previous = from >= 2 ? Text.LastIndexOf('\n', from - 2) + 1 : -1;
            int blank = Text.IndexOf('\n', to);
            if (spaced && previous >= 0 && Text[previous..(from - 1)].Trim().Length == 0 && blank >= 0 && Text[to..blank].Trim().Length == 0)
            {
                to = blank + 1;
            }
            Splice(from, to - from, string.Empty);
        }
        else
        {
            Splice(start, end - start, string.Empty);
        }
    }

    /// <summary>Writes an element as a parent's last child, on a line of its own at the indent given.</summary>
    private void Insert(XElement parent, string element, string indent)
    {
        (int start, int end) = Span(parent);
        int open = TagEnd(start);
        if (Text[open - 2] == '/')
        {
            // An empty element written short is written out, with the child inside it.
            string head = Text[start..(open - 2)].TrimEnd();
            Splice(start, end - start, $"{head}>{NewLine}{indent}{element}{NewLine}{Indent(parent)}</{parent.Name.LocalName}>");
            return;
        }
        if (parent.Elements().LastOrDefault() is { } last)
        {
            Splice(Span(last).End, 0, NewLine + indent + element);
            return;
        }
        int close = Text.LastIndexOf("</", end - 1, StringComparison.Ordinal);
        string inside = Text[open..close];
        Splice(open, close - open, string.IsNullOrWhiteSpace(inside)
            ? $"{NewLine}{indent}{element}{NewLine}{Indent(parent)}"
            : $"{inside.TrimEnd(' ', '\t')}{indent}{element}{NewLine}{Indent(parent)}");
    }

    private void Splice(int at, int length, string with)
    {
        Text = string.Concat(Text.AsSpan(0, at), with, Text.AsSpan(at + length));
        Parse();
    }

    private void Parse()
    {
        root = XDocument.Parse(Text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace).Root
            ?? throw new XmlException("The file has no root element");
        List<int> starts = [0];
        for (int i = 0; i < Text.Length; i++)
        {
            if (Text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }
        lines = [.. starts];
    }

    private bool StartsLine(int at)
    {
        int from = at;
        while (from > 0 && Text[from - 1] is ' ' or '\t')
        {
            from--;
        }
        return from == 0 || Text[from - 1] == '\n';
    }

    /// <summary>The name in the tag that starts at a place.</summary>
    private string TagName(int start)
    {
        int end = start + 1;
        while (!char.IsWhiteSpace(Text[end]) && Text[end] is not ('/' or '>'))
        {
            end++;
        }
        return Text[(start + 1)..end];
    }

    /// <summary>The whitespace the line holding a place begins with, never what stands on it before the place.</summary>
    private string LineIndent(int at)
    {
        int from = Text.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        int end = from;
        while (end < at && Text[end] is ' ' or '\t')
        {
            end++;
        }
        return Text[from..end];
    }

    /// <summary>The whitespace before an element on its line; empty when other text comes before it there.</summary>
    private string Indent(XElement element)
    {
        int start = Span(element).Start;
        int from = start;
        while (from > 0 && Text[from - 1] is ' ' or '\t')
        {
            from--;
        }
        return from == 0 || Text[from - 1] == '\n' ? Text[from..start] : string.Empty;
    }

    /// <summary>Where an element's text begins (its &lt;) and ends (after its end tag, or its /&gt;).</summary>
    private (int Start, int End) Span(XElement element)
    {
        IXmlLineInfo at = element;
        int start = lines[at.LineNumber - 1] + at.LinePosition - 1;
        while (start > 0 && Text[start] != '<')
        {
            start--;
        }
        int open = TagEnd(start);
        if (Text[open - 2] == '/')
        {
            return (start, open);
        }
        int depth = 1;
        int i = open;
        while (i < Text.Length)
        {
            ReadOnlySpan<char> rest = Text.AsSpan(i);
            if (rest.StartsWith("<!--"))
            {
                i = Text.IndexOf("-->", i, StringComparison.Ordinal) + 3;
            }
            else if (rest.StartsWith("<![CDATA["))
            {
                i = Text.IndexOf("]]>", i, StringComparison.Ordinal) + 3;
            }
            else if (rest.StartsWith("<?"))
            {
                i = Text.IndexOf("?>", i, StringComparison.Ordinal) + 2;
            }
            else if (rest.StartsWith("</"))
            {
                int closed = TagEnd(i);
                if (--depth == 0)
                {
                    return (start, closed);
                }
                i = closed;
            }
            else if (Text[i] == '<')
            {
                int tag = TagEnd(i);
                depth += Text[tag - 2] == '/' ? 0 : 1;
                i = tag;
            }
            else
            {
                i++;
            }
        }
        throw new XmlException($"The {element.Name.LocalName} element has no end");
    }

    /// <summary>The position after a tag's &gt;, past what its quoted attribute values hold.</summary>
    private int TagEnd(int start)
    {
        char? quote = null;
        for (int i = start + 1; i < Text.Length; i++)
        {
            char c = Text[i];
            if (quote is { } open)
            {
                quote = c == open ? null : quote;
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return i + 1;
            }
        }
        throw new XmlException("A tag has no end");
    }
}
