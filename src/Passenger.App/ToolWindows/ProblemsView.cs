using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Run;

namespace Passenger.App.ToolWindows;

/// <summary>The Problems window over its <see cref="ProblemsModel"/>, built as the window is first shown: a tab for each
/// list with its count, the shown tab's rows, errors before warnings; a double-click or Enter goes to a problem, and the
/// web address in its message is a link.</summary>
public sealed class ProblemsView : DockPanel
{
    private readonly ProblemsModel model;
    private readonly ListBox list;
    private readonly TextBlock empty;
    private readonly Dictionary<ProblemsTab, ToggleButton> tabs = [];
    private bool following;

    public ProblemsView(ProblemsModel model)
    {
        this.model = model;
        StackPanel strip = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (ProblemsTab tab in Enum.GetValues<ProblemsTab>())
        {
            ToggleButton button = new() { Classes = { "panelTab" } };
            button.Click += (_, _) => model.Shown = tab;
            tabs[tab] = button;
            strip.Children.Add(button);
        }
        Border bar = ToolBars.Top(strip);
        Children.Add(bar);
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = model.ShownRows,
            ItemTemplate = new FuncDataTemplate<ProblemRow>((row, _) => Row(row), supportsRecycling: false),
        };
        empty = new TextBlock { Classes = { "emptyText" } };
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
    }

    private void Choose()
    {
        if (list.SelectedItem is ProblemRow { Problem: { } problem })
        {
            model.GoTo(problem);
        }
    }

    private Control Row(ProblemRow? row)
    {
        if (row is null)
        {
            return new TextBlock();
        }
        ShrinkingRow panel = new() { Height = ListRow.Height, Margin = new Thickness(8 + (row.Depth * 16), 0, 8, 0) };
        TextBlock detail = new() { Text = row.Detail, FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        detail.Classes.Add("muted");
        if (row.Problem is not { } problem)
        {
            panel.Children.Add(new TextBlock { Text = row.Label, FontWeight = row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(detail);
            return panel;
        }
        PathIcon icon = new() { Margin = new Thickness(0, 0, 8, 0) };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(problem.Severity == ProblemSeverity.Error ? IconKeys.Error : IconKeys.Problems));
        icon.Bind(PathIcon.ForegroundProperty, icon.GetResourceObservable(problem.Severity == ProblemSeverity.Error ? TokenKeys.Error : TokenKeys.Warning));
        void Where() => detail.Text = problem.Code is { Length: > 0 } code ? $"{row.Detail}  {code}" : row.Detail;
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
        // A message's first line shows in the row, a long one cut, and all of it on hover.
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
            panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 900 });
        }
        else
        {
            // The link's words in the link colour, the words around them as they are.
            string address = problem.LinkText ?? link.OriginalString;
            Button open = new() { Classes = { "link" }, Content = address, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(open, $"Open {link.OriginalString} in the browser");
            open.Click += (_, e) =>
            {
                model.Open(link);
                e.Handled = true;
            };
            panel.Children.Add(new TextBlock { Text = label[..at], VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 700 });
            panel.Children.Add(open);
            if (label[(at + address.Length)..] is { Length: > 0 } after)
            {
                panel.Children.Add(new TextBlock { Text = after, VerticalAlignment = VerticalAlignment.Center });
            }
        }
        panel.Children.Add(detail);
        return panel;
    }
}
