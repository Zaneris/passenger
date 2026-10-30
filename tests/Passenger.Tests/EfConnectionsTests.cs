using Passenger.App.Migrations;
using Passenger.Languages.CSharp;

namespace Passenger.Tests;

/// <summary>The connection a design-time factory reads for a context, by its name in the configuration: the one the
/// context's registration in code reads, directly, through a local or as an Aspire integration's name, else the one the
/// context reads itself, else the one configured under the context's name; never the first configured, and none where
/// nothing tells.</summary>
[Collection(Beside.Name)]
public sealed class EfConnectionsTests
{
    private static readonly string[] Configured = ["Reporting", "Shop"];

    [Fact]
    public void A_registration_in_code_names_the_connection_directly_through_a_local_or_as_aspires_connection_name()
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
    }

    [Fact]
    public void Without_a_registration_the_connection_configured_under_the_contexts_name_is_its_and_never_the_first()
    {
        Assert.Equal("Shop", Name("builder.Services.AddDbContext<SalesContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString(\"Reporting\")));"));
        Assert.Null(EfConnections.NameOf("AuditContext", [("Program.cs", "Console.WriteLine();")], CSharpParsing.Default, Configured));
        Assert.Equal("Audit", EfConnections.Assumed("AuditContext"));
        Assert.Equal("Application", EfConnections.Assumed("ApplicationDbContext"));
    }

    private static string? Name(string code) => EfConnections.NameOf("ShopContext", [("Program.cs", code)], CSharpParsing.Default, Configured)?.Name;
}
