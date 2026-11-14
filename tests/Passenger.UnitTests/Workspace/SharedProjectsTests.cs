using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A shared project's items file read and written as text: the files it names from its own folder, a wildcard's
/// and what an Exclude or a later Remove takes out, a file made written in beside the items of its type, one deleted or a
/// folder's taken out, and its items following files moved within, out of and into its folder.</summary>
public sealed class SharedProjectsTests : IDisposable
{
    private const string Items = """
        <?xml version="1.0" encoding="utf-8"?>
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <HasSharedItems>true</HasSharedItems>
          </PropertyGroup>
          <PropertyGroup Label="Configuration">
            <Import_RootNamespace>Shop.Shared</Import_RootNamespace>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="$(MSBuildThisFileDirectory)Greeting.cs" />
            <Compile Include="$(MSBuildThisFileDirectory)Models\Order.cs" />
            <None Include="$(MSBuildThisFileDirectory)readme.md" />
          </ItemGroup>
        </Project>

        """;

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Shared => Path.Combine(root, "Shop.Shared");

    private string ItemsFile => Path.Combine(Shared, "Shop.Shared.projitems");

    [Fact]
    public void Its_files_are_those_its_items_name_from_its_folder_a_wildcards_found_there_less_what_is_excluded_or_removed()
    {
        const string Text = """
            <Project>
              <ItemGroup>
                <Compile Include="$(MSBuildThisFileDirectory)Greeting.cs" />
                <Compile Include="$(MSBuildThisFileDirectory)Views\**\*.cs" Exclude="$(MSBuildThisFileDirectory)Views\Old\**" />
                <Compile Remove="$(MSBuildThisFileDirectory)Views\Gone.cs" />
                <None Include="$(MSBuildThisFileDirectory)hidden.txt" Visible="false" />
                <None Include="$(MSBuildThisFileDirectory)logo.png">
                  <Link>Assets\logo.png</Link>
                </None>
                <Compile Include="$(Generated)Made.cs" />
                <ProjectCapability Include="SharedAssetsProject" />
              </ItemGroup>
            </Project>
            """;
        string[] views = [Path.Combine(Shared, "Views", "Main.cs"), Path.Combine(Shared, "Views", "Parts", "Bar.cs"), Path.Combine(Shared, "Views", "Gone.cs"),
            Path.Combine(Shared, "Views", "Old", "Legacy.cs"), Path.Combine(Shared, "Views", "Main.xaml")];

        IReadOnlyList<EvaluatedItem> read = SharedProjects.ItemsIn(Text, ItemsFile, folder => views.Where(v => PathNames.IsUnder(v, folder)));

        (string Type, string Place, string? Link)[] expected =
            [("Compile", "Greeting.cs", null), ("Compile", "Views/Main.cs", null), ("Compile", "Views/Parts/Bar.cs", null), ("None", "logo.png", "Assets\\logo.png")];
        Assert.Equal(expected, read.Select(i => (i.Type, Path.GetRelativePath(Shared, i.Path).Replace('\\', '/'), i.Link)));
    }

    [Fact]
    public void A_file_made_goes_after_the_last_item_of_its_type_from_the_items_folder_with_its_separators_and_one_named_already_stays()
    {
        string added = SharedProjects.WithAdded(Items, ItemsFile,
            [Path.Combine(Shared, "Models", "Customer.cs"), Path.Combine(Shared, "Greeting.cs"), Path.Combine(Shared, "Pages", "Index.razor"), Path.Combine(root, "Elsewhere.cs")]);

        Assert.Equal(Items
            .Replace("""Models\Order.cs" />""", """Models\Order.cs" />""" + "\n    <Compile Include=\"$(MSBuildThisFileDirectory)Models\\Customer.cs\" />", StringComparison.Ordinal)
            .Replace("""readme.md" />""", """readme.md" />""" + "\n    <Content Include=\"$(MSBuildThisFileDirectory)Pages\\Index.razor\" />", StringComparison.Ordinal), added);
    }

