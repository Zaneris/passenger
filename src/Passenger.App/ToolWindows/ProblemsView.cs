using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.LiveTree;
using Passenger.App.Run;

namespace Passenger.App.ToolWindows;

/// <summary>Which of Problems' tabs: the current file's, the solution's (with solution-wide analysis on), the projects'
/// load problems (Rider's Toolset, Environment), the last build's, or the bindings that failed in the Avalonia apps run and
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
}

/// <summary>Rider's Problems window: the current file's problems as they are typed, the whole solution's with
/// solution-wide analysis on (by project, then file), the warnings and errors the projects loaded with (Rider's Toolset,
/// Environment, by project), the last build's apart from them, and the bindings that failed in a running Avalonia app (by
/// XAML file, each once with its control, property, path and count), each a tab with its count. Errors come before
/// warnings; a double-click or Enter goes to a problem, and the web address in its message is a link.</summary>
public sealed class ProblemsView : DockPanel
{
    private readonly ObservableCollection<ProblemRow> rows = [];
    private readonly ListBox list;
    private readonly TextBlock empty;
    private readonly Dictionary<ProblemsTab, ToggleButton> tabs = [];
    private IReadOnlyList<Problem> file = [];
    private IReadOnlyList<Problem>? solution;
    private IReadOnlyList<Problem> toolset = [];
    private IReadOnlyList<BindingProblem> bindings = [];
    private IReadOnlyList<Problem>? hotReload;
    private string hotReloadSaid = string.Empty;
    private string? fileName;

