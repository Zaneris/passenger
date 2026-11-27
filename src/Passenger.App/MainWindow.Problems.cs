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

/// <summary>Problems: the current file's as they are typed (the status bar counts its errors and warnings), the
/// solution's with solution-wide analysis on (the status bar's glyph beside those counts turns it on and off and counts
/// its errors), the projects' load problems under Toolset, Environment, and the last build's apart. A link in a problem
/// (an advisory) opens in the browser.</summary>
public partial class MainWindow
{
    private OpenDocument? problemsFile;

    private void AddProblems()
    {
        Editors.ActiveChanged += (_, _) => FollowActiveFile();
        Bottom.Problems.LinkClicked += (_, link) => Browse(link);
        ShowAnalysis();
        Add(AppActions.NextErrorInSolution, "Next Error in Solution", "Navigate", _ => StepSolutionErrors(forward: true), _ => WhyNotStepSolutionErrors(),
            ("Navigate", 3, 2), words: ["Go to Next Error in Solution"]);
        Add(AppActions.PreviousErrorInSolution, "Previous Error in Solution", "Navigate", _ => StepSolutionErrors(forward: false), _ => WhyNotStepSolutionErrors(),
            ("Navigate", 3, 3), words: ["Go to Previous Error in Solution"]);
        AddInspections();
    }

    /// <summary>Next or Previous Error in Solution: the solution's error after the caret (before it, going back) as Problems'
    /// Solution tab lists them, wrapping, gone to and chosen there, the status bar saying it and which of how many it is.</summary>
    private void StepSolutionErrors(bool forward)
    {
        ProblemsModel problems = Bottom.Problems;
        OpenDocument? file = Editors.Active is { Diff: null, Tool: null } shown ? shown : null;
        TextPosition caret = file is null ? new TextPosition(0, 0) : Editors.CodeEditor.Document.Snapshot.GetPosition(Editors.CodeEditor.Selections.Primary.Caret);
        if (problems.SolutionErrorFrom(file?.Path, caret, forward) is not { Path: { } path } error)
        {
            return;
        }
        IReadOnlyList<Problem> errors = problems.SolutionErrorsListed;
        NavigateTo(path, error.Position);
        problems.ChooseInSolution(error);
        Say($"{error.Message} ({Numbers.Said(errors.ToList().IndexOf(error) + 1)} of {Plural.Of(errors.Count, "error")} in the solution)");
    }

    /// <summary>Why Next or Previous Error in Solution has nothing to step through: no C#, the analysis off, or no error listed.</summary>
    private string? WhyNotStepSolutionErrors() =>
        session.CSharp is null ? session.WhyNoCSharp
        : !Bottom.Problems.ListsSolution ? Refusals.SolutionAnalysisOff
        : Bottom.Problems.SolutionErrorsListed.Count == 0 ? Refusals.NoSolutionErrors(Bottom.Problems.SolutionErrors)
        : null;

    /// <summary>The Problems window's view, built as it is asked for; what it lists is <see cref="ToolViews.Problems"/>,
    /// kept while it is not.</summary>
    public ProblemsView ProblemsWindow => (ProblemsView)Dock[ToolWindowIds.Problems].Content;

    /// <summary>Whether the status bar says an error, a warning or neither, as its icon shows it.</summary>
    public ProblemSeverity? StatusSeverity => status.Severity;

    /// <summary>Problems' Toolset, Environment: each project's load warnings and errors, under the project, its file the
    /// place to go to and the advisory or page its message names the link; and what a project's build lacks to reach its Godot
    /// folder, which nothing else says.</summary>
    private void ShowProjectProblems()
    {
        Bottom.Problems.ShowToolset([.. (session.CSharp?.ProjectProblems ?? []).Select(p => new Problem(p.Severity, p.Message, null, p.Project, new TextPosition(0, 0),
            Path.GetFileNameWithoutExtension(p.Project)) { Link = Problem.LinkIn(p.Message) }),
            .. Projects.Select(p => (p.Path, Message: p.GodotLayoutProblem)).Where(p => p.Message is not null)
                .Select(p => new Problem(ProblemSeverity.Warning, p.Message!, null, p.Path, new TextPosition(0, 0), Path.GetFileNameWithoutExtension(p.Path)))]);
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
        service.AnalysingChanged += (_, _) =>
        {
            if (session.CSharp == service)
            {
                ShowAnalysis();
            }
        };
        service.SolutionWide = settings.SolutionWideAnalysis;
        service.SolutionProblemsChanged += (_, files) =>
        {
            if (session.CSharp == service)
            {
                ShowSolutionProblems(files);
                FollowAnalysedErrors(service, files);
            }
        };
        FollowAnalysedErrors(service, null);
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
        ShowFileProblemCounts();
    }

