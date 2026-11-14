using Passenger.Xaml.Dialects;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>A C# expression in an attribute's value, as its dialect's compiler reads one (MAUI's from .NET 11): its code,
/// the class the file makes (<c>x:Class</c>, whose members the code names as they are), the <c>x:DataType</c> in scope
/// (whose members the compiler binds the code to), and for an event's handler the element's type and the event.
/// Types are named by their full metadata names.</summary>
public sealed record XamlExpression(XmlAttribute Attribute, MauiExpressionCode Code, string? RootType, string? DataType, string? EventOwner, string? EventName)
{
    /// <summary>Where the value starts in the file, which the code's places count from.</summary>
    public int ValueStart => Attribute.ValueStart;

    /// <summary>Each type the code names by a XAML prefix (<c>local:Helper</c>) that resolves, by its C# name
    /// (<c>global::App.Helper</c>), as the generator writes it.</summary>
    public IReadOnlyDictionary<string, string> Types { get; init; } = new Dictionary<string, string>();
}

/// <summary>The C# expressions of a XAML file whose dialect reads them (<see cref="XamlDialect.IsExpression"/>), and the
/// one a caret is in as it is typed, its closing brace perhaps not yet written.</summary>
public static class XamlExpressions
{
    /// <summary>Whether an attribute's value is an expression its dialect's compiler reads, not a markup extension.</summary>
    public static bool Is(XamlFile file, XmlAttribute attribute) =>
        attribute.HasValue && file.Dialect.IsExpression(attribute.Value, name => NamesExtension(file, attribute.Owner, name));

    /// <summary>Every expression the file writes, in the order it writes them.</summary>
    public static IReadOnlyList<XamlExpression> In(XamlFile file)
    {
        List<XamlExpression> found = [];
        foreach (XmlElement element in file.Tree.Elements)
        {
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (attribute.Quote != '\0' && Is(file, attribute) && Of(file, attribute) is { } expression)
                {
                    found.Add(expression);
                }
            }
        }
        return found;
    }

    /// <summary>The expression a caret is in, between its braces, as it is typed: a value still unclosed is read as it would
    /// be once closed. <c>Head</c> says the caret is in the name the code starts with (or where one starts), which may
    /// still become a markup extension's as more is typed. Null outside an expression.</summary>
    public static (XamlExpression Expression, bool Head)? At(XamlFile file, int offset)
    {
        XmlPlace place = file.Tree.PlaceAt(offset);
        if (!file.Dialect.ReadsExpressions || place.Kind != XmlPlaceKind.AttributeValue || place.Attribute is not { } attribute || MauiExpressionCode.Of(attribute.RawValue) is not { } code)
        {
            return null;
        }
        int at = offset - attribute.ValueStart;
        // No C# starts with a brace: a value of two is a markup extension's, its first brace typed before it.
        if (at < code.Start || at > code.End || code.Code.StartsWith('{'))
        {
            return null;
        }
        string value = attribute.Value.Trim();
        string closed = value.EndsWith('}') ? value : value + "}";
        bool head = InHead(code.Code, code.CodeAt(at));
        if (!head && !file.Dialect.IsExpression(closed, name => NamesExtension(file, attribute.Owner, name)))
        {
            return null;
        }
        return Of(file, attribute, code) is { } expression ? (expression, head) : null;
    }

    private static XamlExpression? Of(XamlFile file, XmlAttribute attribute) =>
        MauiExpressionCode.Of(attribute.RawValue) is { } code ? Of(file, attribute, code) : null;

    /// <summary>An attribute's expression; null where the <c>x:DataType</c> in scope names a type nothing resolves, whose
    /// members the code may name.</summary>
    private static XamlExpression? Of(XamlFile file, XmlAttribute attribute, MauiExpressionCode code)
    {
        XamlAttributeMeaning meaning = file.MeaningOf(attribute);
        bool handler = meaning.Kind == XamlAttributeKind.Event && meaning.Event is not null;
        string? data = handler ? null : file.DataTypeOf(attribute)?.FullName;
        if (!handler && data is null && file.CompilesBindingsAt(attribute.Owner))
        {
            return null;
        }
        Dictionary<string, string> types = new(StringComparer.Ordinal);
        foreach (string named in MauiExpressionCode.PrefixedNames(code.Code))
        {
            if (file.ResolveType(attribute.Owner, named) is { } type)
            {
                types[named] = "global::" + type.FullName.Replace('+', '.');
            }
        }
        return new XamlExpression(attribute, code, file.ClassType?.FullName ?? file.ClassName, data,
            handler ? file.ObjectOf(attribute.Owner)?.FullName : null, handler ? meaning.Event!.Name : null)
        {
            Types = types,
        };
    }

    /// <summary>Whether a code's place is in the name it starts with, or where a name would start in code still empty.</summary>
    private static bool InHead(string code, int at)
    {
        int end = 0;
        while (end < code.Length && (char.IsLetterOrDigit(code[end]) || code[end] == '_'))
        {
            end++;
        }
        return at <= end && (end == code.Length || code[end] != ':');
    }

    /// <summary>Whether a name in braces is one of the file's markup extensions at an element, as its compiler resolves one.</summary>
    public static bool NamesExtension(XamlFile file, XmlElement element, string name) =>
        file.ResolveType(element, name, extension: true) is { } made && file.Dialect.IsMarkupExtension(file.Types, made);
}
