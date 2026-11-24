namespace Passenger.App.Database;

/// <summary>A table's box in a schema's diagram: where it stands and how big it is, its title row then a row a column.</summary>
public sealed record DiagramBox(TableShape Table, double X, double Y, double Width, double Height);

/// <summary>A foreign key's line in a schema's diagram, from the referring column's row to the referred one's, as points.</summary>
public sealed record DiagramLine(string From, string To, IReadOnlyList<(double X, double Y)> Points);

/// <summary>A schema's tables laid out as a diagram: each in a column by how far its foreign keys reach (a table referring
/// to none first, then each table right of every table it refers to, a cycle broken where it closes), stacked in name order
/// within its column; and each foreign key a line from the left of the referring column's row, across the gap before its
/// table, to the right of the referred column's row: below the boxes of any column between them, and one within a
/// column looping round its right.</summary>
public static class DiagramLayout
{
    public const double RowHeight = 24;

    /// <summary>A box's edge, a framed border's, above its title row and below its last.</summary>
    public const double Edge = 1;

    public const double Across = 56;
    public const double Down = 24;

    public static (IReadOnlyList<DiagramBox> Boxes, IReadOnlyList<DiagramLine> Lines) Place(SchemaShape schema, Func<TableShape, double> width)
    {
        Dictionary<string, TableShape> tables = schema.Tables.ToDictionary(t => t.Name, StringComparer.Ordinal);
        Dictionary<string, int> levels = new(StringComparer.Ordinal);
        foreach (TableShape table in schema.Tables)
        {
            Level(table.Name, []);
        }
        List<DiagramBox> boxes = [];
        double x = 0;
        foreach (IGrouping<int, TableShape> column in schema.Tables.GroupBy(t => levels[t.Name]).OrderBy(g => g.Key))
        {
            double widest = column.Max(width);
            double y = 0;
            foreach (TableShape table in column.OrderBy(t => t.Name, StringComparer.Ordinal))
            {
                double height = (table.Columns.Count + 1) * RowHeight + 2 * Edge;
                boxes.Add(new DiagramBox(table, x, y, widest, height));
                y += height + Down;
            }
            x += widest + Across;
        }
        Dictionary<string, DiagramBox> placed = boxes.ToDictionary(b => b.Table.Name, StringComparer.Ordinal);
        List<DiagramLine> lines = [];
        int lanes = 0;
        foreach (DiagramBox from in boxes)
        {
            foreach (ForeignKeyShape key in from.Table.ForeignKeys)
            {
                if (!placed.TryGetValue(key.Table, out DiagramBox? to))
                {
                    continue;
                }
                double y1 = RowOf(from, key.Columns.FirstOrDefault());
                double y2 = RowOf(to, key.To.FirstOrDefault());
                List<DiagramBox> between = [.. boxes.Where(b => b.X > to.X && b.X < from.X)];
                if (between.Count > 0)
                {
                    // Past a column between, below its boxes, each such line in a lane of its own.
                    double lane = between.Max(b => b.Y + b.Height) + Down / 2 + lanes++ * Down / 4;
                    double left = to.X + to.Width + Across / 2;
                    lines.Add(new DiagramLine(from.Table.Name, to.Table.Name,
                        [(from.X, y1), (from.X - Across / 2, y1), (from.X - Across / 2, lane), (left, lane), (left, y2), (to.X + to.Width, y2)]));
                    continue;
                }
                lines.Add(new DiagramLine(from.Table.Name, to.Table.Name, to.X < from.X
                    ? [(from.X, y1), (from.X - Across / 2, y1), (from.X - Across / 2, y2), (to.X + to.Width, y2)]
                    : to.X > from.X
                        ? [(from.X + from.Width, y1), (to.X - Across / 2, y1), (to.X - Across / 2, y2), (to.X, y2)]
                        : [(from.X + from.Width, y1), (from.X + from.Width + Across / 3, y1), (from.X + from.Width + Across / 3, y2), (to.X + to.Width, y2)]));
            }
        }
        return (boxes, lines);

        int Level(string name, HashSet<string> visiting)
        {
            if (levels.TryGetValue(name, out int known))
            {
                return known;
            }
            if (!tables.TryGetValue(name, out TableShape? table) || !visiting.Add(name))
            {
                return -1;
            }
            int level = table.ForeignKeys.Where(k => k.Table != name).Select(k => Level(k.Table, visiting)).DefaultIfEmpty(-1).Max() + 1;
            visiting.Remove(name);
            levels[name] = level;
            return level;
        }
    }

    /// <summary>The middle of a column's row in a box, the title's where it has no such column.</summary>
    private static double RowOf(DiagramBox box, string? column)
    {
        int at = column is null ? -1 : box.Table.Columns.ToList().FindIndex(c => c.Name == column);
        return box.Y + Edge + (at + 1) * RowHeight + RowHeight / 2;
    }
}
