using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Roslyn;

/// <summary>The documents a path names in the server's solution, which finds a path's documents ignoring letter case: kept
/// to the file the path names, so where a folder tells case apart a twin differing only in case, another project's file, is
/// never taken for it by Hot Reload's start, a localization or Razor read, or the live MAUI check.</summary>
public sealed class SyntaxReadsTests : IDisposable
{
    private readonly string root = PathNames.Canonical(Directory.CreateTempSubdirectory("passenger-unit-").FullName);

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Fact]
    public void A_paths_documents_take_a_twin_differing_only_in_letter_case_where_its_folder_ignores_case_and_never_where_it_tells_case_apart()
    {
        string shared = Path.Join(root, "Shared");
        Directory.CreateDirectory(shared);
        (string upper, string lower) = (Path.Join(shared, "Util.cs"), Path.Join(shared, "util.cs"));
        (string upperPage, string lowerPage) = (Path.Join(shared, "Page.xaml"), Path.Join(shared, "page.xaml"));
        using AdhocWorkspace workspace = new();
        (ProjectId app, ProjectId tools) = (ProjectId.CreateNewId(), ProjectId.CreateNewId());
        (DocumentId appUtil, DocumentId toolsUtil) = (DocumentId.CreateNewId(app), DocumentId.CreateNewId(tools));
        (DocumentId appPage, DocumentId toolsPage) = (DocumentId.CreateNewId(app), DocumentId.CreateNewId(tools));
        Solution solution = workspace.CurrentSolution
            .AddProject(app, "App", "App", LanguageNames.CSharp)
            .AddProject(tools, "Tools", "Tools", LanguageNames.CSharp)
            .AddDocument(appUtil, "Util.cs", SourceText.From("class Util { }"), filePath: upper)
            .AddDocument(toolsUtil, "util.cs", SourceText.From("class util { }"), filePath: lower)
            .AddAdditionalDocument(appPage, "Page.xaml", SourceText.From("<ContentPage />"), filePath: upperPage)
            .AddAdditionalDocument(toolsPage, "page.xaml", SourceText.From("<ContentView />"), filePath: lowerPage);
        bool caseIgnored = PathNames.CaseIgnoredIn(shared);
        Assert.True(solution.GetDocumentIdsWithFilePath(lower).Length == 2, "The solution's own search no longer finds a twin in another letter case");

        Holds(solution, lower, caseIgnored ? [appUtil, toolsUtil] : [toolsUtil], caseIgnored);
        Holds(solution, upper, caseIgnored ? [appUtil, toolsUtil] : [appUtil], caseIgnored);
        Holds(solution, lowerPage, caseIgnored ? [appPage, toolsPage] : [toolsPage], caseIgnored);
        Assert.Empty(SyntaxReads.DocumentIdsOf(solution, Path.Join(shared, "Other.cs")));
    }

    private static void Holds(Solution solution, string path, DocumentId[] expected, bool caseIgnored)
    {
        string[] found = [.. SyntaxReads.DocumentIdsOf(solution, path).Select(id => Named(solution, id)).Order(StringComparer.Ordinal)];
        string[] named = [.. expected.Select(id => Named(solution, id)).Order(StringComparer.Ordinal)];
        Assert.True(found.SequenceEqual(named), $"{Path.GetFileName(path)}, in a folder that {(caseIgnored ? "ignores" : "tells apart")} letter case, "
            + $"names {string.Join(", ", found)} where it is {string.Join(", ", named)}: "
            + (caseIgnored ? "a file's own document under another spelling is passed over" : "a read of it takes its twin's text, and Hot Reload's start writes its text over the twin's"));
    }

    /// <summary>A document as its project and file name.</summary>
    private static string Named(Solution solution, DocumentId id) =>
        $"{solution.GetProject(id.ProjectId)!.Name}/{SyntaxReads.TextDocumentOf(solution, id)!.Name}";
}
