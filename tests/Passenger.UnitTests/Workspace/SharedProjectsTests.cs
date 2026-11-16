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
    public void A_wildcard_takes_a_file_named_in_another_case_only_where_its_folder_ignores_case()
    {
        const string Text = """
            <Project>
              <ItemGroup>
                <Compile Include="$(MSBuildThisFileDirectory)Views\*.CS;$(MSBuildThisFileDirectory)parts\*.cs" />
              </ItemGroup>
            </Project>
            """;
        Directory.CreateDirectory(Shared);
        string[] files = [Path.Combine(Shared, "Parts", "Bar.cs"), Path.Combine(Shared, "Views", "Main.cs")];

        IReadOnlyList<EvaluatedItem> read = SharedProjects.ItemsIn(Text, ItemsFile, folder => files.Where(f => PathNames.IsUnder(f, folder)));

        string[] expected = PathNames.CaseIgnoredIn(Shared) ? [files[1], files[0]] : [];
        Assert.Equal(expected, read.Select(i => i.Path));
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

    private const string Globbed = """
        <Project>
          <ItemGroup>
            <Compile Include="$(MSBuildThisFileDirectory)Models\**\*.cs" Exclude="$(MSBuildThisFileDirectory)Models\Drafts\**" />
            <Compile Include="$(MSBuildThisFileDirectory)Greeting.cs;$(MSBuildThisFileDirectory)Extra.cs" />
            <None Include="$(MSBuildThisFileDirectory)readme.md" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>The file at a path under the shared folder, with its separators.</summary>
    private string At(string relative) => Path.Combine([Shared, .. relative.Split('/')]);

    /// <summary>Whether the items file's text takes a file, as the build would read it there.</summary>
    private bool Takes(string text, string relative) =>
        SharedProjects.ItemsIn(text, ItemsFile, folder => PathNames.IsUnder(At(relative), folder) ? [At(relative)] : []).Any(i => i.Path == At(relative));

    [Fact]
    public void A_file_made_where_a_wildcard_of_its_type_takes_it_gets_no_item_of_its_own_and_one_it_leaves_out_does()
    {
        Assert.Equal(Globbed, SharedProjects.WithAdded(Globbed, ItemsFile, [At("Models/Customer.cs"), At("Models/Parts/Line.cs")]));

        string added = SharedProjects.WithAdded(Globbed, ItemsFile, [At("Models/Drafts/Sketch.cs"), At("Views/Main.cs")]);

        Assert.Equal(Globbed.Replace("Extra.cs\" />", "Extra.cs\" />\n    <Compile Include=\"$(MSBuildThisFileDirectory)Models\\Drafts\\Sketch.cs\" />\n"
            + "    <Compile Include=\"$(MSBuildThisFileDirectory)Views\\Main.cs\" />", StringComparison.Ordinal), added);
    }

    [Fact]
    public void A_file_a_wildcard_takes_is_excluded_by_a_remove_after_it_holding_in_every_build_and_included_again_by_that_remove_going()
    {
        string excluded = SharedProjects.WithExcluded(Globbed, ItemsFile, [At("Models/Order.cs")]);

        Assert.Equal(Globbed.Replace("Drafts\\**\" />", "Drafts\\**\" />\n    <Compile Remove=\"$(MSBuildThisFileDirectory)Models\\Order.cs\" />", StringComparison.Ordinal), excluded);
        Assert.False(Takes(excluded, "Models/Order.cs"), "Order.cs is still compiled once excluded");
        Assert.True(Takes(excluded, "Models/Customer.cs"), "Excluding Order.cs took Customer.cs out of the wildcard too");
        Assert.Equal(Globbed, SharedProjects.WithAdded(excluded, ItemsFile, [At("Models/Order.cs")]));

        const string Conditioned = "<Project>\n  <ItemGroup Condition=\"'$(OS)' == 'Unix'\">\n    <Compile Include=\"$(MSBuildThisFileDirectory)**\\*.cs\" />\n  </ItemGroup>\n</Project>\n";
        Assert.Equal(Conditioned.Replace("</Project>", "  <ItemGroup>\n    <Compile Remove=\"$(MSBuildThisFileDirectory)Greeting.cs\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal),
            SharedProjects.WithExcluded(Conditioned, ItemsFile, [At("Greeting.cs")]));
    }

    [Fact]
    public void A_file_one_item_names_beside_another_leaves_that_item_naming_the_other_whether_deleted_or_excluded()
    {
        string kept = Globbed.Replace("Greeting.cs;$(MSBuildThisFileDirectory)Extra.cs", "Greeting.cs", StringComparison.Ordinal);

        Assert.Equal(kept, SharedProjects.WithRemoved(Globbed, ItemsFile, [At("Extra.cs")]));
        Assert.Equal(kept, SharedProjects.WithExcluded(Globbed, ItemsFile, [At("Extra.cs")]));
        Assert.Equal(Globbed.Replace("$(MSBuildThisFileDirectory)Greeting.cs;", string.Empty, StringComparison.Ordinal),
            SharedProjects.WithRemoved(Globbed, ItemsFile, [At("Greeting.cs")]));
        Assert.DoesNotContain("Greeting.cs", SharedProjects.WithRemoved(Globbed, ItemsFile, [At("Greeting.cs"), At("Extra.cs")]), StringComparison.Ordinal);

        const string Referenced = "<Project>\n  <ItemGroup>\n    <Compile Include=\"A.cs&#59; B&amp;C.cs ;D.cs\" />\n  </ItemGroup>\n</Project>\n";
        Assert.Equal(Referenced.Replace("A.cs&#59; ", string.Empty, StringComparison.Ordinal), SharedProjects.WithRemoved(Referenced, ItemsFile, [At("A.cs")]));
        Assert.Equal(Referenced.Replace("&#59; B&amp;C.cs ", string.Empty, StringComparison.Ordinal), SharedProjects.WithRemoved(Referenced, ItemsFile, [At("B&C.cs")]));
    }

    [Fact]
    public void A_file_one_item_names_beside_another_is_named_where_it_moved_within_the_folder_and_taken_out_where_it_left_it()
    {
        string moved = SharedProjects.WithListsMoved(Globbed, ItemsFile, [(At("Extra.cs"), At("Parts/Extra.cs")), (At("Greeting.cs"), Path.Combine(root, "Api", "Greeting.cs"))]);

        Assert.Equal(Globbed.Replace("$(MSBuildThisFileDirectory)Greeting.cs;$(MSBuildThisFileDirectory)Extra.cs", "$(MSBuildThisFileDirectory)Parts\\Extra.cs", StringComparison.Ordinal), moved);
        string swapped = SharedProjects.WithListsMoved(Globbed, ItemsFile, [(At("Greeting.cs"), At("Swap.cs")), (At("Extra.cs"), At("Greeting.cs")), (At("Swap.cs"), At("Extra.cs"))]);
        Assert.Equal(Globbed.Replace("Greeting.cs;$(MSBuildThisFileDirectory)Extra.cs", "Extra.cs;$(MSBuildThisFileDirectory)Greeting.cs", StringComparison.Ordinal), swapped);
        Assert.Equal(Globbed, SharedProjects.WithListsMoved(Globbed, ItemsFile, [(At("Models/Order.cs"), At("Models/Sale.cs"))]));
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
    public void A_projects_import_of_shared_items_is_written_as_visual_studio_writes_it_after_any_other_and_taken_out_with_a_group_it_empties()
    {
        const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n  </PropertyGroup>\n</Project>\n";
        string api = Path.Combine(root, "Shop.Api", "Shop.Api.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(api)!);
        File.WriteAllText(api, Project);
        string other = Path.Combine(root, "Other", "Other.projitems");

        Assert.Null(ProjectEdits.AddSharedImport(api, ItemsFile));
        string imported = Project.Replace("</Project>", "  <Import Project=\"..\\Shop.Shared\\Shop.Shared.projitems\" Label=\"Shared\" />\n</Project>", StringComparison.Ordinal);
        Assert.Equal(imported, File.ReadAllText(api));
        Assert.Equal("Shop.Api imports Shop.Shared.projitems already", ProjectEdits.AddSharedImport(api, ItemsFile));
        Assert.Null(ProjectEdits.AddSharedImport(api, other));
        Assert.Equal(imported.Replace("Label=\"Shared\" />\n", "Label=\"Shared\" />\n  <Import Project=\"..\\Other\\Other.projitems\" Label=\"Shared\" />\n", StringComparison.Ordinal),
            File.ReadAllText(api));

        Assert.Null(ProjectEdits.RemoveSharedImport(api, other));
        Assert.Equal(imported, File.ReadAllText(api));
        Assert.Null(ProjectEdits.RemoveSharedImport(api, ItemsFile));
        Assert.Equal(Project, File.ReadAllText(api));
        Assert.Equal("Shop.Api does not import Shop.Shared.projitems", ProjectEdits.RemoveSharedImport(api, ItemsFile));

        const string Grouped = "<Project>\n  <ImportGroup Label=\"Shared\">\n    <Import Project=\"$(MSBuildThisFileDirectory)..\\Shop.Shared\\Shop.Shared.projitems\" Label=\"Shared\" />\n  </ImportGroup>\n</Project>\n";
        File.WriteAllText(api, Grouped);
        Assert.Null(ProjectEdits.RemoveSharedImport(api, ItemsFile));
        Assert.Equal("<Project>\n</Project>\n", File.ReadAllText(api));
    }

    [Fact]
    public void A_shared_project_added_to_a_classic_solution_takes_its_own_id_and_type_and_no_configuration()
    {
        Directory.CreateDirectory(Shared);
        string shproj = Path.Combine(Shared, "Shop.Shared.shproj");
        File.WriteAllText(shproj, "<Project>\n  <PropertyGroup Label=\"Globals\">\n    <ProjectGuid>{6a8c1f2e-3b4d-4e5f-8a9b-0c1d2e3f4a5b}</ProjectGuid>\n  </PropertyGroup>\n</Project>\n");
        string solution = Path.Combine(root, "Shop.sln");
        File.WriteAllText(solution, "\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\nGlobal\r\n\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n"
            + "\t\tDebug|Any CPU = Debug|Any CPU\r\n\tEndGlobalSection\r\nEndGlobal\r\n");

        Assert.Null(SolutionEdits.AddProject(solution, null, shproj));

        string written = File.ReadAllText(solution);
        Assert.Contains("Project(\"{D954291E-2A0B-460D-934E-DC6B0785DB48}\") = \"Shop.Shared\", \"Shop.Shared\\Shop.Shared.shproj\", \"{6A8C1F2E-3B4D-4E5F-8A9B-0C1D2E3F4A5B}\"",
            written, StringComparison.Ordinal);
        Assert.DoesNotContain("ActiveCfg", written, StringComparison.Ordinal);
        Assert.Equal([shproj], SolutionModel.Load(solution, out _)!.Projects.Select(p => p.Path));
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
