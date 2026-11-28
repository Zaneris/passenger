using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Migrations;
using Passenger.Workspace;

namespace Passenger.UnitTests.Migrations;

/// <summary>Where Add Migration writes a context's migrations, as dotnet ef's --output-dir takes it: a folder of the project
/// typed or picked, from the project's in forward slashes; the folders already holding the context's migrations offered,
/// found by the attributes dotnet ef writes on them wherever they are kept; and where dotnet ef puts one given none.</summary>
public sealed class EfOutputFoldersTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\shop" : "/shop";

    private static readonly ProjectModel Data = new(Path.Combine(Root, "MyApp.Data", "MyApp.Data.csproj"), "MyApp.Data", null, ["net10.0"],
        [("Microsoft.EntityFrameworkCore.Sqlite", "10.0.0")], [], true);

    private static string Designer(string context, string id) =>
        $"[DbContext(typeof({context}))]\n[Migration(\"{id}\")]\npartial class {id[15..]}\n{{\n}}\n";

    private static string Snapshot(string context) => $"[DbContext(typeof(MyApp.Data.{context}))]\npartial class {context}ModelSnapshot : ModelSnapshot\n{{\n}}\n";

    /// <summary>The project's read with each file at its place under the project's folder.</summary>
    private static EfProjectRead Read(params (string File, string Text)[] files) =>
        Assert.Single(EfTargets.Resolve([(Data, [.. files.Select(f => (Path.Combine(Data.Folder, f.File), EfTargets.ReadOf(f.Text, CSharpSyntaxTree.ParseText(f.Text).GetRoot())))])]));

    private static readonly EfProjectRead TwoSets = Read(
        ("Contexts.cs", "public sealed class PostgresContext : DbContext { }\npublic sealed class SqlServerContext : DbContext { }\n"),
        ("Migrations/Postgres/20260101000000_Initial.Designer.cs", Designer("PostgresContext", "20260101000000_Initial")),
        ("Migrations/Postgres/20260101000000_Initial.cs", "public partial class Initial : Migration { }\n"),
        ("Migrations/Postgres/PostgresContextModelSnapshot.cs", Snapshot("PostgresContext")),
        ("Migrations/SqlServer/20260101000001_Initial.Designer.cs", Designer("SqlServerContext", "20260101000001_Initial")),
        ("Migrations/SqlServer/SqlServerContextModelSnapshot.cs", Snapshot("SqlServerContext")),
        ("Old/20250101000000_First.Designer.cs", Designer("PostgresContext", "20250101000000_First")),
        ("CompiledModels/PostgresContextModel.cs", "[DbContext(typeof(PostgresContext))]\npublic partial class PostgresContextModel : RuntimeModel { }\n"));

    /// <summary>A context's migrations are found by their attributes and their folders offered within the
    /// project.</summary>
    [Fact]
    public void A_contexts_migrations_folders_are_found_and_offered() => Cases.All(
        (nameof(Each_contexts_migrations_are_found_by_their_attributes_in_whatever_folder_they_are_kept),
            Each_contexts_migrations_are_found_by_their_attributes_in_whatever_folder_they_are_kept),
        (nameof(A_folder_typed_or_picked_is_taken_from_the_projects_and_one_outside_it_refused),
            A_folder_typed_or_picked_is_taken_from_the_projects_and_one_outside_it_refused));

    private void Each_contexts_migrations_are_found_by_their_attributes_in_whatever_folder_they_are_kept()
    {
        Assert.True(TwoSets.HoldsMigrations, "Two sets of migrations in folders of their own were not seen as migrations at all");
        string[] postgres = [.. TwoSets.MigrationsOf("MyApp.Data.PostgresContext").Select(m => $"{m.Id ?? "snapshot"} {Path.GetRelativePath(Data.Folder, m.File).Replace('\\', '/')}")];
        Assert.True(postgres.Order(StringComparer.Ordinal).SequenceEqual(["20250101000000_First Old/20250101000000_First.Designer.cs",
            "20260101000000_Initial Migrations/Postgres/20260101000000_Initial.Designer.cs", "snapshot Migrations/Postgres/PostgresContextModelSnapshot.cs"]),
            $"PostgresContext's migrations were read as [{string.Join(", ", postgres)}]: a compiled model, another context's or the migration's own half would be taken for one");
    }

    private void A_folder_typed_or_picked_is_taken_from_the_projects_and_one_outside_it_refused()
    {
        string folder = Data.Folder;
        Assert.Equal(((string?)null, (EfFolderUnfit?)null), EfOutputFolders.Read("  ", Data));
        Assert.Equal(("Migrations/Postgres", (EfFolderUnfit?)null), EfOutputFolders.Read("Migrations/Postgres/", Data));
        Assert.Equal(("Migrations/Postgres", (EfFolderUnfit?)null), EfOutputFolders.Read("./Migrations/../Migrations/Postgres", Data));
        Assert.Equal(("Data/Migrations", (EfFolderUnfit?)null), EfOutputFolders.Read(Path.Combine(folder, "Data", "Migrations"), Data));
        Assert.Equal((".", (EfFolderUnfit?)null), EfOutputFolders.Read(folder, Data));
        foreach (string outside in (string[])["../Shared/Migrations", Path.Combine(Root, "Elsewhere")])
        {
            (string? taken, EfFolderUnfit? unfit) = EfOutputFolders.Read(outside, Data);
            Assert.True(taken is null && unfit == new EfFolderUnfit(outside, null),
                $"{outside} was taken as {taken ?? "nothing"} ({unfit?.ToString() ?? "no refusal"}): dotnet ef would write the migrations outside the project");
        }
    }

}
