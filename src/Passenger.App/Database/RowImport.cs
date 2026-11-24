using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Npgsql;
using Passenger.App.Tables;
using Passenger.Text;

namespace Passenger.App.Database;

/// <summary>A file of rows to import: where it is, whether it is JSON (else CSV), a CSV's separator and whether its first
/// row names its columns.</summary>
public sealed record ImportFile(string Path, bool Json, char Separator, bool Header)
{
    /// <summary>A file's format as its name says it: JSON for .json, .jsonl and .ndjson, else CSV, a .tsv's at its tabs.</summary>
    public static ImportFile Of(string path, char separator) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".json" or ".jsonl" or ".ndjson" => new ImportFile(path, true, ',', true),
        ".tsv" => new ImportFile(path, false, '\t', true),
        _ => new ImportFile(path, false, separator, true),
    };
}

/// <summary>A file's column as an import takes it: its name, the table's column it goes into (null: passed over), and the
/// type a new table gives it.</summary>
public sealed record ImportColumn(string Name, string? Into, string Type);

/// <summary>An import as the person chose it: the file, the table, whether to make it, and each of the file's columns.</summary>
public sealed record ImportChoice(ImportFile File, DatabaseTable Table, bool Made, IReadOnlyList<ImportColumn> Columns);

/// <summary>The first rows of a file, its columns' names and the rows' cells as text.</summary>
public sealed record ImportPreview(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows);

/// <summary>A row the database or its column's type refused: its place in the file, counting from 1, and why.</summary>
public sealed class ImportRefused(long row, string why) : Exception(why)
{
    public long Row { get; } = row;
}

/// <summary>A CSV's or JSON file's rows written into a table, read from the file as they come, in one transaction where the
/// database allows (a new MySQL table is made before it, as its DDL commits, and dropped again where a row is refused): the
/// first row refused, by a value its column's type does not read or by the database, stops it and nothing is written.</summary>
public static class RowImport
{
    /// <summary>How often <see cref="RunAsync"/> tells its progress, in rows.</summary>
    public const int Told = 1000;

