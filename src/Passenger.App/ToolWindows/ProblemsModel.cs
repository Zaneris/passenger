using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Passenger.App.LiveTree;
using Passenger.App.Run;

namespace Passenger.App.ToolWindows;

/// <summary>Which of Problems' tabs: the current file's, the solution's (with solution-wide analysis on), the projects'
/// load problems (Toolset, Environment), the last build's, or the bindings that failed in the Avalonia apps run and
/// debugged last.</summary>
public enum ProblemsTab
{
    File,
    Solution,
    Toolset,
    Build,
    Bindings,

    /// <summary>The edits the last Hot Reload could not apply, or warned of, each at its line.</summary>
    HotReload,

    /// <summary>The container engine's problems (no CLI, not running, Windows containers, no Compose v2), then what it
    /// finds in the open Dockerfiles and compose files, by file.</summary>
    Containers,
}

/// <summary>A row of Problems: a group (a project or a file, with its count) or a problem, at its depth. A problem of the
/// current file moves with its text as it is typed, and its row with it, redrawing only where it is.</summary>
public sealed class ProblemRow(int depth, string label, string? detail, Problem? problem) : Observable
{
    public int Depth { get; } = depth;

    public string Label { get; } = label;

    public string? Detail { get; private set; } = detail;

    public Problem? Problem { get; private set; } = problem;

    /// <summary>Takes the place an edit moved its problem to.</summary>
    internal void MoveTo(Problem moved)
    {
        bool elsewhere = Problem?.Position != moved.Position;
        Problem = moved;
        if (elsewhere)
        {
            Detail = moved.Where;
            Raise(nameof(Detail));
        }
    }

    /// <summary>Whether a row made again is this one: the same problem, or the same group, at the same depth, whatever its
    /// detail says now.</summary>
    internal bool IsSame(ProblemRow made) => Depth == made.Depth && Label == made.Label && Problem == made.Problem;

    /// <summary>Takes the detail the row made again says (a group's count), as this row stays listed in its place.</summary>
    internal void Retell(string? detail)
    {
        if (Detail != detail)
        {
            Detail = detail;
            Raise(nameof(Detail));
        }
    }
}

/// <summary>What the Problems window lists, kept whether or not its view is built: the current file's problems as they
/// are typed, the whole solution's with solution-wide analysis on (by project, then file), the warnings and errors the
/// projects loaded with (Toolset, Environment, by project), the last build's apart from them, the bindings that failed in
/// a running Avalonia app (by XAML file, each once with its control, property, path and count), Hot Reload's edits and
/// the container engine's problems, each a tab with its count, the shown tab's rows and the one chosen. Builds, servers,
/// bindings and containers write here; <see cref="ProblemsView"/> shows it once the window is first shown.</summary>
public sealed class ProblemsModel
{
    private readonly ProblemRows rows = [];
    private IReadOnlyList<Problem> file = [];

    /// <summary>The solution's problems by file, each file's in order; null with solution-wide analysis off.</summary>
    private Dictionary<string, Problem[]>? solution;

    /// <summary>The solution's problems in order, made when first asked for after a change.</summary>
    private IReadOnlyList<Problem>? solutionOrdered;
    private int solutionCount;
    private int solutionErrors;
    private IReadOnlyList<Problem> toolset = [];
    private IReadOnlyList<BindingProblem> bindings = [];
    private IReadOnlyList<(string Said, string Why)> bindingsUntold = [];
    private IReadOnlyList<Problem>? hotReload;
    private string hotReloadSaid = string.Empty;
    private IReadOnlyList<Problem> containers = [];
    private IReadOnlyList<Problem> containerFiles = [];
    private string? fileName;
    private ProblemRow? chosen;

    /// <summary>The tab the rows were made for, whose rows a refresh keeps where it makes them again alike.</summary>
    private ProblemsTab? rowsOf;

    public ProblemsModel() => Refresh();

    /// <summary>A problem's fixes asked for (Alt+Enter on it): the editor goes there and offers them.</summary>
    public event EventHandler<Problem>? FixRequested;

    /// <summary>A problem chosen to go to.</summary>
    public event EventHandler<Problem>? Chosen;

