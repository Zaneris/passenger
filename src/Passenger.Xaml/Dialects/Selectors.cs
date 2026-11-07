using Passenger.Xaml.Metadata;
using Passenger.Xaml.Syntax;

namespace Passenger.Xaml.Dialects;

/// <summary>What a piece of an Avalonia style selector is.</summary>
public enum SelectorPart
{
    Type,
    Class,
    Name,
    PseudoClass,
    Property,
    Value,
    Template,
    Child,
    Descendant,
    Or,
    Nesting,
    Open,
    Close,
}

/// <summary>A piece of a selector, from <see cref="Start"/> to <see cref="End"/> in its text; a type's text keeps its
/// <c>prefix|</c>.</summary>
public sealed record SelectorToken(SelectorPart Kind, int Start, int End, string Text);

/// <summary>Where a caret is in a selector, for completion: what is being written there, from where, and the type of the
/// selector it belongs to so far (the type a class, pseudo-class or property of it is looked up on).</summary>
public sealed record SelectorPlace(SelectorPart Kind, int Start, string? Type);

/// <summary>Avalonia's style selectors (<c>Button.accent:pointerover /template/ ContentPresenter#PART_ContentPresenter</c>):
/// read into their pieces, the type a style's setters set properties of, and what a caret in one is writing.</summary>
public static class Selectors
{
    public static IReadOnlyList<SelectorToken> Tokenize(string text)
    {
        List<SelectorToken> tokens = [];
        int at = 0;
        while (at < text.Length)
        {
            char c = text[at];
            int start = at;
            if (char.IsWhiteSpace(c))
            {
                while (at < text.Length && char.IsWhiteSpace(text[at]))
                {
                    at++;
                }
                // Space between two compound selectors is the descendant combinator; around another it is nothing.
                if (tokens.Count > 0 && at < text.Length && tokens[^1].Kind is not (SelectorPart.Child or SelectorPart.Or or SelectorPart.Template or SelectorPart.Open)
                    && text[at] is not ('>' or ',' or ')' or '/'))
                {
                    tokens.Add(new SelectorToken(SelectorPart.Descendant, start, at, " "));
                }
                continue;
            }
            switch (c)
            {
                case '.':
                    at = Name(text, at + 1);
                    tokens.Add(new SelectorToken(SelectorPart.Class, start, at, text[(start + 1)..at]));
                    break;
                case '#':
                    at = Name(text, at + 1);
                    tokens.Add(new SelectorToken(SelectorPart.Name, start, at, text[(start + 1)..at]));
                    break;
                case ':':
                    at = Name(text, at + 1);
                    tokens.Add(new SelectorToken(SelectorPart.PseudoClass, start, at, text[start..at]));
                    break;
                case '[':
                    int close = text.IndexOf(']', at);
                    int end = close < 0 ? text.Length : close;
                    int equals = text.IndexOf('=', at, end - at);
                    int nameEnd = equals < 0 ? end : equals;
                    tokens.Add(new SelectorToken(SelectorPart.Property, start + 1, nameEnd, text[(start + 1)..nameEnd].Trim().Trim('(', ')')));
                    if (equals >= 0)
                    {
                        tokens.Add(new SelectorToken(SelectorPart.Value, equals + 1, end, text[(equals + 1)..end].Trim()));
                    }
                    at = close < 0 ? text.Length : close + 1;
                    break;
                case '/':
                    int closing = text.IndexOf('/', at + 1);
                    at = closing < 0 ? text.Length : closing + 1;
                    tokens.Add(new SelectorToken(SelectorPart.Template, start, at, text[start..at]));
                    break;
                case '>':
                    at++;
                    tokens.Add(new SelectorToken(SelectorPart.Child, start, at, ">"));
                    break;
                case ',':
                    at++;
                    tokens.Add(new SelectorToken(SelectorPart.Or, start, at, ","));
                    break;
                case '^':
                    at++;
                    tokens.Add(new SelectorToken(SelectorPart.Nesting, start, at, "^"));
                    break;
                case '(':
                    at++;
                    tokens.Add(new SelectorToken(SelectorPart.Open, start, at, "("));
                    break;
                case ')':
                    at++;
                    tokens.Add(new SelectorToken(SelectorPart.Close, start, at, ")"));
                    break;
                default:
                    at = Name(text, at, typeName: true);
                    if (at == start)
                    {
                        at++;
                        continue;
                    }
                    tokens.Add(new SelectorToken(SelectorPart.Type, start, at, text[start..at]));
                    break;
            }
        }
        return tokens;
    }

