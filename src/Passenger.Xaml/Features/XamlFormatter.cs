using System.Text;
using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>What Reformat Code makes of a XAML file: the edits that lay it out (each a change of whitespace, but for the
/// attribute order and an empty element's end tag when those options ask), or why it was left as it is.</summary>
public sealed record XamlFormatted(IReadOnlyList<TextEdit> Edits, string? Problem = null);

/// <summary>Reformat Code on XAML: every element's line indented from its parent's, a start tag's attributes laid out as
/// Attributes format and Attributes indenting say (wrapped at the right margin), line breaks inside an element with
/// children, blank lines kept up to the most allowed; and, when asked, attributes in XAML Styler's order and an empty
/// element closed in its start tag. Only whitespace changes otherwise: names, values with their quotes and character
/// references, comments, CDATA and text are kept as written, text in an element with child elements is not touched, nor
/// anything under <c>xml:space="preserve"</c>. Markup that is not well-formed is left alone, saying where.</summary>
public static class XamlFormatter
{
    public static XamlFormatted Format(string text, XamlFormatSettings settings, IndentOptions indent, string eol, TextRange? within = null)
    {
        XmlTree tree = XmlTree.Parse(text);
        if (tree.Stopped is not null)
        {
            return new XamlFormatted([], "the XAML is nested too deeply for Passenger to read whole");
        }
        if (tree.Problems.Count > 0)
        {
            XmlProblem first = tree.Problems.OrderBy(p => p.Range.Start).First();
            int line = 1 + text.AsSpan(0, Math.Min(first.Range.Start, text.Length)).Count('\n');
            return new XamlFormatted([], $"the XAML is not well-formed: {first.Message} on line {line}");
        }
        Writer writer = new(text, settings, indent, eol);
        if (!writer.Document(tree))
        {
            return new XamlFormatted([], "the XAML holds markup Reformat Code does not read");
        }
        IEnumerable<TextEdit> edits = writer.Edits.OrderBy(e => e.Start);
        if (within is { } range)
        {
            int from = range.Start <= 0 ? 0 : text.LastIndexOf('\n', Math.Min(range.Start, text.Length) - 1) + 1;
            int end = range.End >= text.Length ? text.Length : text.IndexOf('\n', range.End) is var feed and >= 0 ? feed : text.Length;
            edits = edits.Select(e => Clipped(text, e, from, end)).OfType<TextEdit>();
        }
        return new XamlFormatted([.. edits]);
    }

    /// <summary>An edit kept for the lines from <paramref name="from"/> to <paramref name="end"/> alone: whole when it is
    /// in them, the first line's indentation when it is the break before them; null when it is outside.</summary>
    private static TextEdit? Clipped(string text, TextEdit edit, int from, int end)
    {
        if (edit.Start >= from && edit.End <= end)
        {
            return edit;
        }
        if (edit.Start < from && edit.End >= from && edit.End <= end && from > 0 && text[from - 1] == '\n' && text.AsSpan(edit.Start, from - edit.Start).IsWhiteSpace()
            && edit.Text.LastIndexOf('\n') is var feed and >= 0 && !text.AsSpan(from, edit.End - from).SequenceEqual(edit.Text.AsSpan(feed + 1)))
        {
            return new TextEdit(from, edit.End - from, edit.Text[(feed + 1)..]);
        }
        return null;
    }

    private enum ItemKind
    {
        Element,
        Comment,
        Text,
        Cdata,
        Instruction,
        Declaration,
    }

    /// <summary>One piece of an element's content (or of the document's top), where it stands.</summary>
    private readonly record struct Item(ItemKind Kind, int Start, int End, XmlElement? Element = null);

    private sealed class Writer(string text, XamlFormatSettings settings, IndentOptions indent, string eol)
    {
        private readonly AttributeOrder? order = settings.ReorderAttributes ? new AttributeOrder(settings.AttributeOrder) : null;

        public List<TextEdit> Edits { get; } = [];

        public bool Document(XmlTree tree)
        {
            if (Items(0, text.Length, tree.Roots) is not { } items)
            {
                return false;
            }
            Item? previous = null;
            foreach (Item item in items)
            {
                if (previous is { } before)
                {
                    int breaks = Breaks(before.End, item.Start);
                    int blank = Math.Min(Math.Max(0, breaks - 1), settings.MaxBlankLines);
                    if (before.Kind == ItemKind.Instruction && item.Kind != ItemKind.Instruction && settings.BlankLineAfterProcessingInstructions)
                    {
                        blank = Math.Max(blank, 1);
                    }
                    Replace(before.End, item.Start, Repeat(eol, blank + 1));
                }
                if (!Content(item, string.Empty, 0))
                {
                    return false;
                }
                previous = item;
            }
            return true;
        }

        /// <summary>Lays out one item placed at a line's indentation and a column: an element whole, anything else as written.</summary>
        private bool Content(Item item, string lineIndent, int column) =>
            item.Element is not { } element || Element(element, lineIndent, column);

        private bool Element(XmlElement element, string lineIndent, int column)
        {
            bool preserved = Preserves(element);
            int closer = element.IsSelfClosing ? element.StartTagEnd - 2 : element.StartTagEnd - 1;
            bool collapse = settings.CollapseEmptyElements && !element.IsSelfClosing && !preserved && element.EndTagStart is { } endTag
                && IsBlank(element.StartTagEnd, endTag);
            int afterName = column + 1 + element.Name.Length;
            int lastEnd = StartTag(element, lineIndent, afterName, collapse, out int tagEndColumn);
            if (collapse)
            {
                Replace(lastEnd, element.End, settings.SpaceBeforeSelfClosing ? " />" : "/>");
                return true;
            }
            string closing = element.IsSelfClosing ? (settings.SpaceBeforeSelfClosing ? " />" : "/>")
                : (settings.SpaceAfterLastAttribute && element.Attributes.Count > 0 ? " >" : ">");
            Replace(lastEnd, closer + (element.IsSelfClosing ? 2 : 1), closing);
            if (element.IsSelfClosing || element.EndTagStart is not { } end)
            {
                return true;
            }
            if (element.EndTagNameEnd is { } nameEnd)
            {
                Replace(nameEnd, element.End - 1, string.Empty);
            }
            if (preserved)
            {
                return true;
            }
            if (Items(element.StartTagEnd, end, element.Children) is not { } items)
            {
                return false;
            }
            if (items.Count == 0)
            {
                Replace(element.StartTagEnd, end, Breaks(element.StartTagEnd, end) > 0 ? eol + lineIndent : string.Empty);
                return true;
            }
            if (items.Any(i => i.Kind is ItemKind.Text or ItemKind.Cdata))
            {
                // Text alone is indented by its lines; text among elements, or CDATA, is the element's as written.
                if (items.All(i => i.Kind == ItemKind.Text))
                {
                    TextLines(element.StartTagEnd, end, lineIndent);
                }
                return true;
            }
            return Children(element, items, lineIndent, tagEndColumn + closing.Length);
        }

        /// <summary>The start tag's attributes laid out, and where its last token ends (its last attribute, or its name)
        /// for the closer's gap; <paramref name="column"/> is where its name ends.</summary>
        private int StartTag(XmlElement element, string lineIndent, int column, bool collapse, out int endColumn)
        {
            IReadOnlyList<XmlAttribute> attributes = element.Attributes;
            endColumn = column;
            if (attributes.Count == 0)
            {
                return element.NameEnd;
            }
            List<XmlAttribute> laid = order is null ? [.. attributes] : order.Sort(attributes);
            string closer = collapse || element.IsSelfClosing ? (settings.SpaceBeforeSelfClosing ? " />" : "/>") : settings.SpaceAfterLastAttribute ? " >" : ">";
            string? aligned = null;
            int at = column;
            List<(XmlAttribute Attribute, string Before)> written = [];
            for (int i = 0; i < laid.Count; i++)
            {
                XmlAttribute attribute = laid[i];
                string shown = Written(attribute);
                int firstLine = shown.IndexOf('\n') is var feed and >= 0 ? feed : shown.Length;
                bool broken = settings.AttributeStyle switch
                {
                    XamlAttributeStyle.OnSingleLine => false,
                    XamlAttributeStyle.FirstAttributeOnSingleLine => i > 0,
                    XamlAttributeStyle.OnDifferentLines => laid.Count > 1,
                    _ => Breaks(GapStart(attribute), attribute.NameStart) > 0,
                };
                int closing = i == laid.Count - 1 ? closer.Length : 0;
                if (!broken && i > 0 && settings.WrapTags && settings.RightMargin > 0 && at + 1 + firstLine + closing > settings.RightMargin)
                {
                    broken = true;
                }
                string before;
                if (broken)
                {
                    string continued = Continuation(lineIndent, i == 0 ? null : aligned);
                    before = eol + continued;
                    at = indent.Columns(continued);
                    if (i == 0)
                    {
                        aligned = continued;
                    }
                }
                else
                {
                    before = " ";
                    if (i == 0)
                    {
                        aligned = indent.Whitespace(at + 1);
                    }
                    at += 1;
                }
                at = firstLine < shown.Length ? shown.Length - shown.LastIndexOf('\n') - 1 : at + shown.Length;
                written.Add((attribute, before));
            }
            endColumn = at;
            if (order is not null && !laid.SequenceEqual(attributes))
            {
                StringBuilder all = new();
                foreach ((XmlAttribute attribute, string before) in written)
                {
                    all.Append(before).Append(Written(attribute));
                }
                Replace(element.NameEnd, attributes[^1].End, all.ToString());
                return attributes[^1].End;
            }
            foreach ((XmlAttribute attribute, string before) in written)
            {
                Replace(GapStart(attribute), attribute.NameStart, before);
                SpaceEquals(attribute);
            }
            return attributes[^1].End;
        }

        /// <summary>Where the whitespace before an attribute starts: after the attribute before it, or the element's name.</summary>
        private static int GapStart(XmlAttribute attribute)
        {
            IReadOnlyList<XmlAttribute> all = attribute.Owner.Attributes;
            int index = IndexOf(all, attribute);
            return index > 0 ? all[index - 1].End : attribute.Owner.NameEnd;
        }

        private static int IndexOf(IReadOnlyList<XmlAttribute> all, XmlAttribute attribute)
        {
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == attribute)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>The indentation of an attribute on a line of its own: Attributes indenting's, aligned with the first
        /// attribute where that one is on the tag's line.</summary>
        private string Continuation(string lineIndent, string? aligned) => settings.AttributeIndent switch
        {
            XamlAttributeIndent.SingleIndent => lineIndent + indent.Unit,
            XamlAttributeIndent.DoubleIndent => lineIndent + indent.Unit + indent.Unit,
            _ => aligned ?? lineIndent + indent.Unit,
        };

        /// <summary>An attribute as laid out: its name, its '=' spaced as asked, and its value with its quotes as written.</summary>
        private string Written(XmlAttribute attribute)
        {
            string equals = settings.SpacesAroundEquals ? " = " : "=";
            return attribute.Name + equals + text[(attribute.ValueStart - 1)..attribute.End];
        }

        /// <summary>The spaces around an attribute's '=' made as asked.</summary>
        private void SpaceEquals(XmlAttribute attribute)
        {
            int equals = text.IndexOf('=', attribute.NameEnd, attribute.ValueStart - attribute.NameEnd);
            if (equals < 0)
            {
                return;
            }
            string space = settings.SpacesAroundEquals ? " " : string.Empty;
            Replace(attribute.NameEnd, equals, space);
            Replace(equals + 1, attribute.ValueStart - 1, space);
        }

        /// <summary>An element's child elements and comments, each on its line where the options or the person put one.</summary>
        private bool Children(XmlElement element, IReadOnlyList<Item> items, string lineIndent, int column)
        {
            string? childIndent = settings.ChildIndent switch
            {
                XamlContentIndent.OneIndent => lineIndent + indent.Unit,
                XamlContentIndent.ZeroIndent => lineIndent,
                XamlContentIndent.RemoveIndent => string.Empty,
                _ => null,
            };
            bool hasElements = items.Any(i => i.Kind == ItemKind.Element);
            int end = element.EndTagStart ?? element.End;
            int inner = indent.Columns(childIndent ?? lineIndent + indent.Unit);
            // Whether the content will span lines once laid out, not as it is written, so a second Reformat changes nothing.
            bool multiline = (settings.KeepLineBreaks && Breaks(element.StartTagEnd, end) > 0) || (settings.SinglelineElementsOnNewLine && items.Count > 1)
                || items.Any(i => Spans(i, inner));
            int previousEnd = element.StartTagEnd;
            bool previousMultiline = false;
            string currentIndent = lineIndent;
            int at = column;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                int breaks = Breaks(previousEnd, item.Start);
                bool itemMultiline = Spans(item, inner);
                bool broken = i == 0
                    ? (breaks > 0 && settings.KeepLineBreaks) || (hasElements && settings.BreaksInsideElementsWithChildren) || (multiline && settings.BreaksInsideMultilineElements)
                    : (breaks > 0 && settings.KeepLineBreaks) || (itemMultiline ? settings.MultilineElementsOnNewLine : settings.SinglelineElementsOnNewLine)
                        || (previousMultiline && settings.MultilineElementsOnNewLine);
                if (!broken && settings.WrapAroundElements && settings.RightMargin > 0 && at + 1 + Width(item) > settings.RightMargin)
                {
                    broken = true;
                }
                if (broken)
                {
                    int blank = Math.Min(Math.Max(0, breaks - 1), settings.MaxBlankLines);
                    string before = childIndent ?? (breaks > 0 ? LastLine(previousEnd, item.Start) : lineIndent + indent.Unit);
                    int oldColumn = breaks > 0 ? indent.Columns(LastLine(previousEnd, item.Start)) : -1;
                    Replace(previousEnd, item.Start, Repeat(eol, blank + 1) + before);
                    currentIndent = before;
                    at = indent.Columns(before);
                    if (item.Kind == ItemKind.Comment && oldColumn >= 0)
                    {
                        Shift(item.Start, item.End, at - oldColumn);
                    }
                }
                else
                {
                    string between = breaks > 0 || item.Start == previousEnd ? string.Empty : " ";
                    Replace(previousEnd, item.Start, between);
                    at += between.Length;
                }
                if (item.Element is { } child && !Element(child, currentIndent, at))
                {
                    return false;
                }
                at = Breaks(item.Start, item.End) > 0 ? indent.Columns(currentIndent) + LastLineLength(item.Start, item.End) : at + Width(item);
                previousEnd = item.End;
                previousMultiline = itemMultiline;
            }
            int closingBreaks = Breaks(previousEnd, end);
            bool closingBroken = (closingBreaks > 0 && settings.KeepLineBreaks) || (hasElements && settings.BreaksInsideElementsWithChildren)
                || (multiline && settings.BreaksInsideMultilineElements);
            Replace(previousEnd, end, closingBroken ? eol + lineIndent : string.Empty);
            return true;
        }

        /// <summary>Whether an item will span lines once laid out, from its structure and the options rather than its line
        /// breaks as written (but where the options keep those): a start tag broken over lines, content with an element,
        /// text over lines; <paramref name="column"/> is where it would start, for the right margin.</summary>
        private bool Spans(Item item, int column)
        {
            if (item.Element is not { } element)
            {
                return Breaks(item.Start, item.End) > 0;
            }
            if (spans.TryGetValue(element, out bool known))
            {
                return known;
            }
            bool spanning = TagSpans(element, column) || ContentSpans(element, column + indent.Size);
            spans[element] = spanning;
            return spanning;
        }

        private readonly Dictionary<XmlElement, bool> spans = [];

        /// <summary>How wide an item is on one line once laid out: an element's start tag as its attributes are written,
        /// then its content and end tag as written; anything else's first line.</summary>
        private int Width(Item item)
        {
            if (item.Element is not { } element)
            {
                int feed = text.IndexOf('\n', item.Start, item.End - item.Start);
                return (feed < 0 ? item.End : feed) - item.Start;
            }
            int tag = 1 + element.Name.Length + element.Attributes.Sum(a => 1 + Written(a).Length)
                + (element.IsSelfClosing ? (settings.SpaceBeforeSelfClosing ? 3 : 2) : 1);
            return element.IsSelfClosing || element.EndTagStart is not { } end ? tag : tag + (element.End - element.StartTagEnd);
        }

        private bool TagSpans(XmlElement element, int column)
        {
            IReadOnlyList<XmlAttribute> attributes = element.Attributes;
            if (attributes.Any(a => a.RawValue.Contains('\n', StringComparison.Ordinal)))
            {
                return true;
            }
            if (attributes.Count > 1 && settings.AttributeStyle is XamlAttributeStyle.FirstAttributeOnSingleLine or XamlAttributeStyle.OnDifferentLines)
            {
                return true;
            }
            if (settings.AttributeStyle == XamlAttributeStyle.DoNotTouch && attributes.Any(a => Breaks(GapStart(a), a.NameStart) > 0))
            {
                return true;
            }
            int length = column + 1 + element.Name.Length + attributes.Sum(a => 1 + Written(a).Length) + 3;
            return settings.WrapTags && settings.RightMargin > 0 && attributes.Count > 1 && length > settings.RightMargin;
        }

        private bool ContentSpans(XmlElement element, int column)
        {
            if (element.IsSelfClosing || element.EndTagStart is not { } end)
            {
                return false;
            }
            if (Preserves(element) || Items(element.StartTagEnd, end, element.Children) is not { } items || items.Any(i => i.Kind is ItemKind.Text or ItemKind.Cdata))
            {
                return Breaks(element.StartTagEnd, end) > 0;
            }
            if (items.Count == 0)
            {
                return !(settings.CollapseEmptyElements && !Preserves(element)) && Breaks(element.StartTagEnd, end) > 0;
            }
            return (items.Any(i => i.Kind == ItemKind.Element) && settings.BreaksInsideElementsWithChildren) || (settings.KeepLineBreaks && Breaks(element.StartTagEnd, end) > 0)
                || (settings.SinglelineElementsOnNewLine && items.Count > 1) || items.Any(i => Spans(i, column));
        }

        /// <summary>An element's text over several lines: each line's indentation Indent inside element that contains text
        /// says, the end tag's line its element's, a blank line left empty.</summary>
        private void TextLines(int start, int end, string lineIndent)
        {
            string? inside = settings.TextIndent switch
            {
                XamlContentIndent.OneIndent => lineIndent + indent.Unit,
                XamlContentIndent.ZeroIndent => lineIndent,
                XamlContentIndent.RemoveIndent => string.Empty,
                _ => null,
            };
            if (inside is null)
            {
                return;
            }
            for (int feed = text.IndexOf('\n', start); feed >= 0 && feed < end; feed = text.IndexOf('\n', feed + 1))
            {
                int lineStart = feed + 1;
                int first = lineStart;
                while (first < end && text[first] is ' ' or '\t')
                {
                    first++;
                }
                if (first == end)
                {
                    Replace(lineStart, first, lineIndent);
                }
                else if (text[first] is '\r' or '\n')
                {
                    Replace(lineStart, first, string.Empty);
                }
                else
                {
                    Replace(lineStart, first, inside);
                }
            }
        }

        /// <summary>A comment's lines after its first moved as far as its first was, so it keeps its shape.</summary>
        private void Shift(int start, int end, int delta)
        {
            if (delta == 0)
            {
                return;
            }
            for (int feed = text.IndexOf('\n', start); feed >= 0 && feed < end; feed = text.IndexOf('\n', feed + 1))
            {
                int first = feed + 1;
                while (first < end && text[first] is ' ' or '\t')
                {
                    first++;
                }
                if (first < end && text[first] is not ('\r' or '\n'))
                {
                    Replace(feed + 1, first, indent.Whitespace(Math.Max(0, indent.Columns(text.AsSpan(feed + 1, first - feed - 1)) + delta)));
                }
            }
        }

        /// <summary>A range's pieces: elements (from the tree's), comments, CDATA, processing instructions, declarations and
        /// text, the whitespace between them left out; null where something there is none of these.</summary>
        private List<Item>? Items(int start, int end, IReadOnlyList<XmlElement> elements)
        {
            List<Item> items = [];
            int next = 0;
            int at = start;
            while (at < end)
            {
                while (at < end && char.IsWhiteSpace(text[at]))
                {
                    at++;
                }
                if (at >= end)
                {
                    break;
                }
                if (next < elements.Count && elements[next].Start == at)
                {
                    items.Add(new Item(ItemKind.Element, at, elements[next].End, elements[next]));
                    at = elements[next].End;
                    next++;
                    continue;
                }
                (ItemKind kind, string open, string close) = text[at] != '<' ? (ItemKind.Text, string.Empty, string.Empty)
                    : Starts(at, "<!--") ? (ItemKind.Comment, "<!--", "-->")
                    : Starts(at, "<![CDATA[") ? (ItemKind.Cdata, "<![CDATA[", "]]>")
                    : Starts(at, "<?") ? (ItemKind.Instruction, "<?", "?>")
                    : Starts(at, "<!") ? (ItemKind.Declaration, "<!", ">")
                    : (ItemKind.Element, string.Empty, string.Empty);
                if (kind == ItemKind.Element)
                {
                    return null;
                }
                if (kind == ItemKind.Text)
                {
                    int lt = text.IndexOf('<', at, end - at);
                    int stop = lt < 0 ? end : lt;
                    int last = stop;
                    while (last > at && char.IsWhiteSpace(text[last - 1]))
                    {
                        last--;
                    }
                    items.Add(new Item(ItemKind.Text, at, last));
                    at = stop;
                    continue;
                }
                int found = text.IndexOf(close, at + open.Length, StringComparison.Ordinal);
                if (found < 0 || found + close.Length > end)
                {
                    return null;
                }
                items.Add(new Item(kind, at, found + close.Length));
                at = found + close.Length;
            }
            return next == elements.Count ? items : null;
        }

        private bool Starts(int at, string what) => string.CompareOrdinal(text, at, what, 0, what.Length) == 0;

        /// <summary>Whether an element or one it is in keeps its whitespace (<c>xml:space="preserve"</c>).</summary>
        private static bool Preserves(XmlElement element)
        {
            for (XmlElement? at = element; at is not null; at = at.Parent)
            {
                if (at.Attribute("xml:space") is { } space)
                {
                    return space.Value.Trim() == "preserve";
                }
            }
            return false;
        }

        private int Breaks(int start, int end)
        {
            int count = 0;
            for (int i = start; i < end; i++)
            {
                if (text[i] == '\n')
                {
                    count++;
                }
            }
            return count;
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

        /// <summary>A gap's whitespace after its last line break: the indentation its next line has.</summary>
        private string LastLine(int start, int end)
        {
            int feed = end > start ? text.LastIndexOf('\n', end - 1, end - start) : -1;
            return feed < 0 ? string.Empty : text[(feed + 1)..end];
        }

        /// <summary>How long a multiline item's last line is past its indentation, which its own layout sets.</summary>
        private int LastLineLength(int start, int end)
        {
            int feed = end > start ? text.LastIndexOf('\n', end - 1, end - start) : -1;
            int first = feed + 1;
            while (first < end && text[first] is ' ' or '\t')
            {
                first++;
            }
            return end - first;
        }

        private static string Repeat(string what, int times) => times == 1 ? what : string.Concat(Enumerable.Repeat(what, times));

        private void Replace(int start, int end, string with)
        {
            if (end < start)
            {
                return;
            }
            if (end - start == with.Length && string.CompareOrdinal(text, start, with, 0, with.Length) == 0)
            {
                return;
            }
            Edits.Add(new TextEdit(start, end - start, with));
        }
    }

    /// <summary>XAML Styler's attribute ordering rule groups read: where each attribute goes, by the rule that names it
    /// most nearly (a name, then <c>Prefix.*</c> or <c>xmlns:*</c>, then <c>*:*</c>, then <c>*</c>), the first such
    /// group's, its order among the others there kept.</summary>
    private sealed class AttributeOrder
    {
        private readonly List<(int Group, int Position, string Rule)> rules = [];

        public AttributeOrder(string groups)
        {
            string[] split = groups.Split([';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            for (int group = 0; group < split.Length; group++)
            {
                string[] named = split[group].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                for (int position = 0; position < named.Length; position++)
                {
                    rules.Add((group, position, named[position]));
                }
            }
        }

        public List<XmlAttribute> Sort(IReadOnlyList<XmlAttribute> attributes) =>
            [.. attributes.Select((a, i) => (Attribute: a, Index: i, Rank: Rank(a.Name)))
                .OrderBy(x => x.Rank.Group).ThenBy(x => x.Rank.Position).ThenBy(x => x.Index).Select(x => x.Attribute)];

        private (int Group, int Position) Rank(string name)
        {
            (int Group, int Position, int Score) best = (int.MaxValue, 0, -1);
            foreach ((int group, int position, string rule) in rules)
            {
                int score = Score(rule, name);
                if (score > best.Score)
                {
                    best = (group, position, score);
                }
            }
            return best.Score < 0 ? (int.MaxValue, 0) : (best.Group, best.Position);
        }

        private static int Score(string rule, string name)
        {
            if (rule == name)
            {
                return 3;
            }
            if (rule == "*")
            {
                return 0;
            }
            if (rule == "*:*")
            {
                return name.Contains(':', StringComparison.Ordinal) ? 1 : -1;
            }
            if (rule.EndsWith('*') && name.StartsWith(rule[..^1], StringComparison.Ordinal))
            {
                return 2;
            }
            return -1;
        }
    }
}
