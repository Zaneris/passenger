using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Resources;
using System.Text;

namespace Passenger.Core;

/// <summary>Words a person or an agent reads, kept as what they say (a resource's key and its arguments) until they are said
/// in a language: <see cref="Shown"/> in the person's (<see cref="UiLanguage.InForce"/>), <see cref="English"/> for an
/// agent and a log, so one refusal reads in both. Each resource's words are a message as ICU's MessageFormat writes it
/// (<see cref="MessagePattern"/>): its arguments named, a count choosing its words by the language's CLDR plural rules,
/// numbers and dates written in the asked culture. A translation is read from the nearest carried culture of the chain that
/// gives the key, and passed over where its arguments are not the English's. A string converts into verbatim words: a
/// name, said as it is in every language. A while, a size, a number or a list is said by each language's own patterns
/// (<see cref="LanguagePatterns"/>), Passenger's own in English.</summary>
public sealed class Words : IEquatable<Words>
{
    private static readonly ConcurrentDictionary<(ResourceManager Resources, string Culture), ResourceSet?> Sets = new();
    private static readonly ConcurrentDictionary<string, MessagePattern?> Patterns = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(ResourceManager Resources, string Key, string Culture), bool> Fits = new();

    private readonly ResourceManager? resources;
    private readonly string name;
    private readonly (string Name, object? Value)[] arguments;
    private readonly ISaid? patterned;

    /// <summary>A resource's words with its arguments by name, as the generated members make them.</summary>
    public Words(ResourceManager resources, string name, params (string Name, object? Value)[] arguments)
    {
        this.resources = resources;
        this.name = name;
        this.arguments = arguments;
    }

    private Words(string verbatim)
    {
        name = verbatim;
        arguments = [];
    }

    private Words(ISaid said)
    {
        name = string.Empty;
        arguments = [];
        patterned = said;
    }

    /// <summary>Words said by each language's own patterns rather than a resource's message.</summary>
    internal static Words Of(ISaid said) => new(said);

    /// <summary>Names as verbatim words each, for a list said in the person's language
    /// (<see cref="Plural.Listed(IReadOnlyList{Words})"/>).</summary>
    public static IReadOnlyList<Words> Names(IEnumerable<string> names) => [.. names.Select(Verbatim)];

    /// <summary>Verbatim words: a name, a path, a program's message, said as written in every language.</summary>
    public static Words Verbatim(string text) => new(text);

    /// <summary>Verbatim words, as <see cref="Verbatim"/> makes them; none from none.</summary>
    [return: NotNullIfNotNull(nameof(verbatim))]
    public static implicit operator Words?(string? verbatim) => verbatim is null ? null : new(verbatim);

    /// <summary>Whether these are verbatim words rather than a resource's or a language's patterns'.</summary>
    public bool IsVerbatim => resources is null && patterned is null;

    /// <summary>Whether these are verbatim words with nothing in them: nothing to say.</summary>
    public bool IsEmpty => IsVerbatim && name.Length == 0;

    /// <summary>The resource's key, or the verbatim text.</summary>
    public string Key => name;

    /// <summary>The words in the person's language.</summary>
    public string Shown => In(UiLanguage.InForce);

    /// <summary>The words in English, as an agent's answer and a log say them.</summary>
    public string English => In(UiLanguage.English);

    /// <summary>The words in <paramref name="language"/>.</summary>
    public string In(UiLanguage language)
    {
        if (patterned is not null)
        {
            string text = patterned.In(language);
            return language.Pseudo is null ? text : PseudoWords.Padded(PseudoWords.Accented(text));
        }
        if (resources is null)
        {
            return name;
        }
        (MessagePattern pattern, CultureInfo written) = Pattern(language);
        StringBuilder said = new();
        Write(said, pattern.Parts, language, written, null);
        return language.Pseudo is null ? said.ToString() : PseudoWords.Padded(said.ToString());
    }

    public override string ToString() => Shown;

    public bool Equals(Words? other) => other is not null && ReferenceEquals(resources, other.resources) && name == other.name
        && Equals(patterned, other.patterned) && arguments.SequenceEqual(other.arguments);

