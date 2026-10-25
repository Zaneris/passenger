using Avalonia.Controls;
using Avalonia.Interactivity;
using Passenger.App.CSharp;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Core;

namespace Passenger.App;

/// <summary>Problems: the current file's as they are typed, the solution's with solution-wide analysis on (the status
/// bar's icon turns it on and off and counts its errors), the projects' load problems under Toolset, Environment, and
/// the last build's apart. A link in a problem (an advisory) opens in the browser.</summary>
public partial class MainWindow
{
    private OpenDocument? problemsFile;

    private void AddProblems()
    {
        Editors.ActiveChanged += (_, _) => FollowActiveFile();
        Bottom.Problems.LinkClicked += (_, link) => Browse(link);
        ShowAnalysis();
    }

    /// <summary>The Problems window's view, built as it is asked for; what it lists is <see cref="ToolViews.Problems"/>,
    /// kept while it is not.</summary>
    public ProblemsView ProblemsWindow => (ProblemsView)Dock[ToolWindowIds.Problems].Content;

    /// <summary>Whether the status bar says an error, a warning or neither, as its icon shows it.</summary>
    public ProblemSeverity? StatusSeverity => status.Severity;

    /// <summary>Problems' Toolset, Environment: each project's load warnings and errors, under the project, its file the
    /// place to go to and the advisory or page its message names the link.</summary>
    private void ShowProjectProblems()
    {
        Bottom.Problems.ShowToolset([.. (session.CSharp?.ProjectProblems ?? []).Select(p => new Problem(p.Severity, p.Message, null, p.Project, new TextPosition(0, 0),
            Path.GetFileNameWithoutExtension(p.Project)) { Link = Problem.LinkIn(p.Message) })]);
    }

    /// <summary>Turns solution-wide analysis on or off, as the status bar's icon does, and keeps the choice.</summary>
    public void ToggleSolutionAnalysis() => ApplySettings(settings with { SolutionWideAnalysis = !settings.SolutionWideAnalysis });

    /// <summary>Puts the analysis setting in force on the C# service there is (or the next one).</summary>
    private void ApplyAnalysis()
    {
        if (session.CSharp is not null)
        {
            session.CSharp.SolutionWide = settings.SolutionWideAnalysis;
        }
        ShowAnalysis();
    }

    /// <summary>A new C# service follows the window's analysis setting and reports to Problems.</summary>
    private void WatchProblems(CSharpService service)
    {
        service.SolutionWide = settings.SolutionWideAnalysis;
        service.SolutionProblemsChanged += (_, files) =>
        {
            if (session.CSharp == service)
            {
                ShowSolutionProblems(files);
            }
        };
        service.ProjectProblemsChanged += (_, _) =>
        {
            if (session.CSharp == service)
            {
                ShowProjectProblems();
            }
        };
    }

    private void FollowActiveFile()
    {
        if (problemsFile is not null)
        {
            problemsFile.PropertyChanged -= OnProblemsFileChanged;
        }
        problemsFile = Editors.Active;
        if (problemsFile is not null)
        {
            problemsFile.PropertyChanged += OnProblemsFileChanged;
        }
        problemsMoved?.Stop();
        ShowFileProblems();
    }

