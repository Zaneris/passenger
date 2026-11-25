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

    /// <summary>A connection string as a connection of its kind, its secrets marked, a SQLite file's path made whole from
    /// <paramref name="folder"/>; null for one that names no database Passenger opens.</summary>
    internal static DatabaseSource? Source(string name, string found, string value, string folder, IEnumerable<string> packages)
    {
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
