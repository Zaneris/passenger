using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Passenger.App.Tables;

/// <summary>A column as a writer of rows takes it: its name, and its type as its database names it (empty where none does).</summary>
public readonly record struct RowColumn(string Name, string Type = "");

/// <summary>Writes rows one at a time as they come, a format's beginning before the first and its end after the last, so
/// what it writes is never held whole: an export of a million rows holds the row it writes.</summary>
public abstract class RowWriter(TextWriter into)
{
    protected TextWriter Into { get; } = into;

    public abstract void Begin(IReadOnlyList<RowColumn> columns);

    public abstract void Row(IReadOnlyList<object?> values);

    public virtual void End()
    {
    }

    /// <summary>Rows written whole into a text, as a copy takes them.</summary>
    public static string Written(Func<TextWriter, RowWriter> made, IReadOnlyList<RowColumn> columns, IEnumerable<IReadOnlyList<object?>> rows)
    {
        using StringWriter text = new(CultureInfo.InvariantCulture);
        RowWriter writer = made(text);
        writer.Begin(columns);
        foreach (IReadOnlyList<object?> row in rows)
        {
            writer.Row(row);
        }
        writer.End();
        return text.ToString();
    }
}

/// <summary>A value as a file of rows holds it, one rule for every format: no value is null, a date and a time ISO 8601's
/// (2024-05-01 13:45:00, a date column's date alone, a UTC one ending in Z), a number as invariant culture writes it,
/// true or false, bytes as Base64, and an array as PostgreSQL writes one ({1,2,3}).</summary>
public static class RowValues
{
    public static bool IsNull(object? value) => value is null or DBNull;

