using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.UnitTests.Workspace;

/// <summary>A solution filter's list changed as its solution's projects are: a project moved or renamed named where it went,
/// the filter or its solution moved still naming each, and a project removed taken out, each path in the separator its
/// list writes and the rest of the file as it was.</summary>
public sealed class SolutionFilterEditsTests : IDisposable
{
    private const string Filter = "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [\n      \"App\\\\App.csproj\",\n"
        + "      \"src/Lib/Lib.csproj\",\n      \"Tools\\\\Tools.csproj\"\n    ]\n  }\n}\n";

    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_project_moved_or_renamed_is_named_where_it_went_in_the_separator_it_was_written_with()
    {
        string filter = Write("Shop.slnf", Filter);
        Assert.True(SolutionEdits.MoveInFilter(filter, [(At("App"), At("src", "App")), (At("src", "Lib", "Lib.csproj"), At("src", "Lib", "Core.csproj"))]),
            "The filter did not follow its projects moved, so opened again it leaves them out");
        Assert.Equal(Filter.Replace("\"App\\\\App.csproj\"", "\"src\\\\App\\\\App.csproj\"", StringComparison.Ordinal)
            .Replace("src/Lib/Lib.csproj", "src/Lib/Core.csproj", StringComparison.Ordinal), File.ReadAllText(filter));

        string followed = File.ReadAllText(filter);
        Assert.False(SolutionEdits.MoveInFilter(filter, [(At("docs"), At("notes"))]), "A move of nothing the filter names wrote it");
        Assert.Equal(followed, File.ReadAllText(filter));
    }

    [Fact]
    public void The_filter_moved_names_its_solution_from_where_it_went_and_the_solution_moved_names_each_project_from_its_new_folder()
    {
        string filter = Write(Path.Combine("filters", "Shop.slnf"), Filter);
        Assert.True(SolutionEdits.MoveInFilter(filter, [(At("Shop.slnf"), filter)]), "The filter moved still names its solution from where it was");
        string moved = Filter.Replace("\"path\": \"Shop.slnx\"", "\"path\": \"..\\\\Shop.slnx\"", StringComparison.Ordinal);
        Assert.Equal(moved, File.ReadAllText(filter));

        Assert.True(SolutionEdits.MoveInFilter(filter, [(At("Shop.slnx"), At("src", "Store.slnx"))]), "The filter still names its solution where it was");
        Assert.Equal(moved.Replace("..\\\\Shop.slnx", "..\\\\src\\\\Store.slnx", StringComparison.Ordinal)
            .Replace("\"App\\\\App.csproj\"", "\"..\\\\App\\\\App.csproj\"", StringComparison.Ordinal)
            .Replace("src/Lib/Lib.csproj", "Lib/Lib.csproj", StringComparison.Ordinal)
            .Replace("\"Tools\\\\Tools.csproj\"", "\"..\\\\Tools\\\\Tools.csproj\"", StringComparison.Ordinal), File.ReadAllText(filter));
        Assert.True(SolutionFilter.Read(filter) is { } read && read.Projects.SetEquals([At("App", "App.csproj"), At("src", "Lib", "Lib.csproj"), At("Tools", "Tools.csproj")]),
            "The filter no longer reads as keeping the projects it kept");
    }

    [Fact]
    public void A_project_removed_leaves_the_list_with_its_line_and_one_the_list_lacks_changes_nothing()
    {
        string filter = Write("Shop.slnf", Filter);
        Assert.Null(SolutionEdits.RemoveFromFilter(filter, [At("Tools", "Tools.csproj"), At("Docs", "Docs.csproj")]));
        Assert.Equal(Filter.Replace(",\n      \"Tools\\\\Tools.csproj\"", string.Empty, StringComparison.Ordinal), File.ReadAllText(filter));
        DateTime written = File.GetLastWriteTimeUtc(filter);
        Assert.Null(SolutionEdits.RemoveFromFilter(filter, [At("Docs", "Docs.csproj")]));
        Assert.Equal(written, File.GetLastWriteTimeUtc(filter));
    }

    private string At(params string[] parts) => Path.Combine([root, .. parts]);

    private string Write(string relative, string text)
    {
        string path = At(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }
}
