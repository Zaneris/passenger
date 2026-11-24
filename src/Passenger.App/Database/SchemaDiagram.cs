using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.Database;

/// <summary>A schema's tables and their foreign keys drawn as a diagram among the editors (<see cref="DiagramLayout"/>): a
/// box a table, its name over its columns, the primary key's marked, and a line a foreign key. It zooms and pans, its
/// boxes move where the person drags them, kept for the schema, a table opens from its box, and it is read again on asking.</summary>
public sealed class SchemaDiagramTab : IDatabaseTab
{
    private readonly DatabaseSource connection;
    private readonly Func<Task<SchemaShape?>> read;
    private readonly Action<TableShape> open;
    private readonly Func<IReadOnlyDictionary<string, (double X, double Y)>> places;
    private readonly Action<IReadOnlyDictionary<string, (double X, double Y)>> keep;

    /// <param name="read">Reads the schema again, null where it failed or was stopped.</param>
    /// <param name="open">Opens a table's rows.</param>
    /// <param name="places">The places the person left its boxes at, kept for the schema.</param>
    /// <param name="keep">Keeps where the person left its boxes.</param>
    public SchemaDiagramTab(DatabaseSource connection, string database, SchemaShape schema, Func<Task<SchemaShape?>> read, Action<TableShape> open,
        Func<IReadOnlyDictionary<string, (double X, double Y)>> places, Action<IReadOnlyDictionary<string, (double X, double Y)>> keep)
    {
        this.connection = connection;
        this.read = read;
        this.open = open;
        this.places = places;
        this.keep = keep;
        Database = database;
        Schema = schema;
        Document = OpenDocument.OfDatabase(this, Path.Combine(connection.Folder, $".passenger-diagram-{Guid.NewGuid():N}"));
    }

    public OpenDocument Document { get; }
    public Guid Connection => connection.Id;
    public string Database { get; }
    public SchemaShape Schema { get; private set; }

    /// <summary>Its view, once made.</summary>
    public DiagramCanvas? Shown { get; private set; }

    public string Title => Containers.ContainerSecrets.InSaid($"{(Schema.Schema.Length > 0 ? Schema.Schema : Database)} diagram · {connection.Name}");

    public string ShownAs => "a database schema's diagram";

    public bool Matches(DatabaseSource source, string database, string schema) => source.Id == connection.Id && Database == database && Schema.Schema == schema;

    public Control Make()
    {
        DiagramCanvas canvas = new(Schema, places()) { Name = "DatabaseDiagram" };
        Shown = canvas;
        canvas.Opened += (_, table) => open(table);
        canvas.Moved += (_, _) => keep(canvas.Places);
        DockPanel view = new();
        view.Children.Add(ToolBars.Top(new ToolFlow
        {
            Margin = new Thickness(Gaps.Normal, Gaps.Hair),
            Spacing = Gaps.Hair,
            Children =
            {
                ToolFlow.Spring(),
                ToolButtons.Of(IconKeys.ZoomIn, ActionTips.Named("Zoom In", KeyWords.Of(ZoomedPicture.ZoomInKey)), canvas.ZoomIn),
                ToolButtons.Of(IconKeys.ZoomOut, ActionTips.Named("Zoom Out", KeyWords.Of(ZoomedPicture.ZoomOutKey)), canvas.ZoomOut),
                ToolButtons.Of(IconKeys.ActualSize, ActionTips.Named("Actual Size", KeyWords.Of(ZoomedPicture.ActualSizeKey)), canvas.ActualSize),
                ToolButtons.Of(IconKeys.ZoomFit, "Fit Zoom to Window", canvas.Fit),
                Named(ToolButtons.Of(IconKeys.Rerun, "Read the Schema Again", () => IdeErrors.Observe(ReadAgainAsync())), "DatabaseDiagramReadAgain"),
                Named(ToolButtons.Of(IconKeys.Rollback, "Lay Out Again", () =>
                {
                    canvas.LayOutAgain();
                    keep(canvas.Places);
                }), "DatabaseDiagramLayOut"),
            },
        }));
        view.Children.Add(canvas);
        return view;

        static Button Named(Button button, string name)
        {
            button.Name = name;
            return button;
        }
    }

