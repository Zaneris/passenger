using System.Globalization;

namespace Passenger.App.Database;

/// <summary>What an import makes of a file's text: the type a new table's column is proposed from its first values (true or
/// false, whole numbers, numbers with their digits, dates, dates and times, else text) in each database's own names; the
/// value a cell's text is for a column of a .NET type, as its driver binds it (true or false, a number, ISO 8601 dates and
/// times, Base64 or 0x-hex bytes); and which of a table's columns each of the file's goes into, by name first.</summary>
public static class ImportTypes
{
    /// <summary>The type proposed for a new column holding <paramref name="values"/>; text where they agree on nothing else
    /// or hold none.</summary>
    public static string Proposed(IEnumerable<string?> values, DatabaseKind kind)
    {
        bool any = false, truth = true, whole = true, number = true, date = true, stamp = true, zoned = false;
        int digits = 1, places = 0;
        foreach (string text in values.OfType<string>().Select(v => v.Trim()).Where(v => v.Length > 0))
        {
            any = true;
            truth &= text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase);
            whole &= long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
            if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
            {
                string unsigned = text.TrimStart('-', '+');
                int point = unsigned.IndexOf('.', StringComparison.Ordinal);
                digits = Math.Max(digits, point < 0 ? unsigned.Length : point);
                places = Math.Max(places, point < 0 ? 0 : unsigned.Length - point - 1);
            }
            else
            {
                number = false;
            }
            date &= DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
            bool dated = text.Length >= 16 && text[4] == '-' && text[7] == '-' && text[10] is ' ' or 'T'
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);
            stamp &= dated;
            zoned |= dated && (text.EndsWith('Z') || text.LastIndexOfAny(['+', '-']) > 10);
        }
        if (!any)
        {
            return Text(kind);
        }
        if (truth)
        {
            return kind switch { DatabaseKind.Postgres => "boolean", DatabaseKind.SqlServer => "bit", _ => "BOOLEAN" };
        }
        if (whole)
        {
            return kind is DatabaseKind.Postgres or DatabaseKind.SqlServer ? "bigint" : kind == DatabaseKind.Sqlite ? "INTEGER" : "BIGINT";
        }
        if (number)
        {
            int precision = Math.Min(digits + places, kind == DatabaseKind.MySql ? 65 : 38);
            int scale = Math.Min(places, Math.Min(precision, 30));
            return kind switch
            {
                DatabaseKind.Postgres => "numeric",
                DatabaseKind.Sqlite => "REAL",
                DatabaseKind.SqlServer => $"decimal({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})",
                _ => $"DECIMAL({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})",
            };
        }
        if (date)
        {
            return kind is DatabaseKind.Postgres or DatabaseKind.SqlServer ? "date" : "DATE";
        }
        if (stamp)
        {
            return kind switch
            {
                DatabaseKind.Postgres => zoned ? "timestamptz" : "timestamp",
                DatabaseKind.SqlServer => zoned ? "datetimeoffset" : "datetime2",
                DatabaseKind.MySql => "DATETIME(6)",
                _ => "DATETIME",
            };
        }
        return Text(kind);
    }

    private static string Text(DatabaseKind kind) => kind switch
    {
        DatabaseKind.Postgres => "text",
        DatabaseKind.SqlServer => "nvarchar(max)",
        _ => "TEXT",
    };

    /// <summary>Whether a column of this type and .NET type holds bytes, which a file holds as Base64.</summary>
    public static bool IsBinary(Type type, string name) => type == typeof(byte[]) || name.Contains("blob", StringComparison.OrdinalIgnoreCase)
        || name.Equals("bytea", StringComparison.OrdinalIgnoreCase) || name.Contains("binary", StringComparison.OrdinalIgnoreCase);

    /// <summary>A cell's text as a value of a column's .NET type; false where it is none (<see cref="Kind"/> says what it
    /// is not).</summary>
    public static bool TryConvert(string text, Type type, out object value)
    {
        string trimmed = text.Trim();
        try
        {
            value = type switch
            {
                _ when type == typeof(string) => text,
                _ when type == typeof(bool) => trimmed.ToLowerInvariant() switch
                {
                    "true" or "1" or "t" or "yes" => true,
                    "false" or "0" or "f" or "no" => false,
                    _ => throw new FormatException(),
                },
                _ when type == typeof(byte[]) => Bytes(trimmed),
                _ when type == typeof(DateTime) => DateTime.Parse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                _ when type == typeof(DateTimeOffset) => DateTimeOffset.Parse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                _ when type == typeof(DateOnly) => DateOnly.Parse(trimmed, CultureInfo.InvariantCulture),
                _ when type == typeof(TimeOnly) => TimeOnly.Parse(trimmed, CultureInfo.InvariantCulture),
                _ when type == typeof(TimeSpan) => TimeSpan.Parse(trimmed, CultureInfo.InvariantCulture),
                _ when type == typeof(Guid) => Guid.Parse(trimmed),
                _ when type == typeof(decimal) => decimal.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture),
                _ when type == typeof(double) => double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture),
                _ when type == typeof(float) => float.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture),
                _ when type.IsPrimitive && type != typeof(char) => Convert.ChangeType(long.Parse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), type, CultureInfo.InvariantCulture),
                _ => text,
            };
            return true;
        }
        catch (Exception unread) when (unread is FormatException or OverflowException or InvalidCastException)
        {
            value = text;
            return false;
        }
    }

    /// <summary>Bytes as a file holds them: 0x and hex, else Base64.</summary>
    public static byte[] Bytes(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || text.StartsWith("\\x", StringComparison.Ordinal)
        ? Convert.FromHexString(text.AsSpan(2)) : Convert.FromBase64String(text);

    /// <summary>What a value of a .NET type is, in words.</summary>
    public static string Kind(Type type) => type switch
    {
        _ when type == typeof(bool) => "true or false",
        _ when type == typeof(byte[]) => "Base64 or 0x-hex bytes",
        _ when type == typeof(DateTime) || type == typeof(DateTimeOffset) => "date and time",
        _ when type == typeof(DateOnly) => "date",
        _ when type == typeof(TimeOnly) || type == typeof(TimeSpan) => "time",
        _ when type == typeof(Guid) => "GUID",
        _ when type == typeof(decimal) || type == typeof(double) || type == typeof(float) => "number",
        _ => "whole number",
    };

    /// <summary>The table's column each of the file's goes into, by its index among the table's, or null where none takes
    /// it: the one of its name, case aside, else where the file names no columns the one in its place not taken by name.</summary>
    public static int?[] Mapped(IReadOnlyList<string> file, IReadOnlyList<string> table, bool named)
    {
        int?[] into = new int?[file.Count];
        HashSet<int> taken = [];
        for (int i = 0; i < file.Count; i++)
        {
            int at = Enumerable.Range(0, table.Count).FirstOrDefault(c => !taken.Contains(c) && table[c].Equals(file[i], StringComparison.OrdinalIgnoreCase), -1);
            if (at >= 0)
            {
                into[i] = at;
                taken.Add(at);
            }
        }
        for (int i = 0; i < file.Count && !named; i++)
        {
            if (into[i] is null && i < table.Count && taken.Add(i))
            {
                into[i] = i;
            }
        }
        return into;
    }
}
