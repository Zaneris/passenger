using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>Extend Selection in XML and XAML: the ranges around a selection from the markup as read (<see cref="XmlTree"/>),
/// the word, a markup extension's argument and what its braces hold, an attribute's value inside its quotes, the attribute,
/// a start tag's attributes, the tag, a run of an element's text, a comment, its content inside its tags, the element, and
/// so on out through each element holding it to the whole text; never a line, which in markup cuts across what is written.</summary>
public static class XmlSelectionRanges
{
    /// <summary>The ranges around a selection of <paramref name="text"/>, which <paramref name="tree"/> read, in no set order:
    /// Extend Selection takes the smallest that holds the selection and more.</summary>
    public static IEnumerable<TextRange> Around(XmlTree tree, TextSnapshot text, TextRange selection)
    {
        int start = selection.Start;
        int end = selection.End;
        if (start < text.Length && WordCharacters.IsWordAt(text, start) || start > 0 && WordCharacters.IsWordAt(text, start - 1))
        {
            yield return TextNavigation.WordAt(text, start);
        }
        foreach (TextRange comment in tree.Comments)
        {
            if (comment.Start <= start && end <= comment.End)
            {
                yield return TextRange.Trimmed(text, TextRange.FromBounds(Math.Min(comment.Start + 4, comment.End), Math.Max(comment.Start + 4, comment.End - 3)));
                yield return comment;
            }
        }
        XmlElement? element = tree.ElementAt(start);
        while (element is not null && !(element.Start <= start && end <= element.End))
        {
            element = element.Parent;
        }
        if (element is not null)
        {
            foreach (TextRange part in InTags(tree.Text, element, start, end))
            {
                yield return part;
            }
        }
        for (XmlElement? holding = element; holding is not null; holding = holding.Parent)
        {
            foreach (XmlText run in holding.Texts)
            {
                yield return TextRange.Trimmed(text, run.Range);
            }
            if (holding.Content.Length > 0)
            {
                yield return TextRange.Trimmed(text, holding.Content);
            }
            yield return TextRange.FromBounds(holding.Start, holding.End);
        }
        yield return new TextRange(0, text.Length);
    }

    /// <summary>The ranges in an element's start or end tag around a selection: its name, an attribute and what its value
    /// holds, the attributes together, the tag.</summary>
    private static IEnumerable<TextRange> InTags(string text, XmlElement element, int start, int end)
    {
        if (end <= element.StartTagEnd)
        {
            yield return element.NameRange;
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (attribute.NameStart <= start && end <= attribute.End)
                {
                    yield return attribute.NameRange;
                    if (attribute.HasValue)
                    {
                        if (MarkupExtension.Parse(text, attribute.ValueStart, attribute.ValueEnd) is { } extension)
                        {
                            foreach (TextRange part in InExtension(extension, start, end))
                            {
                                yield return part;
                            }
                        }
                        yield return attribute.ValueRange;
                    }
                    yield return TextRange.FromBounds(attribute.NameStart, attribute.End);
                }
            }
            if (element.Attributes is [var first, ..])
            {
                yield return TextRange.FromBounds(first.NameStart, element.Attributes[^1].End);
            }
            yield return TextRange.FromBounds(element.Start, element.StartTagEnd);
        }
        if (element.EndTagStart is { } endTag && start >= endTag)
        {
            if (element.EndTagNameRange is { } name)
            {
                yield return name;
            }
            yield return TextRange.FromBounds(endTag, element.End);
        }
    }

    /// <summary>The ranges a markup extension holds around a selection: an argument holding it, its value, and the extension
    /// nested there, then what the braces hold and the extension itself. Its nesting is as deep as its reading went, which
    /// <see cref="MarkupExtension"/> bounds.</summary>
    private static IEnumerable<TextRange> InExtension(MarkupExtension extension, int start, int end)
    {
        if (start < extension.Start || end > extension.End)
        {
            yield break;
        }
        foreach (MarkupArgument argument in extension.Arguments)
        {
            TextRange whole = argument.Key is null ? argument.ValueRange : TextRange.FromBounds(argument.KeyStart, argument.ValueEnd);
            if (whole.Start <= start && end <= whole.End)
            {
                yield return argument.ValueRange;
                yield return whole;
                if (argument.Nested is { } nested)
                {
                    foreach (TextRange inner in InExtension(nested, start, end))
                    {
                        yield return inner;
                    }
                }
            }
        }
        yield return TextRange.FromBounds(extension.Start + 1, extension.IsClosed ? extension.End - 1 : extension.End);
        yield return TextRange.FromBounds(extension.Start, extension.End);
    }
}
