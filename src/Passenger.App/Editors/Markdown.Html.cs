using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace Passenger.App.Editors;

/// <summary>The HTML GitHub draws in a README, read into Markdown's blocks and words: an HTML block's lines as GFM ends
/// them, and a tag in a paragraph's words. What is read: <c>p</c>, <c>div</c> and <c>h1</c> to <c>h6</c> with their
/// <c>align</c>, <c>a</c> with an <c>href</c>, <c>img</c> (<c>src</c>, <c>alt</c>, <c>width</c>, <c>height</c>),
/// <c>picture</c> with its <c>source</c>s for the light look and the dark, <c>br</c>, <c>hr</c>, <c>details</c> and its
/// <c>summary</c>, <c>b</c>, <c>strong</c>, <c>i</c>, <c>em</c>, <c>code</c> and <c>kbd</c>; every other tag is its words
/// as written. A <c>details</c> its block leaves open takes the blocks after it, Markdown's too, up to the line that closes
/// it; a <c>div</c> or <c>p</c> takes none, so the headings after it stay the page's, which its outline and anchors read.</summary>
internal static partial class Markdown
{
    /// <summary>GFM's seventh kind of HTML block: a whole tag alone on its line, which cannot end a paragraph.</summary>
    private const int LoneTag = 7;