    private void OnProblemsFileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OpenDocument.Markers) || problemsFile is not { } file)
        {
            return;
        }
        ShowFileProblemCounts();
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
    public static readonly TimeSpan ProblemsPause = TimeSpan.FromMilliseconds(250);

    private Debounce? problemsMoved;

    /// <summary>The version of the file's text the File tab last listed problems for.</summary>
    private int problemsShownAt = -1;

    /// <summary>The File tab's problems as last listed, in the order of their markers, and the file they are of, by its name then.</summary>
    private Problem[] fileProblems = [];
    private OpenDocument? fileProblemsOf;
    private string? fileProblemsName;

    /// <summary>Lists the file's problems from its markers: one listed before, saying the same in the same place, is listed
    /// as it was, so a pause that moved or found a few of thousands makes those alone, and one that changed none lists none.</summary>
    private void ShowFileProblems()
    {
        if (problemsFile is not { } file)
        {
            fileProblems = [];
            fileProblemsOf = null;
            fileProblemsName = null;
            Bottom.Problems.ShowFile(null, []);
            return;
        }
        TextSnapshot text = file.Text.Snapshot;
        problemsShownAt = file.Text.Version;
        IReadOnlyList<Marker> markers = file.Markers;
        bool again = fileProblemsOf == file && fileProblemsName == file.Name;
        Problem[] before = again ? fileProblems : [];
        List<Problem> problems = new(Math.Min(markers.Count, before.Length + 16));
        string? project = markers.Count > 0 ? ProjectNameOf(file.Path) : null;
        int next = 0;
        bool same = again;
        for (int i = 0; i < markers.Count; i++)
        {
            Marker marker = markers[i];
            ProblemSeverity? severity = marker.Kind switch
            {
                MarkerKind.Error => ProblemSeverity.Error,
                MarkerKind.Warning => ProblemSeverity.Warning,
                MarkerKind.Info => ProblemSeverity.Message,
                _ => null,
            };
            if (severity is null)
            {
                continue;
            }
            TextPosition at = text.GetPosition(Math.Min(marker.Range.Start, text.Length));
            // The markers and the problems listed from them are both in order of place: those before this one's are gone.
            while (next < before.Length && before[next].Position.CompareTo(at) < 0)
            {
                next++;
            }
            Problem? kept = null;
            for (int k = next; k < before.Length && before[k].Position == at; k++)
            {
                Problem listed = before[k];
                if (listed.Severity == severity && listed.Message == marker.Message && listed.Link == marker.Link && listed.LinkText == marker.LinkText
                    && listed.Path == file.Path && listed.Project == project)
                {
                    kept = listed;
                    break;
                }
            }
            same &= kept is not null && problems.Count < before.Length && ReferenceEquals(before[problems.Count], kept);
            problems.Add(kept ?? new Problem(severity.Value, marker.Message, null, file.Path, at, project) { Link = marker.Link, LinkText = marker.LinkText });
        }
        if (same && problems.Count == before.Length)
        {
            return;
        }
        fileProblems = [.. problems];
        fileProblemsOf = file;
        fileProblemsName = file.Name;
        Bottom.Problems.ShowFile(file.Name, fileProblems);
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
        problem.Diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => ProblemSeverity.Error,
            DiagnosticSeverity.Warning => ProblemSeverity.Warning,
            _ => ProblemSeverity.Message,
        },
        problem.Diagnostic.Message,
        problem.Diagnostic.Code,
        problem.Path,
        problem.At,
        project);

    /// <summary>The file shown's errors and warnings as the status bar counts them; null while it shows no file of text
    /// (a diff, a tool window's tab).</summary>
    public (int Errors, int Warnings)? FileProblemsShown => FileProblemsButton.IsVisible ? fileCountsShown : null;

    /// <summary>The counts the status bar shows and the file it shows them of, by its name then: markers moved by a key,
    /// which change no count, word nothing again.</summary>
    private (int Errors, int Warnings) fileCountsShown;
    private OpenDocument? fileCountsOf;
    private string? fileCountsName;

    /// <summary>The status bar's counts of the file shown: its errors and warnings, each with its icon while there are
    /// some, or a green tick with none; nothing with no file of text shown.</summary>
    private void ShowFileProblemCounts()
    {
        if (problemsFile is not { HoldsText: true } file)
        {
            FileProblemsButton.IsVisible = false;
            ProblemsWidget.IsVisible = AnalysisButton.IsVisible;
            fileCountsOf = null;
            return;
        }
        (int errors, int warnings) = file.Decorations.Problems;
        if (fileCountsOf == file && fileCountsName == file.Name && fileCountsShown == (errors, warnings) && FileProblemsButton.IsVisible)
        {
            return;
        }
        fileCountsOf = file;
        fileCountsName = file.Name;
        fileCountsShown = (errors, warnings);
        FileCleanIcon.IsVisible = errors + warnings == 0;
        FileErrors.IsVisible = errors > 0;
        FileErrorsText.Text = Numbers.Said(errors);
        FileWarnings.IsVisible = warnings > 0;
        FileWarningsText.Text = Numbers.Said(warnings);
        string counted = errors + warnings == 0 ? "No errors or warnings"
            : errors == 0 ? Plural.Of(warnings, "warning")
            : warnings == 0 ? Plural.Of(errors, "error")
            : $"{Plural.Of(errors, "error")} and {Plural.Of(warnings, "warning")}";
        ToolButtons.Tip(FileProblemsButton, $"{counted} in {file.Name}: click to {(errors + warnings == 0 ? "open Problems" : "go to the next and open Problems")}");
        FileProblemsButton.IsVisible = ProblemsWidget.IsVisible = true;
    }

    /// <summary>The status bar's counts were clicked: the editor with the keys goes to its file's next error or warning, and
    /// Problems shows.</summary>
    private void OnFileProblemsClicked(object? sender, RoutedEventArgs e)
    {
        CodeEditor editor = Editors.CodeEditor;
        editor.GoToNextProblem();
        editor.Focus();
        ShowToolWindow(ToolWindowIds.Problems);
    }

    /// <summary>The status bar's analysis glyph, the solution's, beside the file's counts: muted while off, a turning ring
    /// while its first round is under way, with the errors found so far, then green, or red with the solution's error
    /// count.</summary>
    private void ShowAnalysis()
    {
        int errors = Bottom.Problems.ListsSolution ? Bottom.Problems.SolutionErrors : 0;
        bool analysing = settings.SolutionWideAnalysis && session.CSharp is { IsAnalysing: true };
        (string brush, string text, string tip) = !settings.SolutionWideAnalysis
            ? (TokenKeys.TextMuted, string.Empty, "Solution-wide analysis is off: click to analyse every file of the solution")
            : analysing
                ? (TokenKeys.Test_Passed, errors > 0 ? errors.ToString() : string.Empty, errors > 0
                    ? $"Solution-wide analysis is under way, {Plural.Of(errors, "error")} so far: Problems lists them as it finds them. Click to turn it off"
                    : "Solution-wide analysis is under way: Problems lists what it finds as it goes. Click to turn it off")
            : errors == 0
                ? (TokenKeys.Test_Passed, string.Empty, "Solution-wide analysis: no errors. Click to turn it off")
                : (TokenKeys.Error, errors.ToString(), $"Solution-wide analysis: {Plural.Of(errors, "error")}; Problems lists them. Click to turn it off");
        AnalysisIcon.Bind(PathIcon.ForegroundProperty, AnalysisIcon.GetResourceObservable(brush));
        AnalysisIcon.IsVisible = !analysing;
        AnalysisWorking.IsVisible = analysing;
        AnalysisText.Text = text;
        AnalysisText.IsVisible = text.Length > 0;
        ToolButtons.Tip(AnalysisButton, tip);
        AnalysisButton.IsVisible = session.CSharp is not null;
        ProblemsWidget.IsVisible = AnalysisButton.IsVisible || FileProblemsButton.IsVisible;
    }

    private void OnAnalysisClicked(object? sender, RoutedEventArgs e) => ToggleSolutionAnalysis();
}
