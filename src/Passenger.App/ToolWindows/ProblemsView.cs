using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Run;

namespace Passenger.App.ToolWindows;

/// <summary>The Problems window over its <see cref="ProblemsModel"/>, built as the window is first shown: a tab for each
/// list with its count, the shown tab's rows, errors before warnings and messages, and on the right a toggle for each
/// severity with its count and the filter by text; a double-click or Enter goes to a problem, and the web address in its
/// message is a link.</summary>
public sealed class ProblemsView : DockPanel
{
    private static readonly (ProblemSeverity Severity, string Name, string Icon, string Brush)[] Severities =
    [
        (ProblemSeverity.Error, "Errors", IconKeys.Error, TokenKeys.Error),
        (ProblemSeverity.Warning, "Warnings", IconKeys.Problems, TokenKeys.Warning),
        (ProblemSeverity.Message, "Messages", IconKeys.Info, TokenKeys.Editor_Info),
    ];

    private readonly ProblemsModel model;
    private readonly ListBox list;
    private readonly TextBlock empty;
    private readonly TextBox search;
    private readonly Dictionary<ProblemsTab, ToggleButton> tabs = [];
    private readonly Dictionary<ProblemSeverity, (ToggleButton Button, TextBlock Count)> severities = [];
    private bool following;

