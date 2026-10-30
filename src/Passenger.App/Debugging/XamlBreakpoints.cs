using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.App.Debugging;

/// <summary>Breakpoints in Avalonia XAML, which Avalonia's XAML compiler (XamlIl) maps to the code it makes for each
/// element and attribute: a line's breakpoint stops where the first element or attribute written on it is made or set,
/// and an attribute's (Toggle Breakpoint on it) where that attribute's property is set. The debugger is asked for the
/// column of the name, which tells two on one line apart. WPF's BAML is read as the app runs and has no such code.</summary>
public static class XamlBreakpoints
{
    /// <summary>Whether a file is XAML that Avalonia compiles to code a breakpoint can stop in.</summary>
    public static bool Compiled(string path) => Path.GetExtension(path).ToLowerInvariant() is ".axaml" or ".paml";

    /// <summary>The attribute an offset is on (its name or its value) that sets a property as its element is made: its name
    /// as written, and the line and column (from zero) its name begins at; null elsewhere, and for the directives that set
    /// none (xmlns, x:Class, x:DataType, design-time ones).</summary>
    public static (string Name, TextPosition At)? AttributeAt(TextSnapshot text, int offset)
    {
        XmlPlace place = XmlTree.Parse(text.ToString()).PlaceAt(offset);
        return place is { Kind: XmlPlaceKind.AttributeName or XmlPlaceKind.AttributeValue, Element.IsPropertyElement: false, Attribute: { } attribute } && Sets(attribute)
            ? (attribute.Name, text.GetPosition(attribute.NameStart))
            : null;
    }

    /// <summary>Where the attribute of that name a breakpoint on a line (from zero) is on begins, through its value's closing
    /// quote: the one beginning at <paramref name="column"/> (from zero), else the next of its name after it, where an edit
    /// at its start leaves the column before it; with no column, the line's first of its name. Null when none does.</summary>
    public static TextRange? AttributeRange(TextSnapshot text, int line, string attribute, int? column)
    {
        if (line < 0 || line >= text.LineCount)
        {
            return null;
        }
        LineSpan span = text.GetLine(line);
        int from = span.Start + Math.Max(column ?? 0, 0);
        foreach (XmlElement element in XmlTree.Parse(text.ToString()).Elements)
        {
            if (element.Start > span.End)
            {
                break;
            }
            foreach (XmlAttribute found in element.Attributes)
            {
                if (found.Name == attribute && found.NameStart >= from && found.NameStart < span.End)
                {
                    return TextRange.FromBounds(found.NameStart, Math.Min(found.End, span.End));
                }
            }
        }
        return null;
    }

    /// <summary>The column (from one) a breakpoint on a line (from zero) asks the debugger for: its attribute's name (the one
    /// at <paramref name="column"/>, <see cref="AttributeRange"/>), else the first element's or property-setting attribute's
    /// name that begins on the line; null when the line begins none.</summary>
    public static int? Column(TextSnapshot text, int line, string? attribute, int? column)
    {
        if (line < 0 || line >= text.LineCount)
        {
            return null;
        }
        LineSpan span = text.GetLine(line);
        if (attribute is not null)
        {
            return AttributeRange(text, line, attribute, column) is { } range ? range.Start - span.Start + 1 : null;
        }
        foreach (XmlElement element in XmlTree.Parse(text.ToString()).Elements)
        {
            if (element.Start > span.End)
            {
                break;
            }
            if (!element.IsPropertyElement && element.NameStart >= span.Start && element.NameStart < span.End)
            {
                return element.NameStart - span.Start + 1;
            }
            foreach (XmlAttribute found in element.Attributes)
            {
                if (Sets(found) && found.NameStart >= span.Start && found.NameStart < span.End)
                {
                    return found.NameStart - span.Start + 1;
                }
            }
        }
        return null;
    }

    /// <summary>Whether an attribute sets something as its element is made: not a namespace, nor a directive read only by
    /// the compiler or the designer.</summary>
    private static bool Sets(XmlAttribute attribute) =>
        attribute.Name != "xmlns" && attribute.Prefix is not ("xmlns" or "d" or "mc")
        && !(attribute.Prefix == "x" && attribute.LocalName is "Class" or "DataType" or "CompileBindings" or "ClassModifier" or "FieldModifier" or "Key" or "Shared");
}
