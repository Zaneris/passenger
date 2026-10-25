using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Workspace;

namespace Passenger.App.Editors;

/// <summary>A group's own view of a document's shared image or local font face. The control owns its glyph runs,
/// not the asset, so closing one split leaves the other splits' previews intact.</summary>
public sealed class AssetPreviewControl : DockPanel, IDisposable
{
    private const string Uppercase = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lowercase = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";
    private const string Symbols = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
    private const double Gap = 8;
    private const double SampleSize = 30;

    private readonly List<FontSample> samples = [];

    /// <summary>Shows the borrowed asset as a fitted image with dimensions, or as labelled samples of its own font; a
    /// stand-in whose decode is still under way as the ring that says so, which the group replaces once it lands.</summary>
    public AssetPreviewControl(AssetPreview asset)
    {
        Focusable = true;
        LastChildFill = true;
        Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Editor_Background));
        if (asset.IsPending)
        {
            TextBlock loading = new() { Text = asset.Kind == AssetKind.Font ? "Loading the font…" : "Loading the image…", Margin = default };
            loading.Classes.Add("emptyText");
            StackPanel waiting = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = Gap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { new WorkingRing { Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center }, loading },
            };
            AutomationProperties.SetName(waiting, loading.Text);
            Children.Add(waiting);
            return;
        }
        if (asset.Bitmap is { } bitmap)
        {
            DockPanel picture = new() { Margin = new Thickness(2 * Gap) };
            TextBlock dimensions = Label($"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} pixels"
                + (asset.Frames > 1 ? $" · the first of its {asset.Frames} frames" : ""));
            SetDock(dimensions, Dock.Top);
            picture.Children.Add(dimensions);
            // Shrunk to fit but never enlarged, so a small icon shows as it is rather than blurred, its bounds the picture's.
            Image image = new()
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                StretchDirection = StretchDirection.DownOnly,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(Gap),
            };
            AutomationProperties.SetName(image, "Image preview");
            picture.Children.Add(image);
            Children.Add(picture);
            return;
        }
        if (asset.Typeface is not { } typeface)
        {
            return;
        }
        StackPanel content = new() { Spacing = 12, Margin = new Thickness(2 * Gap) };
        content.Children.Add(Label($"{typeface.FamilyName} · Local font"));
        AddSample(content, typeface, "Uppercase A–Z", Uppercase);
        AddSample(content, typeface, "Lowercase a–z", Lowercase);
        AddSample(content, typeface, "Digits 0–9", Digits);
        AddSample(content, typeface, "Printable ASCII punctuation and symbols", Symbols);
        string missing = string.Join(" ", (Uppercase + Lowercase + Digits + Symbols + " ")
            .Where(character => typeface.CharacterToGlyphMap.GetGlyph(character) == 0)
            .Select(character => $"U+{(int)character:X4}"));
        content.Children.Add(Label(missing.Length == 0 ? "All printable ASCII glyphs are present." : $"Missing glyphs (no substitution): {missing}"));
        Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content,
        });
    }

    private void AddSample(StackPanel content, GlyphTypeface typeface, string label, string text)
    {
        content.Children.Add(Label(label));
        FontSample sample = new(typeface, text);
        samples.Add(sample);
        content.Children.Add(sample);
    }

    private static TextBlock Label(string text)
    {
        TextBlock label = new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(Gap) };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable(TokenKeys.Text));
        return label;
    }

    /// <summary>Releases this group's glyph runs and visuals, leaving the document's shared asset alive.</summary>
    public void Dispose()
    {
        foreach (FontSample sample in samples)
        {
            sample.Dispose();
        }
        samples.Clear();
        Children.Clear();
    }

    /// <summary>A sample drawn from the local face's glyphs directly, so missing characters cannot use a fallback font.</summary>
    private sealed class FontSample : TemplatedControl, IDisposable
    {
        private readonly GlyphRun run;
        private readonly Rect bounds;

        static FontSample() => AffectsRender<FontSample>(ForegroundProperty);

        public FontSample(GlyphTypeface typeface, string text)
        {
            ushort[] glyphs = [.. text.Select(character => typeface.CharacterToGlyphMap.GetGlyph(character))];
            run = new GlyphRun(typeface, SampleSize, text.AsMemory(), glyphs);
            bounds = run.Bounds.Union(run.InkBounds);
            AutomationProperties.SetName(this, text);
            Bind(ForegroundProperty, this.GetResourceObservable(TokenKeys.Text));
        }

        /// <inheritdoc/>
        protected override Size MeasureOverride(Size availableSize) => new(bounds.Width + (2 * Gap), bounds.Height + (2 * Gap));

        /// <inheritdoc/>
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            using (context.PushTransform(Matrix.CreateTranslation(Gap - bounds.X, Gap - bounds.Y)))
            {
                context.DrawGlyphRun(Foreground, run);
            }
        }

        /// <inheritdoc/>
        public void Dispose() => run.Dispose();
    }
}