    /// <summary>A problem's link clicked: the advisory or page its message names, for the browser.</summary>
    public event EventHandler<Uri>? LinkClicked;

    /// <summary>The tabs' titles, which shows, or the empty list's words changed.</summary>
    internal event EventHandler? TabsChanged;

    /// <summary>Another row chosen (none when null), or the one chosen to be scrolled to (true).</summary>
    internal event EventHandler<bool>? ChosenRowChanged;

    /// <summary>The last build's problems.</summary>
    public ObservableCollection<Problem> Problems { get; } = [];

    /// <summary>The current file's problems.</summary>
    public IReadOnlyList<Problem> FileProblems => file;

    /// <summary>The solution's problems, or null with solution-wide analysis off.</summary>
    public IReadOnlyList<Problem>? SolutionProblems => solution is null ? null : solutionOrdered ??= Ordered(solution.Values.SelectMany(p => p));

    /// <summary>Whether the solution's problems are listed: solution-wide analysis is on.</summary>
    public bool ListsSolution => solution is not null;

    /// <summary>How many of the solution's problems are errors.</summary>
    public int SolutionErrors => solutionErrors;

    /// <summary>How many problems the Solution tab lists, as its title counts them.</summary>
    public int SolutionCount => solutionCount;

    /// <summary>The warnings and errors the projects loaded with.</summary>
    public IReadOnlyList<Problem> ToolsetProblems => toolset;

    /// <summary>The bindings that failed in the apps run and debugged last, each once.</summary>
    public IReadOnlyList<BindingProblem> BindingProblems => bindings;

    /// <summary>The edits the last Hot Reload could not apply or warned of; null before one in this window.</summary>
    public IReadOnlyList<Problem>? HotReloadProblems => hotReload;

    /// <summary>The rows of the tab shown.</summary>
    public IReadOnlyList<ProblemRow> Rows => rows;

    /// <summary>The rows of the tab shown, for the view's list.</summary>
    internal ObservableCollection<ProblemRow> ShownRows => rows;

    /// <summary>What the list says when the tab shown has no rows.</summary>
    internal string EmptyText { get; private set; } = string.Empty;

    /// <summary>The row chosen, if one is: the view's list follows it and it follows the list.</summary>
    internal ProblemRow? ChosenRow
    {
        get => chosen;
        set
        {
            if (chosen != value)
            {
                chosen = value;
                ChosenRowChanged?.Invoke(this, false);
            }
        }
    }

    /// <summary>The problem row chosen, if one is.</summary>
    public Problem? Selected => chosen?.Problem;

    /// <summary>What the last Hot Reload came to (its words, and its edits by file at their lines), in the Hot Reload tab,
    /// shown when it has problems.</summary>
    public void ShowHotReload(string said, IReadOnlyList<Problem> problems)
    {
        hotReload = Ordered(problems);
        hotReloadSaid = said;
        if (problems.Count > 0)
        {
            Shown = ProblemsTab.HotReload;
            return;
        }
        Refresh();
    }

    /// <summary>A tab's title, without its count.</summary>
    public static string TitleOf(ProblemsTab tab) => tab switch
    {
        ProblemsTab.File => "File",
        ProblemsTab.Solution => "Solution",
        ProblemsTab.Toolset => "Toolset, Environment",
        ProblemsTab.Bindings => "Bindings",
        ProblemsTab.HotReload => "Hot Reload",
        ProblemsTab.Containers => "Containers",
        _ => "Build",
    };

    /// <summary>A tab's title with its count, as its button says it.</summary>
    internal string TitleWithCount(ProblemsTab tab)
    {
        (string title, int count) = tab switch
        {
            ProblemsTab.File => (fileName is null ? TitleOf(ProblemsTab.File) : $"File: {fileName}", file.Count),
            ProblemsTab.Solution => (TitleOf(tab), solutionCount),
            ProblemsTab.Toolset => (TitleOf(tab), toolset.Count),
            ProblemsTab.Bindings => (TitleOf(tab), bindings.Count),
            ProblemsTab.HotReload => (TitleOf(tab), hotReload?.Count ?? 0),
            ProblemsTab.Containers => (TitleOf(tab), containers.Count + containerFiles.Count),
            _ => (TitleOf(tab), Problems.Count),
        };
        return count == 0 ? title : $"{title}  {count}";
    }

