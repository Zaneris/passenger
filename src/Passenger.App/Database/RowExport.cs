using System.Data.Common;
using System.Text;
using Passenger.App.Tables;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Database;

/// <summary>The formats a file of rows is written in.</summary>
public enum ExportFormat
{
    Csv,
    Json,
    Inserts,
}

/// <summary>What an export writes: a query's rows read from its connection as they come (a table's, as its tab filters and
/// sorts them, or a console's statement run again, its parameters given the <see cref="Values"/> asked), or the rows a
/// result holds already (<see cref="Held"/>); <see cref="Title"/> names them, <see cref="Into"/> is where inserts of them go.</summary>
public sealed record ExportSource(DatabaseSource Connection, string Title, InsertTarget Into)
{
    public string Sql { get; init; } = string.Empty;
    public IReadOnlyList<(string Name, object? Value)> Parameters { get; init; } = [];
    public IReadOnlyDictionary<string, string>? Values { get; init; }
    public DatabaseRows? Held { get; init; }

    /// <summary>What the export dialog says it writes.</summary>
    public string Said { get; init; } = string.Empty;
}

/// <summary>How an export writes its rows: the format, a CSV's separator and whether its first row names the columns, the
/// table inserts go into, and the file.</summary>
public sealed record ExportChoice(ExportFormat Format, char Separator, bool Header, string Table, string Path);

/// <summary>An export under way: the rows read from the server one at a time and each written to the file as it comes, the
/// reader, the writer and the row it writes all it holds, so a table of a million rows is never held in memory. The file is
/// written whole or not at all (<see cref="AtomicFile.WriteAsync"/>): stopped or refused partway, it is left as it was.</summary>
public sealed class RowExport
{
    private readonly ExportSource source;
    private readonly ExportChoice choice;
    private readonly Action<long> progress;
    private readonly (long At, Func<RowExport, Task> Hold)? held;
    private DbConnection? connection;
    private DbDataReader? reader;
    private RowWriter? writer;
    private object?[] values = [];
    private long written;

    private RowExport(ExportSource source, ExportChoice choice, Action<long> progress, (long At, Func<RowExport, Task> Hold)? held)
    {
        this.source = source;
        this.choice = choice;
        this.progress = progress;
        this.held = held;
    }

    /// <summary>The rows written so far.</summary>
    public long Written => Interlocked.Read(ref written);

    /// <summary>How often <see cref="RunAsync"/> tells its progress, in rows.</summary>
    public const int Told = 5000;

    /// <summary>Writes the rows to the file chosen, telling <paramref name="progress"/> of every <see cref="Told"/> rows; the
    /// rows written. <paramref name="held"/> holds it once that many rows are written, for a test of what it holds then.</summary>
    public static async Task<long> RunAsync(ExportSource source, ExportChoice choice, Action<long> progress, CancellationToken cancel,
        (long At, Func<RowExport, Task> Hold)? held = null)
    {
        RowExport export = new(source, choice, progress, held);
        await AtomicFile.WriteAsync(choice.Path, stream => export.FillAsync(stream, cancel), null, FileText.SaveSuffix, AtomicFile.Move);
        return export.Written;
    }

    /// <summary>A writer of the format chosen; a CSV file's records end as RFC 4180 ends them, CRLF.</summary>
    public static RowWriter Writer(TextWriter into, ExportChoice choice, DatabaseKind kind) => choice.Format switch
    {
        ExportFormat.Csv => new CsvRows(into, choice.Separator, choice.Header, "\r\n"),
        ExportFormat.Json => new JsonRows(into, "\n"),
        _ => new SqlInserts(into, new InsertTarget(kind, choice.Table), "\n"),
    };

    private async Task FillAsync(Stream stream, CancellationToken cancel)
    {
        await using StreamWriter text = new(stream, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        writer = Writer(text, choice, source.Connection.Kind);
        try
        {
            if (source.Held is { } rows)
            {
                writer.Begin([.. rows.Columns.Select((name, i) => new RowColumn(name, rows.TypeOf(i)))]);
                foreach (object?[] row in rows.Rows)
                {
                    cancel.ThrowIfCancellationRequested();
                    writer.Row(row);
                    await WroteAsync();
                }
            }
            else
            {
                await ReadAsync(cancel);
            }
            writer.End();
            await text.FlushAsync(cancel);
        }
        finally
        {
            writer = null;
        }
    }

    private async Task ReadAsync(CancellationToken cancel)
    {
        DatabaseKind kind = source.Connection.Kind;
        await using (connection = await DatabaseClient.OpenAsync(source.Connection, cancel))
        {
            await using DbCommand command = connection.CreateCommand();
            command.CommandTimeout = 0;
            command.CommandText = source.Values is { } asked ? DatabaseClient.Bind(command, source.Sql, SqlParameters.Of([source.Sql], kind)[0], asked, kind) : source.Sql;
            foreach ((string name, object? value) in source.Parameters)
            {
                DbParameter parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
            using CancellationTokenRegistration interrupt = DatabaseClient.Interrupt(connection, cancel);
            await using (reader = await command.ExecuteReaderAsync(cancel))
            {
                string[] types = DatabaseClient.Types(reader);
                writer!.Begin([.. Enumerable.Range(0, reader.FieldCount).Select(i => new RowColumn(reader.GetName(i), types[i]))]);
                values = new object?[reader.FieldCount];
                while (await reader.ReadAsync(cancel))
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        values[i] = reader.IsDBNull(i) ? null : Value(reader, i);
                    }
                    writer.Row(values);
                    await WroteAsync();
                }
            }
            reader = null;
        }
        connection = null;
    }

    /// <summary>A value as its driver reads it, or as its text where the driver maps its type to none of .NET's (a type of
    /// the database's own it knows no reader for).</summary>
    private static object Value(DbDataReader reader, int column)
    {
        try
        {
            return reader.GetValue(column);
        }
        catch (Exception unmapped) when (unmapped is InvalidCastException or NotSupportedException)
        {
            return reader.GetString(column);
        }
    }

    private async Task WroteAsync()
    {
        long now = Interlocked.Increment(ref written);
        if (now % Told == 0)
        {
            progress(now);
        }
        if (held is { } hold && hold.At == now)
        {
            await hold.Hold(this);
        }
    }
}
