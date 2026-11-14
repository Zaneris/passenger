using Passenger.Text;

namespace Passenger.Xaml.Dialects;

/// <summary>The C# of one of MAUI's expressions in XAML as its source generator reads it, from the attribute's value as the
/// file writes it: what stands between its braces (after <c>{=</c> for an explicit one), trimmed, its character references
/// read, the operator words MAUI takes for what XML would escape (<c>AND</c>, <c>OR</c>, <c>LT</c>, <c>GT</c>, <c>LTE</c>,
/// <c>GTE</c>, each between spaces) as C#'s operators, and each single-quoted string a double-quoted one; with where each
/// character of the code is written in the value, so a place in one is a place in the other. A value still being typed may
/// lack its closing brace.</summary>
public sealed class MauiExpressionCode
{
    private static readonly (string Word, string Operator)[] Words = [("AND", "&&"), ("OR", "||"), ("LTE", "<="), ("GTE", ">="), ("LT", "<"), ("GT", ">")];

    /// <summary>For each character of the code, and its end, where it is written in the value.</summary>
    private readonly int[] written;

    private MauiExpressionCode(string code, int[] written)
    {
        Code = code;
        this.written = written;
    }

    public string Code { get; }

    /// <summary>Where in the value the code starts.</summary>
    public int Start => written[0];

    /// <summary>Where in the value the code ends.</summary>
    public int End => written[^1];

    /// <summary>Where in the value a place of the code is written.</summary>
    public int WrittenAt(int code) => written[Math.Clamp(code, 0, written.Length - 1)];

    /// <summary>The place in the code a place in the value comes to: the first character written there or after it.</summary>
    public int CodeAt(int value)
    {
        int low = 0;
        int high = written.Length - 1;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (written[middle] < value)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    /// <summary>The code of a value that opens with a brace (a markup extension's or an expression's), as written between
    /// its quotes; null for any other value, and for <c>{}</c>, which escapes a brace.</summary>
    public static MauiExpressionCode? Of(string raw)
    {
        (string decoded, int[] from) = Decoded(raw);
        int start = 0;
        while (start < decoded.Length && char.IsWhiteSpace(decoded[start]))
        {
            start++;
        }
        if (start >= decoded.Length || decoded[start] != '{' || (start + 1 < decoded.Length && decoded[start + 1] == '}'))
        {
            return null;
        }
        start++;
        if (start < decoded.Length && decoded[start] == '=')
        {
            start++;
        }
        int end = decoded.Length;
        while (end > start && char.IsWhiteSpace(decoded[end - 1]))
        {
            end--;
        }
        if (end > start && decoded[end - 1] == '}')
        {
            end--;
        }
        while (start < end && char.IsWhiteSpace(decoded[start]))
        {
            start++;
        }
        while (end > start && char.IsWhiteSpace(decoded[end - 1]))
        {
            end--;
        }
        List<char> code = [];
        List<int> written = [];
        void Put(string text, int at)
        {
            foreach (char c in text)
            {
                code.Add(c);
                written.Add(from[at]);
            }
        }
        int i = start;
        while (i < end)
        {
            char c = decoded[i];
            if (c == '"')
            {
                (int close, _) = Closing(decoded, i, end, '"');
                for (int k = i; k < close; k++)
                {
                    Put(decoded[k].ToString(), k);
                }
                i = close;
                continue;
            }
            if (c == '\'')
            {
                (int close, bool closed) = Closing(decoded, i, end, '\'');
                if (!closed)
                {
                    // Unclosed: left as it stands, as the generator leaves it.
                    for (int k = i; k < close; k++)
                    {
                        Put(decoded[k].ToString(), k);
                    }
                    i = close;
                    continue;
                }
                Put("\"", i);
                Quoted(decoded, i + 1, close - 1, Put);
                Put("\"", close - 1);
                i = close;
                continue;
            }
            if (c != ' ' && i > start && decoded[i - 1] == ' ' && WordAt(decoded, i, end) is { } word)
            {
                Put(word.Operator, i);
                i += word.Word.Length;
                continue;
            }
            Put(c.ToString(), i);
            i++;
        }
        written.Add(from[end]);
        return new MauiExpressionCode(new string([.. code]), [.. written]);
    }

    /// <summary>A single-quoted string's content as a double-quoted one's: an escaped single quote unescaped, a bare double
    /// quote escaped, every other escape kept.</summary>
    private static void Quoted(string text, int from, int to, Action<string, int> put)
    {
        int i = from;
        while (i < to)
        {
            if (text[i] == '\\' && i + 1 < to)
            {
                put(text[i + 1] == '\'' ? "'" : text.Substring(i, 2), i);
                i += 2;
                continue;
            }
            put(text[i] == '"' ? "\\\"" : text[i].ToString(), i);
            i++;
        }
    }

    /// <summary>Just past the quote that closes a string opened at <paramref name="open"/>, its escapes passed over; the end
    /// where none closes it.</summary>
    private static (int After, bool Closed) Closing(string text, int open, int end, char quote)
    {
        int i = open + 1;
        while (i < end)
        {
            if (text[i] == '\\' && i + 1 < end)
            {
                i += 2;
                continue;
            }
            if (text[i] == quote)
            {
                return (i + 1, true);
            }
            i++;
        }
        return (end, false);
    }

    /// <summary>The operator word at a place after a space, followed by one; null for none.</summary>
    private static (string Word, string Operator)? WordAt(string text, int at, int end)
    {
        foreach ((string Word, string Operator) word in Words)
        {
            int after = at + word.Word.Length;
            if (after < end && text[after] == ' ' && string.Compare(text, at, word.Word, 0, word.Word.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                return word;
            }
        }
        return null;
    }

    /// <summary>The value with its character references read, and for each of its characters, and its end, where it is
    /// written.</summary>
    private static (string Text, int[] From) Decoded(string raw)
    {
        List<char> text = new(raw.Length);
        List<int> from = new(raw.Length + 1);
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '&' && XmlEntities.ReferenceAt(raw, i, out int length) is { } named)
            {
                foreach (char c in named)
                {
                    text.Add(c);
                    from.Add(i);
                }
                i += length;
                continue;
            }
            text.Add(raw[i]);
            from.Add(i);
            i++;
        }
        from.Add(raw.Length);
        return (new string([.. text]), [.. from]);
    }
}
