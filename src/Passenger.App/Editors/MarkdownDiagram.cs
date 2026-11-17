using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Passenger.App.Editors;

/// <summary>A Mermaid block of a page, as its diagram: its code with "Drawing the diagram…" under it until the picture is
/// drawn (<see cref="MermaidDiagrams"/>), then the picture, no wider than the page and never enlarged; drawn again in the
/// other theme's colours when the theme changes. Should Mermaid fail, its code stays with what Mermaid said.</summary>
public sealed class MarkdownDiagram : StackPanel
{
    private readonly string source;
    private readonly Control code;
    private readonly TextBlock said = new() { Classes = { StyleClasses.Muted }, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Image picture = new() { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private bool? drawnDark;

    internal MarkdownDiagram(Markdown.Code block)
    {
        source = block.Text;
        code = MarkdownCode.Of(block);
        Spacing = 4;
        Classes.Add(StyleClasses.MdItem);
        RenderOptions.SetBitmapInterpolationMode(picture, BitmapInterpolationMode.HighQuality);
        Children.Add(picture);
        Children.Add(code);
        Children.Add(said);
        AttachedToVisualTree += (_, _) => Draw();
        ActualThemeVariantChanged += (_, _) => Draw();
    }

    /// <summary>Whether the picture shows in place of the code.</summary>
    public bool ShowsPicture => picture.IsVisible;

    private void Draw()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        if (drawnDark == dark || TopLevel.GetTopLevel(this) is null)
        {
            return;
        }
        drawnDark = dark;
        said.Text = MermaidDiagrams.Drawn(source, dark) ? string.Empty : "Drawing the diagram… (the first one downloads Mermaid's renderer)";
        IdeErrors.Observe(ShowAsync(dark));
    }

    private async Task ShowAsync(bool dark)
    {
        try
        {
            Bitmap drawn = await MermaidDiagrams.LoadAsync(source, dark);
            if (drawnDark != dark)
            {
                drawn.Dispose();
                return;
            }
            (picture.Source as IDisposable)?.Dispose();
            picture.Source = drawn;
            // Drawn at twice its size, for sharp lines on a screen of any density.
            picture.MaxWidth = drawn.PixelSize.Width / 2.0;
            picture.IsVisible = true;
            code.IsVisible = false;
            said.Text = string.Empty;
        }
        catch (Exception error) when (error is InvalidOperationException or TimeoutException or Win32Exception or IOException)
        {
            if (drawnDark == dark)
            {
                said.Text = $"Could not draw the diagram: {error.Message}";
                drawnDark = null;
            }
        }
    }
}
