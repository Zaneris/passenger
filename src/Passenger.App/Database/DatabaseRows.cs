using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using Passenger.App.Containers;
using Passenger.App.Tables;

namespace Passenger.App.Database;

/// <summary>A database connection: found in the folder's settings, run environments or running services
/// (<see cref="Found"/> says where), or made by hand and kept for the folder.</summary>
public sealed record DatabaseSource(string Name, DatabaseKind Kind, string Folder)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    internal string ConnectionString { get; init; } = string.Empty;

    /// <summary>Where it was found ("found in Shop"), or null for a connection made by hand.</summary>
    public string? Found { get; init; }

    /// <summary>Whether its secret parts are kept in the system's credential store under <see cref="Account"/>, its
    /// connection string holding the rest; a found connection's string is whole.</summary>
    public bool SecretKept { get; init; }

    /// <summary>The account the system's credential store keeps its secret parts under.</summary>
    public string Account => Id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>The database it opens in: its connection string's, or for SQLite its file's name.</summary>
    public string Database => Kind switch
    {
        DatabaseKind.Sqlite => Path.GetFileName(new SqliteConnectionStringBuilder(ConnectionString).DataSource),
        DatabaseKind.Postgres => new NpgsqlConnectionStringBuilder(ConnectionString).Database ?? string.Empty,
        DatabaseKind.SqlServer => new SqlConnectionStringBuilder(ConnectionString).InitialCatalog,
        _ => new MySqlConnectionStringBuilder(ConnectionString).Database,
    };

    /// <summary>The same connection opening another database of its server.</summary>
    public DatabaseSource In(string database) => database == Database || Kind == DatabaseKind.Sqlite ? this : this with
    {
        ConnectionString = Kind switch
        {
            DatabaseKind.Postgres => new NpgsqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString,
            DatabaseKind.SqlServer => new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = database }.ConnectionString,
            _ => new MySqlConnectionStringBuilder(ConnectionString) { Database = database }.ConnectionString,
        },
    };

    public override string ToString() => ContainerSecrets.InSaid($"{Name} · {DatabaseKinds.Title(Kind)}");
}

/// <summary>A table or a view, by its schema (empty for SQLite's) and name.</summary>
public sealed record DatabaseTable(string Schema, string Name)
{
    public override string ToString() => ContainerSecrets.InSaid(Schema.Length == 0 ? Name : $"{Schema}.{Name}");
}

public sealed record DatabaseColumn(string Name, string Type, bool Nullable, bool Primary, bool Generated);

public sealed record DatabaseSchema(DatabaseTable Table, IReadOnlyList<DatabaseColumn> Columns, IReadOnlyList<string> Keys, IReadOnlyList<string> Indexes);

/// <summary>What kind of thing a schema holds.</summary>
public enum DatabaseObjectKind
{
    Table,
    View,
    Routine,
}

/// <summary>A table, view or routine of a schema; a routine's detail says whether it is a function or a procedure.</summary>
public sealed record DatabaseObject(string Schema, string Name, DatabaseObjectKind Kind, string Detail = "");

/// <summary>What one run of a statement or batch came to: its result sets, the rows it changed, or why it failed.</summary>
public sealed record SqlResult(string Statement, IReadOnlyList<DatabaseRows> Sets, int Affected, string? Error, TimeSpan Took);

public sealed class DatabaseRows(string[] columns, object?[][] rows, bool more = false, int affected = -1) : Table
{
    public string[] Columns { get; } = columns;
    public object?[][] Rows { get; } = rows;
    public bool More { get; } = more;
    public int Affected { get; } = affected;
    public DatabaseSchema? Schema { get; init; }
    public int Offset { get; init; }

    /// <summary>The column the server ordered the rows by, and which way, as its header's arrow shows.</summary>
    public (int Column, bool Descending)? OrderedBy { get; init; }

    public override (int Column, bool Descending)? Sort => OrderedBy;

    public override int RowCount => Rows.Length;
    public override int ColumnCount => Columns.Length;
    public override string Header(int column) => ContainerSecrets.InSaid(Columns[column]);
    public override int RowNumber(int row) => Offset + row + 1;
    public override bool IsNumber(int row, int column) => Rows[row][column] is byte or short or int or long or float or double or decimal;

    public override CellValue ValueOf(int row, int column) => Rows[row][column] switch
    {
        null => new CellValue(CellKind.Empty),
        byte or short or int or long or float or double or decimal => new CellValue(CellKind.Number, Convert.ToDouble(Rows[row][column], CultureInfo.InvariantCulture)),
        DateTime date => new CellValue(CellKind.Date, date.Ticks),
        _ => new CellValue(CellKind.Text),
    };

    public override bool IsEditable(int row, int column) => Schema is { } schema && schema.Columns.Any(c => c.Primary)
        && schema.Columns.Where(c => c.Primary).All(c => Array.IndexOf(Columns, c.Name) switch { < 0 => false, int key => Rows[row][key] is not null })
        && schema.Columns.FirstOrDefault(c => c.Name == Columns[column]) is { Primary: false, Generated: false }
        && Rows[row][column] is null or string or long or int or short or byte or double or float or decimal or bool;

    public override string Cell(int row, int column) => ContainerSecrets.InSaid(Rows[row][column] switch
    {
        null => "NULL",
        byte[] bytes => $"[{bytes.Length} bytes]",
        IFormattable value => value.ToString(null, CultureInfo.InvariantCulture),
        object value => value.ToString() ?? string.Empty,
    });
}
