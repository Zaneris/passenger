using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.Profiler;
using Passenger.Core;

namespace Passenger.App.Profiling;

/// <summary>A row of the Frames page's layout table: a control type, or one of its controls under it once the type is opened.</summary>
public sealed class LayoutRow(LayoutCost cost, int depth) : Observable, ITreeRow
{
    private bool isExpanded;
    private List<LayoutRow>? controls;

    public LayoutCost Cost { get; } = cost;

    public int Depth { get; } = depth;

    public bool Expands => Cost.Controls.Count > 0;

    /// <summary>A type's rows of its controls, made the first time it opens.</summary>
    public IReadOnlyList<LayoutRow> Controls => controls ??= [.. Cost.Controls.Select(c => new LayoutRow(c, Depth + 1))];

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
    private readonly TreeRows<LayoutRow> rows = [];
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
        StackPanel legend = new() { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(12, 2, 12, 0) };
        foreach ((string label, string brush) in new[] { ("Measure", TokenKeys.Graph_0), ("Arrange", TokenKeys.Graph_1), ("Render", TokenKeys.Graph_2), ("Other UI work", TokenKeys.TextMuted), ("Compositor", TokenKeys.Graph_3) })
        {
            Border swatch = new() { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Center, Opacity = brush == TokenKeys.TextMuted ? 0.35 : 1 };
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
            Child = new StackPanel { Children = { legend, strip, summary } },
            Padding = new Thickness(0, 4, 0, 0),
        };
        band.Bind(Border.BorderBrushProperty, band.GetResourceObservable(TokenKeys.Divider));

