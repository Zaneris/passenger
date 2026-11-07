using System.Text;
using System.Xml;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Passenger.Core;
using SkiaSharp;
using Svg;
using Svg.Model;
using Svg.Skia;

namespace Passenger.App.Editors;

/// <summary>An SVG document drawn by Svg.Skia as a picture of its own size, which a frame draws again at whatever size it is
/// given, so it stays sharp at every zoom. Read off the UI thread; what it draws is never let go while a frame may still
/// draw it, the collector freeing it once nothing holds it. The images and documents it names are read from its file's
/// folder and those below it alone, never from elsewhere on the disk or over the network, which it says it left out.</summary>
public sealed class SvgPicture : IImage
{
    /// <summary>The most units a side of a drawing may measure, beyond which it is no picture to show.</summary>
    private const float Largest = 1 << 16;

    /// <summary>How deep elements are drawn inside one another, past what any drawing is made of: Svg.Skia recurses as deep as
    /// they go, and a stack that runs out ends the process.</summary>
    private const int DeepestNesting = 256;

    private readonly SKSvg svg;
    private readonly SKPicture picture;

    static SvgPicture()
    {
        Svg.SvgDocument.ResolveExternalImages = Svg.ExternalType.Local;
        Svg.SvgDocument.ResolveExternalElements = Svg.ExternalType.Local;
    }

    private SvgPicture(SKSvg svg, SKPicture picture, string? leftOut)
    {
        this.svg = svg;
        this.picture = picture;
        LeftOut = leftOut;
        Size = new Size(picture.CullRect.Width, picture.CullRect.Height);
    }

    /// <summary>Its size in units: the SVG's own width and height, else its view box's.</summary>
    public Size Size { get; }

    /// <summary>What the picture leaves out of what its text names, and why; null where it leaves out nothing.</summary>
    public string? LeftOut { get; }

    /// <summary>An SVG's text drawn, what it names read from <paramref name="file"/>'s folder, or why it cannot be: XML that
    /// does not read, another root than <c>&lt;svg&gt;</c>, nesting past what is drawn, or nothing of a size to draw.</summary>
    public static (SvgPicture? Picture, string? Problem) Read(string text, string? file) => DeepText.Read(text.Length, () => Reading(text, file));

    private static (SvgPicture? Picture, string? Problem) Reading(string text, string? file)
    {
        SKSvg svg;
        try
        {
            if (RootOf(text) is not "svg" and var root)
            {
                return (null, root is null ? "The file holds no SVG to draw" : $"The file's root is <{root}>, where an SVG's is <svg>");
            }
            if (DeepText.TooDeep(text) || ElementsTooDeep(text))
            {
                return (null, "The SVG is nested too deeply to draw");
            }
            svg = new SKSvg();
            using MemoryStream bytes = new(Encoding.UTF8.GetBytes(text));
            SvgDocumentLoadOptions under = new() { ExternalResources = SvgExternalResourcePolicy.SameOrigin };
            svg.Load(bytes, new SvgParameters(null, null, null, under), file is not null && Path.IsPathFullyQualified(file) ? new Uri(file) : null);
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
        return (new SvgPicture(svg, drawn, svg.SourceDocument is { } document ? LeftOutOf(document) : null), null);
    }

    /// <summary>The addresses a document names that its file's folder does not hold, as written, with why each is left out;
    /// null for none.</summary>
    private static string? LeftOutOf(SvgDocument document)
    {
        List<string> outside = [];
        List<string> web = [];
        foreach (SvgElement element in document.Descendants())
        {
            if (element.TryGetEffectiveHrefString(out string href) && SvgExternalResourceResolver.TryResolveResourceUri(element, href, out Uri at)
                && !SvgExternalResourceResolver.AllowsExternalResource(element, at))
            {
                List<string> kind = at.IsAbsoluteUri && !at.IsFile ? web : outside;
                if (!kind.Contains(href.Trim(), StringComparer.Ordinal))
                {
                    kind.Add(href.Trim());
                }
            }
        }
        List<string> said = [];
        if (outside.Count > 0)
        {
            said.Add($"{Plural.Listed(outside)}, outside the SVG's folder");
        }
        if (web.Count > 0)
        {
            said.Add($"{Plural.Listed(web)}, on the web");
        }
        return said.Count == 0 ? null : $"Not drawn: {string.Join("; ", said)}";
    }

    /// <summary>The local name of a text's first element, null for none; its document type read past, never followed.</summary>
    private static string? RootOf(string text)
    {
        using XmlReader reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return reader.MoveToContent() == XmlNodeType.Element ? reader.LocalName : null;
    }

    /// <summary>Whether a text's elements go past those drawn, read as far as its XML reads.</summary>
    private static bool ElementsTooDeep(string text)
    {
        using XmlReader reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        while (reader.Read())
        {
            if (reader.Depth > DeepestNesting)
            {
                return true;
            }
        }
        return false;
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
