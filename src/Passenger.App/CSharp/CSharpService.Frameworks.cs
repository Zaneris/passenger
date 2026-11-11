using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>The one target framework chosen per multi-targeted project (<see cref="TargetFrameworkChoices"/>) as the server
/// answers for its files: a file the server has in that project's frameworks alone is answered for in the one chosen, and
/// a framework chosen in its switcher is the project's from then on. A file in several projects keeps a choice of its own.</summary>
public sealed partial class CSharpService
{
    /// <summary>The folder's choice of framework for each multi-targeted project; none leaves the server's own.</summary>
    public TargetFrameworkChoices? Frameworks { get; init; }

    /// <summary>The contexts a file's switcher offers: the server's but those of .NET Framework's targets, which Passenger
    /// never runs, while another is left.</summary>
    internal static IReadOnlyList<ProjectContext> Offered(IReadOnlyList<ProjectContext> contexts)
    {
        List<ProjectContext> runs = [.. contexts.Where(c => ProjectContext.FrameworkOf(c.Label) is not { } framework || !FrameworkNames.IsNetFramework(framework))];
        return runs.Count > 0 ? runs : contexts;
    }

    /// <summary>The project whose frameworks a file's contexts are, all of them one project's; null for a file in several
    /// projects, or in none of the folder's.</summary>
    private ProjectModel? FrameworksOf(string path, IReadOnlyList<ProjectContext> contexts)
    {
        if (contexts.Count == 0 || contexts.Any(c => ProjectContext.FrameworkOf(c.Label) is null)
            || contexts.Select(c => ProjectContext.ProjectOf(c.Label)).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            return null;
        }
        return CompilationSymbols.Named(ProjectsNow?.Invoke() ?? [], path, contexts[0].Label);
    }

    /// <summary>The context of the framework chosen for a file's project, where its contexts are that project's
    /// frameworks; null where there is none to follow.</summary>
    internal ProjectContext? ChosenContext(string path, IReadOnlyList<ProjectContext> contexts) =>
        Frameworks is { } choices && FrameworksOf(path, contexts) is { } project && choices.Of(project) is { } framework
            ? contexts.FirstOrDefault(c => string.Equals(ProjectContext.FrameworkOf(c.Label), framework, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>A context chosen in a file's switcher, made its project's framework where its contexts are that project's;
    /// false where the file keeps the choice alone.</summary>
    internal bool ChooseFramework(string path, IReadOnlyList<ProjectContext> contexts, ProjectContext context)
    {
        if (Frameworks is not { } choices || FrameworksOf(path, contexts) is not { } project || ProjectContext.FrameworkOf(context.Label) is not { } framework)
        {
            return false;
        }
        choices.Choose(project, framework);
        return true;
    }

    /// <summary>A project's framework was chosen: each open file answered for in its project's choice from now on, and
    /// solution-wide analysis follows it.</summary>
    public void FrameworkChosen()
    {
        UiThread.Check();
        foreach (CSharpDocument document in documents.Values)
        {
            document.FollowFramework();
        }
        AnalyseChosenFrameworks();
    }

    /// <summary>The framework solution-wide analysis takes each multi-targeted project in, by its file: the one its files are
    /// answered for in, so Problems lists what the editor shows and no other framework's build is analysed.</summary>
    private Dictionary<string, string> AnalysedFrameworks()
    {
        Dictionary<string, string> chosen = new(StringComparer.Ordinal);
        if (Frameworks is { } choices && workspace is { } opened)
        {
            foreach (ProjectModel project in ProjectsNow?.Invoke() ?? opened.Projects)
            {
                if (choices.Of(project) is { } framework)
                {
                    chosen[project.Path] = framework;
                }
            }
        }
        return chosen;
    }

    /// <summary>Solution-wide analysis made to follow the frameworks chosen: the reports of a framework no longer chosen let
    /// go of at once, and the pulls asked again with the choices as they stand, the new framework's reports coming as the
    /// server analyses it.</summary>
    private void AnalyseChosenFrameworks()
    {
        if (!solutionWide || Frameworks is not { } choices || workspace is not { } opened)
        {
            return;
        }
        IReadOnlyList<ProjectModel> projects = ProjectsNow?.Invoke() ?? opened.Projects;
        HashSet<string> files = new(PathNames.Comparer);
        foreach ((ReportKey key, WorkspaceReport report) in solutionReports.ToList())
        {
            if (report.Context is { } context && ProjectContext.FrameworkOf(context.Label) is { } framework
                && CompilationSymbols.Named(projects, report.Path, context.Label) is { } project && choices.Of(project) is { } chosen
                && !string.Equals(framework, chosen, StringComparison.OrdinalIgnoreCase))
            {
                Forget(key, report.Path);
                files.Add(report.Path);
            }
        }
        foreach (string path in files)
        {
            Merge(path);
        }
        if (files.Count > 0)
        {
            SolutionProblemsChanged?.Invoke(this, files);
        }
        // Taken apart first: a pull let go may end at once, leaving the list as it does.
        foreach (CancellationTokenSource asked in askedPulls.Keys.ToList())
        {
            asked.Cancel();
        }
    }
}