    /// <summary>Whether a tab's button shows: the Solution tab with solution-wide analysis on, Hot Reload's after one, the
    /// Containers tab while it has problems or is shown.</summary>
    internal bool HasTab(ProblemsTab tab) => tab switch
    {
        ProblemsTab.Solution => solution is not null,
        ProblemsTab.HotReload => hotReload is not null,
        ProblemsTab.Containers => containers.Count + containerFiles.Count > 0 || Shown == ProblemsTab.Containers,
        _ => true,
    };

    /// <summary>Whether a problem of the tab shown has fixes to offer: none in Toolset, and a failed binding's are the nearest
    /// members of its source's type, when it has any.</summary>
    internal bool HasFixes(Problem problem) => problem.Path is not null && Shown switch
    {
        ProblemsTab.Toolset => false,
        ProblemsTab.Bindings => bindings.Any(b => b.Problem == problem && b.Failure.Nearest.Count > 0),
        _ => true,
    };

    /// <summary>A problem gone to, as a double-click, Enter or Go to does.</summary>
    internal void GoTo(Problem problem) => Chosen?.Invoke(this, problem);

    /// <summary>A problem's fixes asked for, from its row's menu.</summary>
    internal void Fix(Problem problem) => FixRequested?.Invoke(this, problem);

    /// <summary>A problem's link opened, from its row.</summary>
    internal void Open(Uri link) => LinkClicked?.Invoke(this, link);

    /// <summary>A tab shown, the first of its problems <paramref name="which"/> picks chosen and scrolled to, as the status
    /// bar's link to it goes there.</summary>
    public void Reveal(ProblemsTab tab, Func<Problem, bool>? which = null)
    {
        Shown = tab;
        if (which is not null && rows.FirstOrDefault(r => r.Problem is { } problem && which(problem)) is { } row)
        {
            chosen = row;
            ChosenRowChanged?.Invoke(this, true);
        }
    }

    /// <summary>The tab shown.</summary>
    public ProblemsTab Shown
    {
        get;
        set
        {
            field = (value == ProblemsTab.Solution && solution is null) || (value == ProblemsTab.HotReload && hotReload is null) ? ProblemsTab.File : value;
            Refresh();
        }
    }

    /// <summary>The last build's problems gone with their folder, the tab shown as it was.</summary>
    public void ForgetBuild()
    {
        Problems.Clear();
        Refresh();
    }

    /// <summary>The last build's problems, in the Build tab, shown.</summary>
    public void Show(IEnumerable<Problem> problems)
    {
        Problems.Clear();
        foreach (Problem problem in Ordered(problems))
        {
            Problems.Add(problem);
        }
        Shown = ProblemsTab.Build;
    }

