using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Migrations;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.UnitTests.Migrations;

/// <summary>The projects the Migrations window offers dotnet ef, from the projects as evaluated and their C# files' text:
/// for Project those declaring a context or holding its migrations, wherever EF Core reaches them from; for Startup those
/// that load the chosen one and run on .NET, the programs with dotnet ef's Design package first.</summary>
public sealed class EfTargetsTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\shop" : "/shop";

    private const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";

    private const string Design = "Microsoft.EntityFrameworkCore.Design";

    private static ProjectModel Project(string name, string? output = null, string[]? packages = null, string[]? references = null, string framework = "net10.0") =>
        new(Path.Combine(Root, name, $"{name}.csproj"), name, output, [framework], [.. (packages ?? []).Select(p => (p, (string?)"10.0.0"))],
            [.. (references ?? []).Select(r => Path.Combine(Root, r, $"{r}.csproj"))], true);

    private static (string File, EfFileRead Read) File(ProjectModel project, string text) =>
        (Path.Combine(project.Folder, "Code.cs"), EfTargets.ReadOf(text, CSharpSyntaxTree.ParseText(text).GetRoot()));

    private static IReadOnlyList<EfProjectRead> Read(params (ProjectModel Project, string[] Files)[] projects) =>
        EfTargets.Resolve([.. projects.Select(p => (p.Project, (IReadOnlyList<(string, EfFileRead)>)[.. p.Files.Select(f => File(p.Project, f))]))]);

    private static string Names(IEnumerable<ProjectModel> projects) => string.Join(", ", projects.Select(p => p.Name));

    /// <summary>The context's project and startup are those dotnet ef can run, the Design package first.</summary>
    [Fact]
    public void Ef_cores_project_and_startup_are_those_dotnet_ef_can_run() => Cases.All(
        (nameof(A_library_holding_the_context_is_the_project_and_the_web_app_referencing_it_the_first_startup),
            A_library_holding_the_context_is_the_project_and_the_web_app_referencing_it_the_first_startup),
        (nameof(A_program_holding_its_own_context_is_both_its_project_and_its_startup),
            A_program_holding_its_own_context_is_both_its_project_and_its_startup),
        (nameof(Migrations_kept_in_an_assembly_of_their_own_are_made_there_first), Migrations_kept_in_an_assembly_of_their_own_are_made_there_first),
        (nameof(A_startup_dotnet_ef_cannot_run_is_left_out_and_one_with_the_Design_package_comes_first),
            A_startup_dotnet_ef_cannot_run_is_left_out_and_one_with_the_Design_package_comes_first));

    private void A_library_holding_the_context_is_the_project_and_the_web_app_referencing_it_the_first_startup()
    {
        ProjectModel api = Project("MyApp.Api", "Exe", [Design], ["MyApp.Data", "MyApp.Services"]);
        ProjectModel data = Project("MyApp.Data", packages: [Sqlite], references: ["MyApp.Domain"]);
        ProjectModel domain = Project("MyApp.Domain");
        ProjectModel services = Project("MyApp.Services", references: ["MyApp.Data"]);
        ProjectModel[] all = [api, data, domain, services];
        IReadOnlyList<EfProjectRead> read = Read((api, ["var app = WebApplication.Create(args);"]),
            (data, ["public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options) { }"]),
            (services, ["public sealed class Orders(AppDbContext context) { }"]));

        IReadOnlyList<ProjectModel> projects = EfTargets.Projects(all, read);
        Assert.True(Names(projects) == "MyApp.Data", $"Project listed [{Names(projects)}]: the web app with only the Design package would be chosen, and its context never found");
        IReadOnlyList<ProjectModel> startups = EfTargets.Startups(data, all);
        Assert.True(Names(startups) == "MyApp.Api, MyApp.Data, MyApp.Services",
            $"Startup listed [{Names(startups)}] for the library: the web app that runs it would not be offered first");
    }

    private void A_program_holding_its_own_context_is_both_its_project_and_its_startup()
    {
        ProjectModel shop = Project("Shop", "Exe", [Sqlite, Design]);
        IReadOnlyList<EfProjectRead> read = Read((shop, ["using ShopContext context = new();\n\npublic sealed class ShopContext : DbContext { }\n"]));

        Assert.True(Names(EfTargets.Projects([shop], read)) == "Shop", $"Project listed [{Names(EfTargets.Projects([shop], read))}]");
        Assert.True(Names(EfTargets.Startups(shop, [shop])) == "Shop", $"Startup listed [{Names(EfTargets.Startups(shop, [shop]))}]");
    }

    private void Migrations_kept_in_an_assembly_of_their_own_are_made_there_first()
    {
        ProjectModel data = Project("Data", packages: [Sqlite]);
        ProjectModel migrations = Project("Shop.Migrations", packages: ["Microsoft.EntityFrameworkCore.Relational"], references: ["Data"]);
        ProjectModel api = Project("Api", "Exe", [Design], ["Data", "Shop.Migrations"]);
        ProjectModel[] all = [api, data, migrations];
        string context = "public sealed class ShopContext : DbContext { }";
        string program = "builder.Services.AddDbContext<ShopContext>(o => o.UseSqlite(\"Data Source=shop.db\", b => b.MigrationsAssembly(\"Shop.Migrations\")));";

        IReadOnlyList<EfProjectRead> named = Read((api, [program]), (data, [context]), (migrations, []));
        Assert.True(Names(EfTargets.Projects(all, named)) == "Shop.Migrations, Data",
            $"With the migrations assembly named Project listed [{Names(EfTargets.Projects(all, named))}]: dotnet ef refuses to add a migration to the context's own project");

        string byType = "builder.Services.AddDbContext<ShopContext>(o => o.UseSqlite(\"x\", b => b.MigrationsAssembly(typeof(Marker).Assembly.GetName().Name)));";
        IReadOnlyList<EfProjectRead> typed = Read((api, [byType]), (data, [context]), (migrations, ["public static class Marker { }"]));
        Assert.True(Names(EfTargets.Projects(all, typed)) == "Shop.Migrations, Data", $"With the assembly named by a type of it Project listed [{Names(EfTargets.Projects(all, typed))}]");

        IReadOnlyList<EfProjectRead> held = Read((api, []), (data, [context]),
            (migrations, ["[DbContext(typeof(ShopContext))]\npartial class ShopContextModelSnapshot : ModelSnapshot { }"]));
        Assert.True(Names(EfTargets.Projects(all, held)) == "Shop.Migrations, Data", $"With the migrations made there Project listed [{Names(EfTargets.Projects(all, held))}]");
    }

    private void A_startup_dotnet_ef_cannot_run_is_left_out_and_one_with_the_Design_package_comes_first()
    {
        ProjectModel data = Project("Data", packages: [Sqlite], framework: "netstandard2.1");
        ProjectModel api = Project("Api", "Exe", [], ["Data"]);
        ProjectModel web = Project("Web", "Exe", [Design], ["Data"]);
        ProjectModel old = Project("Old", "Exe", [Design], ["Data"], framework: "net48");
        ProjectModel[] all = [api, data, old, web];

        IReadOnlyList<ProjectModel> startups = EfTargets.Startups(data, all);
        Assert.True(Names(startups) == "Web, Api", $"Startup listed [{Names(startups)}]: dotnet ef refuses a .NET Standard or .NET Framework startup, and one without the Design package fails first");
    }

}
