using Passenger.Text;

namespace Passenger.Xaml.Syntax;

/// <summary>A markup extension written in an attribute's value (<c>{Binding Name, Mode=TwoWay}</c>,
/// <c>{StaticResource Key}</c>), with its arguments, those nested in it (<c>Converter={StaticResource X}</c>) included,
/// each where it stands in the file. Read as far as it goes while it is typed.</summary>
public sealed class MarkupExtension
{
    /// <summary>How deep extensions are read inside one another; one deeper holds the rest of the value unread. Far past any
    /// XAML written by hand, and short of the stack.</summary>
    private const int Deepest = 64;

    private readonly List<MarkupArgument> arguments = [];

    private MarkupExtension(int start) => Start = start;

    /// <summary>The offset of its <c>{</c>.</summary>
    public int Start { get; }

    /// <summary>After its <c>}</c>, or where the value stopped holding it.</summary>
    public int End { get; private set; }

    public bool IsClosed { get; private set; }

    /// <summary>Its name as written (<c>Binding</c>, <c>x:Static</c>), empty while none is typed.</summary>
    public string Name { get; private set; } = string.Empty;

    public int NameStart { get; private set; }

    public int NameEnd { get; private set; }

    public TextRange NameRange => TextRange.FromBounds(NameStart, NameEnd);

    public string Prefix => Name.IndexOf(':') is var colon and >= 0 ? Name[..colon] : string.Empty;

    public string LocalName => Name.IndexOf(':') is var colon and >= 0 ? Name[(colon + 1)..] : Name;

    public IReadOnlyList<MarkupArgument> Arguments => arguments;

    /// <summary>The argument named so (<c>Mode</c>), or the first positional one for null.</summary>
    public MarkupArgument? Argument(string? key) => key is null
        ? arguments.FirstOrDefault(a => a.Key is null)
        : arguments.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.Ordinal));

    /// <summary>Reads the markup extension a value holds, between <paramref name="start"/> and <paramref name="end"/> of the
    /// text; null for a value that is not one (no <c>{</c> first, or the <c>{}</c> that escapes a literal brace).</summary>
    public static MarkupExtension? Parse(string text, int start, int end)
    {
        int at = start;
        while (at < end && char.IsWhiteSpace(text[at]))
        {
            at++;
        }
        if (at >= end || text[at] != '{' || (at + 1 < end && text[at + 1] == '}'))
        {
            return null;
        }
        return Read(text, ref at, end, depth: 0);
    }

    /// <summary>The innermost extension holding an offset: itself, or one nested in its arguments.</summary>
    public MarkupExtension InnermostAt(int offset)
    {
        foreach (MarkupArgument argument in arguments)
        {
            if (argument.Nested is { } nested && offset > nested.Start && (offset < nested.End || (offset == nested.End && !nested.IsClosed)))
            {
                return nested.InnermostAt(offset);
            }
        }
        return this;
    }

    /// <summary>The argument whose key or value holds an offset, if any.</summary>
    public MarkupArgument? ArgumentAt(int offset) =>
        arguments.FirstOrDefault(a => (a.Key is not null && offset >= a.KeyStart && offset <= a.KeyEnd) || (offset >= a.ValueStart && offset <= a.ValueEnd));

    private static MarkupExtension Read(string text, ref int at, int end, int depth)
    {
        MarkupExtension extension = new(at);
        at++;
        SkipSpace(text, ref at, end);
        extension.NameStart = at;
        while (at < end && (MarkupNames.IsNameChar(text[at]) || text[at] == '+'))
        {
            at++;
        }
        extension.NameEnd = at;
        extension.Name = text[extension.NameStart..at];
        if (depth == Deepest || !StackLeft.Enough())
        {
            at = end;
            extension.End = end;
            return extension;
        }
        int positional = 0;
        while (true)
        {
            SkipSpace(text, ref at, end);
            if (at >= end)
            {
                extension.End = end;
                return extension;
            }
            if (text[at] == '}')
            {
                at++;
                extension.End = at;
                extension.IsClosed = true;
                return extension;
            }
            if (text[at] == ',')
            {
                at++;
                continue;
            }
            MarkupArgument argument = ReadArgument(text, ref at, end, depth);
            if (argument.Key is null)
            {
                argument.Index = positional++;
            }
            extension.arguments.Add(argument);
        }
    }

    private static MarkupArgument ReadArgument(string text, ref int at, int end, int depth)
    {
        MarkupArgument argument = new();
        int first = at;
        if (text[at] != '{' && text[at] is not ('\'' or '"'))
        {
            // A key is a name before '='; a positional value is anything up to ',' or '}'.
            int scan = at;
            while (scan < end && (MarkupNames.IsNameChar(text[scan]) || text[scan] == '+'))
            {
                scan++;
            }
            int afterName = scan;
            SkipSpace(text, ref scan, end);
            if (scan < end && text[scan] == '=' && afterName > at)
            {
                argument.Key = text[at..afterName];
                argument.KeyStart = at;
                argument.KeyEnd = afterName;
                at = scan + 1;
                SkipSpace(text, ref at, end);
                first = at;
            }
        }
        if (at < end && text[at] == '{' && !(at + 1 < end && text[at + 1] == '}'))
        {
            MarkupExtension nested = Read(text, ref at, end, depth + 1);
            argument.Nested = nested;
            argument.ValueStart = nested.Start;
            argument.ValueEnd = nested.End;
            argument.Value = text[nested.Start..nested.End];
            return argument;
        }
        if (at < end && text[at] is '\'' or '"')
        {
            char quote = text[at];
            int close = text.IndexOf(quote, at + 1, end - at - 1);
            argument.ValueStart = at + 1;
            argument.ValueEnd = close < 0 ? end : close;
            argument.Value = text[argument.ValueStart..argument.ValueEnd];
            at = close < 0 ? end : close + 1;
            return argument;
        }
        argument.ValueStart = first;
        System.Text.StringBuilder value = new();
        while (at < end && text[at] is not (',' or '}'))
        {
            if (text[at] == '\\' && at + 1 < end)
            {
                value.Append(text[at + 1]);
                at += 2;
                continue;
            }
            value.Append(text[at]);
            at++;
        }
        int valueEnd = at;
        while (valueEnd > argument.ValueStart && char.IsWhiteSpace(text[valueEnd - 1]))
        {
            valueEnd--;
        }
        argument.ValueEnd = valueEnd;
        argument.Value = value.ToString().Trim();
        return argument;
    }

    private static void SkipSpace(string text, ref int at, int end)
    {
        while (at < end && char.IsWhiteSpace(text[at]))
        {
            at++;
        }
    }

    public override string ToString() => $"{{{Name} {string.Join(", ", arguments)}}}";
}

/// <summary>One argument of a markup extension: named (<c>Mode=TwoWay</c>) or positional (its <see cref="Index"/>), its
/// value as written or a markup extension of its own.</summary>
public sealed class MarkupArgument
{
    public string? Key { get; internal set; }

    public int KeyStart { get; internal set; }

    public int KeyEnd { get; internal set; }

    /// <summary>The place among the positional arguments; -1 for a named one.</summary>
    public int Index { get; internal set; } = -1;

    public int ValueStart { get; internal set; }

    public int ValueEnd { get; internal set; }

    public TextRange ValueRange => TextRange.FromBounds(ValueStart, ValueEnd);

    public string Value { get; internal set; } = string.Empty;

    public MarkupExtension? Nested { get; internal set; }

    public override string ToString() => Key is null ? Value : $"{Key}={Value}";
}