    /// <summary>Reads the schema again and draws what it holds now, its boxes where the person left them.</summary>
    public async Task ReadAgainAsync()
    {
        if (await read() is { } shape)
        {
            Schema = shape;
            Shown?.Show(shape);
            Document.DatabaseTitleChanged();
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>A schema's diagram shown at a zoom and a place: drawn once in its own units (<see cref="DiagramPanel"/>) and
/// moved and zoomed as a whole through its layer's transform, so panning and zooming lay nothing out again. The wheel pans,
/// with the command key it zooms about the pointer, and a drag on the ground pans; a click chooses a table, whose lines are
/// drawn over the rest, a drag on its box moves it, and a double click or Enter opens its rows.</summary>
public sealed class DiagramCanvas : Control
{
    private readonly Panel layer = new() { RenderTransformOrigin = RelativePoint.TopLeft };
    private readonly DiagramPanel panel;
    private double zoom = 1;
    private Vector offset = new(Gaps.Wide, Gaps.Wide);
    private Point? pressed;
    private Vector pressedOffset;
    private (string Table, double X, double Y)? moving;
    private bool dragged;

    public DiagramCanvas(SchemaShape schema, IReadOnlyDictionary<string, (double X, double Y)> places)
    {
        panel = new DiagramPanel(schema, places);
        layer.Children.Add(panel);
        VisualChildren.Add(layer);
        LogicalChildren.Add(layer);
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>A table to open, double clicked or chosen as Enter was pressed.</summary>
    public event EventHandler<TableShape>? Opened;

    /// <summary>A box was moved where the person left it.</summary>
    public event EventHandler? Moved;

    /// <summary>The zoom changed.</summary>
    public event EventHandler? Zoomed;

    public double Zoom => zoom;
    public Vector Offset => offset;
    public DiagramPanel Panel => panel;
    public IReadOnlyDictionary<string, (double X, double Y)> Places => panel.Moved;

    /// <summary>The table chosen, whose lines are drawn over the rest.</summary>
    public string? Chosen
    {
        get => panel.Chosen;
        set => panel.Chosen = value;
    }

    /// <summary>Draws the schema read again, its boxes where the person left them.</summary>
    public void Show(SchemaShape schema) => panel.Show(schema);

    /// <summary>Lays the boxes out as they first were, forgetting where the person left them.</summary>
    public void LayOutAgain() => panel.LayOutAgain();

    public void ZoomIn() => ZoomAbout(ZoomedPicture.StepIn(zoom), Middle);

    public void ZoomOut() => ZoomAbout(ZoomedPicture.StepOut(zoom), Middle);

    public void ActualSize() => ZoomAbout(1, Middle);

    /// <summary>The zoom at which the whole diagram fits, centred, never enlarged.</summary>
    public void Fit()
    {
        Rect content = panel.Content;
        if (content.Width <= 0 || Bounds.Width <= 0)
        {
            return;
        }
        zoom = Math.Min(1, Math.Min(Bounds.Width / (content.Width + 2 * Gaps.Wide), Bounds.Height / (content.Height + 2 * Gaps.Wide)));
        offset = new Vector((Bounds.Width - content.Width * zoom) / 2 - content.X * zoom, (Bounds.Height - content.Height * zoom) / 2 - content.Y * zoom);
        Place();
        Zoomed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A point of the control in the diagram's own units.</summary>
    public Point ToDiagram(Point at) => new((at.X - offset.X) / zoom, (at.Y - offset.Y) / zoom);

    /// <summary>Where a table's box stands in the control as it is shown now.</summary>
    public Rect? Shown(string table) => panel.Boxes.FirstOrDefault(b => b.Table.Name == table) is { } box
        ? new Rect(box.X * zoom + offset.X, box.Y * zoom + offset.Y, box.Width * zoom, box.Height * zoom)
        : null;

    private Point Middle => new(Bounds.Width / 2, Bounds.Height / 2);

    private void ZoomAbout(double to, Point at)
    {
        Point on = ToDiagram(at);
        zoom = Math.Clamp(to, 0.05, ZoomedPicture.Most);
        offset = new Vector(at.X - on.X * zoom, at.Y - on.Y * zoom);
        Place();
        Zoomed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The layer moved and zoomed to where the diagram stands: its transform alone.</summary>
    private void Place() => layer.RenderTransform = new MatrixTransform(Matrix.CreateScale(zoom, zoom) * Matrix.CreateTranslation(offset.X, offset.Y));

    protected override Size MeasureOverride(Size availableSize)
    {
        layer.Measure(Size.Infinity);
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        layer.Arrange(new Rect(layer.DesiredSize));
        Place();
        return finalSize;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        Focus();
        Point at = e.GetPosition(this);
        DiagramBox? box = panel.BoxAt(ToDiagram(at));
        if (e.ClickCount == 2 && box is not null)
        {
            Opened?.Invoke(this, box.Table);
            e.Handled = true;
            return;
        }
        pressed = at;
        pressedOffset = offset;
        dragged = false;
        moving = box is null ? null : (box.Table.Name, box.X, box.Y);
        if (box is not null)
        {
            Chosen = box.Table.Name;
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (pressed is not { } from)
        {
            return;
        }
        Vector by = e.GetPosition(this) - from;
        dragged |= Math.Abs(by.X) + Math.Abs(by.Y) > 4;
        if (!dragged)
        {
            return;
        }
        if (moving is { } box)
        {
            panel.MoveTo(box.Table, box.X + by.X / zoom, box.Y + by.Y / zoom);
        }
        else
        {
            offset = pressedOffset + by;
            Place();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (pressed is null)
        {
            return;
        }
        pressed = null;
        e.Pointer.Capture(null);
        if (dragged && moving is not null)
        {
            Moved?.Invoke(this, EventArgs.Empty);
        }
        moving = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if ((e.KeyModifiers & Keystroke.Command) != 0)
        {
            ZoomAbout(zoom * Math.Pow(1.2, e.Delta.Y), e.GetPosition(this));
        }
        else
        {
            offset += new Vector(e.Delta.X, e.Delta.Y) * 40;
            Place();
        }
        e.Handled = true;
    }

    /// <summary>The zoom's keys, and Enter or F4 opening the chosen table's rows.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        if (ZoomedPicture.ZoomKeyOf(e) is int step)
        {
            (step > 0 ? (Action)ZoomIn : step < 0 ? ZoomOut : ActualSize)();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key is ListKeys.OpenKey or ListKeys.SourceKey
            && Chosen is { } chosen && panel.Boxes.FirstOrDefault(b => b.Table.Name == chosen) is { } box)
        {
            Opened?.Invoke(this, box.Table);
            e.Handled = true;
        }
    }
}

/// <summary>A schema's diagram in its own units: its tables' boxes, framed and filled, measured as they are drawn and placed
/// by <see cref="DiagramLayout"/> (a box the person moved where they left it), and its foreign keys' lines drawn beneath
/// them, a dot at the referring end, the chosen table's in the accent's colour over the rest.</summary>
public sealed class DiagramPanel : Panel
{
    private readonly Dictionary<string, Control> boxes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double X, double Y)> moved;
    private readonly DiagramLines lines;
    private SchemaShape schema;
    private string? chosen;

    public DiagramPanel(SchemaShape schema, IReadOnlyDictionary<string, (double X, double Y)> places)
    {
        this.schema = schema;
        moved = new Dictionary<string, (double X, double Y)>(places, StringComparer.Ordinal);
        lines = new DiagramLines(this);
        Children.Add(lines);
        Show(schema);
    }

    public IReadOnlyList<DiagramBox> Boxes { get; private set; } = [];
    public IReadOnlyList<DiagramLine> Lines { get; private set; } = [];

    /// <summary>Where the person left the boxes they moved, by table.</summary>
    public IReadOnlyDictionary<string, (double X, double Y)> Moved => moved;

    /// <summary>What the boxes cover, in the diagram's units.</summary>
    public Rect Content => Boxes.Count == 0 ? default
        : new Rect(new Point(Boxes.Min(b => b.X), Boxes.Min(b => b.Y)), new Point(Boxes.Max(b => b.X + b.Width), Boxes.Max(b => b.Y + b.Height)));

    public string? Chosen
    {
        get => chosen;
        set
        {
            chosen = value;
            lines.InvalidateVisual();
        }
    }

    /// <summary>Draws a schema's tables, those it no longer holds gone and its new ones laid out.</summary>
    public void Show(SchemaShape shape)
    {
        schema = shape;
        foreach (Control box in boxes.Values)
        {
            Children.Remove(box);
        }
        boxes.Clear();
        foreach (TableShape table in shape.Tables)
        {
            Control box = Box(table);
            boxes[table.Name] = box;
            Children.Add(box);
        }
        InvalidateMeasure();
    }

    public void MoveTo(string table, double x, double y)
    {
        moved[table] = (x, y);
        InvalidateMeasure();
    }

    public void LayOutAgain()
    {
        moved.Clear();
        InvalidateMeasure();
    }

    /// <summary>The box at a point of the diagram, the one drawn last where boxes overlap.</summary>
    public DiagramBox? BoxAt(Point at) => Boxes.LastOrDefault(b => b.Contains(at.X, at.Y));

    protected override Size MeasureOverride(Size availableSize)
    {
        lines.Measure(Size.Infinity);
        foreach (Control box in boxes.Values)
        {
            box.Measure(Size.Infinity);
        }
        (Boxes, Lines) = DiagramLayout.Place(schema, table => boxes[table.Name].DesiredSize.Width, moved);
        lines.InvalidateVisual();
        return Boxes.Count == 0 ? default : new Size(Math.Max(0, Boxes.Max(b => b.X + b.Width)) + DiagramLayout.Across, Math.Max(0, Boxes.Max(b => b.Y + b.Height)) + DiagramLayout.Down);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        lines.Arrange(new Rect(finalSize));
        foreach (DiagramBox box in Boxes)
        {
            boxes[box.Table.Name].Arrange(new Rect(box.X, box.Y, box.Width, box.Height));
        }
        return finalSize;
    }

    /// <summary>The foreign keys' lines, beneath the boxes as the panel's first child.</summary>
    private sealed class DiagramLines(DiagramPanel owner) : Control
    {
        public override void Render(DrawingContext context)
        {
            IBrush muted = Tokens.Brush(this, TokenKeys.TextMuted);
            IBrush accent = Tokens.Brush(this, TokenKeys.Accent);
            foreach (DiagramLine line in owner.Lines.Where(l => !Of(l)))
            {
                Draw(context, line, muted, 1);
            }
            foreach (DiagramLine line in owner.Lines.Where(Of))
            {
                Draw(context, line, accent, 2);
            }
        }

        private bool Of(DiagramLine line) => owner.chosen is { } table && (line.From == table || line.To == table);

        private static void Draw(DrawingContext context, DiagramLine line, IBrush brush, double thickness)
        {
            Pen pen = new(brush, thickness);
            for (int i = 1; i < line.Points.Count; i++)
            {
                context.DrawLine(pen, new Point(line.Points[i - 1].X, line.Points[i - 1].Y), new Point(line.Points[i].X, line.Points[i].Y));
            }
            context.DrawEllipse(brush, null, new Point(line.Points[0].X, line.Points[0].Y), 3, 3);
        }
    }

    /// <summary>A table's box: its name over a row a column, each its name and type, the primary key's marked by a key.</summary>
    private static Control Box(TableShape table)
    {
        StackPanel rows = new();
        rows.Children.Add(Row(RowIcon.Glyph(IconKeys.DatabaseTable), new TextBlock { Text = table.Name, Classes = { StyleClasses.Strong } }, null));
        foreach (ColumnShape column in table.Columns)
        {
            RowIcon? key = table.PrimaryKey.Contains(column.Name) ? RowIcon.Glyph(IconKeys.DatabaseKey) : null;
            rows.Children.Add(Row(key, new TextBlock { Text = column.Name }, MutedText.Of(column.Nullable ? column.Type : column.Type + " not null")));
        }
        return new Border { Classes = { StyleClasses.Framed }, Child = rows };
    }

    private static Grid Row(RowIcon? icon, TextBlock name, TextBlock? type)
    {
        Grid row = new() { Height = DiagramLayout.RowHeight, ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(Gaps.Normal, 0) };
        // A row with no mark keeps its place, so every name starts in one column.
        RowIcon mark = icon ?? new RowIcon { Width = RowIcon.GlyphSize, Height = RowIcon.GlyphSize };
        mark.Margin = new Thickness(0, 0, Gaps.Small, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(mark);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        if (type is not null)
        {
            type.VerticalAlignment = VerticalAlignment.Center;
            type.Margin = new Thickness(Gaps.Wide, 0, 0, 0);
            type.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(type, 2);
            row.Children.Add(type);
        }
        return row;
    }
}