    public override bool Equals(object? obj) => obj is Words other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(resources, name, patterned, arguments.Length);

    /// <summary>Whether both are the same words (<see cref="Equals(Words?)"/>), as a refusal compared with the one expected is.</summary>
    public static bool operator ==(Words? left, Words? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Words? left, Words? right) => !(left == right);

    /// <summary>The message read in the nearest culture of <paramref name="language"/>'s chain that gives the key with the
    /// English's arguments, else the English, and the culture it is written in (the invariant one for English).</summary>
    private (MessagePattern Pattern, CultureInfo Written) Pattern(UiLanguage language)
    {
        MessagePattern english = Parsed(Set(CultureInfo.InvariantCulture)?.GetString(name)
            ?? throw new MissingManifestResourceException($"{resources!.BaseName} has no words named {name}"))
            ?? throw new FormatException($"{resources!.BaseName}'s {name} is no message");
        if (language.Reached is null)
        {
            return (english, CultureInfo.InvariantCulture);
        }
        foreach (CultureInfo step in UiLanguage.Chain(language.Asked))
        {
            if (Set(step)?.GetString(name) is { } translated && Parsed(translated) is { } pattern && Fit(step, english, pattern))
            {
                return (pattern, step);
            }
        }
        return (english, CultureInfo.InvariantCulture);
    }

    /// <summary>The culture's own set of the resource's words, without its parents', kept once looked for.</summary>
    private ResourceSet? Set(CultureInfo culture) => Sets.GetOrAdd((resources!, culture.Name), key => key.Resources.GetResourceSet(culture, true, false));

    private static MessagePattern? Parsed(string text) => Patterns.GetOrAdd(text, read =>
    {
        try
        {
            return MessagePattern.Parse(read);
        }
        catch (FormatException)
        {
            return null;
        }
    });

    /// <summary>Whether a translation names the English's arguments, no more and no fewer.</summary>
    private bool Fit(CultureInfo culture, MessagePattern english, MessagePattern translated) => Fits.GetOrAdd((resources!, name, culture.Name), _ =>
        english.Arguments.Select(a => a.Name).Order(StringComparer.Ordinal).SequenceEqual(translated.Arguments.Select(a => a.Name).Order(StringComparer.Ordinal)));

    private void Write(StringBuilder said, IReadOnlyList<MessagePart> parts, UiLanguage language, CultureInfo written, string? count)
    {
        foreach (MessagePart part in parts)
        {
            switch (part)
            {
                case TextPart text:
                    said.Append(language.Pseudo is null ? text.Text : PseudoWords.Accented(text.Text));
                    break;
                case CountPart:
                    said.Append(count);
                    break;
                case ArgumentPart argument:
                    WriteArgument(said, argument, Value(argument.Name), language, written);
                    break;
            }
        }
    }

