using System.Xml;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using Svg.Skia;

namespace Passenger.App.Editors;

/// <summary>An SVG document drawn by Svg.Skia as a picture of its own size, which a frame draws again at whatever size it is
/// given, so it stays sharp at every zoom. Read off the UI thread; what it draws is never let go while a frame may still
/// draw it, the collector freeing it once nothing holds it. Images it names resolve only on this machine, never over the
/// network.</summary>
public sealed class SvgPicture : IImage
{
    /// <summary>The most units a side of a drawing may measure, beyond which it is no picture to show.</summary>
    private const float Largest = 1 << 16;

    private readonly SKSvg svg;
    private readonly SKPicture picture;

    static SvgPicture()
    {
        Svg.SvgDocument.ResolveExternalImages = Svg.ExternalType.Local;
        Svg.SvgDocument.ResolveExternalElements = Svg.ExternalType.Local;
    }

    private SvgPicture(SKSvg svg, SKPicture picture)
    {
        this.svg = svg;
        this.picture = picture;
        Size = new Size(picture.CullRect.Width, picture.CullRect.Height);
    }

    /// <summary>Its size in units: the SVG's own width and height, else its view box's.</summary>
    public Size Size { get; }

    /// <summary>An SVG's text drawn, or why it cannot be: XML that does not read, another root than <c>&lt;svg&gt;</c>, or
    /// nothing of a size to draw.</summary>
    public static (SvgPicture? Picture, string? Problem) Read(string text)
    {
        SKSvg svg;
        try
        {
            if (RootOf(text) is not "svg" and var root)
            {
                return (null, root is null ? "The file holds no SVG to draw" : $"The file's root is <{root}>, where an SVG's is <svg>");
            }
            svg = SKSvg.CreateFromSvg(text);
        }
        catch (XmlException error)
        {
            return (null, $"The SVG's XML does not read: {error.Message}");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
        {
            return (null, $"The SVG could not be drawn: {error.Message}");
        }
        if (svg.Picture is not { } drawn || drawn.CullRect.Width <= 0 || drawn.CullRect.Height <= 0 || drawn.CullRect.Width > Largest || drawn.CullRect.Height > Largest)
        {
            return (null, "The SVG has nothing of a size to draw");
        }
        return (new SvgPicture(svg, drawn), null);
    }

    /// <summary>The local name of a text's first element, null for none; its document type read past, never followed.</summary>
    private static string? RootOf(string text)
    {
        using XmlReader reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return reader.MoveToContent() == XmlNodeType.Element ? reader.LocalName : null;
    }

    /// <inheritdoc/>
    public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) => context.Custom(new Drawing(this, sourceRect, destRect));

    /// <summary>The picture drawn into a frame's place, the part of it asked for scaled to fill it.</summary>
    private sealed class Drawing(SvgPicture owner, Rect source, Rect dest) : ICustomDrawOperation
    {
        public Rect Bounds => dest;

        public bool HitTest(Point p) => dest.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => other is Drawing drawn && drawn.Owner == owner && drawn.Source == source && drawn.Dest == dest;

        private SvgPicture Owner => owner;

        private Rect Source => source;

        private Rect Dest => dest;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature || source.Width <= 0 || source.Height <= 0)
            {
                return;
            }
            using ISkiaSharpApiLease lease = feature.Lease();
            SKCanvas canvas = lease.SkCanvas;
            SKRect cull = owner.picture.CullRect;
            canvas.Save();
            canvas.ClipRect(new SKRect((float)dest.Left, (float)dest.Top, (float)dest.Right, (float)dest.Bottom));
            canvas.Translate((float)dest.X, (float)dest.Y);
            canvas.Scale((float)(dest.Width / source.Width), (float)(dest.Height / source.Height));
            canvas.Translate((float)-source.X - cull.Left, (float)-source.Y - cull.Top);
            canvas.DrawPicture(owner.picture);
            canvas.Restore();
            GC.KeepAlive(owner.svg);
        }

        public void Dispose()
        {
        }
    }
}
