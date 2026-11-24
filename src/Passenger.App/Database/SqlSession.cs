using System.Data.Common;

namespace Passenger.App.Database;

/// <summary>A console's connection held open in manual commit: its statements run in a transaction begun by the first of
/// them, which stays open across runs until Commit or Rollback ends it, the connection kept for the next. Used by one run
/// at a time, off the window's thread; let go of, an open transaction is rolled back as the connection closes.</summary>
public sealed class SqlSession(DatabaseSource source, DbConnection connection) : IAsyncDisposable
{
    private DbTransaction? transaction;

    /// <summary>The connection it was opened on, its mark as it was then.</summary>
    public DatabaseSource Source { get; } = source;

    /// <summary>Whether a transaction is open, begun by a statement and not yet committed or rolled back.</summary>
    public bool InTransaction => transaction is not null;

    /// <summary>How many statements ran in the transaction open, and how many rows they changed.</summary>
    public int Statements { get; private set; }

    public int Changed { get; private set; }

    internal static async Task<SqlSession> OpenAsync(DatabaseSource source, CancellationToken cancel) =>
        new(source, await DatabaseClient.OpenAsync(source, cancel));

    /// <summary>Runs statements in the transaction, beginning it first where none is open.</summary>
    internal async Task<bool> RunAsync(IReadOnlyList<string> runs, int limit, Action<SqlResult> told, CancellationToken cancel, IReadOnlyDictionary<string, string>? values = null)
    {
        // SQLite's transaction locks the file for writing only once a statement writes, as the others' do their rows.
        transaction ??= connection is Microsoft.Data.Sqlite.SqliteConnection sqlite ? sqlite.BeginTransaction(deferred: true) : await connection.BeginTransactionAsync(cancel);
        return await DatabaseClient.RunOnAsync(Source, connection, transaction, runs, limit, result =>
        {
            Statements++;
            Changed += Math.Max(0, result.Affected);
            told(result);
        }, cancel, values);
    }

    /// <summary>Commits the transaction open, or rolls it back, the connection kept for the next statement.</summary>
    internal async Task EndAsync(bool commit, CancellationToken cancel)
    {
        if (transaction is not { } open)
        {
            return;
        }
        try
        {
            if (commit)
            {
                await open.CommitAsync(cancel);
            }
            else
            {
                await open.RollbackAsync(cancel);
            }
        }
        finally
        {
            transaction = null;
            Statements = 0;
            Changed = 0;
            await open.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (transaction is { } open)
        {
            transaction = null;
            await open.DisposeAsync();
        }
        await connection.DisposeAsync();
    }
}
