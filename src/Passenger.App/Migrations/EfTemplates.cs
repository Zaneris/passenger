using System.Text.Json;
using Passenger.Text;

namespace Passenger.App.Migrations;

/// <summary>The code the Migrations window writes: a design-time factory for a context no host registers, which makes it
/// with its provider's own <c>Use…</c> and the connection string the startup project names.</summary>
public static class EfTemplates
{
    /// <summary>A factory for the context, in its namespace, making it with the provider the project restored.</summary>
    public static string Factory(EfContext context, EfRestore? restore, string? connection)
    {
        int dot = context.FullName.LastIndexOf('.');
        string? space = dot > 0 ? context.FullName[..dot] : null;
        string name = context.Name;
        string provider = restore?.Providers.Select(p => p.Id).FirstOrDefault(p => Use(p) is not null) ?? string.Empty;
        string quoted = JsonSerializer.Serialize(connection ?? DefaultConnection(provider));
        string use = Use(provider) switch
        {
            "UseMySql" => $"options.UseMySql({quoted}, ServerVersion.AutoDetect({quoted}));",
            { } method => $"options.{method}({quoted});",
            null => $"// Name the provider here: options.UseSqlite({quoted}), UseSqlServer, UseNpgsql…",
        };
        string namespaced = space is null ? string.Empty : $"namespace {space};\n\n";
        return $$"""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Design;

            {{namespaced}}/// <summary>How dotnet ef makes {{name}} at design time, where no host registers it.</summary>
            public sealed class {{name}}Factory : IDesignTimeDbContextFactory<{{name}}>
            {
                public {{name}} CreateDbContext(string[] args)
                {
                    DbContextOptionsBuilder<{{name}}> options = new();
                    {{use}}
                    return new {{name}}(options.Options);
                }
            }

            """;
    }

    /// <summary>The first connection string the startup project's appsettings.json names, or null.</summary>
    public static string? ConnectionOf(string folder)
    {
        try
        {
            if (FileText.Load(Path.Combine(folder, "appsettings.json")) is not { } settings)
            {
                return null;
            }
            using JsonDocument read = JsonDocument.Parse(settings.Text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return read.RootElement.TryGetProperty("ConnectionStrings", out JsonElement strings) && strings.ValueKind == JsonValueKind.Object
                ? strings.EnumerateObject().Select(s => s.Value.ValueKind == JsonValueKind.String ? s.Value.GetString() : null).FirstOrDefault(s => s is { Length: > 0 })
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
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

    private static string DefaultConnection(string provider) => Use(provider) switch
    {
        "UseSqlite" => "Data Source=app.db",
        "UseSqlServer" => "Server=localhost;Database=app;Trusted_Connection=True;TrustServerCertificate=True",
        "UseNpgsql" => "Host=localhost;Database=app;Username=postgres;Password=postgres",
        "UseMySql" => "Server=localhost;Database=app;User=root;Password=root",
        _ => "Data Source=app.db",
    };
}
