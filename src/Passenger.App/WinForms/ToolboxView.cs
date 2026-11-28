using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.WinForms;

/// <summary>A row of the Toolbox: a group, open or not, or an item under it with its picture.</summary>
public sealed class ToolboxRow(string group, ToolboxEntry? entry) : Observable, ITreeRow
{
    private bool expanded;
    private Bitmap? picture;

    public string Group { get; } = group;

    /// <summary>The item it lists; null for its group's row.</summary>
    public ToolboxEntry? Entry { get; } = entry;

    public int Depth => Entry is null ? 0 : 1;

    public bool Expands => Entry is null;

    public bool IsExpanded
    {
        get => expanded;
        internal set => Set(ref expanded, value);
    }

    /// <summary>The item's picture, read once it is first shown; none where its type gives none.</summary>
    public Bitmap? Picture
    {
        get
        {
            if (picture is null && Entry?.Png is { Length: > 0 } png)
            {
                using MemoryStream bytes = new(png);
                picture = new Bitmap(bytes);
            }
            return picture;
        }
    }

    /// <summary>The group's items' rows, made once.</summary>
    internal List<ToolboxRow> Below { get; } = [];
}

/// <summary>The Toolbox: what can be added to the form whose design is shown, as its designer lists it from the project's
/// build (Windows Forms' controls in their groups, the project's own and its references'), narrowed by its filter (Find's
/// keys). Enter or a double-click on an item adds it to the design, into the chosen container (<see cref="AddRequested"/>);
/// a drag of one onto the design adds it where it is dropped (<see cref="Dragging"/>, <see cref="Dropped"/>).</summary>
public sealed class ToolboxView : DockPanel
{
    /// <summary>How far a press on an item moves before it is a drag.</summary>
    private const double DragThreshold = 4;

    private readonly TextBox filter = SearchBox.Finding("Filter the Toolbox");
    private readonly TreeRows<ToolboxRow> rows = [];
    private readonly TreeList list;
    private readonly EmptyState placeholder;
    private List<ToolboxRow> top = [];
    private IReadOnlyList<ToolboxEntry>? listed;
    private (ToolboxEntry Entry, Point Start, bool Dragging)? pressed;

