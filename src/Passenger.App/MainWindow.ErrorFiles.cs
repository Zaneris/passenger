using Passenger.App.CSharp;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The Explorer's marks of the files with errors and of what holds them (<see cref="ErrorFiles"/>), a project by
/// the files its evaluation lists, as Problems knows a file's errors: an open file's by its own markers as its services
/// report them, a closed one's by solution-wide analysis while it is on, and by the last build. A file is asked again only
/// as what tells its errors changes, never as a key moves its markers.</summary>
public partial class MainWindow
{
    private readonly ErrorFiles errorFiles = new();

    /// <summary>The files the last build found errors in, and those solution-wide analysis found them in.</summary>
    private HashSet<string> builtErrors = new(PathNames.Comparer);
    private HashSet<string> analysedErrors = new(PathNames.Comparer);

    /// <summary>Each open file's following of its markers, let go of as it closes.</summary>
    private readonly Dictionary<OpenDocument, EventHandler<DecorationKind>> markersFollowed = [];

    private void AddErrorFiles()
    {
        Explorer.Tree.FollowErrors(errorFiles);
        Editors.Opened += (_, document) =>
        {
            EventHandler<DecorationKind> follow = (_, kind) =>
            {
                if (kind == DecorationKind.Markers)
                {
                    FollowErrors([document.Path]);
                }
            };
            markersFollowed[document] = follow;
            document.Decorations.Changed += follow;
            FollowErrors([document.Path]);
        };
        Editors.Closed += (_, document) =>
        {
            if (markersFollowed.Remove(document, out EventHandler<DecorationKind>? follow))
            {
                document.Decorations.Changed -= follow;
            }
            FollowErrors([document.Path]);
        };
    }

    private void BindErrorFiles(ProjectSession next)
    {
        // Another folder's analysis knows nothing of this one's files until its service says what it found.
        HashSet<string> analysed = analysedErrors;
        analysedErrors = new(PathNames.Comparer);
        FollowErrors(analysed);
        errorFiles.FollowProjects(file => ProjectsHolding(next, file));
        next.ProjectsEvaluated += (_, _) =>
        {
            if (session == next)
            {
                errorFiles.ProjectsChanged();
            }
        };
        next.WorkspaceChanged += (_, _) =>
        {
            if (session == next)
            {
                errorFiles.ProjectsChanged();
            }
        };
        buildRun.BuildFinished += (_, _) =>
        {
            if (session == next)
            {
                FollowBuiltErrors(next);
            }
        };
        FollowBuiltErrors(next);
    }

    /// <summary>The last build's files with errors taken in, those it came or ceased to name asked again.</summary>
    private void FollowBuiltErrors(ProjectSession of)
    {
        HashSet<string> now = new(of.Problems.Where(p => p.Severity == ProblemSeverity.Error && p.Path is not null).Select(p => p.Path!), PathNames.Comparer);
        HashSet<string> moved = new(now, PathNames.Comparer);
        moved.SymmetricExceptWith(builtErrors);
        builtErrors = now;
        FollowErrors(moved);
    }

    /// <summary>Solution-wide analysis's files with errors taken in: the files it says changed, or all of them where it names
    /// none (it was turned on or off, or analysed everything again), those it came or ceased to find errors in asked again.</summary>
    private void FollowAnalysedErrors(CSharpService service, IReadOnlyCollection<string>? files)
    {
        if (files is null)
        {
            HashSet<string> now = new((service.SolutionProblems ?? []).Where(p => p.Diagnostic.Severity == DiagnosticSeverity.Error).Select(p => p.Path),
                PathNames.Comparer);
            HashSet<string> moved = new(now, PathNames.Comparer);
            moved.SymmetricExceptWith(analysedErrors);
            analysedErrors = now;
            FollowErrors(moved);
            return;
        }
        foreach (string file in files)
        {
            if (service.SolutionProblemsOf(file).Any(p => p.Diagnostic.Severity == DiagnosticSeverity.Error))
            {
                analysedErrors.Add(file);
            }
            else
            {
                analysedErrors.Remove(file);
            }
        }
        FollowErrors(files);
    }

    /// <summary>The projects a file is in, by their paths: those whose evaluations list it (or whose file it is), else the
    /// project not yet evaluated whose folder nearest holds it.</summary>
    private static IReadOnlyList<string> ProjectsHolding(ProjectSession of, string file)
    {
        IReadOnlyList<ProjectModel> projects = of.Projects;
        List<string> holding = [.. projects.Where(p => p.IsEvaluated && (PathNames.Same(p.Path, file) || p.Evaluated!.Items.Any(i => PathNames.Same(i.Path, file)))).Select(p => p.Path)];
        if (holding.Count == 0 && ProjectModel.Owning([.. projects.Where(p => !p.IsEvaluated)], file) is { } unread)
        {
            holding.Add(unread.Path);
        }
        return holding;
    }

    /// <summary>Asks again whether each file has errors.</summary>
    private void FollowErrors(IEnumerable<string> files) => errorFiles.Set(files.Select(file => (file, HasErrors(file))));

    /// <summary>Whether a file has errors: an open one by its own markers, which are its text's as it stands, a closed one by
    /// solution-wide analysis or the last build.</summary>
    private bool HasErrors(string file) => Editors.Find(file) is { } open
        ? open.Decorations.Problems.Errors > 0
        : analysedErrors.Contains(file) || builtErrors.Contains(file);
}
