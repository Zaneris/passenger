using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Migrations;

/// <summary>The code the Migrations window writes: a design-time factory for a context no host registers, which makes it
/// with its provider's own <c>Use…</c> and the connection string of the context's name, read from the configuration as
/// dotnet ef runs, never written into the code.</summary>
public static class EfTemplates
{
    /// <summary>A factory for the context, in its namespace, making it with the provider the project restored and the
    /// connection string named <paramref name="connection"/>: the environment's ConnectionStrings__NAME, else
    /// appsettings.{environment}.json's, else appsettings.json's, where dotnet ef runs, as a host reads them.</summary>
    public static string Factory(EfContext context, EfRestore? restore, string connection)
    {
        int dot = context.FullName.LastIndexOf('.');
        string? space = dot > 0 ? context.FullName[..dot] : null;
        string name = context.Name;
        string provider = restore?.Providers.Select(p => p.Id).FirstOrDefault(p => Use(p) is not null) ?? string.Empty;
        string quoted = JsonSerializer.Serialize(connection);
        string use = Use(provider) switch
        {
            "UseMySql" => "options.UseMySql(connection, ServerVersion.AutoDetect(connection));",
            { } method => $"options.{method}(connection);",
            null => "// Name the provider here: options.UseSqlite(connection), UseSqlServer, UseNpgsql…",
        };
        string namespaced = space is null ? string.Empty : $"namespace {space};\n\n";
        return $$"""
            using System.Text.Json;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Design;

            {{namespaced}}/// <summary>How dotnet ef makes {{name}} at design time, where no host registers it, with the connection string
            /// named {{connection}} in the configuration.</summary>
            public sealed class {{name}}Factory : IDesignTimeDbContextFactory<{{name}}>
            {
                public {{name}} CreateDbContext(string[] args)
                {
                    string connection = ConnectionString({{quoted}});
                    DbContextOptionsBuilder<{{name}}> options = new();
                    {{use}}
                    return new {{name}}(options.Options);
                }

                /// <summary>A connection string by its name, as the host reads it: the environment's ConnectionStrings__NAME, else
                /// appsettings.{environment}.json's, else appsettings.json's, in the folder dotnet ef runs in.</summary>
                private static string ConnectionString(string name)
                {
                    if (System.Environment.GetEnvironmentVariable($"ConnectionStrings__{name}") is { Length: > 0 } given)
                    {
                        return given;
                    }
                    string environment = System.Environment.GetEnvironmentVariable("{{AspNetCoreVariables.AspNetCoreEnvironment}}")
                        ?? System.Environment.GetEnvironmentVariable("{{AspNetCoreVariables.DotnetEnvironment}}") ?? "Production";
                    foreach (string file in new[] { $"appsettings.{environment}.json", "appsettings.json" })
                    {
                        string path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), file);
                        if (!System.IO.File.Exists(path))
                        {
                            continue;
                        }
                        JsonDocumentOptions lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                        using JsonDocument settings = JsonDocument.Parse(System.IO.File.ReadAllText(path), lenient);
                        if (settings.RootElement.TryGetProperty("ConnectionStrings", out JsonElement strings) && strings.ValueKind == JsonValueKind.Object
                            && strings.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } found)
                        {
                            return found;
                        }
                    }
                    throw new System.InvalidOperationException(
                        $"No connection string named {name}: give it in appsettings.json's ConnectionStrings or as ConnectionStrings__{name}");
                }
            }

            """;
    }

    /// <summary>The provider's own method on the options builder, by its package; null for one not known here.</summary>
    private static string? Use(string provider) => provider switch
    {
        _ when provider.StartsWith("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.OrdinalIgnoreCase) => "UseSqlite",
        _ when provider.StartsWith("Microsoft.EntityFrameworkCore.SqlServer", StringComparison.OrdinalIgnoreCase) => "UseSqlServer",
        _ when provider.StartsWith("Npgsql.EntityFrameworkCore.PostgreSQL", StringComparison.OrdinalIgnoreCase) => "UseNpgsql",
        _ when provider.StartsWith("Pomelo.EntityFrameworkCore.MySql", StringComparison.OrdinalIgnoreCase) => "UseMySql",
        _ when provider.StartsWith("Oracle.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) => "UseOracle",
        _ => null,
    };
}
