using Passenger.Text;

namespace Passenger.Xaml.Syntax;

/// <summary>A XAML file's markup as written, every name and value with where it stands, read in one pass and never
/// refused: a tag left open while it is typed, a value without its closing quote, an end tag with no start are each
/// taken as far as they go and said in <see cref="Problems"/>, so completion and the diagnostics read a file mid-edit.
/// Immutable once read, so any thread may ask it.</summary>
public sealed class XmlTree
{
    private readonly List<XmlElement> elements;
    private readonly List<TextRange> comments;

    private XmlTree(string text, List<XmlElement> roots, List<XmlElement> elements, List<TextRange> comments, List<XmlProblem> problems)
    {
        Text = text;
        Roots = roots;
        this.elements = elements;
        this.comments = comments;
        Problems = problems;
    }

    public string Text { get; }

    /// <summary>The elements at the top, the root first (a well-formed file has one).</summary>
    public IReadOnlyList<XmlElement> Roots { get; }

    public XmlElement? Root => Roots.Count > 0 ? Roots[0] : null;

    /// <summary>Every element, in the order its start tag is written.</summary>
    public IReadOnlyList<XmlElement> Elements => elements;

    public IReadOnlyList<TextRange> Comments => comments;

    /// <summary>Where the markup is not well-formed, in a person's words.</summary>
    public IReadOnlyList<XmlProblem> Problems { get; }

    public static XmlTree Parse(string text) => new Reader(text).Read();

    /// <summary>Whether an offset is inside a comment (its <c>&lt;!--</c> and <c>--&gt;</c> included).</summary>
    public bool InComment(int offset)
    {
        int low = 0;
        int high = comments.Count - 1;
        int before = -1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (comments[middle].Start < offset)
            {
                before = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        if (before < 0)
        {
            return false;
        }
        TextRange comment = comments[before];
        // One left open runs to the end, where a caret is still in it.
        bool open = comment.End == Text.Length && !Text.EndsWith("-->", StringComparison.Ordinal);
        return offset < comment.End || (open && offset == comment.End);
    }

    /// <summary>The innermost element whose tags or content hold an offset: one a caret just after <c>&lt;</c> is in
    /// counts, one whose end tag's <c>&gt;</c> it is after does not.</summary>
    public XmlElement? ElementAt(int offset)
    {
        XmlElement? found = null;
        IReadOnlyList<XmlElement> level = Roots;
        while (true)
        {
            XmlElement? next = null;
            int low = 0;
            int high = level.Count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                XmlElement element = level[middle];
                if (offset <= element.Start)
                {
                    high = middle - 1;
                }
                else if (offset > element.End || (offset == element.End && element.IsClosed))
                {
                    low = middle + 1;
                }
                else
                {
                    next = element;
                    break;
                }
            }
            if (next is null)
            {
                return found;
            }
            found = next;
            level = next.Children;
        }
    }

