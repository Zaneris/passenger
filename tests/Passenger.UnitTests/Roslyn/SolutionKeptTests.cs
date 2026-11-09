using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Passenger.Roslyn.Extension;

namespace Passenger.UnitTests.Roslyn;

/// <summary>What the extension inside Roslyn's server keeps by a solution's documents and projects (Godot's strings, a
/// project's localization path, Aspire's connection reads), over two workspaces as the server has (the host's and
/// Miscellaneous Files'): a scan of one never lets go of the other's, and a document or project gone from a workspace takes
/// its entry with it.</summary>
public sealed class SolutionKeptTests
{
    private static (AdhocWorkspace Workspace, Project Project, Document First, Document Second) Made(string name)
    {
        AdhocWorkspace workspace = new();
        Project project = workspace.AddProject(name, LanguageNames.CSharp);
        Document first = workspace.AddDocument(project.Id, "First.cs", SourceText.From("class First { string a = \"a\"; }"));
        Document second = workspace.AddDocument(project.Id, "Second.cs", SourceText.From("class Second { string b = \"b\"; }"));
        return (workspace, workspace.CurrentSolution.GetProject(project.Id)!, first, second);
    }

    [Fact]
    public void A_scan_of_one_workspace_never_lets_go_of_another_workspaces_documents()
    {
        (AdhocWorkspace host, _, Document hostFirst, Document hostSecond) = Made("Game");
        (AdhocWorkspace loose, _, Document looseFirst, _) = Made("Loose");
        SolutionKept<DocumentId, string> kept = SolutionKept.ByDocument<string>();
        kept.Of(host.CurrentSolution)[hostFirst.Id] = "a";
        kept.Of(host.CurrentSolution)[hostSecond.Id] = "b";
        kept.Of(loose.CurrentSolution)[looseFirst.Id] = "loose";

        for (int i = 0; i < 3; i++)
        {
            kept.LetGoOfGone(loose.CurrentSolution);
            kept.LetGoOfGone(host.CurrentSolution);
        }
        Assert.True(kept.Of(host.CurrentSolution).Count == 2 && kept.Of(loose.CurrentSolution).Count == 1,
            $"Scans of a loose file's workspace and the project's in turn left {kept.Of(host.CurrentSolution).Count} of the project's 2 entries and "
            + $"{kept.Of(loose.CurrentSolution).Count} of the loose file's 1: each scan would read the other's documents again, every time");
        host.Dispose();
        loose.Dispose();
    }

    [Fact]
    public void A_document_or_project_gone_from_its_workspace_takes_its_entry_with_it()
    {
        (AdhocWorkspace host, Project project, Document first, Document second) = Made("Game");
        SolutionKept<DocumentId, string> documents = SolutionKept.ByDocument<string>();
        SolutionKept<ProjectId, string> projects = SolutionKept.ByProject<string>();
        documents.Of(host.CurrentSolution)[first.Id] = "a";
        documents.Of(host.CurrentSolution)[second.Id] = "b";
        projects.Of(host.CurrentSolution)[project.Id] = "game";

        Assert.True(host.TryApplyChanges(host.CurrentSolution.RemoveDocument(first.Id)), "The workspace took no removal of First.cs");
        documents.LetGoOfGone(host.CurrentSolution);
        Assert.Equal([second.Id], documents.Of(host.CurrentSolution).Keys);

        Assert.True(host.TryApplyChanges(host.CurrentSolution.RemoveProject(project.Id)), "The workspace took no removal of the project");
        documents.LetGoOfGone(host.CurrentSolution);
        projects.LetGoOfGone(host.CurrentSolution);
        Assert.True(documents.Of(host.CurrentSolution).IsEmpty && projects.Of(host.CurrentSolution).IsEmpty,
            "An unloaded project's entries stayed: each unload and load again would leave its documents' reads behind for the server's life");
        host.Dispose();
    }
}
