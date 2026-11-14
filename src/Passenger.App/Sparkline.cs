using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A small chart of the last readings of one value, or of a few read together (a vector's parts), each a line in a
/// colour token of its own, all on one scale: from zero to the highest reading (at least <see cref="Least"/>) where it counts from zero, else
/// from the lowest reading to the highest. Each reading takes one place of <see cref="Kept"/> across, filling from the left
/// edge, or with the newest at the right edge where it ends at now; past <see cref="Kept"/> the oldest goes.</summary>
public sealed class Sparkline(int kept, bool fromZero, bool fromRight, params string[] brushes) : Control
{
    private readonly List<double[]> readings = [];

    /// <summary>The least its scale reaches up to where it counts from zero: 1 for a percentage, nothing for a value of any size.</summary>
    public double Least { get; init; } = 1;

    /// <summary>How wide it is where it has the room, in less as wide as the room; 0 for the width its place gives it.</summary>
    public double Wanted { get; init; }

    /// <summary>How many readings it keeps, and so how many places it has across.</summary>
    public int Kept => kept;

    /// <summary>How many readings it holds.</summary>
    public int Count => readings.Count;

    /// <summary>The colour token of each line, in the order a reading gives its values.</summary>
    public IReadOnlyList<string> Brushes => brushes;

    /// <summary>The readings it holds, the oldest first, each a value for each line.</summary>
    public IReadOnlyList<double[]> Readings => readings;

    /// <summary>Adds a reading, a value for each line.</summary>
    public void Add(params double[] values)
    {
        readings.Add(values);
        if (readings.Count > kept)
        {
            readings.RemoveAt(0);
        }
        InvalidateVisual();
    }

    public void Clear()
    {
        readings.Clear();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(Math.Min(Wanted, availableSize.Width), 0);

    public override void Render(DrawingContext context)
    {
        if (readings.Count < 2)
        {
            return;
        }
        double low = fromZero ? 0 : double.MaxValue;
        double high = fromZero ? Least : double.MinValue;
        foreach (double[] reading in readings)
        {
            foreach (double value in reading)
            {
                low = Math.Min(low, value);
                high = Math.Max(high, value);
            }
        }
        if (high - low < 1e-9)
        {
            // A value that stood still is a level line halfway up.
            low -= 1;
            high += 1;
        }
        Rect area = new Rect(Bounds.Size).Deflate(4);
        int first = fromRight ? kept - readings.Count : 0;
        for (int line = 0; line < brushes.Length; line++)
        {
            if (Tokens.Find(this, brushes[line]) is not { } colour)
            {
                continue;
            }
            StreamGeometry path = new();
            using (StreamGeometryContext draw = path.Open())
            {
                for (int i = 0; i < readings.Count; i++)
                {
                    double value = line < readings[i].Length ? readings[i][line] : low;
                    Point at = new(area.Left + (area.Width * (first + i) / (kept - 1)), area.Bottom - (area.Height * (value - low) / (high - low)));
                    if (i == 0)
                    {
                        draw.BeginFigure(at, isFilled: false);
                    }
                    else
                    {
                        draw.LineTo(at);
                    }
                }
                draw.EndFigure(isClosed: false);
            }
            context.DrawGeometry(null, new Pen(colour, 1.5), path);
        }
    }
}
