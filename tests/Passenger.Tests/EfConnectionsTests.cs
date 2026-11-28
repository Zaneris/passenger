using Passenger.App.Migrations;
using Passenger.Languages.CSharp;

namespace Passenger.Tests;

/// <summary>The connection dotnet ef reads for a context, by its name in the configuration: the one its design-time factory
/// reads, alone where it has one; else the one the context's registration in code reads, directly, through a local or as an
/// Aspire integration's name, else the one the context reads itself, else the one configured under the context's name; never
/// the first configured, and none where nothing tells.</summary>
[Collection(Beside.Name)]
[SameOnEverySystem]
public sealed class EfConnectionsTests
{
    private static readonly string[] Configured = ["Reporting", "Shop"];

    /// <summary>A context's connection is the one its registration names, else the one configured under the context's
    /// name.</summary>
    [Fact]
    public void A_contexts_connection_is_the_one_its_registration_names()
    {
        Flow.Step(nameof(A_registration_in_code_names_the_connection_directly_through_a_local_or_as_aspires_connection_name),
            A_registration_in_code_names_the_connection_directly_through_a_local_or_as_aspires_connection_name);
        Flow.Step(nameof(Without_a_registration_the_connection_configured_under_the_contexts_name_is_its_and_never_the_first),
            Without_a_registration_the_connection_configured_under_the_contexts_name_is_its_and_never_the_first);
    }

    private void A_registration_in_code_names_the_connection_directly_through_a_local_or_as_aspires_connection_name()
    {
        Assert.Equal("Sales", Name("builder.Services.AddDbContext<ShopContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString(\"Sales\")));"));
        Assert.Equal("DefaultConnection", Name("""
            string connection = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException();
            builder.Services.AddDbContext<Data.ShopContext>(options => options.UseSqlServer(connection));
            """));
        Assert.Equal("shopdb", Name("builder.AddNpgsqlDbContext<ShopContext>(\"shopdb\");"));
        Assert.Equal("Sales", Name("""
            public sealed class ShopContext(IConfiguration configuration) : DbContext
            {
                protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(configuration.GetConnectionString("Sales"));
            }
            """));
        // A design-time factory, which dotnet ef makes the context by, comes before the registration, alone.
        Assert.Equal("Local", Name("""
            builder.Services.AddDbContext<ShopContext>(o => o.UseSqlServer(builder.Configuration.GetConnectionString("Sales")));
            public sealed class ShopFactory : IDesignTimeDbContextFactory<ShopContext>
            {
                public ShopContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ShopContext>().UseSqlServer(Settings().GetConnectionString("Local")).Options);
            }
            """));
        const string Unread = """
            builder.Services.AddDbContext<ShopContext>(o => o.UseSqlite("Data Source=production.db"));
            public sealed class ShopFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<Data.ShopContext>
            {
                public ShopContext CreateDbContext(string[] args) => new(Options.From(args));
            }
            """;
        Assert.Null(Name(Unread));
        Assert.Null(EfConnections.WrittenOf("ShopContext", [("Program.cs", Unread)], CSharpParsing.Default));
        Assert.Equal("ShopFactory", EfConnections.FactoryOf("ShopContext", [("Program.cs", Unread)], CSharpParsing.Default));
        Assert.Equal("Data Source=local.db", EfConnections.WrittenOf("ShopContext", [("Program.cs", Unread.Replace("new(Options.From(args))", "new(new DbContextOptionsBuilder<ShopContext>().UseSqlite(\"Data Source=local.db\").Options)", StringComparison.Ordinal))], CSharpParsing.Default));
    }

    private void Without_a_registration_the_connection_configured_under_the_contexts_name_is_its_and_never_the_first()
    {
        Assert.Equal("Shop", Name("builder.Services.AddDbContext<SalesContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString(\"Reporting\")));"));
        Assert.Null(EfConnections.NameOf("AuditContext", [("Program.cs", "Console.WriteLine();")], CSharpParsing.Default, Configured));
        Assert.Equal("Audit", EfConnections.Assumed("AuditContext"));
        Assert.Equal("Application", EfConnections.Assumed("ApplicationDbContext"));
    }

    private static string? Name(string code) => EfConnections.NameOf("ShopContext", [("Program.cs", code)], CSharpParsing.Default, Configured)?.Name;
}
