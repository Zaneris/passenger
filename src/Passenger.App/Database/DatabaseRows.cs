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

    /// <summary>The account its own settings' secrets are kept under, the same each time: a found connection's by its
    /// folder and <see cref="FoundKey"/>, as its id is new each time the folder's files are read.</summary>
    public string SettingsAccount => Found is null ? Account : "found-" + Passenger.Core.FileNames.Hash(Folder + "\n" + FoundKey, 32);

    /// <summary>The account the system's credential store keeps its SSH tunnel's password or key passphrase under.</summary>
    public string TunnelAccount => SettingsAccount + ".ssh";

    /// <summary>The account the secret parts of a found connection's <see cref="Over"/> are kept under.</summary>
    public string OverAccount => SettingsAccount + ".ssl";

    /// <summary>A found connection's own settings laid over the string its folder's files give (its SSL, as connection string
    /// keys): as kept, without their secrets, which are kept under <see cref="OverAccount"/>; as the connection dialog gives
    /// them, with the secrets it was given; empty for none.</summary>
    internal string Over { get; init; } = string.Empty;

    /// <summary>Whether what runs on it may change it freely, never (read-only), or only once the person agrees (production).</summary>
    public DatabaseMark Mark { get; init; }

    /// <summary>The SSH tunnel it reaches its server through, null for none.</summary>
    public SshTunnel? Tunnel { get; init; }

    /// <summary>The tunnel's password or key passphrase as the connection dialog was given it, until the connection is kept
    /// and it is put in the system's credential store (<see cref="TunnelAccount"/>); never kept here.</summary>
    internal string? TunnelSecret { get; init; }

    /// <summary>What a found connection's mark is kept by, the same each time the folder's files name it.</summary>
    public string FoundKey => $"{Kind}|{Found}|{Name}";

    /// <summary>What its history is kept by: a found connection's <see cref="FoundKey"/>, one made by hand its id.</summary>
    public string HistoryKey => Found is null ? Account : FoundKey;

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

/// <summary>How a connection holds what runs on it: any change, none (every statement or edit that would change data or
/// schema refused), or each only once the person agrees.</summary>
public enum DatabaseMark
{
    None,
    ReadOnly,
    Production,
}

/// <summary>A table or a view, by its schema (empty for SQLite's) and name.</summary>
public sealed record DatabaseTable(string Schema, string Name)
{
    public override string ToString() => ContainerSecrets.InSaid(Schema.Length == 0 ? Name : $"{Schema}.{Name}");
}

public sealed record DatabaseColumn(string Name, string Type, bool Nullable, bool Primary, bool Generated);

/// <summary>A table's or view's columns, keys, indexes (each by its name and what it says of itself) and triggers.</summary>
public sealed record DatabaseSchema(DatabaseTable Table, IReadOnlyList<DatabaseColumn> Columns, IReadOnlyList<string> Keys, IReadOnlyList<DatabasePart> Indexes)
{
    public IReadOnlyList<DatabasePart> Triggers { get; init; } = [];
}

/// <summary>A part of a table by its own name (an index, a trigger) and the words its row shows.</summary>
public sealed record DatabasePart(string Name, string Said);

/// <summary>What kind of thing a schema holds, or a table holds of its own (an index, a trigger, a column).</summary>
public enum DatabaseObjectKind
{
    Table,
    View,
    Routine,
    Trigger,
    Index,
    Column,
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

    /// <summary>Each column's type as its database names it, as the reader gave it.</summary>
    public string[] Types { get; init; } = [];

    /// <summary>The table its rows are written into as SQL inserts: the table read, or the one table its statement reads.</summary>
    public InsertTarget? Into { get; init; }

    /// <summary>The column the server ordered the rows by, and which way, as its header's arrow shows.</summary>
    public (int Column, bool Descending)? OrderedBy { get; init; }

    public override (int Column, bool Descending)? Sort => OrderedBy;

    /// <summary>What it was filtered from, for <see cref="Same"/>.</summary>
    private DatabaseRows? from;
    private string? holding;

    /// <summary>The rows any of whose cells hold <paramref name="words"/>, case aside, numbered as before.</summary>
    public DatabaseRows Holding(string words) => new(Columns, [.. Enumerable.Range(0, RowCount).Where(r => Enumerable.Range(0, ColumnCount)
        .Any(c => Cell(r, c).Contains(words, StringComparison.OrdinalIgnoreCase))).Select(r => Rows[r])], More, Affected)
    {
        Schema = Schema, Types = Types, Into = Into, from = this, holding = words,
    };

    /// <summary>Whether two filters are of one set by the same words, so a grid keeps its sort and place.</summary>
    public bool Same(DatabaseRows other) => ReferenceEquals(this, other) || from is not null && ReferenceEquals(from, other.from) && holding == other.holding;

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

    public override object? ValueAt(int row, int column) => Rows[row][column];

    public override string TypeOf(int column) => column < Types.Length ? Types[column] : string.Empty;

    public override RowWriter? Inserts(TextWriter into, string lineBreak) => Into is { } target ? new SqlInserts(into, target, lineBreak) : null;

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
