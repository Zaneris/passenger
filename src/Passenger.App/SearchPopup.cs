using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Passenger.App;

/// <summary>A popup that ends with an answer: what was chosen, or what it says when given up. Shown by
/// <see cref="PopupHost.Show{T}(IAnswers{T}, Action{T}, Point?)"/>, which closes it as it answers.</summary>
public interface IAnswers<T>
{
    event EventHandler<T>? Done;
}

/// <summary>Every search popup's one frame (Find Action, Go to File, Go to Symbol and its kin, Recent Locations, a Git
/// choice): its title, a switch at its right when it has one, the query (its text chosen as the popup opens), the rows
/// found under it, the words for none, and its keys: Up, Down, Page Up and Page Down move, Enter or a double-click
/// chooses, Escape gives up. A popup of it says what a query finds and draws its rows.</summary>
public abstract class SearchPopup<T> : UserControl, IAnswers<T?>
    where T : class
{
    /// <summary>How tall the list grows before it scrolls: fifteen rows.</summary>
    public const double ListHeight = ListRow.Height * 15;

    private readonly TextBlock title;
    private readonly TextBox query;
    private readonly ListBox results;
    private readonly TextBlock empty;
    private readonly CheckBox? option;

    /// <param name="listHeight">How tall the list grows, for rows taller than a line (Recent Locations' code).</param>
    protected SearchPopup(string title, string placeholder, double width, string? initialQuery = null, (string Label, bool Checked)? option = null,
        double listHeight = ListHeight)
    {
        Width = width;
        this.title = new TextBlock { Classes = { "popupTitle" }, Text = title, VerticalAlignment = VerticalAlignment.Center };
        query = new TextBox { Classes = { "search" }, PlaceholderText = placeholder, Text = initialQuery ?? string.Empty };
        results = new ListBox
        {
            Classes = { "tree" },
            MaxHeight = listHeight,
            Margin = new Thickness(0, 0, 0, 8),
        };
        ReusedRows.Use(results, Row);
        empty = new TextBlock { Classes = { "muted" }, Margin = new Thickness(12, 0, 12, 12), IsVisible = false };
        results.DoubleTapped += (_, _) => Choose();
        query.TextChanged += (_, _) => QueryChanged();
        query.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        DockPanel heading = new();
        if (option is { } shown)
        {
            // Not focusable: the keys stay with the query.
            this.option = new CheckBox { Content = shown.Label, IsChecked = shown.Checked, Focusable = false, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            this.option.IsCheckedChanged += (_, _) => OptionTurned();
            DockPanel.SetDock(this.option, Dock.Right);
            heading.Children.Add(this.option);
        }
        heading.Children.Add(this.title);
        StackPanel top = new() { Margin = new Thickness(12, 10, 12, 8), Spacing = 8, Children = { heading, query } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(empty, Dock.Top);
        Content = new Border { Classes = { "popup" }, Child = new DockPanel { Children = { top, empty, results } } };
        Dispatcher.UIThread.Post(() =>
        {
            query.Focus();
            query.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The row chosen, or null when the popup was given up.</summary>
    public event EventHandler<T?>? Done;

    /// <summary>What is listed now.</summary>
    public IReadOnlyList<T> Rows => results.ItemsSource as IReadOnlyList<T> ?? [];

    public T? Selected
    {
        get => results.SelectedItem as T;
        set => results.SelectedItem = value;
    }

    public string Query
    {
        get => query.Text ?? string.Empty;
        set => query.Text = value;
    }

    /// <summary>What the popup is called over its query.</summary>
    public string Title
    {
        get => title.Text ?? string.Empty;
        protected set => title.Text = value;
    }

    /// <summary>Whether the popup's switch is on; false for a popup without one.</summary>
    public bool IsOptionChecked
    {
        get => option?.IsChecked == true;
        set
        {
            if (option is not null)
            {
                option.IsChecked = value;
            }
        }
    }

    /// <summary>Takes the row chosen, as Enter does.</summary>
    public void Choose()
    {
        if (Selected is { } row)
        {
            Done?.Invoke(this, row);
        }
    }

    /// <summary>A row made once for each of the list's containers, showing whichever found item the list hands it
    /// (<see cref="ReusedRows.Showing{T}"/>).</summary>
    protected abstract Control Row();

    /// <summary>The query changed: what it finds is listed again.</summary>
    protected abstract void QueryChanged();

    /// <summary>The switch turned.</summary>
    protected virtual void OptionTurned() => QueryChanged();

    /// <summary>A key of the popup's own in its query, beyond those every search popup has; whether it was taken.</summary>
    protected virtual bool TakesKey(KeyEventArgs e) => false;

    /// <summary>Lists what a query found, <paramref name="chosen"/> chosen and in sight, or <paramref name="none"/> when it
    /// found nothing.</summary>
    protected void Show(IReadOnlyList<T> found, string none, int chosen = 0)
    {
        results.ItemsSource = found;
        results.SelectedIndex = found.Count > 0 ? Math.Clamp(chosen, 0, found.Count - 1) : -1;
        if (results.SelectedIndex > 0)
        {
            results.ScrollIntoView(results.SelectedIndex);
        }
        empty.Text = none;
        empty.IsVisible = found.Count == 0;
    }

    /// <summary>Says why nothing could be listed (a search that failed), over the list.</summary>
    protected void Say(string why)
    {
        empty.Text = why;
        empty.IsVisible = true;
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (ListKeys.Move(results, e) || TakesKey(e))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                Choose();
                e.Handled = true;
                break;
            case Key.Escape:
                Done?.Invoke(this, null);
                e.Handled = true;
                break;
        }
    }
}
