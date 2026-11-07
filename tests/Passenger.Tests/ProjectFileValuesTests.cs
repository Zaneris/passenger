using Passenger.App.MSBuild;
using Passenger.Xaml.Syntax;

namespace Passenger.Tests;

/// <summary>The values completion offers between a property's tags in a project file, from MSBuild's schemas as the app
/// carries them, read from the file's text alone with no evaluation.</summary>
[Collection(Beside.Name)]
public sealed class ProjectFileValuesTests
{
    [Fact]
    public void Analysis_level_offers_the_levels_of_the_sdks_passenger_supports()
    {
        const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <AnalysisLevel></AnalysisLevel>\n  </PropertyGroup>\n</Project>\n";
        MSBuildContext context = new("/repo/App/App.csproj", XmlTree.Parse(Project), MSBuildSchema.Instance, null, []);
        MSBuildCompletionList? list = MSBuildCompletion.At(context, Project.IndexOf("</AnalysisLevel>", StringComparison.Ordinal));
        string[] offered = [.. list?.Items.Select(i => i.Label) ?? []];
        string[] missing = [.. new[] { "8.0", "9.0-recommended", "10.0-recommended", "10.0-all", "11.0" }.Except(offered)];
        Assert.True(missing.Length == 0, $"AnalysisLevel's completion leaves out {string.Join(", ", missing)}, which today's SDKs take and must be typed: it offers [{string.Join(", ", offered)}]");
    }

    [Theory]
    [InlineData("LangVersion", "15.0")]
    [InlineData("TargetFramework", "net11.0")]
    public void A_project_with_no_evaluation_yet_is_offered_the_newest_dotnets_values(string property, string newest)
    {
        string project = $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <{property}></{property}>\n  </PropertyGroup>\n</Project>\n";
        MSBuildContext context = new("/repo/App/App.csproj", XmlTree.Parse(project), MSBuildSchema.Instance, null, []);
        MSBuildCompletionList? list = MSBuildCompletion.At(context, project.IndexOf($"</{property}>", StringComparison.Ordinal));
        string[] offered = [.. list?.Items.Select(i => i.Label) ?? []];
        Assert.True(offered.Contains(newest), $"{property}'s completion leaves out {newest}, which the newest .NET takes and must be typed: it offers [{string.Join(", ", offered)}]");
    }
}
