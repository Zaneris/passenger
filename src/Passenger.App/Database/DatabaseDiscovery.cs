using System.Data.Common;
using System.Text.Json;
using Passenger.App.Migrations;
using Passenger.App.Containers;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Host;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Database;

internal static class DatabaseDiscovery
{
    internal static async Task<IReadOnlyList<DatabaseSource>> ReadAsync(ProjectSession session, CancellationToken cancel)
    {
        List<DatabaseSource> found = [];
        ProjectModel[] projects = [.. session.Projects];
        RunConfiguration[] configurations = [.. session.Configurations.All];
        foreach (ProjectModel project in projects)
        {
            RunConfiguration?[] runs = [.. configurations.Where(r => !r.IsContainer && PathNames.Same(r.Project, project.Path))];
            if (runs.Length == 0)
            {
                runs = [null];
            }
            EfRunDatabase? live = session.ContainerRun is { IsStopped: false } containers ? await EfDatabases.OfComposeAsync(containers, project, cancel) : null;
            live ??= EfDatabases.OfAspire(session.Aspire, project);
            foreach (RunConfiguration? run in runs)
            {
                Dictionary<string, string> environment = run?.Environment ?? [];
                string kind = AspNetCoreVariables.EnvironmentOf(environment.GetValueOrDefault, "Production");
                Dictionary<string, string> values = await BackgroundWork.Run(() => Configured(project, kind), cancel);
                foreach ((string key, string value) in environment)
                {
                    Take(values, key, value);
                }
                Add(values, run?.Name ?? project.Name);
            }
            if (live is not null)
            {
                Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
                foreach ((string key, string value) in live.Environment)
                {
                    Take(values, key, value);
                }
                Add(values, $"{project.Name} · running service");
            }

            void Add(Dictionary<string, string> values, string from)
            {
                foreach ((string name, string value) in values)
                {
                    if (Source(name, $"found in {from}", value, project.Folder, project.Packages.Select(p => p.Name)) is { } source
                        && !found.Any(s => s.Kind == source.Kind && s.ConnectionString == source.ConnectionString))
                    {
                        found.Add(source);
                    }
                }
            }
        }
        // Each database a compose run or an AppHost runs, whoever uses it, as this machine reaches it.
        if (session.ContainerRun is { IsStopped: false } composed)
        {
            foreach ((string service, string value, string[] packages, _) in await EfDatabases.OfServicesAsync(composed, cancel))
            {
                Running(service, $"found in {composed.Name} · running service {service}", value, packages);
            }
        }
        foreach (Aspire.AspireAppRun app in session.Aspire.Apps.Where(a => !a.Stopping))
        {
            foreach ((string resource, string shown, string image) in app.Connections)
            {
                // A resource's connection string says its kind, but for Redis's own, which its image says.
                if (app.Interactions?.ConnectionStrings.GetValueOrDefault(resource) is { } value)
                {
                    Running(shown, $"found in {app.Name} · running resource {shown}", value, RedisImage(image) ? ["StackExchange.Redis"] : []);
                }
            }
        }

        // A running database a service's connection string names already is listed once, as that service names it.
        void Running(string name, string from, string value, string[] packages)
        {
            DatabaseSource? source;
            try
            {
                source = session.Folder is { } open ? Source(name, from, value, open, packages) : null;
            }
            catch (ArgumentException)
            {
                // A resource's string that is no connection string of keys and values (a cache's host and port) opens nothing.
                return;
            }
            if (source is not null && !found.Any(s => Reaches(s, source)))
            {
                found.Add(source);
            }
        }

        // The folder's own DuckDB files, and its Parquet and CSV files, which a DuckDB in memory reads where none is there.
        if (session.Folder is { } open)
        {
            (IReadOnlyList<string> databases, bool files) = await BackgroundWork.Run(() => DuckDbFiles.In(open), cancel);
            found.AddRange(databases.Select(d => new DatabaseSource(Path.GetFileName(d), DatabaseKind.DuckDb, open) { ConnectionString = $"Data Source={d}", Found = "found in the folder" })
                .Where(d => !found.Any(s => s.Kind == d.Kind && s.ConnectionString == d.ConnectionString)));
            if (files && !found.Any(s => s.Kind == DatabaseKind.DuckDb))
            {
                found.Add(new DatabaseSource(DuckDbFiles.Schema, DatabaseKind.DuckDb, open) { ConnectionString = "Data Source=:memory:", Found = "found in the folder's Parquet and CSV files" });
            }
        }
        cancel.ThrowIfCancellationRequested();
        return found;
    }