    /// <summary>The text of a value, null for none; <paramref name="between"/> stands between a date and its time (' ' as
    /// SQL and spreadsheets write it, 'T' as JSON's ISO 8601 does).</summary>
    public static string? Text(object? value, string type = "", char between = ' ') => value switch
    {
        null or DBNull => null,
        string text => text,
        bool truth => truth ? "true" : "false",
        byte[] bytes => Convert.ToBase64String(bytes),
        DateTime date => Dated(date, type, between),
        DateTimeOffset offset => offset.ToString("yyyy-MM-dd'" + between + "'HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(offset.Ticks)
            + offset.ToString("zzz", CultureInfo.InvariantCulture),
        DateOnly day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(time.Ticks),
        TimeSpan span => Timed(span),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        Guid id => id.ToString("D", CultureInfo.InvariantCulture),
        char[] characters => new string(characters),
        Array array => "{" + string.Join(",", array.Cast<object?>().Select(Element)) + "}",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>Whether a value is a number JSON writes bare.</summary>
    public static bool IsNumber(object? value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or decimal or BigInteger
        || value is double number && double.IsFinite(number) || value is float single && float.IsFinite(single);

    /// <summary>Whether a column holds dates alone, so a date and time read from it is written as its date.</summary>
    public static bool IsDateType(string type) => type.Equals("date", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a column holds JSON, so its text is written into a JSON file as the value it is.</summary>
    public static bool IsJsonType(string type) => type.Equals("json", StringComparison.OrdinalIgnoreCase) || type.Equals("jsonb", StringComparison.OrdinalIgnoreCase);

    private static string Dated(DateTime date, string type, char between) =>
        IsDateType(type) && date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : date.ToString("yyyy-MM-dd'" + between + "'HH:mm:ss", CultureInfo.InvariantCulture) + Fraction(date.Ticks) + (date.Kind == DateTimeKind.Utc ? "Z" : string.Empty);

    /// <summary>A time's hours whole, past a day too, as PostgreSQL's and MySQL's time and interval read them.</summary>
    private static string Timed(TimeSpan span)
    {
        TimeSpan whole = span.Duration();
        return (span < TimeSpan.Zero ? "-" : string.Empty) + ((long)whole.TotalHours).ToString("00", CultureInfo.InvariantCulture) + ":"
            + whole.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":" + whole.Seconds.ToString("00", CultureInfo.InvariantCulture) + Fraction(whole.Ticks);
    }

    /// <summary>The part of a second a time holds, its last zeros left out; nothing for a whole second.</summary>
    private static string Fraction(long ticks)
    {
        long part = ticks % TimeSpan.TicksPerSecond;
        return part == 0 ? string.Empty : "." + part.ToString("0000000", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    /// <summary>An array's element as PostgreSQL's array text writes it: quoted where it holds what ends one.</summary>
    private static string Element(object? value) => Text(value) switch
    {
        null => "NULL",
        { Length: 0 } => "\"\"",
        string text when text.AsSpan().IndexOfAny(",{}\"\\ ") >= 0 || text.Equals("NULL", StringComparison.OrdinalIgnoreCase) =>
            "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
        string text => text,
    };
}

/// <summary>Rows as RFC 4180 writes them: fields between <paramref name="separator"/>s, each record ended by
/// <paramref name="lineBreak"/> (a file's CRLF), a field quoted where it holds the separator, a quote or a line break, its
/// quotes doubled; the first record the columns' names where <paramref name="header"/>. A value that is none is an empty
/// field and empty text a quoted one (""), so a reader tells them apart.</summary>
public sealed class CsvRows(TextWriter into, char separator, bool header, string lineBreak) : RowWriter(into)
{
    private string[] types = [];

    public override void Begin(IReadOnlyList<RowColumn> columns)
    {
        types = [.. columns.Select(c => c.Type)];
        if (header)
        {
            for (int i = 0; i < columns.Count; i++)
            {
                Field(i, columns[i].Name);
            }
            Into.Write(lineBreak);
        }
    }

    public override void Row(IReadOnlyList<object?> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            Field(i, RowValues.Text(values[i], i < types.Length ? types[i] : string.Empty));
        }
        Into.Write(lineBreak);
    }

    private void Field(int at, string? text)
    {
        if (at > 0)
        {
            Into.Write(separator);
        }
        if (text is null)
        {
            return;
        }
        if (text.Length == 0 || text.Contains(separator) || text.AsSpan().IndexOfAny("\"\r\n") >= 0)
        {
            Into.Write('"');
            Into.Write(text.Replace("\"", "\"\"", StringComparison.Ordinal));
            Into.Write('"');
        }
        else
        {
            Into.Write(text);
        }
    }
}

/// <summary>Rows as a JSON array of one object per row, each on a line of its own, its columns' names its keys (a name
/// written twice taking a number after it): no value is null, a number and true or false bare, a JSON column's text the
/// value it holds, an array a JSON array, and everything else text as <see cref="RowValues"/> writes it.</summary>
public sealed class JsonRows(TextWriter into, string lineBreak) : RowWriter(into)
{
    private string[] names = [];
    private string[] types = [];
    private bool any;

    public override void Begin(IReadOnlyList<RowColumn> columns)
    {
        HashSet<string> taken = new(StringComparer.Ordinal);
        names = [.. columns.Select(c =>
        {
            string name = c.Name;
            for (int n = 2; !taken.Add(name); n++)
            {
                name = $"{c.Name}_{n.ToString(CultureInfo.InvariantCulture)}";
            }
            return Quoted(name) + ":";
        })];
        types = [.. columns.Select(c => c.Type)];
        Into.Write('[');
    }

    public override void Row(IReadOnlyList<object?> values)
    {
        Into.Write(any ? "," + lineBreak + "  {" : lineBreak + "  {");
        any = true;
        for (int i = 0; i < values.Count && i < names.Length; i++)
        {
            if (i > 0)
            {
                Into.Write(',');
            }
            Into.Write(names[i]);
            Value(values[i], types[i]);
        }
        Into.Write('}');
    }

    public override void End()
    {
        Into.Write(any ? lineBreak + "]" + lineBreak : "]" + lineBreak);
    }

    private void Value(object? value, string type)
    {
        switch (value)
        {
            case null or DBNull:
                Into.Write("null");
                return;
            case bool truth:
                Into.Write(truth ? "true" : "false");
                return;
            case string text when RowValues.IsJsonType(type) && IsJson(text):
                Into.Write(text.Trim());
                return;
            case Array array when value is not (byte[] or char[]):
                Into.Write('[');
                bool first = true;
                foreach (object? element in (IEnumerable)array)
                {
                    if (!first)
                    {
                        Into.Write(',');
                    }
                    first = false;
                    Value(element, string.Empty);
                }
                Into.Write(']');
                return;
        }
        if (RowValues.IsNumber(value))
        {
            Into.Write(RowValues.Text(value));
            return;
        }
        Into.Write(Quoted(RowValues.Text(value, type, 'T') ?? string.Empty));
    }

    private static bool IsJson(string text)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Quoted(string text) => "\"" + JsonEncodedText.Encode(text, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value + "\"";
}