    public ProblemsView()
    {
        StackPanel strip = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (ProblemsTab tab in Enum.GetValues<ProblemsTab>())
        {
            ToggleButton button = new() { Classes = { "panelTab" } };
            button.Click += (_, _) => Shown = tab;
            tabs[tab] = button;
            strip.Children.Add(button);
        }
        Border bar = new() { [DockProperty] = Dock.Top, Height = 32, BorderThickness = new Thickness(0, 0, 0, 1), Child = strip };
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable("Passenger.Divider"));
        Children.Add(bar);
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<ProblemRow>((row, _) => Row(row), supportsRecycling: false),
        };
        empty = new TextBlock { Classes = { "emptyHint" } };
        Children.Add(new Panel { Children = { empty, list } });
        list.DoubleTapped += (_, _) => Choose();
        list.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Choose();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ListMenus.Attach(list, () => list.SelectedItem is not ProblemRow { Problem: { } problem } ? [] :
        [
            ListMenus.Item("Go to", "F4", Choose, problem.Path is not null),
            ListMenus.Item("Copy Message", null, () => _ = ListMenus.CopyAsync(this, problem.Message)),
            ListMenus.Item("Open Link", null, () => LinkClicked?.Invoke(this, problem.Link!), problem.Link is not null),
            new Separator(),
            ListMenus.Item("Show Context Actions", null, () => FixRequested?.Invoke(this, problem), problem.Path is not null && Shown switch
            {
                ProblemsTab.Toolset => false,
                // A failed binding's fixes are the nearest members of its source's type, when it has any.
                ProblemsTab.Bindings => bindings.Any(b => b.Problem == problem && b.Failure.Nearest.Count > 0),
                _ => true,
            }),
        ]);
        Refresh();
    }

    /// <summary>A problem's fixes asked for (Rider's Alt+Enter on it): the editor goes there and offers them.</summary>
    public event EventHandler<Problem>? FixRequested;

    /// <summary>A problem chosen to go to.</summary>
    public event EventHandler<Problem>? Chosen;

    /// <summary>A problem's link clicked: the advisory or page its message names, for the browser.</summary>
    public event EventHandler<Uri>? LinkClicked;

    /// <summary>The last build's problems.</summary>
    public ObservableCollection<Problem> Problems { get; } = [];

    /// <summary>The current file's problems.</summary>
    public IReadOnlyList<Problem> FileProblems => file;

    /// <summary>The solution's problems, or null with solution-wide analysis off.</summary>
    public IReadOnlyList<Problem>? SolutionProblems => solution;

    /// <summary>The warnings and errors the projects loaded with.</summary>
    public IReadOnlyList<Problem> ToolsetProblems => toolset;

    /// <summary>The bindings that failed in the apps run and debugged last, each once.</summary>
    public IReadOnlyList<BindingProblem> BindingProblems => bindings;

    /// <summary>The edits the last Hot Reload could not apply or warned of; null before one in this window.</summary>
    public IReadOnlyList<Problem>? HotReloadProblems => hotReload;

    /// <summary>The rows of the tab shown.</summary>
    public IReadOnlyList<ProblemRow> Rows => rows;

    /// <summary>What the last Hot Reload came to (its words, and its edits by file at their lines), in the Hot Reload tab,
    /// shown when it has problems.</summary>
    public void ShowHotReload(string said, IReadOnlyList<Problem> problems)
    {
        hotReload = [.. Ordered(problems)];
        hotReloadSaid = said;
        if (problems.Count > 0)
        {
            Shown = ProblemsTab.HotReload;
            return;
        }
        Refresh();
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
    /// keep their place in the list, rather than every row being made again.</summary>
    public void ShowFile(string? name, IReadOnlyList<Problem> problems)
    {
        Problem[] ordered = [.. Ordered(problems)];
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
        solution = problems is null ? null : [.. Ordered(problems)];
        if (solution is null && Shown == ProblemsTab.Solution)
        {
            Shown = ProblemsTab.File;
            return;
        }
        Refresh();
    }

    /// <summary>The projects' load problems, in Toolset, Environment.</summary>
    public void ShowToolset(IReadOnlyList<Problem> problems)
    {
        toolset = [.. Ordered(problems)];
        Refresh();
    }

    /// <summary>The failed bindings, in Bindings, as often as their counts change: the row chosen stays chosen, and another
    /// tab shown is left as it is but for the count on Bindings' own.</summary>
    public void ShowBindings(IReadOnlyList<BindingProblem> problems)
    {
        bindings = [.. problems.OrderBy(b => b.Problem.Severity).ThenBy(b => b.Problem.Path, StringComparer.Ordinal)
            .ThenBy(b => b.Problem.Position.Line).ThenBy(b => b.Problem.Position.Column).ThenBy(b => b.Failure.Id)];
        if (Shown != ProblemsTab.Bindings)
        {
            Titles();
            return;
        }
        Problem? chosen = (list.SelectedItem as ProblemRow)?.Problem;
        Refresh();
        if (chosen is not null && rows.FirstOrDefault(r => r.Problem == chosen) is { } again)
        {
            list.SelectedItem = again;
        }
    }

    private static IEnumerable<Problem> Ordered(IEnumerable<Problem> problems) =>
        problems.OrderBy(p => p.Severity).ThenBy(p => p.Path, StringComparer.Ordinal).ThenBy(p => p.Position.Line).ThenBy(p => p.Position.Column);

    /// <summary>Each tab's title with its count, the one shown pressed.</summary>
    private void Titles()
    {
        tabs[ProblemsTab.File].Content = Titled(fileName is null ? "File" : $"File: {fileName}", file.Count);
        tabs[ProblemsTab.Solution].Content = Titled("Solution", solution?.Count ?? 0);
        tabs[ProblemsTab.Solution].IsVisible = solution is not null;
        tabs[ProblemsTab.Toolset].Content = Titled("Toolset, Environment", toolset.Count);
        tabs[ProblemsTab.Build].Content = Titled("Build", Problems.Count);
        tabs[ProblemsTab.Bindings].Content = Titled("Bindings", bindings.Count);
        tabs[ProblemsTab.HotReload].Content = Titled("Hot Reload", hotReload?.Count ?? 0);
        tabs[ProblemsTab.HotReload].IsVisible = hotReload is not null;
        foreach ((ProblemsTab tab, ToggleButton button) in tabs)
        {
            button.IsChecked = tab == Shown;
        }

        static string Titled(string title, int count) => count == 0 ? title : $"{title}  {count}";
    }

    private void Refresh()
    {
        Titles();
        rows.Clear();
        switch (Shown)
        {
            case ProblemsTab.File:
                foreach (Problem problem in file)
                {
                    rows.Add(new ProblemRow(0, problem.Message, problem.Where, problem));
                }
                empty.Text = fileName is null ? "No file is open" : "No problems in this file";
                break;
            case ProblemsTab.Solution:
                Grouped(solution ?? []);
                empty.Text = "No problems in the solution";
                break;
            case ProblemsTab.Toolset:
                foreach (IGrouping<string, Problem> project in toolset.GroupBy(p => p.Project ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    rows.Add(new ProblemRow(0, project.Key, Count(project.Count()), null));
                    foreach (Problem problem in project)
                    {
                        rows.Add(new ProblemRow(1, problem.Message, null, problem));
                    }
                }
                empty.Text = "The projects loaded without problems";
                break;
            case ProblemsTab.Bindings:
                foreach (IGrouping<string, BindingProblem> file in bindings.GroupBy(b => b.Problem.Path ?? string.Empty)
                    .OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key, StringComparer.Ordinal))
                {
                    rows.Add(new ProblemRow(0, file.Key.Length > 0 ? System.IO.Path.GetFileName(file.Key) : "Not from XAML", Count(file.Count()), null));
                    foreach (BindingProblem binding in file)
                    {
                        rows.Add(new ProblemRow(1, binding.Problem.Message, binding.Detail, binding.Problem));
                    }
                }
                empty.Text = "No failed bindings: those of an Avalonia or WPF app run or debugged here are listed as they fail";
                break;
            case ProblemsTab.HotReload:
                rows.Add(new ProblemRow(0, hotReloadSaid, null, null));
                Grouped(hotReload ?? []);
                empty.Text = "Hot Reload applied every change";
                break;
            default:
                Grouped(Problems);
                empty.Text = "No problems from the last build";
                break;
        }
        empty.IsVisible = rows.Count == 0;
    }

    private static string Count(int count) => count == 1 ? "1 problem" : $"{count} problems";

    /// <summary>Problems under their projects and files, as Rider groups the solution's.</summary>
    private void Grouped(IEnumerable<Problem> problems)
    {
        foreach (IGrouping<string, Problem> project in problems.GroupBy(p => p.Project ?? "Solution").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            rows.Add(new ProblemRow(0, project.Key, Count(project.Count()), null));
            foreach (IGrouping<string, Problem> path in project.GroupBy(p => p.Path ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (path.Key.Length > 0)
                {
                    rows.Add(new ProblemRow(1, System.IO.Path.GetFileName(path.Key), Count(path.Count()), null));
                }
                foreach (Problem problem in path)
                {
                    rows.Add(new ProblemRow(path.Key.Length > 0 ? 2 : 1, problem.Message, problem.Where, problem));
                }
            }
        }
    }

    private void Choose()
    {
        if (list.SelectedItem is ProblemRow { Problem: { } problem })
        {
            Chosen?.Invoke(this, problem);
        }
    }

    private Control Row(ProblemRow? row)
    {
        if (row is null)
        {
            return new TextBlock();
        }
        Thickness margin = new(8 + (row.Depth * 16), 0, 8, 0);
        TextBlock detail = new() { Text = row.Detail, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        detail.Bind(TextBlock.ForegroundProperty, detail.GetResourceObservable("Passenger.TextMuted"));
        if (row.Problem is not { } problem)
        {
            TextBlock title = new()
            {
                Text = row.Label, FontWeight = row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(title, row.Label);
            Grid group = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 24, Margin = margin };
            Grid.SetColumn(detail, 1);
            group.Children.Add(title);
            group.Children.Add(detail);
            return group;
        }
        Grid panel = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Height = 24, Margin = margin };
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(detail, 2);
        panel.SizeChanged += (_, _) => detail.MaxWidth = Math.Max(0, panel.Bounds.Width * 0.3);
        PathIcon icon = new() { Margin = new Thickness(0, 0, 8, 0) };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(problem.Severity == ProblemSeverity.Error ? "Icon.Error" : "Icon.Problems"));
        icon.Bind(PathIcon.ForegroundProperty, icon.GetResourceObservable(problem.Severity == ProblemSeverity.Error ? "Passenger.Editor.Error" : "Passenger.Editor.Warning"));
        void Where()
        {
            detail.Text = problem.Code is { Length: > 0 } code ? $"{row.Detail}  {code}" : row.Detail;
            detail.IsVisible = !string.IsNullOrEmpty(detail.Text);
            ToolTip.SetTip(detail, detail.Text);
        }
        Where();
        // Followed only while shown, so a row let go of by the list holds nothing it showed.
        void OnMoved(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Where();
        panel.AttachedToVisualTree += (_, _) =>
        {
            row.PropertyChanged += OnMoved;
            Where();
        };
        panel.DetachedFromVisualTree += (_, _) => row.PropertyChanged -= OnMoved;
        panel.Children.Add(icon);
        // A message of several lines (a compiler's stack trace) shows its first in the row and all of them on hover.
        string label = row.Label;
        ToolTip.SetTip(panel, row.Label);
        if (label.IndexOf('\n') is int lineEnd and >= 0)
        {
            label = label[..lineEnd].TrimEnd('\r');
        }
        // A rule's name the message ends with is its link (an ESLint problem's), else the address where the message has it.
        int at = problem.Link is not { } named ? -1
            : problem.LinkText is { } words ? label.LastIndexOf(words, StringComparison.Ordinal)
            : label.IndexOf(named.OriginalString, StringComparison.Ordinal);
        if (problem.Link is not { } link || at < 0)
        {
            TextBlock message = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = 1 };
            ToolTip.SetTip(message, row.Label);
            panel.Children.Add(message);
        }
        else
        {
            // The link's words in the link colour, the words around them as they are.
            string address = problem.LinkText ?? link.OriginalString;
            Button open = new() { Classes = { "link" }, Content = address, VerticalAlignment = VerticalAlignment.Center };
            open.ContentTemplate = new FuncDataTemplate<string>((text, _) => new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTip.SetTip(open, $"Open {link.OriginalString} in the browser");
            open.Click += (_, e) =>
            {
                LinkClicked?.Invoke(this, link);
                e.Handled = true;
            };
            string after = label[(at + address.Length)..];
            Grid message = new()
            {
                ColumnDefinitions = new ColumnDefinitions($"{(at > 0 ? "*" : "0")},Auto,{(after.Length > 0 ? "*" : "0")}"),
                HorizontalAlignment = HorizontalAlignment.Left,
                [Grid.ColumnProperty] = 1,
                Children =
                {
                    new TextBlock { Text = label[..at], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    open,
                    new TextBlock { Text = after, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, [Grid.ColumnProperty] = 2 },
                },
            };
            Grid.SetColumn(open, 1);
            panel.SizeChanged += (_, _) => open.MaxWidth = Math.Max(0, (panel.Bounds.Width - detail.MaxWidth - icon.DesiredSize.Width - detail.Margin.Left) * (at > 0 || after.Length > 0 ? 0.55 : 1));
            panel.Children.Add(message);
        }
        panel.Children.Add(detail);
        return panel;
    }
}
