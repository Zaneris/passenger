using Passenger.Text;
using Passenger.Xaml;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Syntax;

namespace Passenger.App.Debugging;

/// <summary>Breakpoints in Avalonia XAML, which Avalonia's XAML compiler (XamlIl) maps to the code it makes for each
/// element and attribute: a line's breakpoint stops where the first element or attribute written on it is made or set,
/// and an attribute's (Toggle Breakpoint on it) where that attribute's property is set. The debugger is asked for the
/// column of the name, which tells two on one line apart. WPF's BAML is read as the app runs and has no such code: there a
/// breakpoint stops as its element joins the window, through Passenger's inspector in the app (<see cref="ElementLine"/>).</summary>
public static class XamlBreakpoints
{
    /// <summary>Whether a file is XAML that Avalonia compiles to code a breakpoint can stop in.</summary>
    public static bool Compiled(string path) => Path.GetExtension(path).ToLowerInvariant() is ".axaml" or ".paml";

    /// <summary>Whether a file is XAML whose elements and attributes take breakpoints: Avalonia's, and a WPF page, whose
    /// breakpoints stop through Passenger's inspector as the app reads it.</summary>
    public static bool Takes(string path) => Compiled(path) || Path.GetExtension(path).Equals(".xaml", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Where the element a breakpoint on a line (or on an attribute there) stops as it is made starts, where the app
    /// reads its XAML as it runs (WPF's BAML) and tells of each element by its start tag's place alone: the attribute's
    /// element, else the first element whose start tag begins on the line or holds a property it sets there. Its start tag's
    /// line (from zero), and where another element starts on that line too its name's column (from one), as the app
    /// records it, which tells the two apart. Null when the line holds none.</summary>
    public static (int Line, int? Column)? ElementPlace(TextSnapshot text, int line, string? attribute, int? column)
    {
        if (line < 0 || line >= text.LineCount)
        {
            return null;
        }
        LineSpan span = text.GetLine(line);
        int? named = attribute is null ? null : AttributeRange(text, line, attribute, column)?.Start;
        IReadOnlyList<XmlElement> elements = XmlTree.Parse(text.ToString()).Elements;
        foreach (XmlElement element in elements)
        {
            if (element.Start > span.End)
            {
                break;
            }
            bool on = named is int start ? element.Attributes.Any(a => a.NameStart == start)
                : !element.IsPropertyElement && (element.NameStart >= span.Start && element.NameStart < span.End
                    || element.Attributes.Any(a => Sets(a) && a.NameStart >= span.Start && a.NameStart < span.End));
            if (on)
            {
                int starts = text.GetPosition(element.Start).Line;
                bool shared = elements.Count(e => !e.IsPropertyElement && text.GetPosition(e.Start).Line == starts) > 1;
                return (starts, shared ? text.GetPosition(element.NameStart).Column + 1 : null);
            }
        }
        return null;
    }

    /// <summary>Whether an attribute sets something as its element is made: not a namespace, nor a directive read only by
    /// the compiler or the designer, each known by the namespace its prefix is declared for, whatever the prefix.</summary>
    internal static bool Sets(XmlAttribute attribute) =>
        attribute.Name != "xmlns" && attribute.Prefix != "xmlns"
        && (attribute.Prefix.Length == 0 || XamlFile.DeclaredUri(attribute.Owner, attribute.Prefix) switch
        {
            XamlDialect.DesignerUri or XamlDialect.CompatibilityUri => false,
            XamlDialect.XamlUri => attribute.LocalName is not ("Class" or "DataType" or "CompileBindings" or "ClassModifier" or "FieldModifier" or "Key" or "Shared"),
            _ => true,
        });
}
