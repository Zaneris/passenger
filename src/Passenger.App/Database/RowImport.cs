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

/// <summary>The first rows of a file, its columns' names and the rows' cells as text, and each column's first values (a
/// JSON member's from wherever it first comes), which a new table's types are proposed from.</summary>
public sealed record ImportPreview(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows)
{
    public IReadOnlyList<IReadOnlyList<string>> Samples { get; init; } = [];
}

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

    /// <summary>How many rows go to a server in one round trip.</summary>
    public const int BatchRows = 250;

    private const string Savepoint = "passenger_import";

    /// <summary>A cell's value as its parameter takes it, and whether PostgreSQL reads it as a literal of its column's type.</summary>
    private readonly record struct Cell(object Value, bool Literal);

    /// <summary>The first <paramref name="count"/> rows of a file and its columns: a CSV's first row's names where it has a
    /// header, else column1 and on; every member a JSON file's rows name, the whole file read for them as it comes, in the
    /// order they first come, each with its first <paramref name="count"/> values.</summary>
    public static async Task<ImportPreview> PreviewAsync(ImportFile file, int count, CancellationToken cancel)
    {
        List<string?[]> rows = [];
        List<string> names = [];
        if (file.Json)
        {
            List<IReadOnlyList<(string Name, string? Value)>> read = [];
            Dictionary<string, List<string>> samples = new(StringComparer.Ordinal);
            await using Stream stream = FileText.OpenRead(file.Path);
            await foreach (IReadOnlyList<(string Name, string? Value)> row in JsonRecords.ReadAsync(stream, cancel))
            {
                if (read.Count < count)
                {
                    read.Add(row);
                }
                foreach ((string name, string? value) in row)
                {
                    if (!samples.TryGetValue(name, out List<string>? values))
                    {
                        names.Add(name);
                        values = samples[name] = [];
                    }
                    if (value is not null && values.Count < count)
                    {
                        values.Add(value);
                    }
                }
            }
            rows.AddRange(read.Select(row => names.Select(n => row.FirstOrDefault(m => m.Name == n).Value).ToArray()));
            return new ImportPreview(names, rows) { Samples = [.. names.Select(n => (IReadOnlyList<string>)samples[n])] };
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
        return new ImportPreview(names, rows)
        {
            Samples = [.. Enumerable.Range(0, width).Select(i => (IReadOnlyList<string>)[.. rows.Select(r => i < r.Length ? r[i] : null).OfType<string>()])],
        };
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
        if (!DatabaseKinds.HasTransactions(kind))
        {
            throw new InvalidOperationException(Refusals.NoImport(DatabaseKinds.Title(kind)));
        }
        await using DbConnection connection = await DatabaseClient.OpenAsync(source, cancel);
        using CancellationTokenRegistration interrupt = DatabaseClient.Interrupt(connection, cancel);
        // MySQL's and Snowflake's CREATE TABLE commits its transaction: made before it, it is dropped where nothing is written.
        bool madeApart = choice.Made && kind is DatabaseKind.MySql or DatabaseKind.Snowflake;
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
            string inserting = $"INSERT INTO {table} ({columns}) VALUES ({string.Join(", ", mapped.Select((_, i) => DatabaseClient.Placed(connection, Named(i), i).Written))})";
            await using DbCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandTimeout = 0;
            insert.CommandText = inserting;
            DbParameter[] parameters = [.. mapped.Select((_, i) =>
            {
                DbParameter parameter = insert.CreateParameter();
                parameter.ParameterName = DatabaseClient.Placed(connection, Named(i), i).Bound;
                insert.Parameters.Add(parameter);
                return parameter;
            })];
            // A server's rows go in batches, a savepoint before each to find a refused row again where its driver names none.
            bool batched = connection.CanCreateBatch && transaction.SupportsSavepoints;
            List<(long Row, Cell[] Values)> pending = [];
            await foreach (string?[] cells in RowsAsync(choice.File, [.. choice.Columns.Select(c => c.Name)], cancel))
            {
                row++;
                Cell[] values = new Cell[mapped.Length];
                for (int i = 0; i < mapped.Length; i++)
                {
                    string? text = places[i] < cells.Length ? cells[places[i]] : null;
                    if (Valued(text, types[i]) is not { } value)
                    {
                        // A row the batch before it holds may be refused first.
                        await FlushAsync();
                        throw new ImportRefused(row, Refusals.NotAValue(text!, ImportTypes.Kind(Wanted(types[i])), mapped[i].Into!));
                    }
                    values[i] = value;
                }
                if (batched)
                {
                    pending.Add((row, values));
                    if (pending.Count == BatchRows)
                    {
                        await FlushAsync();
                    }
                }
                else
                {
                    await OneAsync(row, values);
                }
                if (row % Told == 0)
                {
                    progress(row);
                }
            }
            await FlushAsync();

            // The rows waiting written in one round trip; the one refused named by the batch's command its driver names, else
            // found by writing them again one at a time from the savepoint before them.
            async Task FlushAsync()
            {
                if (pending.Count == 0)
                {
                    return;
                }
                await transaction.SaveAsync(Savepoint, cancel);
                await using DbBatch batch = connection.CreateBatch();
                batch.Transaction = transaction;
                batch.Timeout = 0;
                foreach ((long _, Cell[] values) in pending)
                {
                    DbBatchCommand command = batch.CreateBatchCommand();
                    command.CommandText = inserting;
                    for (int i = 0; i < values.Length; i++)
                    {
                        DbParameter parameter = insert.CreateParameter();
                        parameter.ParameterName = DatabaseClient.Placed(connection, Named(i), i).Bound;
                        Give(parameter, values[i]);
                        command.Parameters.Add(parameter);
                    }
                    batch.BatchCommands.Add(command);
                }
                try
                {
                    await batch.ExecuteNonQueryAsync(cancel);
                }
                catch (DbException refused) when (!cancel.IsCancellationRequested)
                {
                    if (refused.BatchCommand is { } named && batch.BatchCommands.IndexOf(named) is >= 0 and int at)
                    {
                        throw new ImportRefused(pending[at].Row, refused.Message);
                    }
                    await transaction.RollbackAsync(Savepoint, cancel);
                    foreach ((long again, Cell[] values) in pending)
                    {
                        await OneAsync(again, values);
                    }
                    throw new ImportRefused(pending[0].Row, refused.Message);
                }
                await ReleaseAsync();
                pending.Clear();
            }

            async Task OneAsync(long at, Cell[] values)
            {
                for (int i = 0; i < values.Length; i++)
                {
                    Give(parameters[i], values[i]);
                }
                try
                {
                    await insert.ExecuteNonQueryAsync(cancel);
                }
                catch (DbException refused) when (!cancel.IsCancellationRequested)
                {
                    throw new ImportRefused(at, refused.Message);
                }
            }

            // A savepoint let go of once its batch is written, as PostgreSQL slows past a few dozen held; SQL Server keeps them.
            async Task ReleaseAsync()
            {
                try
                {
                    await transaction.ReleaseAsync(Savepoint, cancel);
                }
                catch (NotSupportedException)
                {
                    IdeLog.Write(IdeLog.Ide, "Database import: the provider keeps its savepoints, which its transaction ends with.");
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

        // A cell's value, null where its column's type does not read it: PostgreSQL's text read as a literal of its column's type.
        Cell? Valued(string? text, (Type Type, string Name) type)
        {
            if (text is null)
            {
                return new Cell(DBNull.Value, false);
            }
            Type wanted = Wanted(type);
            if (DatabaseKinds.SpeaksPostgres(kind) && wanted == typeof(string))
            {
                return new Cell(text, true);
            }
            return ImportTypes.TryConvert(text, wanted, out object value) ? new Cell(value, false) : null;
        }

        // Text SQLite takes by its column's affinity; bytes as bytes; the others' a value of the type their driver reads the column as.
        Type Wanted((Type Type, string Name) type) => ImportTypes.IsBinary(type.Type, type.Name) ? typeof(byte[])
            : kind == DatabaseKind.Sqlite && type.Name.Contains("bool", StringComparison.OrdinalIgnoreCase) ? typeof(bool)
            : DatabaseKinds.SpeaksPostgres(kind) || kind == DatabaseKind.Sqlite ? typeof(string) : type.Type;

        static void Give(DbParameter parameter, Cell cell)
        {
            if (parameter is NpgsqlParameter literal)
            {
                literal.ResetDbType();
                if (cell.Literal)
                {
                    literal.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Unknown;
                }
            }
            parameter.Value = cell.Value;
        }

        static string Named(int column) => $"p{column.ToString(CultureInfo.InvariantCulture)}";
    }
}
