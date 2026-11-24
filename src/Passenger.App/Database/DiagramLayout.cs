namespace Passenger.App.Database;

/// <summary>A table's box in a schema's diagram: where it stands and how big it is, its title row then a row a column.</summary>
public sealed record DiagramBox(TableShape Table, double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>A foreign key's line in a schema's diagram, from the referring column's row to the referred one's, as points.</summary>
public sealed record DiagramLine(string From, string To, IReadOnlyList<(double X, double Y)> Points);

/// <summary>A schema's tables laid out as a diagram: each in a column by how far its foreign keys reach (a table referring
/// to none first, then each table right of every table it refers to, a cycle broken where it closes), stacked in name order
/// within its column, unless the person moved it, where it stands where they left it; and each foreign key a line from the
/// side of the referring column's row facing the referred table, across a gap, to the referred column's row: below the
/// boxes standing between them, and one between boxes side by side looping round their right. Lines running through one
/// gap each take a lane of their own in it, so their ends stay apart.</summary>
public static class DiagramLayout
{
    public const double RowHeight = 24;

    /// <summary>A box's edge, a framed border's, above its title row and below its last.</summary>
    public const double Edge = 1;

    public const double Across = 56;
    public const double Down = 24;

    /// <summary>The room between two lanes running through one gap.</summary>
    public const double Lane = 6;

    public static (IReadOnlyList<DiagramBox> Boxes, IReadOnlyList<DiagramLine> Lines) Place(SchemaShape schema, Func<TableShape, double> width,
        IReadOnlyDictionary<string, (double X, double Y)>? moved = null)
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
                (double X, double Y) at = moved is not null && moved.TryGetValue(table.Name, out (double X, double Y) left) ? left : (x, y);
                boxes.Add(new DiagramBox(table, at.X, at.Y, widest, height));
                y += height + Down;
            }
            x += widest + Across;
        }
        Dictionary<string, DiagramBox> placed = boxes.ToDictionary(b => b.Table.Name, StringComparer.Ordinal);
        List<List<(double X, double Y)>> routes = [];
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
                bool left = to.X + to.Width < from.X;
                bool right = from.X + from.Width < to.X;
                List<(double X, double Y)> points;
                if (left || right)
                {
                    // The near sides facing each other, and the gaps just beside them.
                    double start = left ? from.X : from.X + from.Width;
                    double end = left ? to.X + to.Width : to.X;
                    double nearFrom = left ? from.X - Across / 2 : from.X + from.Width + Across / 2;
                    double nearTo = left ? to.X + to.Width + Across / 2 : to.X - Across / 2;
                    double low = Math.Min(nearFrom, nearTo);
                    double high = Math.Max(nearFrom, nearTo);
                    List<DiagramBox> between = [.. boxes.Where(b => b != from && b != to && b.X + b.Width > low && b.X < high)];
                    if (between.Count > 0)
                    {
                        double below = between.Max(b => b.Y + b.Height) + Down / 2 + lanes++ * Lane;
                        points = [(start, y1), (nearFrom, y1), (nearFrom, below), (nearTo, below), (nearTo, y2), (end, y2)];
                    }
                    else
                    {
                        points = [(start, y1), (nearTo, y1), (nearTo, y2), (end, y2)];
                    }
                }
                else
                {
                    double round = Math.Max(from.X + from.Width, to.X + to.Width) + Across / 3;
                    points = [(from.X + from.Width, y1), (round, y1), (round, y2), (to.X + to.Width, y2)];
                }
                routes.Add(points);
                lines.Add(new DiagramLine(from.Table.Name, to.Table.Name, points));
            }
        }
        Spread(routes);
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

    /// <summary>Each run down a gap (a route's vertical segment) moved into a lane of its own among the runs through the same
    /// gap, spread about its middle, so lines sharing a gap stay apart to their ends.</summary>
    private static void Spread(List<List<(double X, double Y)>> routes)
    {
        List<(List<(double X, double Y)> Route, int At)> runs = [];
        foreach (List<(double X, double Y)> route in routes)
        {
            for (int i = 1; i + 1 < route.Count; i++)
            {
                if (route[i].X == route[i + 1].X)
                {
                    runs.Add((route, i));
                }
            }
        }
        foreach (IGrouping<double, (List<(double X, double Y)> Route, int At)> gap in runs.GroupBy(r => Math.Round(r.Route[r.At].X)))
        {
            int count = gap.Count();
            int index = 0;
            foreach ((List<(double X, double Y)> route, int at) in gap)
            {
                double shift = (index++ - (count - 1) / 2.0) * Lane;
                route[at] = (route[at].X + shift, route[at].Y);
                route[at + 1] = (route[at + 1].X + shift, route[at + 1].Y);
            }
        }
    }

    /// <summary>The middle of a column's row in a box, the title's where it has no such column.</summary>
    private static double RowOf(DiagramBox box, string? column)
    {
        int at = column is null ? -1 : box.Table.Columns.ToList().FindIndex(c => c.Name == column);
        return box.Y + Edge + (at + 1) * RowHeight + RowHeight / 2;
    }
}
