using Passenger.App.CSharp;
using Passenger.Core;

namespace Passenger.UnitTests.Languages;

/// <summary>Where a copy of a source generator's code goes: named by its hint name, in a folder of its project's, the same
/// place each time it is copied, never outside the copies' folder whatever the hint name holds; and the project a copy is
/// of read back from its place.</summary>
public sealed class GeneratedCopiesTests
{
    private const string Generator = "System.Text.RegularExpressions.Generator.RegexGenerator";

    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "state", "cache", "generated");

    private static readonly string Project = Path.Combine(Path.GetTempPath(), "src", "My-App", "My-App.csproj");

    [Fact]
    public void A_generated_copy_goes_to_one_place_under_its_project_never_outside_the_copies_folder() => Cases.All(
        ("a copy is named by its hint name under its projects folder and goes to the same place each time", A_copy_is_named_by_its_hint_name_under_its_projects_folder_and_goes_to_the_same_place_each_time),
        ("a hint names folders stay under the copies folder each a name every file system takes", () => Cases.Each<(string, string[])>(
        [
            ("../../escape.g.cs", new[] { "_", "_", "escape.g.cs" }),
            ("A:B*C?.g.cs", new[] { "A_B_C_.g.cs" }),
        ], row => A_hint_names_folders_stay_under_the_copies_folder_each_a_name_every_file_system_takes(row.Item1, row.Item2))));

    private void A_copy_is_named_by_its_hint_name_under_its_projects_folder_and_goes_to_the_same_place_each_time()
    {
        string copy = GeneratedCopies.PlaceOf(Folder, Project, "My-App", Generator, "RegexGenerator.g.cs");
        Assert.Equal("RegexGenerator.g.cs", Path.GetFileName(copy));
        Assert.Equal(Folder, Path.GetDirectoryName(Path.GetDirectoryName(copy)));
        Assert.StartsWith("My-App-", Path.GetFileName(Path.GetDirectoryName(copy)), StringComparison.Ordinal);
        Assert.Equal(copy, GeneratedCopies.PlaceOf(Folder, Project, "My-App", Generator, "RegexGenerator.g.cs"));
        Assert.Equal("My-App", GeneratedCopies.ProjectNameOf(Folder, copy));
    }

    private void A_hint_names_folders_stay_under_the_copies_folder_each_a_name_every_file_system_takes(string hint, string[] names)
    {
        string copy = GeneratedCopies.PlaceOf(Folder, Project, "My-App", Generator, hint);
        string project = Path.GetDirectoryName(GeneratedCopies.PlaceOf(Folder, Project, "My-App", Generator, "x.cs"))!;
        Assert.True(PathNames.IsUnder(copy, Folder), $"The copy of a document hinted '{hint}' goes to {copy}, outside {Folder}");
        Assert.Equal(names, Path.GetRelativePath(project, copy).Split(Path.DirectorySeparatorChar));
    }
}
