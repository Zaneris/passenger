using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Controls;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Editors;
using Passenger.Languages;

namespace Passenger.App.CSharp;

/// <summary>C# documentation has the same words, links and example styling in Quick Info and in the editor.</summary>
internal static partial class DocumentationView
{
    public static Control Inline(string xml, MarkdownPage page)
    {
        StackPanel blocks = new() { Spacing = Gaps.Small };
        foreach (Markdown.Block block in Read(xml))
        {
            Add(blocks, block, page);
        }
        return blocks;
    }

    public static void Add(Panel into, Markdown.Block block, MarkdownPage page)
    {
        if (block is Markdown.Phrase { Level: 0, Aligned: null } prose)
        {
            MarkdownText words = MarkdownText.Of(prose.Parts, page, StyleClasses.Caption);
            if (words.Shown.Length > 0)
            {
                words.Foreground = InfoCard.Brush(TokenKeys.TextMuted);
                into.Children.Add(words);
            }
        }
        else if (block is Markdown.Code { Info: "text" } example)
        {
            into.Children.Add(ColouredCode.Of(example.Text, IsCSharpExample(example.Text) ? Language.CSharp : null));
        }
        else
        {
            into.Children.Add(Markdown.Control(block, page));
        }
    }

    private static List<Markdown.Block> Read(string xml)
    {
        XElement body;
        try
        {
            body = XElement.Parse($"<doc>{Roslyn.Extension.EscapedDocumentationTags.Of(xml)}</doc>", LoadOptions.PreserveWhitespace);
        }
        catch (XmlException)
        {
            return [new Markdown.Phrase([new Markdown.Words(xml)])];
        }

        List<Markdown.Block> blocks = [];
        foreach (XElement section in body.Elements())
        {
            string name = section.Name.LocalName;
            Markdown.Inline[] label = name switch
            {
                "summary" => [],
                "param" or "typeparam" => [new Markdown.CodeSpan((string?)section.Attribute("name") ?? name), new Markdown.Words("  ")],
                "returns" => [new Markdown.Strong([new Markdown.Words("Returns")]), new Markdown.Words("  ")],
                "remarks" => [new Markdown.Strong([new Markdown.Words("Remarks")]), new Markdown.Words("  ")],
                "exception" => [new Markdown.CodeSpan(Symbol((string?)section.Attribute("cref") ?? name)), new Markdown.Words("  ")],
                _ => [],
            };
            blocks.AddRange(BlockContent(name is "code" or "list" or "seealso" ? [section] : section.Nodes(), 0, label));
        }
        return blocks;
    }

    private static List<Markdown.Block> BlockContent(IEnumerable<XNode> nodes, int depth, IEnumerable<Markdown.Inline>? prefix = null)
    {
        List<Markdown.Block> blocks = [];
        List<Markdown.Inline> prose = [.. prefix ?? []];
        foreach (XNode node in nodes)
        {
            switch (node)
            {
                case XElement element when depth < 32 && element.Name.LocalName == "para":
                    Flush();
                    blocks.AddRange(BlockContent(element.Nodes(), depth + 1));
                    break;
                case XElement element when element.Name.LocalName == "code":
                    Flush();
                    blocks.Add(new Markdown.Code(element.Value.Trim('\n', '\r', ' '), (string?)element.Attribute("language") ?? "text"));
                    break;
                case XElement element when depth < 32 && element.Name.LocalName == "list":
                    Flush();
                    blocks.Add(ListOf(element, depth));
                    break;
                default:
                    prose.AddRange(InlineOf(node, depth));
                    break;
            }
        }
        Flush();
        return blocks;

        void Flush()
        {
            if (prose.LastOrDefault() is Markdown.Words last)
            {
                prose[^1] = new Markdown.Words(last.Text.TrimEnd());
            }
            if (prose.FirstOrDefault() is Markdown.Words first)
            {
                prose[0] = new Markdown.Words(first.Text.TrimStart());
            }
            if (prose.Any(part => part is not Markdown.Words { Text.Length: 0 }))
            {
                blocks.Add(new Markdown.Phrase([.. prose]));
            }
            prose.Clear();
        }
    }

    private static Markdown.Block ListOf(XElement list, int depth)
    {
        XElement[] items = [.. list.Elements("item")];
        string? type = (string?)list.Attribute("type");
        if (type == "table")
        {
            XElement? header = list.Element("listheader");
            return new Markdown.RichTable([.. (header is null ? items : [header, .. items]).Select(Row)], header is not null);
        }
        return new Markdown.Listing(type == "number", 1, [.. items.Select(item => BlockContent(item.Nodes(), depth + 1))]);

        IReadOnlyList<IReadOnlyList<Markdown.Inline>> Row(XElement row) =>
            [Cell(row.Element("term")), Cell(row.Element("description"))];
        IReadOnlyList<Markdown.Inline> Cell(XElement? cell) => cell is null ? [] : [.. cell.Nodes().SelectMany(node => InlineOf(node, depth + 1))];
    }

    private static IReadOnlyList<Markdown.Inline> InlineOf(XNode node, int depth)
    {
        if (node is XText text)
        {
            return [new Markdown.Words(Whitespace().Replace(text.Value, " "))];
        }
        if (node is not XElement element || depth >= 32)
        {
            return [];
        }
        IReadOnlyList<Markdown.Inline> words = [.. element.Nodes().SelectMany(child => InlineOf(child, depth + 1))];
        return element.Name.LocalName switch
        {
            "b" or "strong" => [new Markdown.Strong(words)],
            "i" or "em" => [new Markdown.Emphasis(words)],
            "u" => [new Markdown.Underline(words)],
            "c" or "tt" => [new Markdown.CodeSpan(element.Value)],
            "paramref" or "typeparamref" => [new Markdown.CodeSpan((string?)element.Attribute("name") ?? element.Value)],
            "see" or "seealso" or "a" => Reference(element, words),
            "br" => [new Markdown.Break()],
            _ => words,
        };
    }

    private static IReadOnlyList<Markdown.Inline> Reference(XElement element, IReadOnlyList<Markdown.Inline> words)
    {
        if ((string?)element.Attribute("href") is { Length: > 0 } href)
        {
            return [new Markdown.Link(words.Count > 0 ? words : [new Markdown.Words(href)], href)];
        }
        if (words.Count > 0)
        {
            return words;
        }
        string? target = (string?)element.Attribute("cref") ?? (string?)element.Attribute("langword");
        return target is { Length: > 0 } ? [new Markdown.CodeSpan(Symbol(target))] : [];
    }

    private static string Symbol(string text) => text.Length > 2 && text[1] == ':' ? text[2..] : text;

    /// <summary>A text fence with a C# declaration is code. A console message or a loose fragment stays plain.</summary>
    private static bool IsCSharpExample(string code)
    {
        SyntaxNode root = Languages.CSharp.CSharpParsing.Parse(code).GetRoot();
        return !root.ContainsDiagnostics && root.DescendantNodes().Any(node => node is PredefinedTypeSyntax or UsingDirectiveSyntax
            or BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax or VariableDeclarationSyntax { Type.IsVar: true });
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
