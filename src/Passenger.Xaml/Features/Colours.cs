using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;
using Passenger.Xaml.Dialects;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Features;

/// <summary>How a colour is written, which a colour chosen for it is written back as.</summary>
public enum ColourForm
{
    /// <summary>XAML's or a string's <c>#RRGGBB</c>, a name where the colour has one.</summary>
    Text,

    /// <summary>C#'s <c>Color.FromRgb(r, g, b)</c> or <c>Color.FromArgb(a, r, g, b)</c>, the whole call.</summary>
    Call,

    /// <summary>C#'s <c>0xAARRGGBB</c> given to <c>Color.FromUInt32</c>.</summary>
    Number,

    /// <summary>C#'s <c>Colors.Name</c>, the whole member access.</summary>
    Named,
}

/// <summary>A colour a file writes or names: where it is, the colour, and where a colour chosen for it is written (in
/// <see cref="WritePath"/>, another file's for a resource a brush names, else this file) and in what form; no range to
/// write for one nothing can change in place (<c>Brushes.Red</c>).</summary>
public sealed record ColourUse(int Offset, Argb Colour, string? WritePath, TextRange? WriteRange, ColourForm Form, string Receiver = "Color");

/// <summary>The colours a XAML or C# file writes or names, and a colour chosen for one written back as it was written.</summary>
public static class Colours
{
    private static readonly HashSet<string> BrushTypes = new(StringComparer.Ordinal)
    {
        "Avalonia.Media.IBrush", "Avalonia.Media.Brush", "Avalonia.Media.ISolidColorBrush", "Avalonia.Media.SolidColorBrush", "Avalonia.Media.Color",
        "System.Windows.Media.Brush", "System.Windows.Media.SolidColorBrush", "System.Windows.Media.Color",
    };

    /// <summary>A XAML file's colours: a brush or colour property's literal, a Color element's text, and the colour of a
    /// resource a value names (its <paramref name="variant"/>'s definition where the theme has several), written back
    /// where that is defined.</summary>
    public static IReadOnlyList<ColourUse> InXaml(XamlFile file, string? variant)
    {
        List<ColourUse> found = [];
        XamlProject project = file.Project;
        XamlFileSummary own = project.Files.TryGetValue(file.Path, out XamlFileSummary? summary) ? summary : XamlFileSummary.Of(file.Path, file.Tree, project.ResolveSource);
        foreach (XmlElement element in file.Tree.Elements)
        {
            if (element.LocalName is "Color" or "SolidColorBrush" && element.Children.Count == 0 && element.Texts.Count == 1)
            {
                XmlText text = element.Texts[0];
                TextRange trimmed = Trim(file.Text, text.Range);
                if (KnownColours.Parse(file.Text[trimmed.Start..trimmed.End]) is { } literal)
                {
                    found.Add(new ColourUse(trimmed.Start, literal, file.Path, trimmed, ColourForm.Text));
                }
            }
            foreach (XmlAttribute attribute in element.Attributes)
            {
                if (!attribute.HasValue || attribute.Prefix == "xmlns" || attribute.Name == "xmlns")
                {
                    continue;
                }
                if (MarkupExtension.Parse(file.Text, attribute.ValueStart, attribute.ValueEnd) is { } extension)
                {
                    if (extension.LocalName is "StaticResource" or "DynamicResource" && extension.Argument(null) is { Nested: null, Value.Length: > 0 } key
                        && Resource(project, own, key.Value, variant, 2) is { } resource)
                    {
                        found.Add(resource with { Offset = key.ValueStart });
                    }
                    continue;
                }
                if (!TakesColour(file, attribute))
                {
                    continue;
                }
                TextRange value = Trim(file.Text, attribute.ValueRange);
                if (KnownColours.Parse(file.Text[value.Start..value.End]) is { } colour)
                {
                    found.Add(new ColourUse(value.Start, colour, file.Path, value, ColourForm.Text));
                }
            }
        }
        return found;
    }

    /// <summary>Whether an attribute's value is read as a colour or brush: a property of such a type, or a setter's value
    /// for one.</summary>
    private static bool TakesColour(XamlFile file, XmlAttribute attribute)
    {
        if (attribute.Owner.LocalName == "Setter" && attribute.Name == "Value")
        {
            return XamlCompletion.SetterProperty(file, attribute.Owner) is { } set && BrushTypes.Contains(file.Dialect.ValueTypeOf(set.ValueType).Name);
        }
        return file.MeaningOf(attribute).Property is { } property && BrushTypes.Contains(file.Dialect.ValueTypeOf(property.ValueType).Name);
    }

    /// <summary>The colour a resource key names, from its definition for the variant asked (else its first), following a
    /// definition that names another resource a few steps.</summary>
    private static ColourUse? Resource(XamlProject project, XamlFileSummary own, string key, string? variant, int steps)
    {
        IReadOnlyList<ResourceDefinition> definitions = project.Definitions(own, key);
        ResourceDefinition? chosen = definitions.FirstOrDefault(d => d.Variant == variant && d.Colour is not null) ?? definitions.FirstOrDefault(d => d.Colour is not null);
        if (chosen?.Colour is not { } written)
        {
            return null;
        }
        if (KnownColours.Parse(written) is { } colour)
        {
            return new ColourUse(0, colour, chosen.Path, chosen.ColourRange, ColourForm.Text);
        }
        string trimmed = written.Trim();
        if (steps > 0 && trimmed.StartsWith('{') && MarkupExtension.Parse(trimmed, 0, trimmed.Length) is { LocalName: "StaticResource" or "DynamicResource" } named
            && named.Argument(null) is { Value.Length: > 0 } next && project.Files.TryGetValue(chosen.Path, out XamlFileSummary? defining))
        {
            return Resource(project, defining, next.Value, variant, steps - 1);
        }
        return null;
    }

