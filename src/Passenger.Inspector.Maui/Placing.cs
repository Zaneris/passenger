namespace Passenger.Inspector.Maui;

/// <summary>Where a MAUI app's elements are in their window, worked out from numbers alone: a platform's pixels as the
/// window's units, a point of the screen as one of the window, a point as the pixel a touch is given at, and the element a
/// finger at a point reaches. Nothing here touches MAUI.</summary>
internal static class Placing
{
    /// <summary>A box in the window's units from a view's place and size in the window's pixels, at the window's pixels a unit.</summary>
    public static Box InUnits(int x, int y, int width, int height, double density) =>
        density > 0 ? Words.Boxed(x / density, y / density, width / density, height / density) : Words.Boxed(x, y, width, height);

    /// <summary>A point of the screen in units as a point of the window, by where a view of the window is on the screen and
    /// in the window, in pixels.</summary>
    public static (double X, double Y) InWindow(double x, double y, (int X, int Y) onScreen, (int X, int Y) inWindow, double density) =>
        density > 0 ? (x - ((onScreen.X - inWindow.X) / density), y - ((onScreen.Y - inWindow.Y) / density)) : (x, y);

    /// <summary>The window's pixel a point in its units falls on, where a touch is given.</summary>
    public static (float X, float Y) Pixel(double x, double y, double density) =>
        density > 0 ? ((float)(x * density), (float)(y * density)) : ((float)x, (float)y);

    /// <summary>The element a finger at a point of the window reaches, of those under <paramref name="root"/>: the last one
    /// drawn, deepest, whose box holds the point. An element with no box of its own (a shell's item) is passed through to
    /// what it holds, and what one not <paramref name="reached"/> holds (hidden, taking no input) is passed over with it, as
    /// is what one holds whose box misses the point. A walk with a stack of its own, so no tree is too deep for it.</summary>
    public static T? Deepest<T>(T root, Func<T, IReadOnlyList<T>> held, Func<T, Box?> box, Func<T, bool> reached, double x, double y) where T : class
    {
        T? hit = null;
        // Each level walked into, outermost first, with how many of its elements are walked.
        List<(IReadOnlyList<T> Elements, int Next)> open = [([root], 0)];
        while (open.Count > 0)
        {
            (IReadOnlyList<T> elements, int next) = open[^1];
            if (next >= elements.Count)
            {
                open.RemoveAt(open.Count - 1);
                continue;
            }
            open[^1] = (elements, next + 1);
            T element = elements[next];
            if (!reached(element))
            {
                continue;
            }
            if (box(element) is { } placed)
            {
                if (!Holds(placed, x, y))
                {
                    continue;
                }
                hit = element;
            }
            open.Add((held(element), 0));
        }
        return hit;
    }

    /// <summary>Whether a box holds a point, its top and left edges included and its bottom and right not, as a platform
    /// gives a touch on an edge to the element after it.</summary>
    public static bool Holds(Box box, double x, double y) => x >= box.X && x < box.X + box.Width && y >= box.Y && y < box.Y + box.Height;
}