    /// <summary>The tags GFM's sixth kind of HTML block begins with.</summary>
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "base", "basefont", "blockquote", "body", "caption", "center", "col", "colgroup", "dd",
        "details", "dialog", "dir", "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form", "frame",
        "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hr", "html", "iframe", "legend", "li", "link",
        "main", "menu", "menuitem", "nav", "noframes", "ol", "optgroup", "option", "p", "param", "section", "source",
        "summary", "table", "tbody", "td", "tfoot", "th", "thead", "title", "tr", "track", "ul",
    };

    /// <summary>The tags of GFM's first kind of HTML block, which runs to the line that closes it.</summary>
    private static readonly string[] RawTags = ["script", "pre", "style", "textarea"];

    /// <summary>HTML's elements with no content and no end tag.</summary>
    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>The tags a <c>p</c> left open ends at, as HTML ends it.</summary>
    private static readonly HashSet<string> EndParagraph = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "details", "div", "dl", "fieldset", "figcaption", "figure", "footer",
        "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "main", "menu", "nav", "ol", "p", "pre", "section",
        "table", "ul",
    };

    /// <summary>The tags read inside words; every other is its words as written.</summary>
    private static readonly HashSet<string> WordTags = new(StringComparer.Ordinal)
    {
        "a", "img", "picture", "br", "b", "strong", "i", "em", "code", "kbd",
    };

    private static readonly HashSet<string> DocumentationTags = new(StringComparer.Ordinal)
    {
        "a", "b", "strong", "i", "em", "see", "seealso", "c", "code", "para", "br",
        "paramref", "typeparamref", "list", "listheader", "item", "term", "description",
    };

    /// <summary>The tags read as blocks; with those read in words, the tags whose end tag closing nothing open is left out.</summary>
    private static readonly HashSet<string> BlockTagsRead = new(StringComparer.Ordinal)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "details", "summary", "hr",
    };

    /// <summary>The tags whose element an HTML block may leave open to take the blocks after it.</summary>
    private static readonly HashSet<string> Holders = new(StringComparer.Ordinal) { "details" };

    /// <summary>Words read from HTML (a <c>p</c>, a heading's <c>h1</c> to <c>h6</c>, the words between its blocks), read
    /// already, standing as its <c>align</c> says (null where it said nothing); a heading's level, 0 for none.</summary>
    internal sealed record Phrase(IReadOnlyList<Inline> Parts, Align? Aligned = null, int Level = 0) : Block;

    /// <summary>Blocks read from HTML that stand together (a <c>div</c>, an HTML block of several), those of their words
    /// that say nothing of their own standing as its <c>align</c> says.</summary>
    internal sealed record Division(IReadOnlyList<Block> Blocks, Align? Aligned = null) : Block;

    /// <summary>A <c>details</c>: its summary's words, and the blocks it shows once opened, as its <c>open</c> has it first.</summary>
    internal sealed record Details(IReadOnlyList<Inline> Summary, IReadOnlyList<Block> Blocks, bool Open) : Block;

    /// <summary>An HTML tag as written: its name in lower case, whether it closes, whether it closes itself (<c>/&gt;</c>),
    /// its attributes by name (lower case, the first of a name, their values' references decoded), and where it ends.</summary>
    private readonly record struct Tag(string Name, bool Closing, bool SelfClosing, IReadOnlyDictionary<string, string> Attributes, int End);

    private abstract class HtmlNode;

    /// <summary>Text between tags, as written.</summary>
    private sealed class HtmlText(string written) : HtmlNode
    {
        public string Written { get; } = written;
    }

    /// <summary>A tag as written that closes nothing open, kept as its words.</summary>
    private sealed class HtmlStray(string written) : HtmlNode
    {
        public string Written { get; } = written;
    }

    private sealed class HtmlElement(Tag tag, string written) : HtmlNode
    {
        public string Name => tag.Name;

        public IReadOnlyDictionary<string, string> Attributes => tag.Attributes;

        public bool SelfClosing => tag.SelfClosing;

        /// <summary>Its start tag as written.</summary>
        public string Written { get; } = written;

        public List<HtmlNode> Children { get; } = [];

        /// <summary>Its end tag as written, null for one never closed.</summary>
        public string? Closed { get; set; }

        /// <summary>The blocks after its HTML block it took, its own left open there.</summary>
        public IReadOnlyList<Block> Taken { get; set; } = [];
    }

    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    /// <summary>The kind of HTML block a line begins, GFM's 1 to 7 (a comment's, the second, is left out before), 0 for none.</summary>
    private static int HtmlBlockKind(string line)
    {
        int indent = Indent(line);
        if (indent > 3 || indent >= line.Length || line[indent] != '<')
        {
            return 0;
        }
        string text = line[indent..];
        foreach (string raw in RawTags)
        {
            if (text.Length > raw.Length && text.AsSpan(1).StartsWith(raw, StringComparison.OrdinalIgnoreCase)
                && (text.Length == raw.Length + 1 || char.IsWhiteSpace(text[raw.Length + 1]) || text[raw.Length + 1] == '>'))
            {
                return 1;
            }
        }
        if (text.StartsWith("<!--", StringComparison.Ordinal))
        {
            return 2;
        }
        if (text.StartsWith("<?", StringComparison.Ordinal))
        {
            return 3;
        }
        if (text.StartsWith("<![CDATA[", StringComparison.Ordinal))
        {
            return 5;
        }
        if (text.Length > 2 && text[1] == '!' && char.IsAsciiLetter(text[2]))
        {
            return 4;
        }
        int name = text.Length > 1 && text[1] == '/' ? 2 : 1;
        int end = name;
        while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '-'))
        {
            end++;
        }
        if (end > name && BlockTags.Contains(text[name..end])
            && (end == text.Length || char.IsWhiteSpace(text[end]) || text[end] == '>' || text.AsSpan(end).StartsWith("/>")))
        {
            return 6;
        }
        return TagAt(text, 0) is { } tag && Array.IndexOf(RawTags, tag.Name) < 0 && string.IsNullOrWhiteSpace(text[tag.End..]) ? LoneTag : 0;
    }

    /// <summary>An HTML block from its first line, to the line that ends its kind: the line closing a raw tag, a
    /// processing instruction or a declaration, else a blank line. Its HTML read into blocks: a <c>pre</c>'s as code, the
    /// other raw tags' (<c>script</c>, <c>style</c>), an instruction's and a declaration's as written; null for one that
    /// draws nothing.</summary>
    private static Block? HtmlFrom(IReadOnlyList<string> lines, ref int i, int kind, int depth)
    {
        int start = i;
        if (kind is LoneTag or 6)
        {
            while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]))
            {
                i++;
            }
        }
        else
        {
            while (i < lines.Count && !EndsHtml(lines[i++], kind))
            {
            }
        }
        string html = string.Join('\n', Enumerable.Range(start, i - start).Select(at => lines[at]));
        if (kind is not (LoneTag or 6))
        {
            return HtmlNodes(html.TrimStart()) is [HtmlElement { Name: "pre" } pre, ..] ? new Code(WebUtility.HtmlDecode(TextOf(pre)).TrimStart('\n').TrimEnd()) : new Phrase([new Words(Collapsed(html))]);
        }
        List<HtmlNode> nodes = HtmlNodes(html);
        if (depth < DeepestNesting && RuntimeHelpers.TryEnsureSufficientExecutionStack() && LeftOpen(nodes) is { } holder && HolderEnd(lines, i, holder.Name) is int closing)
        {
            holder.Taken = Blocks([.. Enumerable.Range(i, closing - i).Select(at => lines[at])], depth: depth + 1);
            i = closing + 1;
        }
        List<Block> blocks = HtmlBlocks(nodes, depth);
        return blocks.Count switch
        {
            0 => null,
            1 => blocks[0],
            _ => new Division(blocks),
        };
    }

    /// <summary>Whether a line ends an HTML block of the first five kinds, the comment's aside.</summary>
    private static bool EndsHtml(string line, int kind) => kind switch
    {
        1 => RawTags.Any(raw => line.Contains($"</{raw}>", StringComparison.OrdinalIgnoreCase)),
        3 => line.Contains("?>", StringComparison.Ordinal),
        4 => line.Contains('>', StringComparison.Ordinal),
        _ => line.Contains("]]>", StringComparison.Ordinal),
    };

    /// <summary>The element of a block's last that its HTML leaves open and that may take the blocks after it, null for none.</summary>
    private static HtmlElement? LeftOpen(List<HtmlNode> nodes)
    {
        HtmlNode? last = nodes.LastOrDefault(n => n is not HtmlText { Written: var text } || !string.IsNullOrWhiteSpace(text));
        return last is HtmlElement { Closed: null } element && Holders.Contains(element.Name) ? element : null;
    }

    /// <summary>The line that closes an element left open (its end tag alone on it, past those of the same name the
    /// lines between open and close), from a line on; null where none does.</summary>
    private static int? HolderEnd(IReadOnlyList<string> lines, int from, string name)
    {
        int open = 1;
        for (int at = from; at < lines.Count; at++)
        {
            string line = lines[at].Trim();
            if (line.StartsWith('<') && TagAt(line, 0) is { } tag && tag.Name == name && !tag.SelfClosing)
            {
                if (tag.Closing)
                {
                    if (--open == 0)
                    {
                        return tag.End == line.Length ? at : null;
                    }
                }
                else if (!line.Contains($"</{name}>", StringComparison.OrdinalIgnoreCase))
                {
                    open++;
                }
            }
        }
        return null;
    }

    /// <summary>A text's HTML as elements and the text between them, each end tag closing the last element of its name
    /// open and all opened inside it; a comment left out.</summary>
    private static List<HtmlNode> HtmlNodes(string html, bool documentation = false)
    {
        List<HtmlNode> roots = [];
        List<HtmlElement> open = [];
        int text = 0;
        int i = 0;
        while (i < html.Length)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                break;
            }
            if (!documentation && html.AsSpan(lt).StartsWith("<!--"))
            {
                Add(new HtmlText(html[text..lt]));
                int end = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                i = text = end < 0 ? html.Length : end + 3;
                continue;
            }
            if (TagAt(html, lt) is not { } tag || (documentation && !DocumentationTags.Contains(tag.Name)))
            {
                i = lt + 1;
                continue;
            }
            Add(new HtmlText(html[text..lt]));
            string written = html[lt..tag.End];
            if (tag.Closing)
            {
                int at = open.FindLastIndex(e => e.Name == tag.Name);
                if (at >= 0)
                {
                    open[at].Closed = written;
                    open.RemoveRange(at, open.Count - at);
                }
                else if (documentation || (!BlockTagsRead.Contains(tag.Name) && !WordTags.Contains(tag.Name)))
                {
                    Add(new HtmlStray(written));
                }
            }
            else
            {
                if (EndParagraph.Contains(tag.Name) && open.Count > 0 && open[^1].Name == "p")
                {
                    open.RemoveAt(open.Count - 1);
                }
                HtmlElement element = new(tag, written);
                Add(element);
                if (documentation && !tag.SelfClosing && tag.Name is "c" or "code" && EndTagAt(html, tag.End, tag.Name) is { } close)
                {
                    element.Children.Add(new HtmlText(html[tag.End..close.Start]));
                    element.Closed = html[close.Start..close.End];
                    i = text = close.End;
                    continue;
                }
                if (!tag.SelfClosing && !VoidTags.Contains(tag.Name))
                {
                    open.Add(element);
                }
            }
            i = text = tag.End;
        }
        Add(new HtmlText(html[text..]));
        return roots;

        void Add(HtmlNode node)
        {
            if (node is HtmlText { Written.Length: 0 })
            {
                return;
            }
            (open.Count > 0 ? open[^1].Children : roots).Add(node);
        }
    }

    /// <summary>An HTML tag at a '&lt;' as GFM reads one: a name of ASCII letters, digits and hyphens, attributes each after
    /// white space, a value unquoted or in either quote; null where none is written there.</summary>
    private static Tag? TagAt(string text, int at)
    {
        int i = at + 1;
        bool closing = i < text.Length && text[i] == '/';
        if (closing)
        {
            i++;
        }
        int start = i;
        if (i >= text.Length || !char.IsAsciiLetter(text[i]))
        {
            return null;
        }
        while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '-'))
        {
            i++;
        }
        string name = text[start..i].ToLowerInvariant();
        Dictionary<string, string>? attributes = null;
        while (true)
        {
            int spaced = i;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            if (i >= text.Length)
            {
                return null;
            }
            if (text[i] == '>')
            {
                return new Tag(name, closing, false, attributes ?? NoAttributes, i + 1);
            }
            if (!closing && text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>')
            {
                return new Tag(name, closing, true, attributes ?? NoAttributes, i + 2);
            }
            if (closing || i == spaced || !(char.IsAsciiLetter(text[i]) || text[i] is '_' or ':'))
            {
                return null;
            }
            int named = i;
            while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.' or ':' or '-'))
            {
                i++;
            }
            string attribute = text[named..i].ToLowerInvariant();
            string value = "";
            int equals = i;
            while (equals < text.Length && char.IsWhiteSpace(text[equals]))
            {
                equals++;
            }
            if (equals < text.Length && text[equals] == '=')
            {
                int from = equals + 1;
                while (from < text.Length && char.IsWhiteSpace(text[from]))
                {
                    from++;
                }
                if (from >= text.Length)
                {
                    return null;
                }
                if (text[from] is '"' or '\'')
                {
                    int close = text.IndexOf(text[from], from + 1);
                    if (close < 0)
                    {
                        return null;
                    }
                    value = text[(from + 1)..close];
                    i = close + 1;
                }
                else
                {
                    int to = from;
                    while (to < text.Length && !char.IsWhiteSpace(text[to]) && text[to] is not ('"' or '\'' or '=' or '<' or '>' or '`'))
                    {
                        to++;
                    }
                    if (to == from)
                    {
                        return null;
                    }
                    value = text[from..to];
                    i = to;
                }
            }
            (attributes ??= new Dictionary<string, string>(StringComparer.Ordinal)).TryAdd(attribute, WebUtility.HtmlDecode(value));
        }
    }

    /// <summary>A tag read inside a paragraph's words from its '&lt;' and where what it read ends: a void one's element, a
    /// <c>picture</c> to its end tag, and another's words to its end tag, read as Markdown; null for a tag not read in
    /// words, or one never closed.</summary>
    private static (IReadOnlyList<Inline> Parts, int End)? HtmlAt(string text, int at, int depth)
    {
        if (TagAt(text, at) is not { Closing: false } tag || !WordTags.Contains(tag.Name))
        {
            return null;
        }
        HtmlElement element = new(tag, text[at..tag.End]);
        if (VoidTags.Contains(tag.Name) || tag.SelfClosing)
        {
            return (WordsOf([element]), tag.End);
        }
        if (EndTagAt(text, tag.End, tag.Name) is not (int closeStart, int closeEnd))
        {
            return null;
        }
        if (tag.Name == "picture")
        {
            return (WordsOf(HtmlNodes(text[at..closeEnd])), closeEnd);
        }
        IReadOnlyList<Inline> inner = Inlines(text[tag.End..closeStart], depth + 1);
        return (Element(element, inner), closeEnd);
    }

    /// <summary>Where the end tag of an element opened before <paramref name="from"/> stands and ends, past those of the
    /// same name opened and closed between; null where it is never closed.</summary>
    private static (int Start, int End)? EndTagAt(string text, int from, string name)
    {
        int open = 1;
        for (int at = text.IndexOf('<', from); at >= 0; at = text.IndexOf('<', at + 1))
        {
            if (TagAt(text, at) is not { } tag || tag.Name != name || tag.SelfClosing)
            {
                continue;
            }
            open += tag.Closing ? -1 : 1;
            if (open == 0)
            {
                return (at, tag.End);
            }
        }
        return null;
    }

    /// <summary>An HTML block's nodes as blocks: <c>p</c>, <c>div</c>, a heading, <c>details</c> and <c>hr</c> each a block of
    /// its own, the words between them a paragraph's.</summary>
    private static List<Block> HtmlBlocks(IReadOnlyList<HtmlNode> nodes, int depth)
    {
        List<Block> blocks = [];
        List<Inline> words = [];
        Walk(nodes);
        Flush();
        return blocks;

        void Walk(IReadOnlyList<HtmlNode> walked)
        {
            foreach (HtmlNode node in walked)
            {
                if (node is not HtmlElement element)
                {
                    words.AddRange(WordsOf([node]));
                    continue;
                }
                switch (element.Name)
                {
                    case "p":
                        Flush();
                        if (Trimmed(WordsOf(element.Children)) is { Count: > 0 } said)
                        {
                            blocks.Add(new Phrase(said, AlignOf(element)));
                        }
                        break;
                    case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                        Flush();
                        blocks.Add(new Phrase(Trimmed(WordsOf(element.Children)), AlignOf(element), element.Name[1] - '0'));
                        break;
                    case "div" when depth < DeepestNesting && RuntimeHelpers.TryEnsureSufficientExecutionStack():
                        Flush();
                        if (HtmlBlocks(element.Children, depth + 1) is { Count: > 0 } held)
                        {
                            blocks.Add(new Division(held, AlignOf(element)));
                        }
                        break;
                    case "details" when depth < DeepestNesting && RuntimeHelpers.TryEnsureSufficientExecutionStack():
                        Flush();
                        HtmlElement? summary = element.Children.OfType<HtmlElement>().FirstOrDefault(e => e.Name == "summary");
                        IReadOnlyList<Inline> title = summary is null ? [new Words("Details")] : Trimmed(WordsOf(summary.Children));
                        blocks.Add(new Details(title, [.. HtmlBlocks([.. element.Children.Where(c => c != summary)], depth + 1), .. element.Taken], element.Attributes.ContainsKey("open")));
                        break;
                    case "summary":
                        Flush();
                        blocks.Add(new Phrase(Trimmed(WordsOf(element.Children))));
                        break;
                    case "hr":
                        Flush();
                        blocks.Add(new Rule());
                        break;
                    case var name when WordTags.Contains(name):
                        words.AddRange(WordsOf([element]));
                        break;
                    default:
                        words.Add(new Words(element.Written));
                        Walk(element.Children);
                        if (element.Closed is { } closed)
                        {
                            words.Add(new Words(closed));
                        }
                        break;
                }
            }
        }

        void Flush()
        {
            IReadOnlyList<Inline> said = Trimmed(words);
            if (said.Count > 0)
            {
                blocks.Add(new Phrase(said));
            }
            words.Clear();
        }
    }

    /// <summary>HTML's nodes as words: text with its references decoded and its white space a space, the tags read in
    /// words as what they are, the words of a block's tag (a <c>p</c> inside an <c>a</c>) as its words, and every
    /// other tag as written.</summary>
    private static List<Inline> WordsOf(IReadOnlyList<HtmlNode> nodes)
    {
        List<Inline> parts = [];
        foreach (HtmlNode node in nodes)
        {
            switch (node)
            {
                case HtmlText text:
                    parts.Add(new Words(Collapsed(WebUtility.HtmlDecode(text.Written))));
                    break;
                case HtmlStray stray:
                    parts.Add(new Words(stray.Written));
                    break;
                case HtmlElement { Name: "img" } img:
                    if (ImageOf(img, null, null) is { } image)
                    {
                        parts.Add(image);
                    }
                    break;
                case HtmlElement { Name: "picture" } picture:
                    if (PictureOf(picture) is { } pictured)
                    {
                        parts.Add(pictured);
                    }
                    break;
                case HtmlElement { Name: "br" }:
                    parts.Add(new Break());
                    break;
                case HtmlElement element when WordTags.Contains(element.Name):
                    parts.AddRange(Element(element, WordsOf(element.Children)));
                    break;
                case HtmlElement { Name: "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "summary" or "details" } block:
                    parts.AddRange(WordsOf(block.Children));
                    break;
                case HtmlElement { Name: "hr" }:
                    break;
                case HtmlElement other:
                    parts.Add(new Words(other.Written));
                    parts.AddRange(WordsOf(other.Children));
                    if (other.Closed is { } closed)
                    {
                        parts.Add(new Words(closed));
                    }
                    break;
            }
        }
        return parts;
    }

    /// <summary>Some generators escape XML documentation tags into Roslyn's prose. Recover those tags in words
    /// while leaving Markdown code spans intact.</summary>
    internal static IReadOnlyList<Inline> DocumentationInlines(string text) =>
        [.. Inlines(text).SelectMany(part => part is Words words && words.Text.Contains('<')
            ? DocumentationWords(HtmlNodes(words.Text, documentation: true), 0)
            : [part])];

    private static IEnumerable<Inline> DocumentationWords(IEnumerable<HtmlNode> nodes, int depth) =>
        nodes.SelectMany<HtmlNode, Inline>(node => node switch
        {
            HtmlText text => [new Words(text.Written)],
            HtmlStray stray => [new Words(stray.Written)],
            HtmlElement element when depth >= DeepestNesting => [new Words(element.Written)],
            HtmlElement { Closed: null, SelfClosing: false, Name: not "br" } element =>
                [new Words(element.Written), .. DocumentationWords(element.Children, depth + 1)],
            HtmlElement element => DocumentationElement(element, depth + 1),
            _ => [],
        });

    private static IReadOnlyList<Inline> DocumentationElement(HtmlElement element, int depth)
    {
        if (element.Name == "list")
        {
            return DocumentationList(element, depth);
        }
        IReadOnlyList<Inline> inner = [.. DocumentationWords(element.Children, depth)];
        IReadOnlyDictionary<string, string> attributes = element.Attributes;
        return element.Name switch
        {
            "a" or "see" or "seealso" when attributes.TryGetValue("href", out string? href) =>
                [new Link(inner.Count == 0 ? [new Words(href)] : inner, href)],
            "see" or "seealso" when inner.Count == 0 && (attributes.GetValueOrDefault("cref") ?? attributes.GetValueOrDefault("langword")) is { } reference =>
                [new CodeSpan(reference is [_, ':', ..] ? reference[2..] : reference)],
            "paramref" or "typeparamref" when attributes.TryGetValue("name", out string? name) => [new CodeSpan(name)],
            "c" => [new CodeSpan(Plain(inner))],
            "code" => [new Break(), new CodeSpan(Plain(inner)), new Break()],
            "para" => [new Break(), new Break(), .. inner, new Break(), new Break()],
            "term" => [new Strong(inner), new Words(" – ")],
            _ => Element(element, inner),
        };
    }

    private static IReadOnlyList<Inline> DocumentationList(HtmlElement list, int depth)
    {
        string? type = list.Attributes.GetValueOrDefault("type");
        List<Inline> parts = [new Break()];
        int number = 0;
        foreach (HtmlElement entry in list.Children.OfType<HtmlElement>().Where(e => e.Name is "item" or "listheader"))
        {
            if (entry.Name == "item" && type is "bullet" or "number")
            {
                parts.Add(new Words(type == "number" ? $"{++number}. " : "• "));
            }
            parts.AddRange(DocumentationWords(entry.Children, depth + 1));
            parts.Add(new Break());
        }
        return parts;
    }

    /// <summary>A tag read in words around its words read: a link where an <c>a</c> has an <c>href</c> (its words alone
    /// where it has none), strong, emphasis, or code.</summary>
    private static IReadOnlyList<Inline> Element(HtmlElement element, IReadOnlyList<Inline> inner) => element.Name switch
    {
        "a" when element.Attributes.TryGetValue("href", out string? href) => [new Link(inner, href)],
        "b" or "strong" => [new Strong(inner)],
        "i" or "em" => [new Emphasis(inner)],
        "code" or "kbd" => [new CodeSpan(Plain(inner))],
        "img" => ImageOf(element, null, null) is { } image ? [image] : [],
        "br" => [new Break()],
        _ => inner,
    };

    /// <summary>An <c>img</c> as an image, its sources for each look given: null for one with no <c>src</c>.</summary>
    private static Image? ImageOf(HtmlElement img, string? light, string? dark) =>
        img.Attributes.TryGetValue("src", out string? src) && !string.IsNullOrWhiteSpace(src)
            ? new Image(img.Attributes.GetValueOrDefault("alt", ""), src.Trim(), Size(img, "width"), Size(img, "height"), light, dark)
            : null;

    /// <summary>A <c>picture</c> as its <c>img</c>, with the first <c>source</c> that each look's
    /// <c>prefers-color-scheme</c> chooses (one with no <c>media</c> chosen by both, one of another <c>media</c> by neither).</summary>
    private static Image? PictureOf(HtmlElement picture)
    {
        string? light = null;
        string? dark = null;
        bool lightChosen = false, darkChosen = false;
        foreach (HtmlElement source in picture.Children.OfType<HtmlElement>().Where(e => e.Name == "source"))
        {
            if (!source.Attributes.TryGetValue("srcset", out string? set) || set.Split(',')[0].Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) is not [var first, ..])
            {
                continue;
            }
            string media = source.Attributes.TryGetValue("media", out string? written) ? string.Concat(written.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant() : "";
            bool forLight = media is "" or "all" or "(prefers-color-scheme:light)";
            bool forDark = media is "" or "all" or "(prefers-color-scheme:dark)";
            if (forLight && !lightChosen)
            {
                (light, lightChosen) = (first, true);
            }
            if (forDark && !darkChosen)
            {
                (dark, darkChosen) = (first, true);
            }
        }
        HtmlElement? img = picture.Children.OfType<HtmlElement>().FirstOrDefault(e => e.Name == "img");
        return img is null ? null : ImageOf(img, light, dark);
    }

    /// <summary>An <c>img</c>'s width or height in pixels (a page's units), null for none, a share of the page or one not read.</summary>
    private static double? Size(HtmlElement img, string attribute) =>
        img.Attributes.TryGetValue(attribute, out string? written)
        && double.TryParse(written.Trim().TrimEnd("px".ToCharArray()), NumberStyles.Float, CultureInfo.InvariantCulture, out double size)
        && size > 0 && double.IsFinite(size)
            ? size
            : null;

    /// <summary>An element's <c>align</c>, null where it has none or one not read.</summary>
    private static Align? AlignOf(HtmlElement element) =>
        element.Attributes.TryGetValue("align", out string? align)
            ? align.Trim().ToLowerInvariant() switch
            {
                "center" => Align.Center,
                "right" => Align.Right,
                "left" => Align.Left,
                _ => null,
            }
            : null;

    /// <summary>Words with the white space at their ends and either side of a break taken off, and none left that say
    /// nothing.</summary>
    private static IReadOnlyList<Inline> Trimmed(IReadOnlyList<Inline> parts)
    {
        List<Inline> kept = [];
        for (int at = 0; at < parts.Count; at++)
        {
            if (parts[at] is not Words { Text: var text })
            {
                kept.Add(parts[at]);
                continue;
            }
            bool first = at == 0 || parts[at - 1] is Break, last = at == parts.Count - 1 || parts[at + 1] is Break;
            string trimmed = first && last ? text.Trim() : first ? text.TrimStart() : last ? text.TrimEnd() : text;
            if (trimmed.Length > 0)
            {
                kept.Add(new Words(trimmed));
            }
        }
        return kept.Any(p => p is not Words { Text: var words } || !string.IsNullOrWhiteSpace(words)) ? kept : [];
    }

    /// <summary>The text an element holds, its tags left out.</summary>
    private static string TextOf(HtmlElement element) =>
        string.Concat(element.Children.Select(c => c switch
        {
            HtmlText text => text.Written,
            HtmlElement inner => TextOf(inner),
            _ => "",
        }));

    /// <summary>HTML's text with each run of white space a space, as a page draws it.</summary>
    private static string Collapsed(string text)
    {
        StringBuilder collapsed = new(text.Length);
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                collapsed.Append(c);
            }
            else if (collapsed.Length == 0 || collapsed[^1] != ' ')
            {
                collapsed.Append(' ');
            }
        }
        return collapsed.ToString();
    }
}