    /// <summary>The current file's problems, as they are typed: the same problems moved by a key move their rows, which
    /// keep their place in the list, rather than every row being made again; another tab shown is left as it is but for
    /// the File tab's title.</summary>
    public void ShowFile(string? name, IReadOnlyList<Problem> problems)
    {
        Problem[] ordered = Ordered(problems);
        if (name == fileName && Moved(file, ordered))
        {
            file = ordered;
            if (Shown == ProblemsTab.File)
            {
                for (int i = 0; i < ordered.Length; i++)
                {
                    rows[i].MoveTo(ordered[i]);
                }
            }
            return;
        }
        fileName = name;
        file = ordered;
        if (Shown != ProblemsTab.File)
        {
            TabsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        Refresh();
    }

    /// <summary>Whether two lists are the same problems, in order, some perhaps in another place.</summary>
    private static bool Moved(IReadOnlyList<Problem> before, IReadOnlyList<Problem> after)
    {
        if (before.Count != after.Count)
        {
            return false;
        }
        for (int i = 0; i < before.Count; i++)
        {
            if (before[i] != after[i] with { Position = before[i].Position })
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The solution's problems, or null to take the tab away when solution-wide analysis goes off.</summary>
    public void ShowSolution(IReadOnlyList<Problem>? problems)
    {
        solution = problems is null ? null : ByFile(problems);
        solutionOrdered = null;
        solutionCount = problems?.Count ?? 0;
        solutionErrors = problems?.Count(p => p.Severity == ProblemSeverity.Error) ?? 0;
        if (solution is null && Shown == ProblemsTab.Solution)
        {
            Shown = ProblemsTab.File;
            return;
        }
        Refresh();
    }

    /// <summary>One file's problems in the solution's, as the analysis or the file's own pull changed them: the Solution tab
    /// makes that file's rows again, or moves them when they are the same problems in other places, the rest left as they
    /// are, so a typing pause costs what it changed however many problems the solution has.</summary>
    public void ShowSolutionFile(string path, IReadOnlyList<Problem> problems)
    {
        if (solution is null)
        {
            return;
        }
        Problem[] fresh = Ordered(problems);
        Problem[] old = solution.GetValueOrDefault(path) ?? [];
        if (old.AsSpan().SequenceEqual(fresh))
        {
            return;
        }
        if (fresh.Length > 0)
        {
            solution[path] = fresh;
        }
        else
        {
            solution.Remove(path);
        }
        solutionOrdered = null;
        solutionCount += fresh.Length - old.Length;
        solutionErrors += fresh.Count(p => p.Severity == ProblemSeverity.Error) - old.Count(p => p.Severity == ProblemSeverity.Error);
        if (Shown != ProblemsTab.Solution)
        {
            TabsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (old.Length > 0 && Moved(old, fresh) && FileRowOf(path) is int at and >= 0)
        {
            for (int i = 0; i < fresh.Length; i++)
            {
                rows[at + 1 + i].MoveTo(fresh[i]);
            }
            return;
        }
        if (old.Length > 0)
        {
            RowsOut(path, ProjectOf(old[0]));
        }
        if (fresh.Length > 0)
        {
            RowsIn(path, ProjectOf(fresh[0]), fresh);
        }
        Forget();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The project a problem is listed under in the Solution tab.</summary>
    private static string ProjectOf(Problem problem) => problem.Project ?? "Solution";

    /// <summary>Where a file's row is in the Solution tab; -1 when it has none.</summary>
    private int FileRowOf(string path)
    {
        for (int i = 0; i + 1 < rows.Count; i++)
        {
            if (rows[i] is { Depth: 1, Problem: null } && rows[i + 1].Problem?.Path is { } at && at == path)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The project's row in the Solution tab, or where it goes among the others, and whether it is there.</summary>
    private (int At, bool There) ProjectRow(string project)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Depth == 0 && string.CompareOrdinal(rows[i].Label, project) is int order and >= 0)
            {
                return (i, order == 0);
            }
        }
        return (rows.Count, false);
    }

    /// <summary>A file's row among its project's, or where it goes among them, and whether it is there.</summary>
    private (int At, bool There) FileRow(string path, int project)
    {
        int i = project + 1;
        for (; i < rows.Count && rows[i].Depth != 0; i++)
        {
            if (rows[i] is { Depth: 1, Problem: null } && i + 1 < rows.Count && rows[i + 1].Problem?.Path is { } at
                && string.CompareOrdinal(at, path) is int order and >= 0)
            {
                return (i, order == 0);
            }
        }
        return (i, false);
    }

    /// <summary>A file's rows taken out of the Solution tab, and its project's row with them once it has none left.</summary>
    private void RowsOut(string path, string project)
    {
        (int projectAt, bool listed) = ProjectRow(project);
        (int fileAt, bool there) = listed ? FileRow(path, projectAt) : (0, false);
        if (!there)
        {
            return;
        }
        int end = fileAt + 1;
        while (end < rows.Count && rows[end].Depth == 2)
        {
            end++;
        }
        rows.RemoveRange(fileAt, end - fileAt);
        Recount(projectAt, project);
    }

    /// <summary>A file's rows put in the Solution tab in their place under its project's, made for it if need be.</summary>
    private void RowsIn(string path, string project, Problem[] problems)
    {
        (int projectAt, bool listed) = ProjectRow(project);
        if (!listed)
        {
            rows.Insert(projectAt, new ProblemRow(0, project, null, null));
        }
        (int fileAt, _) = FileRow(path, projectAt);
        List<ProblemRow> made = new(problems.Length + 1) { new ProblemRow(1, System.IO.Path.GetFileName(path), Count(problems.Length), null) };
        foreach (Problem problem in problems)
        {
            made.Add(new ProblemRow(2, problem.Message, problem.Where, problem));
        }
        rows.InsertRange(fileAt, made);
        Recount(projectAt, project);
    }

    /// <summary>A project's row in the Solution tab made to say how many problems its files have, or taken out with none.</summary>
    private void Recount(int projectAt, string project)
    {
        int count = 0;
        for (int i = projectAt + 1; i < rows.Count && rows[i].Depth != 0; i++)
        {
            count += rows[i].Problem is null ? 0 : 1;
        }
        if (count == 0)
        {
            rows.RemoveAt(projectAt);
        }
        else if (rows[projectAt].Detail != Count(count))
        {
            rows[projectAt] = new ProblemRow(0, project, Count(count), null);
        }
    }

    /// <summary>The container engine's problems, in Containers, its tab there while it has any.</summary>
    public void ShowContainers(IReadOnlyList<Problem> problems)
    {
        containers = Ordered(problems);
        Refresh();
    }

    /// <summary>What the engine finds in the open Dockerfiles and compose files (BuildKit's checks, Compose's errors), by
    /// file in Containers after the engine's own.</summary>
    public void ShowContainerFiles(IReadOnlyList<Problem> problems)
    {
        containerFiles = Ordered(problems);
        Refresh();
    }

    /// <summary>The container engine's problems, then those it finds in the open files.</summary>
    public IReadOnlyList<Problem> ContainerProblems => [.. containers, .. containerFiles];

    /// <summary>The projects' load problems, in Toolset, Environment.</summary>
    public void ShowToolset(IReadOnlyList<Problem> problems)
    {
        toolset = Ordered(problems);
        Refresh();
    }

    /// <summary>The failed bindings, in Bindings, as often as their counts change, with what the apps could not tell of theirs
    /// under them (<see cref="BindingErrors.Untold"/>): the row chosen stays chosen, and another tab shown is left as it is
    /// but for the count on Bindings' own.</summary>
    public void ShowBindings(IReadOnlyList<BindingProblem> problems, IReadOnlyList<(string Said, string Why)> untold)
    {
        bindings = [.. problems.OrderBy(b => b.Problem.Severity).ThenBy(b => b.Problem.Path, StringComparer.Ordinal)
            .ThenBy(b => b.Problem.Position.Line).ThenBy(b => b.Problem.Position.Column).ThenBy(b => b.Failure.Id)];
        bindingsUntold = untold;
        if (Shown != ProblemsTab.Bindings)
        {
            TabsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        Problem? before = chosen?.Problem;
        Refresh();
        if (before is not null && rows.FirstOrDefault(r => r.Problem == before) is { } again)
        {
            ChosenRow = again;
        }
    }

    /// <summary>Problems by severity, file, line and column, those alike in the order they came, in one sort.</summary>
    private static Problem[] Ordered(IEnumerable<Problem> problems)
    {
        Problem[] listed = [.. problems];
        if (listed.Length < 2)
        {
            return listed;
        }
        (Problem Problem, int Came)[] keyed = new (Problem, int)[listed.Length];
        for (int i = 0; i < listed.Length; i++)
        {
            keyed[i] = (listed[i], i);
        }
        Array.Sort(keyed, ByPlace);
        for (int i = 0; i < keyed.Length; i++)
        {
            listed[i] = keyed[i].Problem;
        }
        return listed;
    }

    /// <summary>Problems by severity, file, line and column, then the order they came.</summary>
    private static readonly Comparison<(Problem Problem, int Came)> ByPlace = static (a, b) =>
    {
        int order = a.Problem.Severity.CompareTo(b.Problem.Severity);
        order = order != 0 ? order : string.CompareOrdinal(a.Problem.Path, b.Problem.Path);
        order = order != 0 ? order : a.Problem.Position.Line.CompareTo(b.Problem.Position.Line);
        order = order != 0 ? order : a.Problem.Position.Column.CompareTo(b.Problem.Position.Column);
        return order != 0 ? order : a.Came.CompareTo(b.Came);
    };

    /// <summary>The solution's problems by file, each file's in order.</summary>
    private static Dictionary<string, Problem[]> ByFile(IReadOnlyList<Problem> problems)
    {
        Dictionary<string, List<Problem>> found = new(Core.PathNames.Comparer);
        foreach (Problem problem in problems)
        {
            string path = problem.Path ?? string.Empty;
            if (!found.TryGetValue(path, out List<Problem>? listed))
            {
                found[path] = listed = [];
            }
            listed.Add(problem);
        }
        Dictionary<string, Problem[]> byFile = new(found.Count, Core.PathNames.Comparer);
        foreach ((string path, List<Problem> listed) in found)
        {
            byFile[path] = Ordered(listed);
        }
        return byFile;
    }

    /// <summary>The row chosen let go once it is no longer listed, as the list lets go of an item taken out of it.</summary>
    private void Forget()
    {
        if (chosen is not null && !rows.Contains(chosen))
        {
            ChosenRow = null;
        }
    }

    /// <summary>The shown tab's rows made again: another tab's replace them all, unchosen; the same tab's keep the rows
    /// they make alike, and the row chosen while it is one of them.</summary>
    private void Refresh()
    {
        List<ProblemRow> made = [];
        bool again = rowsOf == Shown;
        if (!again)
        {
            ChosenRow = null;
        }
        switch (Shown)
        {
            case ProblemsTab.File:
                foreach (Problem problem in file)
                {
                    made.Add(new ProblemRow(0, problem.Message, problem.Where, problem));
                }
                EmptyText = fileName is null ? Refusals.NoFile : "No problems in this file";
                break;
            case ProblemsTab.Solution:
                Grouped(made, SolutionProblems ?? []);
                EmptyText = "No problems in the solution";
                break;
            case ProblemsTab.Toolset:
                foreach (IGrouping<string, Problem> project in toolset.GroupBy(p => p.Project ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    made.Add(new ProblemRow(0, project.Key, Count(project.Count()), null));
                    foreach (Problem problem in project)
                    {
                        made.Add(new ProblemRow(1, problem.Message, null, problem));
                    }
                }
                EmptyText = "The projects loaded without problems";
                break;
            case ProblemsTab.Bindings:
                foreach (IGrouping<string, BindingProblem> file in bindings.GroupBy(b => b.Problem.Path ?? string.Empty)
                    .OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key, StringComparer.Ordinal))
                {
                    made.Add(new ProblemRow(0, file.Key.Length > 0 ? System.IO.Path.GetFileName(file.Key) : "Not from XAML", Count(file.Count()), null));
                    foreach (BindingProblem binding in file)
                    {
                        made.Add(new ProblemRow(1, binding.Problem.Message, binding.Detail, binding.Problem));
                    }
                }
                foreach ((string said, string why) in bindingsUntold)
                {
                    made.Add(new ProblemRow(0, said, why, null));
                }
                EmptyText = "No failed bindings: those of an Avalonia or WPF app run or debugged here are listed as they fail";
                break;
            case ProblemsTab.HotReload:
                made.Add(new ProblemRow(0, hotReloadSaid, null, null));
                Grouped(made, hotReload ?? []);
                EmptyText = "Hot Reload applied every change";
                break;
            case ProblemsTab.Containers:
                foreach (Problem problem in containers)
                {
                    made.Add(new ProblemRow(0, problem.Message, null, problem));
                }
                foreach (IGrouping<string, Problem> path in containerFiles.GroupBy(p => p.Path ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    made.Add(new ProblemRow(0, System.IO.Path.GetFileName(path.Key), Count(path.Count()), null));
                    foreach (Problem problem in path)
                    {
                        made.Add(new ProblemRow(1, problem.Message, problem.Where, problem));
                    }
                }
                EmptyText = "The container engine has no problems";
                break;
            default:
                Grouped(made, Problems);
                EmptyText = "No problems from the last build";
                break;
        }
        rowsOf = Shown;
        if (again)
        {
            rows.Become(made);
            Forget();
        }
        else
        {
            rows.ReplaceAll(made);
        }
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Count(int count) => Core.Plural.Of(count, "problem");

    /// <summary>Problems under their projects and files, each group in its key's order and its problems as they came.</summary>
    private static void Grouped(List<ProblemRow> made, IEnumerable<Problem> problems)
    {
        (Problem Problem, int Came)[] keyed = [.. problems.Select(static (p, i) => (p, i))];
        Array.Sort(keyed, ByGroup);
        int at = 0;
        while (at < keyed.Length)
        {
            string project = GroupOf(keyed[at].Problem);
            int projectEnd = at;
            while (projectEnd < keyed.Length && GroupOf(keyed[projectEnd].Problem) == project)
            {
                projectEnd++;
            }
            made.Add(new ProblemRow(0, project, Count(projectEnd - at), null));
            while (at < projectEnd)
            {
                string path = keyed[at].Problem.Path ?? string.Empty;
                int pathEnd = at;
                while (pathEnd < projectEnd && (keyed[pathEnd].Problem.Path ?? string.Empty) == path)
                {
                    pathEnd++;
                }
                if (path.Length > 0)
                {
                    made.Add(new ProblemRow(1, System.IO.Path.GetFileName(path), Count(pathEnd - at), null));
                }
                for (; at < pathEnd; at++)
                {
                    Problem problem = keyed[at].Problem;
                    made.Add(new ProblemRow(path.Length > 0 ? 2 : 1, problem.Message, problem.Where, problem));
                }
            }
        }
    }

    /// <summary>The project a problem is grouped under.</summary>
    private static string GroupOf(Problem problem) => problem.Project ?? "Solution";

    /// <summary>Problems by project, then file, then the order they came.</summary>
    private static readonly Comparison<(Problem Problem, int Came)> ByGroup = static (a, b) =>
    {
        int order = string.CompareOrdinal(GroupOf(a.Problem), GroupOf(b.Problem));
        order = order != 0 ? order : string.CompareOrdinal(a.Problem.Path ?? string.Empty, b.Problem.Path ?? string.Empty);
        return order != 0 ? order : a.Came.CompareTo(b.Came);
    };
}

/// <summary>Problems' rows, a file's put in or taken out as one change, and a tab's made again as one, which the list
/// hears once rather than row by row.</summary>
internal sealed class ProblemRows : ObservableCollection<ProblemRow>
{
    private List<ProblemRow> All => (List<ProblemRow>)Items;

    public void InsertRange(int index, List<ProblemRow> added)
    {
        CheckReentrancy();
        All.InsertRange(index, added);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, index));
    }

    public void RemoveRange(int index, int count)
    {
        CheckReentrancy();
        List<ProblemRow> removed = All.GetRange(index, count);
        All.RemoveRange(index, count);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    public void ReplaceAll(List<ProblemRow> made)
    {
        CheckReentrancy();
        All.Clear();
        All.AddRange(made);
        Changed(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>The rows made again: those the same at the start and the end are kept, each taking its new detail, and only
    /// the rows between are taken out and put in, so a report that changed nothing tells the list nothing and the list goes
    /// on showing each row kept where it was.</summary>
    public void Become(List<ProblemRow> made)
    {
        CheckReentrancy();
        int start = 0;
        while (start < Count && start < made.Count && All[start].IsSame(made[start]))
        {
            start++;
        }
        int end = 0;
        while (end < Count - start && end < made.Count - start && All[Count - 1 - end].IsSame(made[made.Count - 1 - end]))
        {
            end++;
        }
        int gone = Count - start - end;
        int come = made.Count - start - end;
        if (start + end == 0 && (gone > 0 || come > 0))
        {
            ReplaceAll(made);
            return;
        }
        for (int i = 0; i < start; i++)
        {
            All[i].Retell(made[i].Detail);
        }
        for (int i = 1; i <= end; i++)
        {
            All[Count - i].Retell(made[made.Count - i].Detail);
        }
        if (gone > 0)
        {
            RemoveRange(start, gone);
        }
        if (come > 0)
        {
            InsertRange(start, made.GetRange(start, come));
        }
    }

    private void Changed(NotifyCollectionChangedEventArgs change)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(change);
    }
}