    private static TextRange Trim(string text, TextRange range)
    {
        int start = range.Start;
        int end = range.End;
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

    /// <summary>A C# file's colours: <c>Color.Parse("#…")</c> (and a brush's), <c>Color.FromRgb</c>, <c>Color.FromArgb</c>
    /// and <c>Color.FromUInt32</c> of constants, <c>Colors.Name</c> and <c>Brushes.Name</c>.</summary>
    public static IReadOnlyList<ColourUse> InCSharp(SyntaxNode root)
    {
        List<ColourUse> found = [];
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            switch (node)
            {
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } call:
                    Call(call, access, found);
                    break;
                case MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Colors" or "Brushes" } owner } member
                    when member.Parent is not InvocationExpressionSyntax && KnownColours.Parse(member.Name.Identifier.Text) is { } named:
                    bool colours = owner.Identifier.Text == "Colors";
                    found.Add(new ColourUse(member.SpanStart, named, null, colours ? new TextRange(member.SpanStart, member.Span.Length) : null, ColourForm.Named));
                    break;
            }
        }
        return found;
    }

    private static void Call(InvocationExpressionSyntax call, MemberAccessExpressionSyntax access, List<ColourUse> found)
    {
        string method = access.Name.Identifier.Text;
        string receiver = access.Expression.ToString();
        string type = receiver[(receiver.LastIndexOf('.') + 1)..];
        SeparatedSyntaxList<ArgumentSyntax> arguments = call.ArgumentList.Arguments;
        if (method == "Parse" && type is "Color" or "Brush" or "SolidColorBrush" && arguments.Count == 1
            && arguments[0].Expression is LiteralExpressionSyntax { Token.Value: string written } literal && KnownColours.Parse(written) is { } parsed)
        {
            TextRange inside = new(literal.SpanStart + 1, Math.Max(0, literal.Span.Length - 2));
            found.Add(new ColourUse(literal.SpanStart, parsed, null, literal.Token.Text.StartsWith('"') ? inside : null, ColourForm.Text));
            return;
        }
        if (type != "Color")
        {
            return;
        }
        if (method is "FromRgb" or "FromArgb" && arguments.Count == (method == "FromRgb" ? 3 : 4))
        {
            int[] values = [.. arguments.Select(a => Constant(a.Expression))];
            if (values.All(v => v is >= 0 and <= 255))
            {
                Argb colour = method == "FromRgb"
                    ? new Argb(255, (byte)values[0], (byte)values[1], (byte)values[2])
                    : new Argb((byte)values[0], (byte)values[1], (byte)values[2], (byte)values[3]);
                found.Add(new ColourUse(call.SpanStart, colour, null, new TextRange(call.SpanStart, call.Span.Length), ColourForm.Call, receiver));
            }
            return;
        }
        if (method == "FromUInt32" && arguments.Count == 1 && UInt(arguments[0].Expression) is { } value)
        {
            Argb colour = new((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            ExpressionSyntax argument = arguments[0].Expression;
            found.Add(new ColourUse(call.SpanStart, colour, null, new TextRange(argument.SpanStart, argument.Span.Length), ColourForm.Number));
        }
    }

    /// <summary>A small integer constant as written (a decimal or hex literal, maybe cast to byte); -1 for anything else.</summary>
    private static int Constant(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax { Token.Value: int value } => value,
        LiteralExpressionSyntax { Token.Value: uint value } when value <= 255 => (int)value,
        CastExpressionSyntax cast => Constant(cast.Expression),
        ParenthesizedExpressionSyntax inner => Constant(inner.Expression),
        _ => -1,
    };

    private static uint? UInt(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax { Token.Value: uint value } => value,
        LiteralExpressionSyntax { Token.Value: int value } when value >= 0 => (uint)value,
        LiteralExpressionSyntax { Token.Value: long value } when value is >= 0 and <= uint.MaxValue => (uint)value,
        _ => null,
    };

    /// <summary>A colour written as a use of it was: a name where the colour has one and the use wrote a name or C# names
    /// it, else <c>#RRGGBB</c> (with alpha first when not opaque); a call's arguments, a number's hex.</summary>
    public static string Written(ColourUse use, Argb colour, string written)
    {
        string? name = KnownColours.Names.Where(n => KnownColours.Parse(n.Value) == colour).Select(n => n.Name).FirstOrDefault();
        switch (use.Form)
        {
            case ColourForm.Named:
                return name is not null ? $"Colors.{name}" : Rgb(use.Receiver, colour);
            case ColourForm.Call:
                return Rgb(use.Receiver, colour);
            case ColourForm.Number:
                return $"0x{colour.A:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}";
        }
        bool wroteName = written.Length > 0 && !written.TrimStart().StartsWith('#');
        return wroteName && name is not null ? name : colour.Hex;
    }

    private static string Rgb(string receiver, Argb colour) => colour.A == 255
        ? string.Create(CultureInfo.InvariantCulture, $"{receiver}.FromRgb({colour.R}, {colour.G}, {colour.B})")
        : string.Create(CultureInfo.InvariantCulture, $"{receiver}.FromArgb({colour.A}, {colour.R}, {colour.G}, {colour.B})");
}
