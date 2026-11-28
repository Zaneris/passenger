using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>Enter typed between the empty quotes of a XAML attribute (<c>&lt;Grid Style="|"&gt;</c>): the attribute taken out of
/// its start tag and written as a property element, the element's first child on a line of its own
/// (<c>&lt;Grid.Style&gt;|&lt;/Grid.Style&gt;</c>), a self-closing element opened into its start and end tags. An attached
/// property (<c>Grid.Row</c>) is named by its owner as written; a namespace's declaration and a directive (<c>x:Name</c>)
/// set no property, so Enter in theirs is a line break as anywhere.</summary>
public static class PropertyElementTyping
{
    /// <summary>Whether a caret stands between the quotes of an attribute of no value that a property element can be written
    /// for, where Enter writes one; read from the quotes around it before the markup.</summary>
    public static bool Takes(TextSnapshot text, int caret) =>
        caret > 0 && caret < text.Length && text[caret - 1] is '"' or '\'' && text[caret] == text[caret - 1]
        && Settable(XmlTree.Parse(text.ToString()), caret) is { Attribute.RawValue.Length: 0 };

    /// <summary>The edits that write the property element once Enter broke the line between the empty quotes, in the text as
    /// Enter left it, and where they put the caret: inside the property element, in the text they make. Null where the caret
    /// no longer stands in such an attribute's value, white space and the line break alone. <paramref name="unit"/> is one
    /// level of the file's indentation, which the child takes in from the element's line.</summary>
    public static (IReadOnlyList<TextEdit> Edits, int Caret)? Written(TextSnapshot text, int caret, string eol, string unit)
    {
        string whole = text.ToString();
        if (Settable(XmlTree.Parse(whole), caret) is not { } place || place.Attribute is not { } attribute || place.Element is not { } element
            || !string.IsNullOrWhiteSpace(attribute.RawValue) || !attribute.RawValue.Contains('\n') && !attribute.RawValue.Contains('\r'))
        {
            return null;
        }
        LineSpan line = text.GetLine(text.GetLineFromOffset(element.Start));
        string indent = text.GetText(line.Start, TextNavigation.IndentEnd(text, line) - line.Start);
        string name = attribute.LocalName.Contains('.', StringComparison.Ordinal) ? attribute.Name : $"{element.Name}.{attribute.Name}";
        string child = $"<{name}></{name}>";

        // The attribute goes with the white space before it, back to what comes before it in the tag.
        int before = element.Attributes.TakeWhile(a => a != attribute).Select(a => a.End).DefaultIfEmpty(element.NameEnd).Max();
        int from = attribute.NameStart;
        while (from > before && char.IsWhiteSpace(whole[from - 1]))
        {
            from--;
        }
        TextEdit taken = new(from, attribute.End - from, string.Empty);

        TextEdit written;
        int into;
        if (element.IsSelfClosing)
        {
            int slash = element.StartTagEnd - 2;
            while (slash > attribute.End && char.IsWhiteSpace(whole[slash - 1]))
            {
                slash--;
            }
            string opened = $">{eol}{indent}{unit}{child}{eol}{indent}</{element.Name}>";
            written = new TextEdit(slash, element.StartTagEnd - slash, opened);
            into = 1;
        }
        else
        {
            // What follows on the start tag's line goes to a line of its own: the end tag at the element's indent, else content at the child's.
            int next = element.StartTagEnd;
            while (next < whole.Length && whole[next] is ' ' or '\t')
            {
                next++;
            }
            bool lineEnds = next == whole.Length || whole[next] is '\n' or '\r';
            string after = lineEnds ? string.Empty : eol + indent + (next == element.EndTagStart ? string.Empty : unit);
            written = new TextEdit(element.StartTagEnd, lineEnds ? 0 : next - element.StartTagEnd, $"{eol}{indent}{unit}{child}{after}");
            into = 0;
        }
        into += eol.Length + indent.Length + unit.Length + name.Length + 2;
        if (taken.Start + taken.Length == written.Start)
        {
            return ([new TextEdit(taken.Start, taken.Length + written.Length, written.Text)], taken.Start + into);
        }
        return ([taken, written], written.Start - taken.Length + into);
    }

    /// <summary>The attribute whose value holds a caret, with its element, where a property element can set what it sets: an
    /// object element's start tag closed, the attribute's value quoted and closed, and the attribute a property's, its own or
    /// attached, never a namespace's declaration or a directive of a prefix's own (<c>x:Name</c>, <c>d:DesignWidth</c>).</summary>
    private static XmlPlace? Settable(XmlTree tree, int caret)
    {
        XmlPlace place = tree.PlaceAt(caret);
        return place is { Kind: XmlPlaceKind.AttributeValue, Element: { IsStartTagClosed: true, IsPropertyElement: false }, Attribute: { IsValueClosed: true } attribute }
            && attribute.Name != "xmlns" && attribute.Prefix != "xmlns"
            && (attribute.Prefix.Length == 0 || attribute.LocalName.Contains('.', StringComparison.Ordinal))
            ? place : null;
    }
}