        (string Title, double Width)[] columns = [("Control", 0), ("Own time", NumberWidth), ("Measures", NumberWidth), ("Measure time", NumberWidth),
            ("Arranges", NumberWidth), ("Arrange time", NumberWidth + 16)];
        Grid header = Row(columns, [.. columns.Select((c, i) => (Control)Title(c.Title, i == 0))]);
        header.Margin = new Thickness(10, 4, 10, 0);
        Border heading = new() { [DockProperty] = Dock.Top, BorderThickness = new Thickness(0, 0, 0, 1), Child = header, Padding = new Thickness(0, 0, 0, 3) };
        heading.Bind(Border.BorderBrushProperty, heading.GetResourceObservable(TokenKeys.Divider));
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
        TreeKeys.Attach<LayoutRow>(table, Toggle, OpenControl);
        shown = new DockPanel { Children = { band, heading, table } };
        placeholder = Muted(NoFrames);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.TextWrapping = TextWrapping.Wrap;
        placeholder.MaxWidth = 560;
        placeholder.TextAlignment = TextAlignment.Center;
        Children.Add(new Panel { Children = { shown, placeholder } });
        Show([], 0);
    }

    /// <summary>What the page says of a run with no frames.</summary>
    private const string NoFrames = "No Avalonia frames in this run. Passenger records an Avalonia app's frames, layout and rendering when it profiles one it launched (Profile) or one Run or Debug started (Attach Profiler).";

    /// <summary>Why a run in a container has no frames: Passenger puts its inspector, which records them, in no container.</summary>
    public static string InContainer(string where) =>
        $"No Avalonia frames in this run: it ran in container {where}, where Passenger puts no inspector to record an Avalonia app's frames, layout and rendering. "
        + "Profile the app on this machine (Profile, or Attach Profiler to what Run or Debug started there) for them.";

    /// <summary>A control's row opened (double-click, Enter): its XAML, or its place in the Live Visual Tree.</summary>
    public event EventHandler<UiControl>? ControlOpened;

    /// <summary>The line (from one) a control's row names: where its element is in the editor's lines (the app's count is
    /// its XAML compiler's).</summary>
    public Func<UiControl, int>? LineOf { get; set; }

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

    /// <summary>What the page says in place of frames, while it shows none.</summary>
    public string? Placeholder => placeholder.IsVisible ? placeholder.Text : null;

    /// <summary>Shows some frames, all of them chosen; <paramref name="start"/> is where the run began, which their times are
    /// said from. With none, the page says why: <paramref name="why"/>, else how a run gets them.</summary>
    public void Show(IReadOnlyList<UiFrame> shownFrames, double start, string? why = null)
    {
        frames = shownFrames;
        origin = start;
        chart.Frames = shownFrames;
        shown.IsVisible = shownFrames.Count > 0;
        placeholder.IsVisible = shownFrames.Count == 0;
        placeholder.Text = why ?? NoFrames;
        Fill();
    }

    /// <summary>Opens or closes a type's row, as a click on its chevron does.</summary>
    public void Toggle(LayoutRow row)
    {
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            rows.Close(row);
        }
        else if (row.Expands)
        {
            row.IsExpanded = true;
            rows.Open(row, row.Controls);
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
        UiFrame longest = all.MaxBy(f => f.DurationMs)!;
        return $"{all.Count:N0} {Plural.Noun(all.Count, "frame")}: {UiFrames.MedianMs(all):0.0} ms on the UI thread at the median, {longest.DurationMs:0.0} ms at the longest (frame {longest.Number}); "
            + $"measuring {all.Sum(f => f.MeasureMs):N1} ms, arranging {all.Sum(f => f.ArrangeMs):N1} ms, rendering {all.Sum(f => f.RecordMs):N1} ms, the compositor {all.Sum(f => f.CompositeMs):N1} ms in all. "
            + "Choose a frame for its own.";
    }

    private string Said(UiFrame frame) =>
        $"Frame {frame.Number} at {frame.StartMs - origin:N0} ms: {frame.DurationMs:0.0} ms on the UI thread, {frame.MeasurePasses:N0} {Plural.Noun(frame.MeasurePasses, "measure pass", "measure passes")} and "
        + $"{frame.ArrangePasses:N0} {Plural.Noun(frame.ArrangePasses, "arrange pass", "arrange passes")} ({frame.Measures:N0} {Plural.Noun(frame.Measures, "measure")}, {frame.Arranges:N0} {Plural.Noun(frame.Arranges, "arrange")}); "
        + $"measuring {frame.MeasureMs:0.0} ms, arranging {frame.ArrangeMs:0.0} ms, rendering {frame.RecordMs:0.0} ms, then the compositor {frame.CompositeMs:0.0} ms.";


    private void Open()
    {
        if (table.SelectedItem is LayoutRow row && !OpenControl(row))
        {
            Toggle(row);
        }
    }

    /// <summary>A control's row opened: its XAML, or its place in the Live Visual Tree; false for a type's row.</summary>
    private bool OpenControl(LayoutRow row)
    {
        if (row.Cost.Control is not { } control)
        {
            return false;
        }
        ControlOpened?.Invoke(this, control);
        return true;
    }

    private Control RowOf(LayoutRow row, (string Title, double Width)[] columns)
    {
        PathIcon chevron = new() { Classes = { "chevron" }, IsVisible = row.Cost.Controls.Count > 0, RenderTransformOrigin = RelativePoint.Center };
        chevron.Bind(PathIcon.DataProperty, chevron.GetResourceObservable(IconKeys.ChevronRight));
        chevron.RenderTransform = new RotateTransform(row.IsExpanded ? 90 : 0);
        row.FollowWhileShown(chevron, (_, _) => chevron.RenderTransform = new RotateTransform(row.IsExpanded ? 90 : 0));
        Border toggle = new() { Width = 18, Height = ListRow.Height, Background = Brushes.Transparent, Child = chevron };
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
            ? $"{control.FullType}{(control.File is { } file ? $"\n{Path.GetFileName(file)}:{LineOf?.Invoke(control) ?? control.Line}" : string.Empty)}"
            : Plural.Of(row.Cost.Controls.Count, "control"));
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
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions(string.Join(",", columns.Select(c => c.Width == 0 ? "*" : $"{c.Width}"))), MinHeight = ListRow.Height };
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
            block.Classes.Add("muted");
        }
        return block;
    }

    private static TextBlock Muted(string text)
    {
        TextBlock block = MutedText.Of(text);
        block.FontSize = 12;
        return block;
    }
}
