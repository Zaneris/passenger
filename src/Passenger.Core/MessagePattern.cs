using System.Text;

namespace Passenger.Core;

/// <summary>What a message's argument is: a word put in as it comes (<c>{name}</c>), a number (<c>{n, number}</c>), a
/// count choosing its words by the language's plural rules (<c>{n, plural, one {…} other {…}}</c>), a value choosing them by
/// itself (<c>{kind, select, file {…} other {…}}</c>), or a moment's date or time (<c>{when, date}</c>, <c>{when, time}</c>).</summary>
public enum ArgumentKind
{
    Plain,
    Number,
    Plural,
    Select,
    Date,
    Time,
}

/// <summary>A run of a message: its words as written, an argument, or a plural case's <c>#</c>, the count it chooses by.</summary>
public abstract class MessagePart;

/// <summary>Words of the message as written, its quoting read.</summary>
public sealed class TextPart(string text) : MessagePart
{
    public string Text { get; } = text;
}

/// <summary>A plural case's <c>#</c>: the count it chose by, written as a number.</summary>
public sealed class CountPart : MessagePart;

/// <summary>An argument: its name, kind, style (a date's <c>short</c>, a number's <c>integer</c>; empty for none) and, for a
/// plural or a select, its cases.</summary>
public sealed class ArgumentPart(string name, ArgumentKind kind, string style, IReadOnlyList<MessageCase> cases) : MessagePart
{
    public string Name { get; } = name;

    public ArgumentKind Kind { get; } = kind;

    public string Style { get; } = style;

    public IReadOnlyList<MessageCase> Cases { get; } = cases;
}

/// <summary>One case of a plural or a select: its key (<c>one</c>, <c>other</c>, <c>=0</c>, a select's value) and its words.</summary>
public sealed class MessageCase(string key, IReadOnlyList<MessagePart> parts)
{
    public string Key { get; } = key;

    public IReadOnlyList<MessagePart> Parts { get; } = parts;
}

/// <summary>A message as ICU's MessageFormat writes it, the form translators' tools check: words, arguments by name,
/// plurals and selects, an apostrophe quoting a brace (<c>'{'</c>) or itself (<c>''</c>) and standing for itself anywhere
/// else, so "the folder's files" needs no quoting. Read once by the generator, which makes a parameter of each argument, and
/// by the words' resolution, which writes it in a language.</summary>
public sealed class MessagePattern
{
    /// <summary>The plural categories CLDR names, the keys a plural case takes besides <c>=N</c>.</summary>
    public static readonly IReadOnlyList<string> PluralCategories = ["zero", "one", "two", "few", "many", "other"];

    private readonly string text;
    private int at;

    private MessagePattern(string text, IReadOnlyList<MessagePart> parts, IReadOnlyList<(string Name, ArgumentKind Kind)> arguments)
    {
        this.text = text;
        Parts = parts;
        Arguments = arguments;
    }

    private MessagePattern(string text)
    {
        this.text = text;
        Parts = [];
        Arguments = [];
    }

    public IReadOnlyList<MessagePart> Parts { get; }

    /// <summary>Each argument once, in the order first written, with its kind: a plain mention of an argument another place
    /// writes as a number, plural or select takes that kind.</summary>
    public IReadOnlyList<(string Name, ArgumentKind Kind)> Arguments { get; }

    /// <summary>Reads a message, or throws <see cref="FormatException"/> naming where it went wrong.</summary>
    public static MessagePattern Parse(string text)
    {
        MessagePattern reading = new(text);
        List<MessagePart> parts = reading.Message(inPlural: false, nested: false);
        List<(string Name, ArgumentKind Kind)> arguments = [];
        Collect(parts, arguments);
        return new MessagePattern(text, parts, arguments);
    }

    private static void Collect(IReadOnlyList<MessagePart> parts, List<(string Name, ArgumentKind Kind)> into)
    {
        foreach (MessagePart part in parts)
        {
            if (part is not ArgumentPart argument)
            {
                continue;
            }
            int known = into.FindIndex(a => a.Name == argument.Name);
            if (known < 0)
            {
                into.Add((argument.Name, argument.Kind));
            }
            else if (into[known].Kind != argument.Kind && argument.Kind != ArgumentKind.Plain)
            {
                if (into[known].Kind != ArgumentKind.Plain && !(Counted(into[known].Kind) && Counted(argument.Kind)))
                {
                    throw new FormatException($"{{{argument.Name}}} is written both as {into[known].Kind.ToString().ToLowerInvariant()} and as {argument.Kind.ToString().ToLowerInvariant()}");
                }
                into[known] = (argument.Name, Counted(into[known].Kind) ? ArgumentKind.Plural : argument.Kind);
            }
            foreach (MessageCase @case in argument.Cases)
            {
                Collect(@case.Parts, into);
            }
        }
    }

    private static bool Counted(ArgumentKind kind) => kind is ArgumentKind.Number or ArgumentKind.Plural;

