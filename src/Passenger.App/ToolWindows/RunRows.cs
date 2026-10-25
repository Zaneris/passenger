using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.ToolWindows;

/// <summary>What a row's state dot says of what it stands for: no dot, running, running but unhealthy, between states
/// (paused, starting, stopping), failed, or stopped.</summary>
public enum RunState
{
    None,
    Running,
    Unhealthy,
    Between,
    Failed,
    Stopped,
}

/// <summary>An address a row offers: its words, and where a click opens the browser (null: words alone, as a port not
/// reachable now is).</summary>
public sealed record RunLink(string Words, Uri? Target, string? Tip = null);

/// <summary>A row of a list of what runs (the Containers window's projects, services and containers, the Aspire window's
/// resources): its depth, a chevron when rows go under it, its state's dot, its name with muted words after it, and at its
/// right its health's tag, its addresses and muted words of its own. A heading is its words in bold, its count at its right.</summary>
public sealed record RunRow(int Depth, string Label)
{
    public bool Heading { get; init; }

    public bool Expands { get; init; }

    public bool Expanded { get; init; }

    public RunState State { get; init; }

    public string? StateTip { get; init; }

    /// <summary>A health check's state (healthy, unhealthy, starting, degraded), shown as its tag.</summary>
    public string? Health { get; init; }

    public IReadOnlyList<RunLink> Links { get; init; } = [];

    /// <summary>Muted words after the name: an image, a resource's type.</summary>
    public string? Detail { get; init; }

    /// <summary>Muted words at the row's right: a project's status, a heading's count.</summary>
    public string? Trailing { get; init; }

    public string? Tip { get; init; }
}

/// <summary>A row of a list of what runs as the list holds it, kept by its key while the list is read again (a resource's
/// health streaming in, an engine's event): the same item, so its control, a press under way on it and the list's choice of
/// it stay, told what it stands for now.</summary>
public sealed class RunItem<T>(string key, T data)
    where T : class
{
    public string Key { get; } = key;

    /// <summary>What it stands for as last read.</summary>
    public T Data { get; private set; } = data;

    /// <summary>What it stands for changed: its control shows it anew in place.</summary>
    public event EventHandler? Changed;

    /// <summary>What it stands for read again: its control drawn anew only when that differs from what it drew (an AppHost's
    /// update keeps each resource it did not change, whose row then costs nothing).</summary>
    internal void Take(T data)
    {
        if (EqualityComparer<T>.Default.Equals(Data, data))
        {
            return;
        }
        Data = data;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public override string ToString() => Key;
}

/// <summary>The one way the Containers and Aspire windows list what runs and draw its rows, so both lists read alike and a
/// click on either survives the list being read again.</summary>
public static class RunRows
{
    /// <summary>Makes a list's items the rows given, by their keys: a row whose key it lists keeps its item (its control,
    /// a press on it, its choice) and is told what it stands for now; the others are put in or taken out where they stand.
    /// Never the list made anew, whose controls a click's press and release would straddle, the click then lost.</summary>
    public static void Put<T>(ObservableCollection<RunItem<T>> items, IReadOnlyList<(string Key, T Data)> rows)
        where T : class
    {
        HashSet<string> keys = rows.Select(row => row.Key).ToHashSet(StringComparer.Ordinal);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!keys.Contains(items[i].Key))
            {
                items.RemoveAt(i);
            }
        }
        for (int i = 0; i < rows.Count; i++)
        {
            (string key, T data) = rows[i];
            if (i < items.Count && items[i].Key == key)
            {
                items[i].Take(data);
                continue;
            }
            int at = -1;
            for (int j = i + 1; j < items.Count; j++)
            {
                if (items[j].Key == key)
                {
                    at = j;
                    break;
                }
            }
            if (at >= 0)
            {
                items.Move(at, i);
                items[i].Take(data);
            }
            else
            {
                items.Insert(i, new RunItem<T>(key, data));
            }
        }
        while (items.Count > rows.Count)
        {
            items.RemoveAt(items.Count - 1);
        }
    }

    /// <summary>A list's rows: each item drawn by <paramref name="draw"/> as a <see cref="RunRowView"/>, shown anew in place
    /// as its item changes; <paramref name="toggle"/> runs on a chevron's press and <paramref name="browse"/> on a link's
    /// click, each link named <paramref name="linkName"/>.</summary>
    public static FuncDataTemplate<RunItem<T>> Template<T>(Func<T, RunRow> draw, Action<T> toggle, Action<Uri> browse, string linkName)
        where T : class =>
        new((item, _) =>
        {
            if (item is null)
            {
                return new Panel();
            }
            RunRowView view = new(() => toggle(item.Data), browse, linkName);
            void Show(object? sender, EventArgs e) => view.Show(draw(item.Data));
            view.Show(draw(item.Data));
            // Followed while shown: a row scrolled away or taken out holds no handler on its item.
            view.AttachedToVisualTree += (_, _) =>
            {
                item.Changed -= Show;
                item.Changed += Show;
                view.Show(draw(item.Data));
            };
            view.DetachedFromVisualTree += (_, _) => item.Changed -= Show;
            return view;
        }, supportsRecycling: false);

    /// <summary>A list of such rows opens and closes a row by a double click, and takes a tree's keys (<see cref="TreeKeys"/>).</summary>
    public static void Toggles<T>(ListBox list, Action<T> toggle)
        where T : class, ITreeRow
    {
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is RunItem<T> { Data.Expands: true } item)
            {
                toggle(item.Data);
            }
        };
        TreeKeys.Attach<RunItem<T>>(list, item => item.Data, item => toggle(item.Data));
    }

    /// <summary>The state dot's colour: running green, unhealthy or failed red, between states amber, the rest muted.</summary>
    public static string Colour(RunState state) => state switch
    {
        RunState.Running => TokenKeys.Test_Passed,
        RunState.Unhealthy or RunState.Failed => TokenKeys.Error,
        RunState.Between => TokenKeys.Warning,
        _ => TokenKeys.TextMuted,
    };

    internal static TextBlock Words(string text, bool muted, Dock? dock)
    {
        TextBlock words = new() { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (muted)
        {
            words.Classes.Add("muted");
        }
        if (dock is { } side)
        {
            DockPanel.SetDock(words, side);
        }
        return words;
    }
}

