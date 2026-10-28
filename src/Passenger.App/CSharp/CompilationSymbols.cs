using Microsoft.CodeAnalysis.CSharp;
using Passenger.Host;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>What a C# file of the folder is parsed with in process (<see cref="CSharpParsing"/>): the preprocessor symbols
/// its project compiles it with for the framework the editor shows, as MSBuild evaluated them (the SDK's framework and
/// platform symbols among them). A file in no project, or in one not yet evaluated, has a Debug build's. Pure, so any
/// thread may ask.</summary>
public static class CompilationSymbols
{
    /// <summary>A file's: the project of the language server's context it is shown in (<paramref name="context"/>, the
    /// server's name for it), else the project holding it, for the framework that context names, else its first.</summary>
    public static CSharpParseOptions Of(IReadOnlyList<ProjectModel> projects, string path, string? context = null)
    {
        ProjectModel? project = Named(projects, path, context) ?? ProjectModel.Owning(projects, path);
        return project is null ? CSharpParsing.Default : Of(project, context is null ? null : ProjectContext.FrameworkOf(context));
    }

    /// <summary>A project's, for one of its frameworks (its first when not given, or not one of its own).</summary>
    public static CSharpParseOptions Of(ProjectModel project, string? framework = null)
    {
        IReadOnlyList<FrameworkSymbols> evaluated = project.Evaluated?.Symbols ?? [];
        FrameworkSymbols? chosen = evaluated.FirstOrDefault(f => string.Equals(f.Framework, framework, StringComparison.OrdinalIgnoreCase)) ?? evaluated.FirstOrDefault();
        return chosen is null ? CSharpParsing.Default : CSharpParsing.Defining(chosen.Symbols);
    }

    /// <summary>The project a server's context names, as Roslyn names a project after its file: of several of that name,
    /// the one holding the file.</summary>
    private static ProjectModel? Named(IReadOnlyList<ProjectModel> projects, string path, string? context)
    {
        if (context is null)
        {
            return null;
        }
        string name = ProjectContext.ProjectOf(context);
        ProjectModel? found = null;
        foreach (ProjectModel project in projects)
        {
            if (Path.GetFileNameWithoutExtension(project.Path) == name && (found is null || project.Holds(path)))
            {
                found = project;
            }
        }
        return found;
    }
}
