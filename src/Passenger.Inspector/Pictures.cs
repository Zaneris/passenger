using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Passenger.Inspector;

/// <summary>A picture of one of the app's windows as the app itself draws it (its visual tree rendered into a bitmap), so it
/// is the same on the screen, behind other windows, off it, or with no screen at all (a headless app); the popups open
/// over the window (a menu, a drop-down) are drawn where they are. Cropped to an element's part, or a part asked for.</summary>
internal static class Pictures
{
    private static MethodInfo? render;

    /// <summary>The longer side of a picture when no scale is asked for: the window's own scaling, less for a large one.</summary>
    private const int Longest = 1600;

    /// <summary>Pixels the whole window may take while it is drawn, before the crop: a scale asked for is lowered to fit.</summary>
    private const double MostPixels = 25_000_000;

    public static Picture Take(Visual shown, Request request)
    {
        TopLevel top = shown as TopLevel ?? TopLevel.GetTopLevel(shown) ?? throw new Refusal($"{Details.Describe(shown)} is in no window");
        top.UpdateLayout();
        Rect window = new(top.ClientSize);
        Rect area = request.Area is { } asked
            ? new Rect(asked.X, asked.Y, asked.Width, asked.Height).Intersect(window)
            : (Shown.RectIn(shown, top) ?? window).Intersect(window);
        if (area.Width < 1 || area.Height < 1)
        {
            throw new Refusal(request.Area is null
                ? string.Create(CultureInfo.InvariantCulture, $"{Shown.Called(shown)} is outside its window, which is {window.Width:0} by {window.Height:0}")
                : string.Create(CultureInfo.InvariantCulture, $"That part is outside the window, which is {window.Width:0} by {window.Height:0}"));
        }
        double scale = request.Scale ?? Math.Min(top.RenderScaling, Longest / Math.Max(area.Width, area.Height));
        scale = Math.Clamp(Math.Min(scale, Math.Sqrt(MostPixels / (window.Width * window.Height))), 0.05, 8);
        PixelSize size = new(Math.Max(1, (int)Math.Ceiling(window.Width * scale)), Math.Max(1, (int)Math.Ceiling(window.Height * scale)));
        using RenderTargetBitmap whole = new(size);
        using (DrawingContext context = whole.CreateDrawingContext())
        {
            Draw(context, top, default, scale);
            foreach (PopupRoot popup in Popups(top))
            {
                popup.UpdateLayout();
                Draw(context, popup, top.PointToClient(popup.PointToScreen(default)), scale);
            }
        }
        PixelRect part = PixelRect.FromRect(area, scale).Intersect(new PixelRect(size));
        using MemoryStream png = new();
        if (part.Width == size.Width && part.Height == size.Height)
        {
            whole.Save(png, PngBitmapEncoderOptions.Default);
        }
        else
        {
            // The pixels as they are, so the part keeps the bitmap's own layout.
            using WriteableBitmap cropped = new(part.Size, new Vector(96 * scale, 96 * scale), whole.Format ?? PixelFormat.Bgra8888, whole.AlphaFormat ?? AlphaFormat.Premul);
            using (ILockedFramebuffer target = cropped.Lock())
            {
                whole.CopyPixels(part, target.Address, target.RowBytes * part.Height, target.RowBytes);
            }
            cropped.Save(png, PngBitmapEncoderOptions.Default);
        }
        return new Picture(png.ToArray(), part.Width, part.Height, scale,
            new Box(part.X / scale, part.Y / scale, part.Width / scale, part.Height / scale));
    }

    /// <summary>A top level drawn as <see cref="RenderTargetBitmap.Render"/> draws one (by Avalonia's own name for that
    /// drawing), at a place in the window's units, into a bitmap of 96 DPI scaled by a transform: in a bitmap of more DPI,
    /// what a control draws after popping a clip of its own (an editor's text beside its gutter, a tab's title) would be
    /// scaled twice over, off the picture.</summary>
    private static void Draw(DrawingContext context, TopLevel shown, Point at, double scale)
    {
        render ??= typeof(Visual).Assembly.GetType("Avalonia.Rendering.ImmediateRenderer")
            ?.GetMethod("Render", BindingFlags.Public | BindingFlags.Static, [typeof(DrawingContext), typeof(Visual)])
            ?? throw new MissingMethodException("Avalonia's ImmediateRenderer no longer has Render(DrawingContext, Visual)");
        using (context.PushTransform(Matrix.CreateTranslation(at) * Matrix.CreateScale(scale, scale)))
        {
            render.Invoke(null, BindingFlags.DoNotWrapExceptions, null, [context, shown], CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The popups shown over a window, and theirs over them, from the top levels Avalonia draws.</summary>
    private static IEnumerable<PopupRoot> Popups(TopLevel top) =>
        AvaloniaHooks.Drawn().OfType<PopupRoot>().Where(popup => popup.IsVisible && AvaloniaHooks.Owner(popup) == top);
}