    /// <summary>What an offset is in: a tag's name, an attribute's name or value, the space between a start tag's
    /// attributes, an end tag, a comment, or an element's content.</summary>
    public XmlPlace PlaceAt(int offset)
    {
        if (InComment(offset))
        {
            return new XmlPlace(XmlPlaceKind.Comment, ElementAt(offset), null);
        }
        XmlElement? element = ElementAt(offset);
        if (element is null)
        {
            return new XmlPlace(XmlPlaceKind.Content, null, null);
        }
        if (offset >= element.NameStart && offset <= element.NameEnd)
        {
            return new XmlPlace(XmlPlaceKind.StartTagName, element, null);
        }
        if (element.EndTagNameStart is { } endName && offset >= endName - 1 && offset <= element.EndTagNameEnd)
        {
            return new XmlPlace(XmlPlaceKind.EndTagName, element, null);
        }
        if (offset < element.StartTagEnd || (!element.IsStartTagClosed && offset <= element.StartTagEnd))
        {
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (offset >= attribute.NameStart && offset <= attribute.NameEnd)
                {
                    return new XmlPlace(XmlPlaceKind.AttributeName, element, attribute);
                }
                if (attribute.HasValue && attribute.Quote != '\0' && offset >= attribute.ValueStart && offset <= attribute.ValueEnd)
                {
                    return new XmlPlace(XmlPlaceKind.AttributeValue, element, attribute);
                }
            }
            return new XmlPlace(XmlPlaceKind.InStartTag, element, null);
        }
        return new XmlPlace(XmlPlaceKind.Content, element, null);
    }

    /// <summary>The single pass over the text.</summary>
    private sealed class Reader(string text)
    {
        private readonly List<XmlElement> roots = [];
        private readonly List<XmlElement> elements = [];
        private readonly List<TextRange> comments = [];
        private readonly List<XmlProblem> problems = [];
        private readonly List<XmlElement> open = [];
        private int at;

        public XmlTree Read()
        {
            int length = text.Length;
            while (at < length)
            {
                int lt = text.IndexOf('<', at);
                if (lt < 0)
                {
                    AddText(at, length);
                    at = length;
                    break;
                }
                AddText(at, lt);
                at = lt;
                if (StartsWith("<!--"))
                {
                    Comment();
                }
                else if (StartsWith("<![CDATA["))
                {
                    int end = text.IndexOf("]]>", at, StringComparison.Ordinal);
                    int stop = end < 0 ? length : end + 3;
                    if (open.Count > 0)
                    {
                        open[^1].AddText(new XmlText(at + 9, Math.Max(at + 9, end < 0 ? length : end), IsCdata: true));
                    }
                    if (end < 0)
                    {
                        problems.Add(new XmlProblem(TextRange.FromBounds(at, at + 9), "CDATA section is not closed"));
                    }
                    at = stop;
                }
                else if (StartsWith("<?") || StartsWith("<!"))
                {
                    int end = text.IndexOf('>', at);
                    at = end < 0 ? length : end + 1;
                }
                else if (StartsWith("</"))
                {
                    EndTag();
                }
                else
                {
                    StartTag();
                }
            }
            foreach (XmlElement unclosed in open)
            {
                unclosed.End = length;
                problems.Add(new XmlProblem(unclosed.NameRange, $"Element '{unclosed.Name}' is not closed"));
            }
            return new XmlTree(text, roots, elements, comments, problems);
        }

        private bool StartsWith(string what) => string.CompareOrdinal(text, at, what, 0, what.Length) == 0;

        private void AddText(int start, int end)
        {
            if (end <= start || open.Count == 0)
            {
                if (end > start && open.Count == 0 && !IsBlank(start, end) && roots.Count > 0)
                {
                    problems.Add(new XmlProblem(Trimmed(start, end), "Text is outside the root element"));
                }
                return;
            }
            if (!IsBlank(start, end))
            {
                open[^1].AddText(new XmlText(start, end, IsCdata: false));
            }
        }

        private bool IsBlank(int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return false;
                }
            }
            return true;
        }

        private TextRange Trimmed(int start, int end)
        {
            while (start < end && char.IsWhiteSpace(text[start]))
            {
                start++;
            }
            while (end > start && char.IsWhiteSpace(text[end - 1]))
            {
                end--;
            }
            return TextRange.FromBounds(start, end);
        }

        private void Comment()
        {
            int end = text.IndexOf("-->", at + 4, StringComparison.Ordinal);
            if (end < 0)
            {
                comments.Add(TextRange.FromBounds(at, text.Length));
                problems.Add(new XmlProblem(TextRange.FromBounds(at, at + 4), "Comment is not closed"));
                at = text.Length;
                return;
            }
            comments.Add(TextRange.FromBounds(at, end + 3));
            at = end + 3;
        }

        private void StartTag()
        {
            int start = at;
            at++;
            int nameStart = at;
            SkipName();
            XmlElement element = new(text.Substring(nameStart, at - nameStart), start, nameStart, at)
            {
                Parent = open.Count > 0 ? open[^1] : null,
            };
            if (element.Name.Length == 0)
            {
                problems.Add(new XmlProblem(TextRange.FromBounds(start, start + 1), "An element's name is missing"));
            }
            elements.Add(element);
            if (open.Count > 0)
            {
                open[^1].AddChild(element);
            }
            else
            {
                if (roots.Count > 0 && element.Name.Length > 0)
                {
                    problems.Add(new XmlProblem(element.NameRange, "A XAML file has one root element"));
                }
                roots.Add(element);
            }
            Attributes(element);
            if (StartsWith("/>"))
            {
                at += 2;
                element.Close(startTagEnd: at, selfClosing: true, end: at);
                return;
            }
            if (at < text.Length && text[at] == '>')
            {
                at++;
                element.StartTagEnd = at;
                element.IsStartTagClosed = true;
                open.Add(element);
                return;
            }
            // A start tag left open (a '<' or the end met first): taken as empty, so what follows stays its sibling.
            element.Close(startTagEnd: at, selfClosing: false, end: at, startTagClosed: false);
            if (element.Name.Length > 0)
            {
                problems.Add(new XmlProblem(element.NameRange, $"The start tag of '{element.Name}' is not closed"));
            }
        }

        private void Attributes(XmlElement element)
        {
            while (true)
            {
                SkipSpace();
                if (at >= text.Length || text[at] is '>' or '<' || StartsWith("/>"))
                {
                    return;
                }
                int nameStart = at;
                SkipName();
                if (at == nameStart)
                {
                    // A character no name has (a stray quote, a '/'), stepped over and said once.
                    problems.Add(new XmlProblem(new TextRange(at, 1), $"Unexpected '{text[at]}' in the tag of '{element.Name}'"));
                    at++;
                    continue;
                }
                XmlAttribute attribute = new(element, text.Substring(nameStart, at - nameStart), nameStart, at);
                int afterName = at;
                SkipSpace();
                if (at < text.Length && text[at] == '=')
                {
                    at++;
                    SkipSpace();
                    if (at < text.Length && text[at] is '"' or '\'')
                    {
                        char quote = text[at];
                        int valueStart = at + 1;
                        int close = text.IndexOf(quote, valueStart);
                        int lt = text.IndexOf('<', valueStart);
                        if (close >= 0 && (lt < 0 || close < lt))
                        {
                            attribute.SetValue(quote, valueStart, close, closed: true, text);
                            at = close + 1;
                        }
                        else
                        {
                            int end = lt < 0 ? text.Length : lt;
                            // Back off the line breaks before the next tag, so an unclosed value ends with its line.
                            int lineEnd = text.IndexOf('\n', valueStart);
                            if (lineEnd >= 0 && lineEnd < end)
                            {
                                end = lineEnd;
                            }
                            attribute.SetValue(quote, valueStart, end, closed: false, text);
                            problems.Add(new XmlProblem(attribute.NameRange, $"The value of '{attribute.Name}' has no closing quote"));
                            at = end;
                        }
                    }
                    else
                    {
                        attribute.SetValue('\0', at, at, closed: false, text);
                        problems.Add(new XmlProblem(attribute.NameRange, $"The value of '{attribute.Name}' must be in quotes"));
                    }
                }
                else
                {
                    at = afterName;
                    problems.Add(new XmlProblem(attribute.NameRange, $"'{attribute.Name}' has no value"));
                }
                if (element.Attributes.Any(a => a.Name == attribute.Name))
                {
                    problems.Add(new XmlProblem(attribute.NameRange, $"'{attribute.Name}' is set twice"));
                }
                element.AddAttribute(attribute);
            }
        }

        private void EndTag()
        {
            int start = at;
            at += 2;
            int nameStart = at;
            SkipName();
            string name = text.Substring(nameStart, at - nameStart);
            int nameEnd = at;
            SkipSpace();
            bool closed = at < text.Length && text[at] == '>';
            if (closed)
            {
                at++;
            }
            int match = open.FindLastIndex(e => e.Name == name);
            if (match < 0)
            {
                problems.Add(new XmlProblem(nameEnd > nameStart ? TextRange.FromBounds(nameStart, nameEnd) : TextRange.FromBounds(start, start + 2),
                    name.Length > 0 ? $"End tag '{name}' has no start tag" : "An end tag's name is missing"));
                return;
            }
            for (int i = open.Count - 1; i > match; i--)
            {
                XmlElement unclosed = open[i];
                unclosed.End = start;
                problems.Add(new XmlProblem(unclosed.NameRange, $"Element '{unclosed.Name}' is not closed"));
            }
            XmlElement element = open[match];
            open.RemoveRange(match, open.Count - match);
            element.EndTagStart = start;
            element.EndTagNameStart = nameStart;
            element.EndTagNameEnd = nameEnd;
            element.End = at;
            element.IsClosed = closed;
            if (!closed)
            {
                problems.Add(new XmlProblem(TextRange.FromBounds(nameStart, nameEnd), $"The end tag of '{name}' is not closed"));
            }
        }

        private void SkipName()
        {
            while (at < text.Length && MarkupNames.IsNameChar(text[at]))
            {
                at++;
            }
        }

        private void SkipSpace()
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }
        }
    }
}

