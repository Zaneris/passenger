using System.Globalization;
using Passenger.App.Containers;
using Passenger.App.Tables;

namespace Passenger.App.Database;

public sealed record DatabaseSource(string Name, DatabaseKind Kind, string Folder)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    internal string ConnectionString { get; init; } = string.Empty;

    public override string ToString() => ContainerSecrets.InSaid($"{Name} · {Kind}");
}

public sealed record DatabaseTable(string Schema, string Name)
{
    public override string ToString() => ContainerSecrets.InSaid(Schema.Length == 0 ? Name : $"{Schema}.{Name}");
}

public sealed record DatabaseColumn(string Name, string Type, bool Nullable, bool Primary, bool Generated);

public sealed record DatabaseSchema(DatabaseTable Table, IReadOnlyList<DatabaseColumn> Columns, IReadOnlyList<string> Keys, IReadOnlyList<string> Indexes);

public sealed class DatabaseRows(string[] columns, object?[][] rows, bool more = false, int affected = -1) : Table
{
    public string[] Columns { get; } = columns;
    public object?[][] Rows { get; } = rows;
    public bool More { get; } = more;
    public int Affected { get; } = affected;
    public DatabaseSchema? Schema { get; init; }
    public int Offset { get; init; }

    public override int RowCount => Rows.Length;
    public override int ColumnCount => Columns.Length;
    public override string Header(int column) => ContainerSecrets.InSaid(Columns[column]);
    public override int RowNumber(int row) => Offset + row + 1;
    public override bool IsNumber(int row, int column) => Rows[row][column] is byte or short or int or long or float or double or decimal;
    public override CellValue ValueOf(int row, int column) => new(Rows[row][column] is null ? CellKind.Empty : CellKind.Text);
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
