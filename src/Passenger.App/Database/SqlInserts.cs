using Passenger.App.Tables;

namespace Passenger.App.Database;

/// <summary>Where rows written as SQL inserts go: the table, as its database's SQL names it, and that database.</summary>
public sealed record InsertTarget(DatabaseKind Kind, string Table);

/// <summary>Rows as SQL inserts in a database's own SQL, one INSERT a row, each ended by a semicolon on a line of its own,
/// so a console runs them as they are: its columns quoted as it quotes names, NULL for no value, text quoted as it reads a
/// string (MySQL's backslashes doubled, SQL Server's as N'…'), bytes as its byte literal, true and false as it writes them,
/// and a date or time as text its columns read.</summary>
public sealed class SqlInserts(TextWriter into, InsertTarget target, string lineBreak) : RowWriter(into)
{
    private string head = string.Empty;
    private string[] types = [];

    public override void Begin(IReadOnlyList<RowColumn> columns)
    {
        head = $"INSERT INTO {target.Table} ({string.Join(", ", columns.Select(c => SqlText.QuotedName(target.Kind, c.Name)))}) VALUES (";
        types = [.. columns.Select(c => c.Type)];
    }

    public override void Row(IReadOnlyList<object?> values)
    {
        Into.Write(head);
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                Into.Write(", ");
            }
            Into.Write(Literal(values[i], i < types.Length ? types[i] : string.Empty, target.Kind));
        }
        Into.Write(");");
        Into.Write(lineBreak);
    }

    /// <summary>A value as a literal of the database's SQL.</summary>
    public static string Literal(object? value, string type, DatabaseKind kind) => value switch
    {
        null or DBNull => "NULL",
        bool truth => kind is DatabaseKind.Sqlite or DatabaseKind.SqlServer ? truth ? "1" : "0" : truth ? "TRUE" : "FALSE",
        byte[] bytes => kind switch
        {
            DatabaseKind.Postgres => $"'\\x{Convert.ToHexStringLower(bytes)}'::bytea",
            DatabaseKind.SqlServer => "0x" + Convert.ToHexString(bytes),
            DatabaseKind.DuckDb or DatabaseKind.ClickHouse => $"unhex('{Convert.ToHexString(bytes)}')",
            DatabaseKind.Snowflake => $"TO_BINARY('{Convert.ToHexString(bytes)}', 'HEX')",
            DatabaseKind.Redshift => $"FROM_HEX('{Convert.ToHexString(bytes)}')",
            _ => $"X'{Convert.ToHexString(bytes)}'",
        },
        _ when RowValues.IsNumber(value) => RowValues.Text(value)!,
        _ => Quoted(RowValues.Text(value, type) ?? string.Empty, kind),
    };

    /// <summary>Text as a string of the database's SQL.</summary>
    public static string Quoted(string text, DatabaseKind kind)
    {
        string inside = text.Replace("'", "''", StringComparison.Ordinal);
        return kind switch
        {
            DatabaseKind.MySql or DatabaseKind.ClickHouse or DatabaseKind.Snowflake => "'" + inside.Replace("\\", "\\\\", StringComparison.Ordinal) + "'",
            DatabaseKind.SqlServer => "N'" + inside + "'",
            _ => "'" + inside + "'",
        };
    }

    /// <summary>The table rows of a statement's result are written into: the one table it reads, as it names it, else
    /// <c>result</c>, for the person to name.</summary>
    public static string TableOf(string statement, DatabaseKind kind) => SqlText.OnlyTable(statement, kind) ?? "result";
}