/// <summary>A row of a list of what runs drawn (<see cref="RunRows"/>): indented by its depth, a chevron when rows go under
/// it, its state's dot, its name and muted words after it, and at its right its health's tag, its addresses (links to the
/// browser, else words) and muted words of its own. Shown again in place, each part changed only where what it shows
/// changed, so a press on a link or the chevron is not taken from under the pointer by a read that changed nothing of it.</summary>
public sealed class RunRowView : DockPanel
{
    private readonly Action<Uri> browse;
    private readonly string linkName;
    private readonly PathIcon chevron;
    private readonly Ellipse dot = new() { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel right = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel links = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock trailing = RunRows.Words(string.Empty, muted: true, dock: null);
    private readonly TextBlock name = RunRows.Words(string.Empty, muted: false, dock: Dock.Left);
    private readonly TextBlock detail = RunRows.Words(string.Empty, muted: true, dock: null);
    private Control? healthTag;
    private string? tagged;
    private RunRow? shown;

    public RunRowView(Action toggle, Action<Uri> browse, string linkName)
    {
        this.browse = browse;
        this.linkName = linkName;
        Height = ListRow.Height;
        // Its whole box takes a press, not only the glyph's thin strokes.
        chevron = new PathIcon
        {
            Width = 12, Height = 12, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, Name = "rowChevron",
        };
        chevron.Bind(PathIcon.ForegroundProperty, chevron.GetResourceObservable(TokenKeys.TextMuted));
        chevron.PointerPressed += (_, e) =>
        {
            // The press's capture let go first, or rows moved under the pointer would take the next press from the chevron.
            e.Pointer.Capture(null);
            toggle();
            e.Handled = true;
        };
        // A second press soon after is the chevron's again, never the list's double click, which would toggle it back.
        chevron.DoubleTapped += (_, e) => e.Handled = true;
        SetDock(chevron, Dock.Left);
        SetDock(dot, Dock.Left);
        SetDock(right, Dock.Right);
        right.Children.Add(links);
        right.Children.Add(trailing);
        detail.Margin = new Thickness(8, 0, 0, 0);
        detail.FontSize = 12;
        Children.Add(chevron);
        Children.Add(dot);
        Children.Add(right);
        Children.Add(name);
        Children.Add(detail);
    }

    /// <summary>What the row shows now.</summary>
    public RunRow? Shown => shown;

    /// <summary>Shows a row, changing only the parts that differ from what it shows.</summary>
    public void Show(RunRow row)
    {
        RunRow? was = shown;
        shown = row;
        Margin = new Thickness(6 + (row.Depth * 16), 0, 6, 0);
        chevron.IsVisible = row.Expands;
        if (was is null || was.Expanded != row.Expanded)
        {
            chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(row.Expanded ? IconKeys.ChevronDown : IconKeys.ChevronRight));
        }
        dot.IsVisible = !row.Heading && row.State != RunState.None;
        if (was is null || was.State != row.State)
        {
            dot.Bind(Shape.FillProperty, dot.GetResourceObservable(RunRows.Colour(row.State)));
        }
        ToolTip.SetTip(dot, row.StateTip);
        string? health = row.Heading ? null : row.Health;
        if (health != tagged)
        {
            tagged = health;
            if (healthTag is not null)
            {
                right.Children.Remove(healthTag);
            }
            healthTag = health is null ? null : Containers.ContainerDetailsPane.HealthTag(health);
            if (healthTag is not null)
            {
                right.Children.Insert(0, healthTag);
            }
        }
        IReadOnlyList<RunLink> offered = row.Heading ? [] : row.Links;
        if (was is null || !offered.SequenceEqual(was.Heading ? [] : was.Links))
        {
            links.Children.Clear();
            foreach (RunLink link in offered)
            {
                links.Children.Add(Link(link));
            }
        }
        links.IsVisible = links.Children.Count > 0;
        trailing.Text = row.Trailing ?? string.Empty;
        trailing.IsVisible = row.Trailing is not null;
        name.Text = row.Label;
        name.FontWeight = row.Heading ? FontWeight.SemiBold : FontWeight.Normal;
        name.MaxWidth = row.Heading ? double.PositiveInfinity : 260;
        detail.Text = row.Detail ?? string.Empty;
        detail.IsVisible = !row.Heading && row.Detail is { Length: > 0 };
        ToolTip.SetTip(this, row.Heading ? null : row.Tip ?? row.Label);
    }

    private Control Link(RunLink link)
    {
        if (link.Target is not { } target)
        {
            return RunRows.Words(link.Words, muted: true, dock: null);
        }
        Button open = new() { Classes = { "link" }, Content = link.Words, VerticalAlignment = VerticalAlignment.Center, Name = linkName };
        ToolTip.SetTip(open, link.Tip ?? target.ToString());
        open.Click += (_, _) => browse(target);
        return open;
    }
}