/// <summary>An element: its name as written (<c>prefix:Name</c>, or <c>Owner.Property</c> for a property element), its
/// attributes, children and content, and where each of its tags stands.</summary>
public sealed class XmlElement
{
    private List<XmlAttribute>? attributes;
    private List<XmlElement>? children;
    private List<XmlText>? texts;

    internal XmlElement(string name, int start, int nameStart, int nameEnd)
    {
        Name = name;
        Start = start;
        NameStart = nameStart;
        NameEnd = nameEnd;
        StartTagEnd = nameEnd;
        End = nameEnd;
    }

    public XmlElement? Parent { get; internal set; }

    public string Name { get; }

    /// <summary>The name's prefix (<c>x</c> of <c>x:String</c>), empty for none.</summary>
    public string Prefix => Name.IndexOf(':') is var colon and >= 0 ? Name[..colon] : string.Empty;

    /// <summary>The name after its prefix.</summary>
    public string LocalName => Name.IndexOf(':') is var colon and >= 0 ? Name[(colon + 1)..] : Name;

    /// <summary>Whether it sets a property of its parent's (<c>&lt;Grid.RowDefinitions&gt;</c>) rather than make an object.</summary>
    public bool IsPropertyElement => LocalName.Contains('.', StringComparison.Ordinal);

