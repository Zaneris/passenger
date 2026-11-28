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
    Redis,
    DynamoDb,
    Cassandra,
    Redshift,
}

/// <summary>What a kind of database is, as code that reads its queries' words, without its drivers, asks it.</summary>
public static partial class DatabaseKinds
{
    /// <summary>Whether the kind keeps documents (MongoDB's, Cosmos DB's, DynamoDB's items): its tables are collections of
    /// them, read and written through its own client, a console of it run in its own queries.</summary>
    public static bool HoldsDocuments(DatabaseKind kind) => kind is DatabaseKind.MongoDb or DatabaseKind.CosmosDb or DatabaseKind.DynamoDb;

    /// <summary>Whether the kind is read in SQL, through its ADO.NET driver: every kind but the document databases, Redis's
    /// keys and Cassandra, each read through its own client in its own queries.</summary>
    public static bool SpeaksSql(DatabaseKind kind) => !HoldsDocuments(kind) && kind is not (DatabaseKind.Redis or DatabaseKind.Cassandra);

    /// <summary>Whether the kind speaks PostgreSQL's protocol and its SQL, through Npgsql: PostgreSQL, and Amazon Redshift,
    /// whose catalog, plans and keys are its own.</summary>
    public static bool SpeaksPostgres(DatabaseKind kind) => kind is DatabaseKind.Postgres or DatabaseKind.Redshift;

    /// <summary>Whether a host is a Redshift cluster's or serverless workgroup's endpoint, the name AWS gives it.</summary>
    public static bool IsRedshiftHost(string host) =>
        host.Contains(".redshift.amazonaws.com", StringComparison.OrdinalIgnoreCase) || host.Contains(".redshift-serverless.amazonaws.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the kind's own queries read as SQL does, their statements, strings, names and effects: DynamoDB's
    /// PartiQL and Cassandra's CQL.</summary>
    public static bool QueriesAsSql(DatabaseKind kind) => SpeaksSql(kind) || kind is DatabaseKind.DynamoDb or DatabaseKind.Cassandra;
}