    [Fact]
    public void A_file_made_in_an_items_file_naming_none_goes_in_an_item_group_of_its_own()
    {
        const string Empty = "<Project>\n  <PropertyGroup>\n    <HasSharedItems>true</HasSharedItems>\n  </PropertyGroup>\n</Project>\n";

        string added = SharedProjects.WithAdded(Empty, ItemsFile, [Path.Combine(Shared, "First.cs")]);

        Assert.Equal(Empty.Replace("</Project>", "  <ItemGroup>\n    <Compile Include=\"$(MSBuildThisFileDirectory)First.cs\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal), added);
        Assert.Equal([Path.Combine(Shared, "First.cs")], SharedProjects.ItemsIn(added, ItemsFile, _ => []).Select(i => i.Path));
    }

    [Fact]
    public void A_file_deleted_leaves_its_item_and_a_folder_deleted_every_item_under_it_an_item_group_left_empty_going_too()
    {
        string removed = SharedProjects.WithRemoved(Items, ItemsFile, [Path.Combine(Shared, "Greeting.cs"), Path.Combine(Shared, "Models")]);

        Assert.Equal(Items.Replace("    <Compile Include=\"$(MSBuildThisFileDirectory)Greeting.cs\" />\n", string.Empty, StringComparison.Ordinal)
            .Replace("    <Compile Include=\"$(MSBuildThisFileDirectory)Models\\Order.cs\" />\n", string.Empty, StringComparison.Ordinal), removed);

        string emptied = SharedProjects.WithRemoved(removed, ItemsFile, [Path.Combine(Shared, "readme.md")]);
        Assert.DoesNotContain("<ItemGroup>", emptied, StringComparison.Ordinal);
        Assert.Contains("<Import_RootNamespace>Shop.Shared</Import_RootNamespace>", emptied, StringComparison.Ordinal);
    }

    [Fact]
    public void Its_items_follow_a_file_renamed_within_its_folder_one_moved_out_and_one_moved_in()
    {
        Directory.CreateDirectory(Path.Combine(Shared, "Models"));
        File.WriteAllText(ItemsFile, Items);
        string[] moved = [Path.Combine(Shared, "Welcome.cs"), Path.Combine(root, "Api", "Order.cs"), Path.Combine(Shared, "Models", "Customer.cs")];
        (string, string)[] moves = [(Path.Combine(Shared, "Greeting.cs"), moved[0]), (Path.Combine(Shared, "Models", "Order.cs"), moved[1]),
            (Path.Combine(root, "Api", "Customer.cs"), moved[2])];

        Assert.True(SharedProjects.Follow(ItemsFile, moves, path => [path]), "The items file did not follow the files moved");

        Assert.Equal([Path.Combine(Shared, "Models", "Customer.cs"), Path.Combine(Shared, "Welcome.cs"), Path.Combine(Shared, "readme.md")],
            SharedProjects.ItemsIn(File.ReadAllText(ItemsFile), ItemsFile, _ => []).Select(i => i.Path).Order(StringComparer.Ordinal));
        Assert.Contains("<Compile Include=\"$(MSBuildThisFileDirectory)Welcome.cs\" />", File.ReadAllText(ItemsFile), StringComparison.Ordinal);
    }

    [Fact]
    public void A_shared_project_names_its_items_file_by_its_shared_import_else_by_its_own_name()
    {
        Directory.CreateDirectory(Shared);
        string shproj = Path.Combine(Shared, "Shop.Shared.shproj");
        File.WriteAllText(shproj, """
            <Project>
              <Import Project="$(MSBuildExtensionsPath32)\Microsoft\VisualStudio\v$(VisualStudioVersion)\CodeSharing\Microsoft.CodeSharing.Common.props" />
              <Import Project="Other.projitems" />
              <Import Project="Items\Common.projitems" Label="Shared" />
            </Project>
            """);
        Assert.Equal(Path.Combine(Shared, "Items", "Common.projitems"), SharedProjects.ItemsFileOf(shproj));

        File.WriteAllText(shproj, "<Project />");
        Assert.Equal(Path.Combine(Shared, "Shop.Shared.projitems"), SharedProjects.ItemsFileOf(shproj));
    }
}