    /// <summary>The offset of its <c>&lt;</c>.</summary>
    public int Start { get; }

    public int NameStart { get; }

    public int NameEnd { get; }

    public TextRange NameRange => TextRange.FromBounds(NameStart, NameEnd);

    /// <summary>After the start tag's <c>&gt;</c> or <c>/&gt;</c>; where it stopped when the tag was never closed.</summary>
    public int StartTagEnd { get; internal set; }

    public bool IsStartTagClosed { get; internal set; }

    public bool IsSelfClosing { get; private set; }

    /// <summary>After its end tag (or <c>/&gt;</c>), or where the markup stopped holding it.</summary>
    public int End { get; internal set; }

    /// <summary>Whether its markup ends as written (a self-closing tag, or an end tag with its <c>&gt;</c>).</summary>
    public bool IsClosed { get; internal set; }

    public int? EndTagStart { get; internal set; }

    public int? EndTagNameStart { get; internal set; }

    public int? EndTagNameEnd { get; internal set; }

    public TextRange? EndTagNameRange => EndTagNameStart is { } start && EndTagNameEnd is { } end ? TextRange.FromBounds(start, end) : null;

    public IReadOnlyList<XmlAttribute> Attributes => attributes ?? (IReadOnlyList<XmlAttribute>)[];

    public IReadOnlyList<XmlElement> Children => children ?? (IReadOnlyList<XmlElement>)[];

    /// <summary>Its content's text, each run between child elements that is more than white space.</summary>
    public IReadOnlyList<XmlText> Texts => texts ?? (IReadOnlyList<XmlText>)[];

    /// <summary>Where its content starts and ends (empty for a self-closing element).</summary>
    public TextRange Content => IsSelfClosing || !IsStartTagClosed ? new TextRange(StartTagEnd, 0) : TextRange.FromBounds(StartTagEnd, EndTagStart ?? End);

    public XmlAttribute? Attribute(string name)
    {
        foreach (XmlAttribute attribute in Attributes)
        {
            if (attribute.Name == name)
            {
                return attribute;
            }
        }
        return null;
    }

