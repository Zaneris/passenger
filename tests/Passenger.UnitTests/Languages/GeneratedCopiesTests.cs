using Passenger.App.CSharp;
using Passenger.Core;

namespace Passenger.UnitTests.Languages;

/// <summary>Where a copy of a source generator's code goes: named by its hint name, in a folder of its project's, the same
/// place each time it is copied, one of its own for each target framework and generator, never outside the copies' folder
/// whatever the hint name holds; and the project a copy is of read back from its place.</summary>
public sealed class GeneratedCopiesTests
{
    private const string Generator = "System.Text.RegularExpressions.Generator.RegexGenerator";

    private static readonly string Folder = Path.Combine(Path.GetTempPath(), "state", "cache", "generated");

    private static readonly string Project = Path.Combine(Path.GetTempPath(), "src", "My-App", "My-App.csproj");

    [Fact]
    public void A_copy_is_named_by_its_hint_name_under_its_projects_folder_and_goes_to_the_same_place_each_time()
    {
        string copy = GeneratedCopies.PlaceOf(Folder, Project, "My-App", Generator, "RegexGenerator.g.cs");
        Assert.Equal("RegexGenerator.g.cs", Path.GetFileName(copy));
        Assert.Equal(Folder, Path.GetDirectoryName(Path.GetDirectoryName(copy)));
        Assert.StartsWith("My-App-", Path.GetFileName(Path.GetDirectoryName(copy)), StringComparison.Ordinal);
        Assert.Equal(copy, GeneratedCopies.PlaceOf(Folder, Project, "My-App", Generator, "RegexGenerator.g.cs"));
        Assert.Equal("My-App", GeneratedCopies.ProjectNameOf(Folder, copy));
    }

    [Fact]
    public void Each_target_framework_and_generator_of_a_project_has_copies_of_its_own()
    {
        string[] copies =
        [
            GeneratedCopies.PlaceOf(Folder, Project, "My-App (net8.0)", Generator, "RegexGenerator.g.cs"),
            GeneratedCopies.PlaceOf(Folder, Project, "My-App (net10.0)", Generator, "RegexGenerator.g.cs"),
            GeneratedCopies.PlaceOf(Folder, Project, "My-App (net8.0)", "Other.Generator", "RegexGenerator.g.cs"),
        ];
        Assert.Equal(copies.Length, copies.Distinct(StringComparer.Ordinal).Count());
        Assert.All(copies, c => Assert.Equal("My-App", GeneratedCopies.ProjectNameOf(Folder, c)));
    }

    [Theory]
    [InlineData("Views/Main.g.cs", new[] { "Views", "Main.g.cs" })]
    [InlineData("Views\\Main.g.cs", new[] { "Views", "Main.g.cs" })]
    [InlineData("../../escape.g.cs", new[] { "_", "_", "escape.g.cs" })]
    [InlineData("A:B*C?.g.cs", new[] { "A_B_C_.g.cs" })]
    [InlineData("", new[] { "generated.cs" })]
    public void A_hint_names_folders_stay_under_the_copies_folder_each_a_name_every_file_system_takes(string hint, string[] names)
    {
        string copy = GeneratedCopies.PlaceOf(Folder, Project, "My-App", Generator, hint);
        string project = Path.GetDirectoryName(GeneratedCopies.PlaceOf(Folder, Project, "My-App", Generator, "x.cs"))!;
        Assert.True(PathNames.IsUnder(copy, Folder), $"The copy of a document hinted '{hint}' goes to {copy}, outside {Folder}");
        Assert.Equal(names, Path.GetRelativePath(project, copy).Split(Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_project_with_no_file_names_its_copies_folder_by_its_name_and_a_file_elsewhere_is_no_copy()
    {
        Assert.Equal("Scripts", GeneratedCopies.ProjectNameOf(Folder, GeneratedCopies.PlaceOf(Folder, string.Empty, "Scripts", Generator, "A.g.cs")));
        Assert.Null(GeneratedCopies.ProjectNameOf(Folder, Path.Combine(Path.GetTempPath(), "src", "My-App", "Program.cs")));
    }
}