    private static int Name(string text, int at, bool typeName = false)
    {
        while (at < text.Length && (char.IsLetterOrDigit(text[at]) || text[at] is '_' or '-' || (typeName && text[at] == '|')))
        {
            at++;
        }
        return at;
    }

    /// <summary>The type a selector's first alternative ends at, in a style at <paramref name="scope"/>: the last type
    /// written (one inside <c>:is()</c> too); null for a nested selector with none, whose target is its parent style's.</summary>
    public static ClrType? TargetOf(XamlFile file, XmlElement scope, string selector)
    {
        IReadOnlyList<SelectorToken> tokens = Tokenize(selector);
        int depth = 0;
        string? type = null;
        bool nested = false;
        foreach (SelectorToken token in tokens)
        {
            switch (token.Kind)
            {
                case SelectorPart.Open:
                    depth++;
                    break;
                case SelectorPart.Close:
                    depth--;
                    break;
                case SelectorPart.Or when depth == 0:
                    goto done;
                case SelectorPart.Nesting:
                    nested = true;
                    break;
                case SelectorPart.Type:
                    type = token.Text;
                    break;
                case SelectorPart.Child or SelectorPart.Descendant or SelectorPart.Template when depth == 0:
                    type = null;
                    nested = false;
                    break;
            }
        }
    done:
        if (type is not null)
        {
            return file.ResolveType(scope, type.Replace('|', ':'));
        }
        return nested && scope.Parent is not null ? null : file.Types.Find("Avalonia.Controls.Control");
    }

    /// <summary>What a caret at <paramref name="at"/> in a selector is writing.</summary>
    public static SelectorPlace PlaceAt(string selector, int at)
    {
        IReadOnlyList<SelectorToken> tokens = Tokenize(selector[..at]);
        string? type = null;
        foreach (SelectorToken token in tokens)
        {
            if (token.End < at || (token.End == at && token.Kind is SelectorPart.Child or SelectorPart.Descendant or SelectorPart.Or or SelectorPart.Template or SelectorPart.Open))
            {
                if (token.Kind == SelectorPart.Type)
                {
                    type = token.Text;
                }
                else if (token.Kind is SelectorPart.Child or SelectorPart.Descendant or SelectorPart.Or or SelectorPart.Template)
                {
                    type = null;
                }
            }
        }
        SelectorToken? last = tokens.Count > 0 ? tokens[^1] : null;
        if (last is null || last.End < at || last.Kind is SelectorPart.Child or SelectorPart.Descendant or SelectorPart.Or or SelectorPart.Template or SelectorPart.Open
            or SelectorPart.Close or SelectorPart.Nesting)
        {
            return new SelectorPlace(SelectorPart.Type, at, type);
        }
        return last.Kind switch
        {
            SelectorPart.Class or SelectorPart.Name => new SelectorPlace(last.Kind, last.Start + 1, type),
            SelectorPart.PseudoClass => new SelectorPlace(SelectorPart.PseudoClass, last.Start, type),
            SelectorPart.Property => new SelectorPlace(SelectorPart.Property, last.Start, type),
            SelectorPart.Value => new SelectorPlace(SelectorPart.Value, last.Start, type),
            _ => new SelectorPlace(SelectorPart.Type, last.Start, type),
        };
    }

    /// <summary>The pseudo-classes a type and its bases declare (<c>[PseudoClasses(":pressed")]</c>), and those every
    /// control has.</summary>
    public static IEnumerable<string> PseudoClassesOf(TypeSystem types, ClrType? type)
    {
        HashSet<string> found = new(StringComparer.Ordinal) { ":pointerover", ":focus", ":focus-within", ":focus-visible", ":disabled", ":selected" };
        if (type is not null)
        {
            foreach (ClrType at in types.SelfAndBases(type))
            {
                foreach (ClrAttribute attribute in at.AttributesNamed("PseudoClasses"))
                {
                    foreach (object? argument in attribute.Arguments)
                    {
                        switch (argument)
                        {
                            case string one:
                                found.Add(one);
                                break;
                            case object?[] many:
                                found.UnionWith(many.OfType<string>());
                                break;
                        }
                    }
                }
            }
        }
        return found.Order(StringComparer.Ordinal);
    }
}
