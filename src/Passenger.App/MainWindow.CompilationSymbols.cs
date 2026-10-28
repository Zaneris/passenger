using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>Each C# file's in-process parse takes its project's preprocessor symbols (<see cref="CompilationSymbols"/>): a
/// file opens parsed with them, and its shared parse follows a change of them, its project evaluated (again) or another
/// framework chosen for it.</summary>
public partial class MainWindow
{
    private void AddCompilationSymbols()
    {
        Editors.ParseOptions = path => CompilationSymbols.Of(session.Workspace?.Projects ?? [], path);
        Editors.Opened += (_, document) => document.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OpenDocument.ProjectContext) or nameof(OpenDocument.Path))
            {
                FollowSymbols(document);
            }
        };
    }

    /// <summary>The open files' parses given their projects' symbols as the projects now are.</summary>
    private void FollowSymbols()
    {
        foreach (OpenDocument document in Editors.Documents)
        {
            FollowSymbols(document);
        }
    }

    private void FollowSymbols(OpenDocument document)
    {
        if (document.Syntax is { } syntax)
        {
            IReadOnlyList<ProjectModel> projects = session.Workspace?.Projects ?? [];
            syntax.Options = CompilationSymbols.Of(projects, document.Path, document.ProjectContext);
        }
    }
}