    private void WriteArgument(StringBuilder said, ArgumentPart argument, object? value, UiLanguage language, CultureInfo written)
    {
        switch (argument.Kind)
        {
            case ArgumentKind.Plural:
                double number = Number(value);
                string exactly = "=" + number.ToString("0.###", CultureInfo.InvariantCulture);
                string category = PluralRules.Of(written, number, Places(number));
                MessageCase chosen = argument.Cases.FirstOrDefault(c => c.Key == exactly)
                    ?? argument.Cases.FirstOrDefault(c => c.Key == category) ?? argument.Cases.First(c => c.Key == "other");
                Write(said, chosen.Parts, language, written, Number(number, language, argument.Style));
                break;
            case ArgumentKind.Select:
                string choice = value?.ToString() ?? string.Empty;
                Write(said, (argument.Cases.FirstOrDefault(c => c.Key == choice) ?? argument.Cases.First(c => c.Key == "other")).Parts, language, written, null);
                break;
            case ArgumentKind.Number:
                said.Append(Number(Number(value), language, argument.Style));
                break;
            case ArgumentKind.Date or ArgumentKind.Time:
                said.Append(Moment(value, argument.Kind, argument.Style, language));
                break;
            default:
                said.Append(value switch
                {
                    null => string.Empty,
                    Words words => words.In(language),
                    string text => text,
                    DateTimeOffset or DateTime => Moment(value, ArgumentKind.Date, "short", language),
                    IConvertible convertible when convertible.GetTypeCode() is >= TypeCode.SByte and <= TypeCode.Decimal => Number(Number(value), language, string.Empty),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture),
                });
                break;
        }
    }

    private object? Value(string argument)
    {
        foreach ((string named, object? value) in arguments)
        {
            if (named == argument)
            {
                return value;
            }
        }
        throw new ArgumentException($"{resources!.BaseName}'s {name} takes {{{argument}}}, which it was not given");
    }

    private static double Number(object? value) => value is IConvertible convertible ? convertible.ToDouble(CultureInfo.InvariantCulture) : 0;

    /// <summary>The digits after the point a number is written with, at most three.</summary>
    private static int Places(double number)
    {
        string written = number.ToString("0.###", CultureInfo.InvariantCulture);
        int point = written.IndexOf('.', StringComparison.Ordinal);
        return point < 0 ? 0 : written.Length - point - 1;
    }

    /// <summary>A number as the language writes it: Passenger's own digits for English (<see cref="Numbers"/>), else the
    /// asked culture's, a whole number for the integer style.</summary>
    private static string Number(double number, UiLanguage language, string style)
    {
        double shown = style == "integer" ? Math.Round(number, MidpointRounding.AwayFromZero) : number;
        return language.Reached is null ? Numbers.Said(shown, 3) : shown.ToString("#,0.###", language.Formats);
    }

    private static string Moment(object? value, ArgumentKind kind, string style, UiLanguage language)
    {
        DateTimeOffset moment = value switch
        {
            DateTimeOffset offset => offset,
            DateTime time => new DateTimeOffset(time),
            _ => default,
        };
        DateTimeFormatInfo format = language.Formats.DateTimeFormat;
        string pattern = (kind, style) switch
        {
            (ArgumentKind.Date, "long" or "full") => format.LongDatePattern,
            (ArgumentKind.Date, "medium") => format.LongDatePattern.Replace("dddd, ", string.Empty, StringComparison.Ordinal),
            (ArgumentKind.Date, _) => format.ShortDatePattern,
            (_, "short" or "") => format.ShortTimePattern,
            _ => format.LongTimePattern,
        };
        return moment.ToString(pattern, language.Formats);
    }
}

/// <summary>What words say by a language's own patterns rather than a resource's message (a while, a size, a number, a list),
/// equal to another saying the same.</summary>
internal interface ISaid
{
    /// <summary>What it says in <paramref name="language"/>: Passenger's own English where no carried language is reached.</summary>
    string In(UiLanguage language);
}

/// <summary>The pseudo-locale's words: each letter accented, the whole made 40% longer and bracketed, so words that are
/// not a resource's, or that a layout cuts short, show themselves.</summary>
internal static class PseudoWords
{
    private const string Plain = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Marked = "åƀçđéƒĝĥîĵķļɱñöþǫŕšţûṽŵẋýžÅƁÇĐÉƑĜĤÎĴĶĻṀÑÖÞǪŔŠŢÛṼŴẊÝŽ";

    public static string Accented(string text) => string.Create(text.Length, text, (into, from) =>
    {
        for (int i = 0; i < from.Length; i++)
        {
            int at = Plain.IndexOf(from[i], StringComparison.Ordinal);
            into[i] = at < 0 ? from[i] : Marked[at];
        }
    });

    public static string Padded(string text)
    {
        int more = (int)Math.Ceiling(text.Length * 0.4);
        StringBuilder padded = new("[");
        padded.Append(text);
        if (more > 0)
        {
            padded.Append('·');
            for (int i = 0; i < more - 1; i++)
            {
                padded.Append(text.Length > 0 && !char.IsWhiteSpace(text[i % text.Length]) ? text[i % text.Length] : '·');
            }
        }
        return padded.Append(']').ToString();
    }
}
