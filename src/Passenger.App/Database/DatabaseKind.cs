namespace Passenger.App.Database;

public enum DatabaseKind
{
    Sqlite,
    Postgres,
    SqlServer,
    MySql,
    DuckDb,
    ClickHouse,
    Snowflake,
    MongoDb,
    CosmosDb,
}

/// <summary>What a kind of database is, as code that reads its queries' words, without its drivers, asks it.</summary>
public static partial class DatabaseKinds
{
    /// <summary>Whether the kind keeps documents (MongoDB's, Cosmos DB's): its tables are collections of them, read and
    /// written through its own client, a console of it run in its own queries.</summary>
    public static bool HoldsDocuments(DatabaseKind kind) => kind is DatabaseKind.MongoDb or DatabaseKind.CosmosDb;
}
