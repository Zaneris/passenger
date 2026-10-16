using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.Profiler;

namespace Passenger.App.Profiling;

/// <summary>A row of the Frames page's layout table: a control type, or one of its controls under it once the type is opened.</summary>
public sealed class LayoutRow(LayoutCost cost, int depth) : Observable
{
    private bool isExpanded;

    public LayoutCost Cost { get; } = cost;

    public int Depth { get; } = depth;

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }
}

/// <summary>The profiler window's Frames page, for an Avalonia app profiled with Passenger's inspector in it: its frames as
/// bars (<see cref="FrameChart"/>), what they or the frame chosen cost, and their layout by control type, the heaviest by
/// its own time first, each type opening to its controls. A control's row opens its XAML, and chooses it in the Live Visual
/// Tree when that follows the app the frames came from.</summary>
public sealed class FramesView : DockPanel
{
    private const double NumberWidth = 92;

    private readonly FrameChart chart;
    private readonly TextBlock summary;
    private readonly ObservableCollection<LayoutRow> rows = [];
    private readonly ListBox table;
    private readonly TextBlock placeholder;
    private readonly DockPanel shown;
    private IReadOnlyList<UiFrame> frames = [];
    private double origin;

    public FramesView()
    {
        chart = new FrameChart();
        chart.Chosen += (_, _) => Fill();
        ScrollViewer strip = new()
        {
            Content = chart,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        WrapPanel legend = new() { ItemSpacing = 14, LineSpacing = 4, Margin = new Thickness(12, 2, 12, 0) };
        foreach ((string label, string brush) in new[] { ("Measure", "Passenger.Graph.0"), ("Arrange", "Passenger.Graph.1"), ("Render", "Passenger.Graph.2"), ("Other UI work", "Passenger.TextMuted"), ("Compositor", "Passenger.Graph.3") })
        {
            Border swatch = new() { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Opacity = brush == "Passenger.TextMuted" ? 0.35 : 1 };
            swatch.Bind(Border.BackgroundProperty, swatch.GetResourceObservable(brush));
            legend.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { swatch, Muted(label) } });
        }
        summary = Muted(string.Empty);
        summary.Margin = new Thickness(12, 6, 12, 6);
        summary.TextWrapping = TextWrapping.Wrap;
        Border band = new()
        {
            [DockProperty] = Dock.Top,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel { Children = { legend, strip, summary } },
            },
            Padding = new Thickness(0, 4, 0, 0),
        };
        band.Bind(Border.BorderBrushProperty, band.GetResourceObservable("Passenger.Divider"));
        // A wrapped summary scrolls before it takes the table's header and two rows away.
        SizeChanged += (_, change) => band.MaxHeight = Math.Max(0, change.NewSize.Height - 88);

        (string Title, double Width)[] columns = [("Control", 0), ("Own time", NumberWidth), ("Measures", NumberWidth), ("Measure time", NumberWidth),
            ("Arranges", NumberWidth), ("Arrange time", NumberWidth + 16)];
        Grid header = Row(columns, [.. columns.Select((c, i) => (Control)Title(c.Title, i == 0))]);
        header.Margin = new Thickness(10, 4, 10, 0);
        Border heading = new() { [DockProperty] = Dock.Top, BorderThickness = new Thickness(0, 0, 0, 1), Child = header, Padding = new Thickness(0, 0, 0, 3) };
        heading.Bind(Border.BorderBrushProperty, heading.GetResourceObservable("Passenger.Divider"));
        table = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
            ItemTemplate = new FuncDataTemplate<LayoutRow>((row, _) => row is null ? new TextBlock() : RowOf(row, columns), supportsRecycling: false),
        };
        table.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch) },
        });
        table.DoubleTapped += (_, _) => Open();
        table.KeyDown += OnKey;
        ScrollViewer columnsView = new()
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new DockPanel { Children = { heading, table } },
        };
        shown = new DockPanel { Children = { band, columnsView } };
        placeholder = Muted("No Avalonia frames in this run. Passenger records an Avalonia app's frames, layout and rendering when it profiles one it launched (Profile) or one Run or Debug started (Attach Profiler).");
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.TextWrapping = TextWrapping.Wrap;
        placeholder.MaxWidth = 560;
        placeholder.Margin = new Thickness(12);
        placeholder.TextAlignment = TextAlignment.Center;
        Children.Add(new Panel { Children = { shown, placeholder } });
        Show([], 0);
    }

    /// <summary>A control's row opened (double-click, Enter): its XAML, or its place in the Live Visual Tree.</summary>
    public event EventHandler<UiControl>? ControlOpened;

    /// <summary>The frames shown, in order: the run's, or the stretch's chosen on the timeline.</summary>
    public IReadOnlyList<UiFrame> Frames => frames;

    /// <summary>The frame chosen on the chart, or null for all.</summary>
    public UiFrame? Chosen => chart.Selection is int at ? frames[at] : null;

    public FrameChart Chart => chart;

    /// <summary>The layout table's rows as listed now: types, and the controls of those opened.</summary>
    public IReadOnlyList<LayoutRow> Rows => rows;

    public ListBox Table => table;

    /// <summary>The line over the table: the frames', or the chosen frame's, times.</summary>
    public string Summary => summary.Text ?? string.Empty;

    /// <summary>Shows some frames, all of them chosen; <paramref name="start"/> is where the run began, which their times are
    /// said from.</summary>
    public void Show(IReadOnlyList<UiFrame> shownFrames, double start)
    {
        frames = shownFrames;
        origin = start;
        chart.Frames = shownFrames;
        shown.IsVisible = shownFrames.Count > 0;
        placeholder.IsVisible = shownFrames.Count == 0;
        Fill();
    }

    /// <summary>Opens or closes a type's row, as a click on its chevron does.</summary>
    public void Toggle(LayoutRow row)
    {
        if (row.IsExpanded)
        {
            int at = rows.IndexOf(row) + 1;
            while (at < rows.Count && rows[at].Depth > row.Depth)
            {
                rows.RemoveAt(at);
            }
            row.IsExpanded = false;
            return;
        }
        if (row.Cost.Controls.Count == 0)
        {
            return;
        }
        row.IsExpanded = true;
        int next = rows.IndexOf(row) + 1;
        foreach (LayoutCost control in row.Cost.Controls)
        {
            rows.Insert(next++, new LayoutRow(control, row.Depth + 1));
        }
    }

    /// <summary>The table and the summary, for the frame chosen or for all; the heaviest type opened.</summary>
    private void Fill()
    {
        IReadOnlyList<UiFrame> over = Chosen is { } frame ? [frame] : frames;
        summary.Text = Chosen is { } one ? Said(one) : Said(frames);
        rows.Clear();
        foreach (LayoutCost type in UiFrames.ByType(over))
        {
            rows.Add(new LayoutRow(type, 0));
        }
        if (rows.Count > 0)
        {
            Toggle(rows[0]);
            table.SelectedItem = rows.Count > 1 ? rows[1] : rows[0];
        }
    }

    private string Said(IReadOnlyList<UiFrame> all)
    {
        if (all.Count == 0)
        {
            return string.Empty;
        }
        double[] times = [.. all.Select(f => f.DurationMs).Order()];
        UiFrame longest = all.MaxBy(f => f.DurationMs)!;
        return $"{all.Count:N0} frame{(all.Count == 1 ? "" : "s")}: {times[times.Length / 2]:0.0} ms on the UI thread at the median, {longest.DurationMs:0.0} ms at the longest (frame {longest.Number}); "
            + $"measuring {all.Sum(f => f.MeasureMs):N1} ms, arranging {all.Sum(f => f.ArrangeMs):N1} ms, rendering {all.Sum(f => f.RecordMs):N1} ms, the compositor {all.Sum(f => f.CompositeMs):N1} ms in all. "
            + "Choose a frame for its own.";
    }

    private string Said(UiFrame frame) =>
        $"Frame {frame.Number} at {frame.StartMs - origin:N0} ms: {frame.DurationMs:0.0} ms on the UI thread, {Count(frame.MeasurePasses, "measure pass", "measure passes")} and "
        + $"{Count(frame.ArrangePasses, "arrange pass", "arrange passes")} ({Count(frame.Measures, "measure", "measures")}, {Count(frame.Arranges, "arrange", "arranges")}); "
        + $"measuring {frame.MeasureMs:0.0} ms, arranging {frame.ArrangeMs:0.0} ms, rendering {frame.RecordMs:0.0} ms, then the compositor {frame.CompositeMs:0.0} ms.";

    private static string Count(int count, string one, string many) => $"{count:N0} {(count == 1 ? one : many)}";

    private void Open()
    {
        if (table.SelectedItem is not LayoutRow row)
        {
            return;
        }
        if (row.Cost.Control is { } control)
        {
            ControlOpened?.Invoke(this, control);
        }
        else
        {
            Toggle(row);
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (table.SelectedItem is not LayoutRow row)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Right when !row.IsExpanded && row.Cost.Controls.Count > 0:
            case Key.Left when row.IsExpanded:
                Toggle(row);
                e.Handled = true;
                break;
            case Key.Enter or Key.F4:
                Open();
                e.Handled = true;
                break;
        }
    }

    private Control RowOf(LayoutRow row, (string Title, double Width)[] columns)
    {
        PathIcon chevron = new() { Classes = { "chevron" }, IsVisible = row.Cost.Controls.Count > 0, RenderTransformOrigin = RelativePoint.Center };
        chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable("Icon.ChevronRight"));
        chevron.RenderTransform = new RotateTransform(row.IsExpanded ? 90 : 0);
        row.PropertyChanged += (_, _) => chevron.RenderTransform = new RotateTransform(row.IsExpanded ? 90 : 0);
        Border toggle = new() { Width = 18, Height = 22, Background = Brushes.Transparent, Child = chevron };
        toggle.PointerPressed += (_, e) =>
        {
            Toggle(row);
            e.Handled = true;
        };
        TextBlock name = new()
        {
            Text = row.Cost.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal,
        };
        ToolTip.SetTip(name, row.Cost.Control is { } control
            ? $"{control.FullType}{(control.File is { } file ? $"\n{Path.GetFileName(file)}:{control.Line}" : string.Empty)}"
            : $"{row.Cost.Controls.Count} control{(row.Cost.Controls.Count == 1 ? "" : "s")}");
        LayoutWork work = row.Cost.Work;
        return Row(columns,
        [
            new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(row.Depth * 14, 0, 0, 0), Children = { toggle, name } },
            Number($"{work.SelfMs:N1} ms"),
            Number($"{work.Measures:N0}"),
            Number($"{work.MeasureMs:N1} ms"),
            Number($"{work.Arranges:N0}"),
            Number($"{work.ArrangeMs:N1} ms"),
        ]);
    }

    private static Grid Row((string Title, double Width)[] columns, Control[] cells)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions(string.Join(",", columns.Select(c => c.Width == 0 ? "*" : $"{c.Width}"))), MinHeight = 22 };
        row.ColumnDefinitions[0].MinWidth = 240;
        for (int i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i], i);
            row.Children.Add(cells[i]);
        }
        return row;
    }

    private static TextBlock Title(string text, bool first)
    {
        TextBlock title = Muted(text);
        title.TextAlignment = first ? TextAlignment.Left : TextAlignment.Right;
        title.HorizontalAlignment = first ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        return title;
    }

    private static TextBlock Number(string text)
    {
        TextBlock block = new() { Text = text, FontFamily = CSharp.Fonts.Mono, FontSize = 12, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        if (text is "0" or "0.0 ms")
        {
            block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("Passenger.TextMuted"));
        }
        return block;
    }

    private static TextBlock Muted(string text)
    {
        TextBlock block = new() { Text = text, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        block.Bind(TextBlock.ForegroundProperty, block.GetResourceObservable("Passenger.TextMuted"));
        return block;
    }
}