    /// <summary>Whether an image is Redis's, or Valkey's or Garnet's, which take its commands.</summary>
    internal static bool RedisImage(string? image) => image is not null
        && (image.Contains("redis", StringComparison.OrdinalIgnoreCase) || image.Contains("valkey", StringComparison.OrdinalIgnoreCase) || image.Contains("garnet", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether two connections open one database of one server: of one kind, at one host and port, the same database.</summary>
    private static bool Reaches(DatabaseSource one, DatabaseSource other) =>
        one.Kind == other.Kind && (one.ConnectionString == other.ConnectionString || !DatabaseKinds.IsFile(one.Kind)
            && DatabaseKinds.Endpoint(one.Kind, one.ConnectionString) is var at && DatabaseKinds.Endpoint(other.Kind, other.ConnectionString) is var there
            && DatabaseKinds.SameHost(at.Host, there.Host) && at.Port == there.Port && one.Database.Equals(other.Database, StringComparison.OrdinalIgnoreCase));

    /// <summary>The connection strings a project reads running in an environment, by name: its appsettings.json's, then the
    /// environment's, then its user secrets' in Development.</summary>
    internal static Dictionary<string, string> Configured(ProjectModel project, string environment)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in (string[])["appsettings.json", $"appsettings.{environment}.json"])
        {
            foreach ((string name, string value) in BackingService.ConnectionStrings(project.Folder, file))
            {
                values[name] = value;
            }
        }
        if (environment == "Development" && UserSecrets.FileOf(UserSecrets.Of(project.Path, project.Property(CarriedProperties.UserSecretsId),
                file => FileText.Load(file)?.Text)) is { } secrets && FileText.Load(secrets) is { } text)
        {
            using JsonDocument json = JsonDocument.Parse(text.Text, LenientJson.Document);
            foreach (JsonProperty property in JsonMembers.Properties(json.RootElement, "ConnectionStrings"))
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    values[property.Name] = property.Value.GetString()!;
                }
            }
            foreach (JsonProperty property in json.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    Take(values, property.Name, property.Value.GetString()!);
                }
            }
        }
        return values;
    }

    /// <summary>A variable taken as the configuration reads it, where it names a connection string (ConnectionStrings__NAME).</summary>
    internal static void Take(Dictionary<string, string> values, string key, string value)
    {
        string normalized = key.Replace("__", ":", StringComparison.Ordinal);
        if (normalized.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase))
        {
            values[normalized[18..]] = value;
        }
    }

    /// <summary>A Redis connection string as Passenger keeps one: a redis:// or rediss:// URI's parts, or StackExchange.Redis's
    /// configuration (host:port,password=…) whole where the project reads Redis; its password marked. Null for another.</summary>
    private static string? Redis(string value, IEnumerable<string> packages)
    {
        string written = value.Trim();
        if ((written.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) || written.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(written, UriKind.Absolute, out Uri? address))
        {
            string[] user = address.UserInfo.Split(':', 2);
            // A URI's user part alone is its password, as redis://secret@host writes it.
            string? password = user.Length == 2 ? Uri.UnescapeDataString(user[1]) : user[0].Length > 0 ? Uri.UnescapeDataString(user[0]) : null;
            ContainerSecrets.Mark(written);
            if (password is not null)
            {
                ContainerSecrets.Mark(password);
            }
            string kept = DatabaseKinds.With(DatabaseKinds.With(string.Empty, "Host", address.Host), "Port",
                (address.Port > 0 ? address.Port : DatabaseKinds.Port(DatabaseKind.Redis)).ToString(System.Globalization.CultureInfo.InvariantCulture));
            kept = user.Length == 2 && user[0].Length > 0 ? DatabaseKinds.With(kept, "Username", Uri.UnescapeDataString(user[0])) : kept;
            kept = password is not null ? DatabaseKinds.With(kept, "Password", password) : kept;
            kept = address.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase) ? DatabaseKinds.With(kept, "Ssl", "true") : kept;
            return address.AbsolutePath.Trim('/') is { Length: > 0 } number ? DatabaseKinds.With(kept, "Database", RedisNames.Database(RedisNames.Number(number))) : kept;
        }
        if (value.Contains(';') || value.Contains("://", StringComparison.Ordinal) || !packages.Any(p => p.Contains("Redis", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        try
        {
            StackExchange.Redis.ConfigurationOptions options = StackExchange.Redis.ConfigurationOptions.Parse(written);
            if (options.EndPoints.Count == 0)
            {
                return null;
            }
            ContainerSecrets.Mark(written);
            if (options.Password is { Length: > 0 } secret)
            {
                ContainerSecrets.Mark(secret);
            }
            string kept = DatabaseKinds.With(string.Empty, "Configuration", written);
            return options.DefaultDatabase is { } database ? DatabaseKinds.With(kept, "Database", RedisNames.Database(database)) : kept;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A connection string as a connection of its kind, its secrets marked, a SQLite file's path made whole from
    /// <paramref name="folder"/>; null for one that names no database Passenger opens.</summary>
    internal static DatabaseSource? Source(string name, string found, string value, string folder, IEnumerable<string> packages)
    {
        // MongoDB's connection string is a URI, its password marked as each secret is; Cosmos DB's names its account's endpoint.
        if (value.Trim() is var uri && (uri.StartsWith("mongodb://", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("mongodb+srv://", StringComparison.OrdinalIgnoreCase)))
        {
            ContainerSecrets.Mark(uri);
            if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? address) && address.UserInfo.Split(':', 2) is [_, var password])
            {
                ContainerSecrets.Mark(Uri.UnescapeDataString(password));
            }
            return new DatabaseSource(name, DatabaseKind.MongoDb, folder) { ConnectionString = DatabaseKinds.With(string.Empty, "Uri", uri), Found = found };
        }
        if (Redis(value, packages) is { } redis)
        {
            return new DatabaseSource(name, DatabaseKind.Redis, folder) { ConnectionString = redis, Found = found };
        }
        if (value.Contains("AccountEndpoint=", StringComparison.OrdinalIgnoreCase))
        {
            DbConnectionStringBuilder account = new() { ConnectionString = value };
            foreach (string key in account.Keys)
            {
                if (ContainerSecrets.Secret(key))
                {
                    ContainerSecrets.Mark(Convert.ToString(account[key], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
            return new DatabaseSource(name, DatabaseKind.CosmosDb, folder) { ConnectionString = value, Found = found };
        }
        if (string.IsNullOrWhiteSpace(value) || !value.Contains('=') || value.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        DbConnectionStringBuilder parsed = new() { ConnectionString = value };
        ContainerSecrets.Mark(value);
        foreach (string key in parsed.Keys)
        {
            if (ContainerSecrets.Secret(key))
            {
                ContainerSecrets.Mark(Convert.ToString(parsed[key], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
            }
        }
        string[] references = [.. packages];
        bool sqlServer = parsed.ContainsKey("Initial Catalog") || parsed.ContainsKey("Integrated Security") || parsed.ContainsKey("Trusted_Connection")
            || references.Any(p => p.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) || p.Contains("SqlServer", StringComparison.OrdinalIgnoreCase));
        bool clickHouse = references.Any(p => p.Contains("ClickHouse", StringComparison.OrdinalIgnoreCase));
        string? file = DatabaseKinds.Value(value, "Data Source", "DataSource");
        bool duck = references.Any(p => p.Contains("DuckDB", StringComparison.OrdinalIgnoreCase)) || file is not null && PathNames.HasExtension(file, [".duckdb", ".ddb"]);
        DatabaseKind? kind = parsed.ContainsKey("account") ? DatabaseKind.Snowflake
            : parsed.ContainsKey("Host") ? clickHouse ? DatabaseKind.ClickHouse : DatabaseKind.Postgres
            : parsed.ContainsKey("Server") ? parsed.ContainsKey("Port") || references.Any(p => p.Contains("MySql", StringComparison.OrdinalIgnoreCase)) ? DatabaseKind.MySql : DatabaseKind.SqlServer
            : parsed.ContainsKey("Data Source") || parsed.ContainsKey("DataSource") || parsed.ContainsKey("Filename")
                ? sqlServer ? DatabaseKind.SqlServer : duck ? DatabaseKind.DuckDb : DatabaseKind.Sqlite
            : null;
        if (kind == DatabaseKind.Sqlite)
        {
            Microsoft.Data.Sqlite.SqliteConnectionStringBuilder sqlite = new(value);
            if (sqlite.DataSource == ":memory:" || sqlite.Mode == Microsoft.Data.Sqlite.SqliteOpenMode.Memory)
            {
                return null;
            }
            sqlite.DataSource = PathNames.Canonical(Path.GetFullPath(sqlite.DataSource, folder));
            value = sqlite.ConnectionString;
        }
        else if (kind == DatabaseKind.DuckDb && file is { Length: > 0 } && file != ":memory:")
        {
            value = DatabaseKinds.With(value, "Data Source", PathNames.Canonical(Path.GetFullPath(file, folder)));
        }
        return kind is { } provider ? new DatabaseSource(name, provider, folder) { ConnectionString = value, Found = found } : null;
    }
}