    private void OnProblemsFileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpenDocument.Markers) || problemsFile is not { } file)
        {
            return;
        }
        if (file.Text.Version == problemsShownAt)
        {
            // The server's problems for the text the tab already shows: listed at once.
            problemsMoved?.Stop();
            ShowFileProblems();
            return;
        }
        // Every key moves the file's markers: thousands of problems are placed and listed again once typing pauses.
        (problemsMoved ??= new Debounce(ProblemsPause, () =>
        {
            ShowFileProblems();
            return Task.CompletedTask;
        })).Restart();
    }

    /// <summary>How long typing pauses before the File tab lists where the keys moved the file's problems, less than the
    /// server's pull waits, so the problems it finds for that text are listed as they land.</summary>
    private static readonly TimeSpan ProblemsPause = TimeSpan.FromMilliseconds(250);

    private Debounce? problemsMoved;

    /// <summary>The version of the file's text the File tab last listed problems for.</summary>
    private int problemsShownAt = -1;

    private void ShowFileProblems()
    {
        if (problemsFile is not { } file)
        {
            Bottom.Problems.ShowFile(null, []);
            return;
        }
        TextSnapshot text = file.Text.Snapshot;
        problemsShownAt = file.Text.Version;
        List<Problem> problems = [];
        string? project = null;
        foreach (Marker marker in file.Markers)
        {
            if (marker.Kind is not (MarkerKind.Error or MarkerKind.Warning))
            {
                continue;
            }
            int start = Math.Min(marker.Range.Start, text.Length);
            problems.Add(new Problem(marker.Kind == MarkerKind.Error ? ProblemSeverity.Error : ProblemSeverity.Warning, marker.Message, null,
                file.Path, text.GetPosition(start), project ??= ProjectNameOf(file.Path)) { Link = marker.Link, LinkText = marker.LinkText });
        }
        Bottom.Problems.ShowFile(file.Name, problems);
    }

    /// <summary>Problems' Solution tab made to list the solution's problems: every file's, or only those of the files named,
    /// as a typing pause changes the open file's, each file's project found once.</summary>
    private void ShowSolutionProblems(IReadOnlyCollection<string>? files = null)
    {
        if (files is not null && session.CSharp is { } service && Bottom.Problems.ListsSolution)
        {
            foreach (string path in files)
            {
                IReadOnlyList<SolutionProblem> found = service.SolutionProblemsOf(path);
                string? project = found.Count > 0 ? ProjectNameOf(path) : null;
                Bottom.Problems.ShowSolutionFile(path, [.. found.Select(p => ProblemOf(p, project!))]);
            }
        }
        else
        {
            Dictionary<string, string> projects = new(Passenger.Core.PathNames.Comparer);
            Bottom.Problems.ShowSolution(session.CSharp?.SolutionProblems?.Select(p => ProblemOf(p,
                projects.TryGetValue(p.Path, out string? project) ? project : projects[p.Path] = ProjectNameOf(p.Path))).ToList());
        }
        ShowAnalysis();
    }

    /// <summary>One of the solution's problems as Problems lists it, under its file's project.</summary>
    private static Problem ProblemOf(SolutionProblem problem, string project) => new(
        problem.Diagnostic.Severity == DiagnosticSeverity.Error ? ProblemSeverity.Error : ProblemSeverity.Warning,
        problem.Diagnostic.Message,
        problem.Diagnostic.Code?.ValueKind == System.Text.Json.JsonValueKind.String ? problem.Diagnostic.Code.Value.GetString() : problem.Diagnostic.Code?.ToString(),
        problem.Path,
        problem.At,
        project);

    /// <summary>The status bar's analysis icon: grey and off, a green tick, or the solution's error count in red.</summary>
    private void ShowAnalysis()
    {
        int errors = Bottom.Problems.SolutionErrors;
        (string icon, string brush, string text, string tip) = !settings.SolutionWideAnalysis
            ? (IconKeys.Problems, TokenKeys.TextMuted, string.Empty, "Solution-wide analysis is off: click to analyse every file of the solution")
            : !Bottom.Problems.ListsSolution || errors == 0
                ? (IconKeys.Check, TokenKeys.Test_Passed, string.Empty, "Solution-wide analysis: no errors. Click to turn it off")
                : (IconKeys.Error, TokenKeys.Error, errors.ToString(), $"Solution-wide analysis: {Plural.Of(errors, "error")}; Problems lists them. Click to turn it off");
        AnalysisIcon.Bind(PathIcon.DataProperty, AnalysisIcon.GetResourceObservable(icon));
        AnalysisIcon.Bind(PathIcon.ForegroundProperty, AnalysisIcon.GetResourceObservable(brush));
        AnalysisText.Text = text;
        AnalysisText.IsVisible = text.Length > 0;
        ToolTip.SetTip(AnalysisButton, tip);
        AnalysisButton.IsVisible = session.CSharp is not null;
    }

    private void OnAnalysisClicked(object? sender, RoutedEventArgs e) => ToggleSolutionAnalysis();
}