    /// <summary>Its ancestors, its parent first.</summary>
    public IEnumerable<XmlElement> Ancestors()
    {
        for (XmlElement? parent = Parent; parent is not null; parent = parent.Parent)
        {
            yield return parent;
        }
    }

    /// <summary>It and everything under it, in document order.</summary>
    public IEnumerable<XmlElement> DescendantsAndSelf()
    {
        yield return this;
        foreach (XmlElement child in Children)
        {
            foreach (XmlElement below in child.DescendantsAndSelf())
            {
                yield return below;
            }
        }
    }

    public override string ToString() => $"<{Name}> at {Start}";

    internal void AddAttribute(XmlAttribute attribute) => (attributes ??= []).Add(attribute);

    internal void AddChild(XmlElement child) => (children ??= []).Add(child);

    internal void AddText(XmlText text) => (texts ??= []).Add(text);

    internal void Close(int startTagEnd, bool selfClosing, int end, bool startTagClosed = true)
    {
        StartTagEnd = startTagEnd;
        IsStartTagClosed = startTagClosed;
        IsSelfClosing = selfClosing;
        End = end;
        IsClosed = selfClosing;
    }
}

/// <summary>An attribute: its name as written (<c>xmlns:vm</c>, <c>Grid.Row</c>, <c>x:Name</c>), and its value both as
/// written between the quotes and with XML's character references read.</summary>
public sealed class XmlAttribute
{
    internal XmlAttribute(XmlElement owner, string name, int nameStart, int nameEnd)
    {
        Owner = owner;
        Name = name;
        NameStart = nameStart;
        NameEnd = nameEnd;
    }

    public XmlElement Owner { get; }

    public string Name { get; }

    public string Prefix => Name.IndexOf(':') is var colon and >= 0 ? Name[..colon] : string.Empty;

    public string LocalName => Name.IndexOf(':') is var colon and >= 0 ? Name[(colon + 1)..] : Name;

    public int NameStart { get; }

    public int NameEnd { get; }

    public TextRange NameRange => TextRange.FromBounds(NameStart, NameEnd);

    public bool HasValue { get; private set; }

    public char Quote { get; private set; }

    /// <summary>After the opening quote.</summary>
    public int ValueStart { get; private set; }

    /// <summary>At the closing quote, or where an unclosed value stops.</summary>
    public int ValueEnd { get; private set; }

    public TextRange ValueRange => TextRange.FromBounds(ValueStart, ValueEnd);

    public bool IsValueClosed { get; private set; }

    /// <summary>The value between the quotes as written.</summary>
    public string RawValue { get; private set; } = string.Empty;

    /// <summary>The value as XAML reads it, its character references (<c>&amp;gt;</c>) replaced.</summary>
    public string Value { get; private set; } = string.Empty;

    /// <summary>Where the whole attribute ends: after its closing quote, or its value's end.</summary>
    public int End => HasValue ? (IsValueClosed ? ValueEnd + 1 : ValueEnd) : NameEnd;

    internal void SetValue(char quote, int start, int end, bool closed, string text)
    {
        HasValue = true;
        Quote = quote;
        ValueStart = start;
        ValueEnd = end;
        IsValueClosed = closed;
        RawValue = text.Substring(start, end - start);
        Value = XmlEntities.Decode(RawValue);
    }

    public override string ToString() => $"{Name}=\"{RawValue}\"";
}

/// <summary>A run of an element's content that is more than white space, or a CDATA section's.</summary>
public readonly record struct XmlText(int Start, int End, bool IsCdata)
{
    public TextRange Range => TextRange.FromBounds(Start, End);
}

/// <summary>Where the markup is not well-formed.</summary>
public sealed record XmlProblem(TextRange Range, string Message);

/// <summary>What an offset is in.</summary>
public enum XmlPlaceKind
{
    Content,
    Comment,
    StartTagName,
    EndTagName,
    InStartTag,
    AttributeName,
    AttributeValue,
}

public readonly record struct XmlPlace(XmlPlaceKind Kind, XmlElement? Element, XmlAttribute? Attribute);