    /// <summary>The first <paramref name="count"/> rows of a file and its columns: a CSV's first row's names where it has a
    /// header, else column1 and on; a JSON file's members' names in the order they first come.</summary>
    public static async Task<ImportPreview> PreviewAsync(ImportFile file, int count, CancellationToken cancel)
    {
        List<string?[]> rows = [];
        List<string> names = [];
        if (file.Json)
        {
            List<IReadOnlyList<(string Name, string? Value)>> read = [];
            await using Stream stream = FileText.OpenRead(file.Path);
            await foreach (IReadOnlyList<(string Name, string? Value)> row in JsonRecords.ReadAsync(stream, cancel))
            {
                read.Add(row);
                names.AddRange(row.Select(m => m.Name).Where(n => !names.Contains(n)));
                if (read.Count == count)
                {
                    break;
                }
            }
            rows.AddRange(read.Select(row => names.Select(n => row.FirstOrDefault(m => m.Name == n).Value).ToArray()));
            return new ImportPreview(names, rows);
        }
        using StreamReader reader = new(FileText.OpenRead(file.Path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        CsvRecords records = new(reader, file.Separator);
        string?[]? header = file.Header ? records.Next() : null;
        while (rows.Count < count && records.Next() is { } record)
        {
            rows.Add(record);
        }
        int width = Math.Max(header?.Length ?? 0, rows.Count == 0 ? 0 : rows.Max(r => r.Length));
        names.AddRange(Enumerable.Range(0, width).Select(i => header is not null && i < header.Length && header[i] is { Length: > 0 } name
            ? name : $"column{(i + 1).ToString(CultureInfo.InvariantCulture)}"));
        return new ImportPreview(names, rows);
    }

    /// <summary>Each row of the file, its cells in the order of <paramref name="columns"/> (a JSON member it lacks is none),
    /// read as they come.</summary>
    private static async IAsyncEnumerable<string?[]> RowsAsync(ImportFile file, IReadOnlyList<string> columns, [EnumeratorCancellation] CancellationToken cancel)
    {
        if (file.Json)
        {
            await using Stream stream = FileText.OpenRead(file.Path);
            Dictionary<string, int> at = columns.Select((name, i) => (name, i)).ToDictionary(c => c.name, c => c.i, StringComparer.Ordinal);
            await foreach (IReadOnlyList<(string Name, string? Value)> row in JsonRecords.ReadAsync(stream, cancel))
            {
                string?[] cells = new string?[columns.Count];
                foreach ((string name, string? value) in row)
                {
                    if (at.TryGetValue(name, out int place))
                    {
                        cells[place] = value;
                    }
                }
                yield return cells;
            }
            yield break;
        }
        using StreamReader reader = new(FileText.OpenRead(file.Path), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        CsvRecords records = new(reader, file.Separator);
        if (file.Header)
        {
            records.Next();
        }
        while (records.Next() is { } record)
        {
            cancel.ThrowIfCancellationRequested();
            yield return record;
        }
    }

    /// <summary>Writes the file's rows into the table as <paramref name="choice"/> maps them, telling
    /// <paramref name="progress"/> of every <see cref="Told"/> rows; the rows written. A row refused throws
    /// <see cref="ImportRefused"/>, nothing written.</summary>
    public static async Task<long> RunAsync(DatabaseSource source, ImportChoice choice, Action<long> progress, CancellationToken cancel)
    {
        DatabaseKind kind = source.Kind;
        string table = DatabaseClient.Full(kind, choice.Table);
        int[] places = [.. Enumerable.Range(0, choice.Columns.Count).Where(i => choice.Columns[i].Into is not null)];
        ImportColumn[] mapped = [.. places.Select(i => choice.Columns[i])];
        if (mapped.Length == 0)
        {
            throw new InvalidOperationException(Refusals.NoColumnImported);
        }
        string make = $"CREATE TABLE {table} ({string.Join(", ", mapped.Select(c => $"{SqlText.QuotedName(kind, c.Into!)} {c.Type}"))})";
        await using DbConnection connection = await DatabaseClient.OpenAsync(source, cancel);
        using CancellationTokenRegistration interrupt = DatabaseClient.Interrupt(connection, cancel);
        bool madeApart = choice.Made && kind == DatabaseKind.MySql;
        if (madeApart)
        {
            await ExecuteAsync(make, null);
        }
        long row = 0;
        try
        {
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancel);
            if (choice.Made && !madeApart)
            {
                await ExecuteAsync(make, transaction);
            }
            string columns = string.Join(", ", mapped.Select(c => SqlText.QuotedName(kind, c.Into!)));
            (Type Type, string Name)[] types = await TypesAsync(columns, transaction);
            await using DbCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandTimeout = 0;
            insert.CommandText = $"INSERT INTO {table} ({columns}) VALUES ({string.Join(", ", mapped.Select((_, i) => $"@p{i.ToString(CultureInfo.InvariantCulture)}"))})";
            DbParameter[] parameters = [.. mapped.Select((_, i) =>
            {
                DbParameter parameter = insert.CreateParameter();
                parameter.ParameterName = $"@p{i.ToString(CultureInfo.InvariantCulture)}";
                insert.Parameters.Add(parameter);
                return parameter;
            })];
            await foreach (string?[] cells in RowsAsync(choice.File, [.. choice.Columns.Select(c => c.Name)], cancel))
            {
                row++;
                for (int i = 0; i < mapped.Length; i++)
                {
                    string? text = places[i] < cells.Length ? cells[places[i]] : null;
                    Give(parameters[i], text, types[i], mapped[i].Into!, row);
                }
                try
                {
                    await insert.ExecuteNonQueryAsync(cancel);
                }
                catch (DbException refused) when (!cancel.IsCancellationRequested)
                {
                    throw new ImportRefused(row, refused.Message);
                }
                if (row % Told == 0)
                {
                    progress(row);
                }
            }
            await transaction.CommitAsync(cancel);
            return row;
        }
        catch (Exception failed) when (madeApart && failed is DbException or ImportRefused or OperationCanceledException or FormatException or IOException
            or System.Text.Json.JsonException)
        {
            await using DbCommand drop = connection.CreateCommand();
            drop.CommandText = $"DROP TABLE {table}";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
            throw;
        }

        async Task ExecuteAsync(string sql, DbTransaction? transaction)
        {
            await using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancel);
        }

        // Each column's .NET type and database type, as an empty read of them gives them.
        async Task<(Type Type, string Name)[]> TypesAsync(string columns, DbTransaction transaction)
        {
            await using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT {columns} FROM {table} WHERE 1=0";
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancel);
            string[] names = DatabaseClient.Types(reader);
            return [.. Enumerable.Range(0, reader.FieldCount).Select(i => (reader.GetFieldType(i), names[i]))];
        }

        // Text PostgreSQL reads as its column's literal and SQLite by affinity; the others take their driver's type.
        void Give(DbParameter parameter, string? text, (Type Type, string Name) type, string column, long at)
        {
            if (parameter is NpgsqlParameter literal)
            {
                literal.ResetDbType();
                if (text is not null && !ImportTypes.IsBinary(type.Type, type.Name))
                {
                    literal.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown;
                }
            }
            if (text is null)
            {
                parameter.Value = DBNull.Value;
                return;
            }
            Type wanted = ImportTypes.IsBinary(type.Type, type.Name) ? typeof(byte[])
                : kind == DatabaseKind.Sqlite && type.Name.Contains("bool", StringComparison.OrdinalIgnoreCase) ? typeof(bool)
                : kind is DatabaseKind.Postgres or DatabaseKind.Sqlite ? typeof(string) : type.Type;
            if (!ImportTypes.TryConvert(text, wanted, out object value))
            {
                throw new ImportRefused(at, Refusals.NotAValue(text, ImportTypes.Kind(wanted), column));
            }
            parameter.Value = value;
        }
    }
}