    public ProblemsView(ProblemsModel model)
    {
        this.model = model;
        ToolFlow bar = new() { Margin = new Thickness(8, 2) };
        foreach (ProblemsTab tab in Enum.GetValues<ProblemsTab>())
        {
            ToggleButton button = new() { Classes = { StyleClasses.PanelTab } };
            button.Click += (_, _) => model.Shown = tab;
            tabs[tab] = button;
            bar.Children.Add(button);
        }
        search = FilterBox.Of();
        ToolFlow.SetFills(search, new ToolLayout.Fills(160, 200));
        search.Text = model.Filter.Text;
        search.TextChanged += (_, _) => model.Filter = model.Filter with { Text = (search.Text ?? string.Empty).Trim() };
        search.KeyDown += OnSearchKey;
        StackPanel shown = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach ((ProblemSeverity severity, string name, string icon, string brush) in Severities)
        {
            ToggleButton button = ToolButtons.Counted(icon, brush, $"Show {name}", out TextBlock count);
            button.Click += (_, _) => model.Filter = model.Filter.Showing(severity, button.IsChecked == true);
            severities[severity] = (button, count);
            shown.Children.Add(button);
        }
        bar.Children.AddRange([ToolFlow.Spring(), shown, search]);
        Border topBar = ToolBars.Top(bar);
        list = new ListBox
        {
            Classes = { StyleClasses.Tree },
            ItemsSource = model.ShownRows,
        };
        ReusedRows.Use(list, Row);
        empty = new TextBlock { Classes = { StyleClasses.EmptyText } };
        // The list comes first among the parts, so the window's action gives it the keys and Find's give them the filter.
        Grid.SetRow(topBar, 0);
        Children.Add(new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Children = { new Panel { [Grid.RowProperty] = 1, Children = { empty, list } }, topBar } });
        list.DoubleTapped += (_, _) => Choose();
        list.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Choose();
                e.Handled = true;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        list.SelectionChanged += (_, _) =>
        {
            if (!following)
            {
                model.ChosenRow = list.SelectedItem as ProblemRow;
            }
        };
        ListMenus.Attach(list, () => list.SelectedItem is not ProblemRow { Problem: { } problem } ? [] :
        [
            ListMenus.Item("Go to", "F4", Choose, problem.Path is not null),
            ListMenus.Item("Copy Message", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, problem.Message))),
            ListMenus.Item("Open Link", null, () => model.Open(problem.Link!), problem.Link is not null),
            new Separator(),
            ListMenus.Item("Show Context Actions", null, () => model.Fix(problem), model.HasFixes(problem)),
        ]);
        model.TabsChanged += (_, _) => Titles();
        model.ChosenRowChanged += (_, reveal) => Follow(reveal);
        Titles();
        Follow(reveal: false);
        // A row chosen before the view was built (the status bar's link to it) is scrolled to once it is laid out.
        AttachedToVisualTree += (_, _) =>
        {
            if (model.ChosenRow is { } row)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => list.ScrollIntoView(row));
            }
        };
    }

    /// <summary>The list's choice made the model's, scrolled to when the model reveals it.</summary>
    private void Follow(bool reveal)
    {
        following = true;
        try
        {
            list.SelectedItem = model.ChosenRow;
        }
        finally
        {
            following = false;
        }
        if (reveal && model.ChosenRow is { } row)
        {
            list.ScrollIntoView(row);
        }
    }

    /// <summary>Each tab's title with its count, the one shown pressed, and the empty list's words.</summary>
    private void Titles()
    {
        foreach ((ProblemsTab tab, ToggleButton button) in tabs)
        {
            button.Content = model.TitleWithCount(tab);
            button.IsVisible = model.HasTab(tab);
            button.IsChecked = tab == model.Shown;
        }
        empty.Text = model.EmptyText;
        empty.IsVisible = model.Rows.Count == 0;
        // The filter taken away for a problem revealed.
        if ((search.Text ?? string.Empty).Trim() != model.Filter.Text)
        {
            search.Text = model.Filter.Text;
        }
        (int errors, int warnings, int messages) = model.SeverityCounts();
        foreach ((ProblemSeverity severity, (ToggleButton button, TextBlock count)) in severities)
        {
            count.Text = (severity switch { ProblemSeverity.Error => errors, ProblemSeverity.Warning => warnings, _ => messages })
                .ToString(System.Globalization.CultureInfo.CurrentCulture);
            button.IsChecked = model.Filter.Shows(severity);
        }
    }

    /// <summary>Gives the filter the keys, its text chosen: Find's keys while Problems has them.</summary>
    public void FocusSearch()
    {
        search.Focus();
        search.SelectAll();
    }

    /// <summary>The filter's keys: Escape empties it, then gives the list the keys, as Down and Enter do, on the row chosen
    /// or the first.</summary>
    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Down or Key.Enter or Key.Escape))
        {
            return;
        }
        e.Handled = true;
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(search.Text))
        {
            search.Text = string.Empty;
            return;
        }
        if (list.SelectedItem is null && model.Rows.FirstOrDefault(r => r.Problem is not null) is { } first)
        {
            list.SelectedItem = first;
        }
        if (list.SelectedItem is { } chosen)
        {
            list.ScrollIntoView(chosen);
            list.ContainerFromItem(chosen)?.Focus();
        }
        else
        {
            list.Focus();
        }
    }

    private void Choose()
    {
        if (list.SelectedItem is ProblemRow { Problem: { } problem })
        {
            model.GoTo(problem);
        }
    }

    /// <summary>A row for whichever group or problem the list hands its container: a group's name and count; a problem's
    /// icon, its message's first line (a long one cut, all of it on hover) with its link, and where it is.</summary>
    private Control Row()
    {
        RowIcon icon = new() { Margin = new Thickness(0, 0, 8, 0) };
        TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center };
        Button open = new() { Classes = { StyleClasses.Link }, VerticalAlignment = VerticalAlignment.Center };
        TextBlock after = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock detail = new() { FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        detail.Classes.Add(StyleClasses.Muted);
        ShrinkingRow panel = new() { Height = ListRow.Height, Children = { icon, text, open, after, detail } };
        open.Click += (_, e) =>
        {
            if (panel.DataContext is ProblemRow { Problem.Link: { } link })
            {
                model.Open(link);
                e.Handled = true;
            }
        };
        ProblemRow? drawn = null;
        return panel.Showing<ProblemRow>(row =>
        {
            if (row != drawn)
            {
                drawn = row;
                Draw(row);
            }
            detail.Text = row.Problem?.Code is { Length: > 0 } code ? $"{row.Detail}  {code}" : row.Detail;
        });

        void Draw(ProblemRow row)
        {
            panel.Margin = new Thickness(8 + (row.Depth * 16), 0, 8, 0);
            Problem? problem = row.Problem;
            icon.IsVisible = problem is not null;
            int kind = problem is null ? -1 : Array.FindIndex(Severities, s => s.Severity == problem.Severity);
            icon.DataKey = kind < 0 ? null : Severities[kind].Icon;
            icon.ForegroundKey = kind < 0 ? null : Severities[kind].Brush;
            ToolTip.SetTip(panel, problem is null ? null : row.Label);
            text.FontWeight = problem is null && row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal;
            text.TextTrimming = problem is null ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            open.IsVisible = after.IsVisible = false;
            if (problem is null)
            {
                text.Text = row.Label;
                text.MaxWidth = double.PositiveInfinity;
                return;
            }
            // A message's first line shows in the row.
            string label = row.Label;
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
                text.Text = label;
                text.MaxWidth = 900;
                return;
            }
            // The link's words in the link colour, the words around them as they are.
            string address = problem.LinkText ?? link.OriginalString;
            text.Text = label[..at];
            text.MaxWidth = 700;
            open.Content = address;
            open.IsVisible = true;
            ToolTip.SetTip(open, $"Open {link.OriginalString} in the browser");
            after.Text = label[(at + address.Length)..];
            after.IsVisible = after.Text.Length > 0;
        }
    }
}
