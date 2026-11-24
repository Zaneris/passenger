using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.Database;

/// <summary>A schema's tables and their foreign keys drawn as a diagram among the editors (<see cref="DiagramLayout"/>): a
/// box a table, its name over its columns, the primary key's marked, and a line a foreign key, as the schema stood when
/// read.</summary>
public sealed class SchemaDiagramTab : IDatabaseTab
{
    private readonly DatabaseSource connection;

    public SchemaDiagramTab(DatabaseSource connection, string database, SchemaShape schema)
    {
        this.connection = connection;
        Database = database;
        Schema = schema;
        Document = OpenDocument.OfDatabase(this, Path.Combine(connection.Folder, $".passenger-diagram-{Guid.NewGuid():N}"));
    }

    public OpenDocument Document { get; }
    public Guid Connection => connection.Id;
    public string Database { get; }
    public SchemaShape Schema { get; }

    /// <summary>The boxes and lines its view last laid out.</summary>
    public DiagramPanel? Shown { get; private set; }

    public string Title => Containers.ContainerSecrets.InSaid($"{(Schema.Schema.Length > 0 ? Schema.Schema : Database)} diagram · {connection.Name}");

    public string ShownAs => "a database schema's diagram";

    public bool Matches(DatabaseSource source, string database, string schema) => source.Id == connection.Id && Database == database && Schema.Schema == schema;

    public Control Make()
    {
        DiagramPanel panel = new(Schema) { Name = "DatabaseDiagram", Margin = new Thickness(Gaps.Wide) };
        Shown = panel;
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = panel,
        };
    }

    public void Dispose()
    {
    }
}

/// <summary>A schema's diagram: its tables' boxes, framed and filled, measured as they are drawn and placed by
/// <see cref="DiagramLayout"/>, and its foreign keys' lines drawn beneath them, a dot at the referring end.</summary>
public sealed class DiagramPanel : Panel
{
    private readonly SchemaShape schema;
    private readonly Dictionary<string, Control> boxes = new(StringComparer.Ordinal);
    private readonly DiagramLines lines;

    public DiagramPanel(SchemaShape schema)
    {
        this.schema = schema;
        lines = new DiagramLines(this);
        Children.Add(lines);
        foreach (TableShape table in schema.Tables)
        {
            Control box = Box(table);
            boxes[table.Name] = box;
            Children.Add(box);
        }
    }

    public IReadOnlyList<DiagramBox> Boxes { get; private set; } = [];
    public IReadOnlyList<DiagramLine> Lines { get; private set; } = [];

    protected override Size MeasureOverride(Size availableSize)
    {
        lines.Measure(Size.Infinity);
        foreach (Control box in boxes.Values)
        {
            box.Measure(Size.Infinity);
        }
        (Boxes, Lines) = DiagramLayout.Place(schema, table => boxes[table.Name].DesiredSize.Width);
        return Boxes.Count == 0 ? default : new Size(Boxes.Max(b => b.X + b.Width) + DiagramLayout.Across, Boxes.Max(b => b.Y + b.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        lines.Arrange(new Rect(finalSize));
        lines.InvalidateVisual();
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
            IBrush brush = Tokens.Brush(this, TokenKeys.TextMuted);
            Pen pen = new(brush, 1);
            foreach (DiagramLine line in owner.Lines)
            {
                for (int i = 1; i < line.Points.Count; i++)
                {
                    context.DrawLine(pen, new Point(line.Points[i - 1].X, line.Points[i - 1].Y), new Point(line.Points[i].X, line.Points[i].Y));
                }
                context.DrawEllipse(brush, null, new Point(line.Points[0].X, line.Points[0].Y), 3, 3);
            }
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