    /// <summary>Words and arguments up to the end, or up to the closing brace of the case they are inside.</summary>
    private List<MessagePart> Message(bool inPlural, bool nested)
    {
        List<MessagePart> parts = [];
        StringBuilder words = new();
        void Flush()
        {
            if (words.Length > 0)
            {
                parts.Add(new TextPart(words.ToString()));
                words.Clear();
            }
        }
        while (at < text.Length)
        {
            char c = text[at];
            if (c == '\'')
            {
                Quoted(words, inPlural);
            }
            else if (c == '{')
            {
                Flush();
                parts.Add(Argument());
            }
            else if (c == '}')
            {
                if (!nested)
                {
                    throw Wrong("a } closes nothing");
                }
                Flush();
                return parts;
            }
            else if (c == '#' && inPlural)
            {
                Flush();
                parts.Add(new CountPart());
                at++;
            }
            else
            {
                words.Append(c);
                at++;
            }
        }
        if (nested)
        {
            throw Wrong("a case's { is never closed");
        }
        Flush();
        return parts;
    }

    /// <summary>An apostrophe: two stand for one; one before a brace (or a plural's #) quotes up to the next lone one; any
    /// other stands for itself.</summary>
    private void Quoted(StringBuilder words, bool inPlural)
    {
        if (at + 1 < text.Length && text[at + 1] == '\'')
        {
            words.Append('\'');
            at += 2;
            return;
        }
        if (at + 1 >= text.Length || !(text[at + 1] is '{' or '}' || (inPlural && text[at + 1] == '#')))
        {
            words.Append('\'');
            at++;
            return;
        }
        at++;
        while (at < text.Length)
        {
            if (text[at] == '\'')
            {
                if (at + 1 < text.Length && text[at + 1] == '\'')
                {
                    words.Append('\'');
                    at += 2;
                    continue;
                }
                at++;
                return;
            }
            words.Append(text[at]);
            at++;
        }
    }

    private ArgumentPart Argument()
    {
        at++;
        Blanks();
        string name = Name("an argument's name");
        Blanks();
        if (Take('}'))
        {
            return new ArgumentPart(name, ArgumentKind.Plain, string.Empty, []);
        }
        Expect(',');
        Blanks();
        string kindWord = Name("an argument's kind");
        Blanks();
        ArgumentKind kind = kindWord switch
        {
            "number" => ArgumentKind.Number,
            "plural" => ArgumentKind.Plural,
            "select" => ArgumentKind.Select,
            "date" => ArgumentKind.Date,
            "time" => ArgumentKind.Time,
            _ => throw Wrong($"{{{name}, {kindWord}}} is no kind of argument: number, plural, select, date or time"),
        };
        if (kind is ArgumentKind.Plural or ArgumentKind.Select)
        {
            Expect(',');
            return new ArgumentPart(name, kind, string.Empty, Cases(name, kind));
        }
        string style = string.Empty;
        if (Take(','))
        {
            Blanks();
            style = Name("an argument's style");
            Blanks();
            bool known = kind == ArgumentKind.Number ? style is "integer" : style is "short" or "medium" or "long" or "full";
            if (!known)
            {
                throw Wrong($"{{{name}, {kindWord}, {style}}} is no style of {kindWord}");
            }
        }
        Expect('}');
        return new ArgumentPart(name, kind, style, []);
    }

    private List<MessageCase> Cases(string name, ArgumentKind kind)
    {
        List<MessageCase> cases = [];
        while (true)
        {
            Blanks();
            if (Take('}'))
            {
                break;
            }
            string key = Take('=') ? "=" + Digits() : Name("a case's key");
            if (kind == ArgumentKind.Plural && !key.StartsWith("=", StringComparison.Ordinal) && !PluralCategories.Contains(key))
            {
                throw Wrong($"{{{name}}}'s case {key} is no plural category: zero, one, two, few, many, other or =N");
            }
            if (cases.Exists(c => c.Key == key))
            {
                throw Wrong($"{{{name}}} has two cases {key}");
            }
            Blanks();
            Expect('{');
            cases.Add(new MessageCase(key, Message(inPlural: kind == ArgumentKind.Plural, nested: true)));
            at++;
        }
        if (!cases.Exists(c => c.Key == "other"))
        {
            throw Wrong($"{{{name}}} has no case other");
        }
        return cases;
    }

    /// <summary>A name of ASCII letters, digits and underscores, not starting with a digit, so it is a C# parameter's too.</summary>
    private string Name(string what)
    {
        int start = at;
        while (at < text.Length && (text[at] is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
        {
            at++;
        }
        if (at == start || text[start] is >= '0' and <= '9')
        {
            throw Wrong($"{what} is missing");
        }
        return text.Substring(start, at - start);
    }

    private string Digits()
    {
        int start = at;
        while (at < text.Length && text[at] is >= '0' and <= '9')
        {
            at++;
        }
        return at > start ? text.Substring(start, at - start) : throw Wrong("an = takes a number");
    }

    private void Blanks()
    {
        while (at < text.Length && char.IsWhiteSpace(text[at]))
        {
            at++;
        }
    }

    private bool Take(char c)
    {
        if (at < text.Length && text[at] == c)
        {
            at++;
            return true;
        }
        return false;
    }

    private void Expect(char c)
    {
        if (!Take(c))
        {
            throw Wrong(at < text.Length ? $"{c} was expected where {text[at]} is" : $"{c} was expected at its end");
        }
    }

    private FormatException Wrong(string why) => new($"{why} (at {at + 1} of \"{text}\")");
}