    public ToolboxView()
    {
        filter.TextChanged += (_, _) => Relist();
        filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && rows.Count > 0)
            {
                list!.SelectedItem ??= rows.FirstOrDefault(r => r.Entry is not null) ?? rows[0];
                ListKeys.Hold(list, chooseFirst: true);
                e.Handled = true;
            }
        };
        filter.Margin = new Thickness(Gaps.Small, 0, Gaps.Tight, 0);
        Children.Add(ToolBars.Top(filter));
        list = new TreeList { Classes = { StyleClasses.Tree }, ItemsSource = rows };
        ReusedRows.Use(list, Row);
        TreeKeys.Attach<ToolboxRow>(list, Toggle, row =>
        {
            if (row.Entry is { } entry)
            {
                AddRequested?.Invoke(this, entry);
            }
            return row.Entry is not null;
        });
        list.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        list.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Bubble, handledEventsToo: true);
        placeholder = new EmptyState();
        Children.Add(new Panel { Children = { list, placeholder } });
        Show([], Refusals.NoFormDesign.Shown);
    }

    /// <summary>An item to add to the design, into its chosen container.</summary>
    public event EventHandler<ToolboxEntry>? AddRequested;

    /// <summary>An item dragged on, at the pointer the event carries; ended with <see cref="Dropped"/>, or with
    /// <see cref="DragLost"/> where the pointer was taken from it.</summary>
    public event EventHandler<(ToolboxEntry Entry, PointerEventArgs Pointer)>? Dragging;

    /// <summary>A drag ended with no drop.</summary>
    public event EventHandler? DragLost;

    /// <summary>A dragged item let go of, at the pointer the event carries.</summary>
    public event EventHandler<(ToolboxEntry Entry, PointerEventArgs Pointer)>? Dropped;

    /// <summary>The rows listed, as the filter and the groups opened leave them.</summary>
    public IReadOnlyList<ToolboxRow> Rows => rows;

    /// <summary>What the window says in place of rows; null while it lists some.</summary>
    public string? Said => placeholder.IsVisible ? placeholder.Text : null;

    /// <summary>The item of the row chosen; null for a group's or none.</summary>
    public ToolboxEntry? Chosen => (list.SelectedItem as ToolboxRow)?.Entry;

    /// <summary>The filter's words.</summary>
    public string Filter
    {
        get => filter.Text ?? string.Empty;
        set => filter.Text = value;
    }

    /// <summary>Gives the filter the keys, its text chosen: Find's keys while the window has them.</summary>
    public void FocusFilter()
    {
        filter.Focus();
        filter.SelectAll();
    }

    /// <summary>Chooses an item's row by its name, opening its group.</summary>
    public void Choose(string name)
    {
        if (top.SelectMany(g => g.Below).FirstOrDefault(r => r.Entry?.Name == name) is not { } row)
        {
            return;
        }
        ToolboxRow group = top.First(g => g.Below.Contains(row));
        if (!group.IsExpanded)
        {
            Toggle(group);
        }
        list.SelectedItem = row;
    }

    /// <summary>Shows a design's items, each group open as it was by its name; none says why (<paramref name="why"/>). The
    /// items shown already (the design drawn again) keep their rows, and with them the row chosen and the keys.</summary>
    public void Show(IReadOnlyList<ToolboxEntry> entries, string? why)
    {
        UiThread.Check();
        if (ReferenceEquals(entries, listed))
        {
            if (top.Count == 0)
            {
                placeholder.Text = (why ?? Refusals.NoFormDesign).Shown;
            }
            return;
        }
        listed = entries;
        HashSet<string> closed = [.. top.Where(g => !g.IsExpanded).Select(g => g.Group)];
        top = [];
        foreach (IGrouping<string, ToolboxEntry> group in entries.GroupBy(e => e.Group))
        {
            ToolboxRow row = new(group.Key, null) { IsExpanded = !closed.Contains(group.Key) };
            row.Below.AddRange(group.Select(e => new ToolboxRow(group.Key, e)));
            top.Add(row);
        }
        placeholder.Text = (top.Count == 0 ? why ?? Refusals.NoFormDesign : null)?.Shown;
        Relist();
    }

    /// <summary>Opens or closes a group's row, as a click on its chevron does.</summary>
    public void Toggle(ToolboxRow row)
    {
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            rows.Close(row);
        }
        else if (row.Expands)
        {
            row.IsExpanded = true;
            rows.Open(row, Matching(row));
        }
    }

    /// <summary>Every group opened.</summary>
    public void ExpandAll()
    {
        top.ForEach(r => r.IsExpanded = true);
        Relist();
    }

    /// <summary>Every group closed.</summary>
    public void CollapseAll()
    {
        top.ForEach(r => r.IsExpanded = false);
        Relist();
    }

    /// <summary>A group's items the filter keeps.</summary>
    private List<ToolboxRow> Matching(ToolboxRow group) =>
        [.. group.Below.Where(r => Filter.Length == 0 || r.Entry!.Name.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase))];

    /// <summary>The rows listed again, the one chosen (or another design's row of its item) chosen still, with the keys
    /// where the list had them.</summary>
    private void Relist()
    {
        ToolboxRow? chosen = list.SelectedItem as ToolboxRow;
        bool held = list.IsKeyboardFocusWithin;
        rows.Clear();
        List<ToolboxRow> shown = [.. top.Where(g => Filter.Length == 0 || Matching(g).Count > 0)];
        rows.InsertRange(0, TreeRows<ToolboxRow>.Shown(shown, r => Filter.Length > 0 || r.IsExpanded ? Matching(r) : null));
        list.SelectedItem = chosen is null ? null : rows.FirstOrDefault(r => r == chosen || r.Group == chosen.Group && r.Entry?.Name == chosen.Entry?.Name);
        if (held && list.SelectedItem is not null)
        {
            ListKeys.Hold(list);
        }
        placeholder.IsVisible = rows.Count == 0;
        list.IsVisible = rows.Count > 0;
        if (rows.Count == 0 && top.Count > 0)
        {
            placeholder.Text = $"Nothing in the Toolbox is named like '{Filter.Trim()}'";
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        pressed = e.GetCurrentPoint(list).Properties.IsLeftButtonPressed && (e.Source as Visual)?.DataContext is ToolboxRow { Entry: { } entry }
            ? (entry, e.GetPosition(list), false)
            : null;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (pressed is not { } press)
        {
            return;
        }
        Vector moved = e.GetPosition(list) - press.Start;
        if (press.Dragging || Math.Abs(moved.X) >= DragThreshold || Math.Abs(moved.Y) >= DragThreshold)
        {
            pressed = press with { Dragging = true };
            Dragging?.Invoke(this, (press.Entry, e));
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (pressed is { Dragging: true } press)
        {
            pressed = null;
            Dropped?.Invoke(this, (press.Entry, e));
            return;
        }
        pressed = null;
    }

    private void EndDrag()
    {
        bool dragging = pressed is { Dragging: true };
        pressed = null;
        if (dragging)
        {
            DragLost?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A row for whichever row the list hands its container: a group's chevron and name; an item's picture and name,
    /// its description in its tip.</summary>
    private Control Row()
    {
        RowExpander chevron = RowExpander.Of<ToolboxRow>(Toggle);
        DockPanel.SetDock(chevron, Dock.Left);
        Image picture = new() { Width = 16, Height = 16, Margin = new Thickness(0, 0, Gaps.Normal, 0), VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapInterpolationMode(picture, BitmapInterpolationMode.None);
        DockPanel.SetDock(picture, Dock.Left);
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel row = new() { Children = { chevron, picture, name } };
        return row.Showing<ToolboxRow>(shown =>
        {
            chevron.Expands = shown.Expands;
            chevron.IsOpen = shown.IsExpanded;
            row.Margin = new Thickness(shown.Depth * Gaps.Indent, 0, 0, 0);
            name.Classes.Set(StyleClasses.Strong, shown.Entry is null);
            name.Text = shown.Entry?.Name ?? shown.Group;
            picture.Source = shown.Picture;
            picture.IsVisible = shown.Entry is not null;
            ToolTip.SetTip(row, shown.Entry is { } entry ? entry.Description is { } said ? $"{entry.Name}: {said}" : entry.Name : null);
        });
    }
}
