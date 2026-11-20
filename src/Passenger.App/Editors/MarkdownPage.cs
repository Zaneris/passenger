using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Passenger.App.Editors;

/// <summary>What a page of rendered Markdown opens and draws: release notes (What's New) a web link alone and no image; a
/// file's preview also its relative links and <c>#heading</c>s, and the images of files inside the open folder, which it
/// loads itself (<see cref="Load"/>), a <c>picture</c>'s for the look the page is shown in. Every link a page draws is
/// handed to <see cref="Follow"/>.</summary>
internal sealed class MarkdownPage(Action<Uri> follow)
{
    public Action<Uri> Follow { get; } = follow;

    /// <summary>Whether a link to a file beside the page's (or a heading of it) is one: a file's preview only.</summary>
    public bool LocalLinks { get; init; }

    /// <summary>The file an image's target names when the page may draw it, else null (a web image, one outside the open
    /// folder, a file of no image type or none there); null on a page that draws no image.</summary>
    public Func<string, string?>? ImageFile { get; init; }

    /// <summary>A picture's decode, off the window's thread, null when it could not be read.</summary>
    public Func<string, Task<Bitmap?>>? Load { get; init; }

    /// <summary>What a link's target opens: a web address, or on a page of local links a relative one (a file beside it or
    /// a heading); null for anything else, which is drawn as its words.</summary>
    public Uri? LinkOf(string target)
    {
        if (Markdown.Web(target) is { } web)
        {
            return web;
        }
        return LocalLinks && Local(target) is { } written && Uri.TryCreate(written, UriKind.Relative, out Uri? local) ? local : null;
    }

    /// <summary>A link's or image's target as written when it names a place in the open folder: a path from the page's file,
    /// one from the folder's root (a single slash first, which macOS and Linux would take for an absolute file address), or
    /// a heading; null for an address of any scheme and for one beginning with two slashes (another host's).</summary>
    public static string? Local(string target)
    {
        string written = target.Trim().Trim('<', '>');
        return written.Length > 0 && !written.StartsWith("//", StringComparison.Ordinal)
            && (written.StartsWith('/') || !Uri.TryCreate(written, UriKind.Absolute, out _))
            ? written
            : null;
    }

    /// <summary>The files an image is drawn from in the light look and the dark, where the page draws one: each look's
    /// source where the page draws that, else the image's own, else the other look's; null where it draws none.</summary>
    public (string Light, string Dark)? FilesOf(Markdown.Image image)
    {
        if (ImageFile is not { } fileOf)
        {
            return null;
        }
        string? own = fileOf(image.Target);
        string? light = (image.Light is { } lit && lit != image.Target ? fileOf(lit) : null) ?? own;
        string? dark = (image.Dark is { } darkened && darkened != image.Target ? fileOf(darkened) : null) ?? own;
        return light is null && dark is null ? null : (light ?? dark!, dark ?? light!);
    }

    /// <summary>An image of files the page may draw, its words standing for it until it is read and should it not read.</summary>
    public Control Picture(Markdown.Image image, (string Light, string Dark) files) =>
        new MarkdownPicture(files, image.Alt, Load) { Widest = image.Width, Tallest = image.Height };
}

/// <summary>A picture of a file in a page, the light look's or the dark's as the page shows, no wider than the page and never
/// enlarged but to a width or height it was given; its words in its place until its decode lands off the window's
/// thread, and should it fail. It holds the picture only while it is shown, and asks for it again as it comes back and
/// as the look changes.</summary>
public sealed class MarkdownPicture : Panel
{
    private readonly Image image;
    private readonly TextBlock alt;
    private readonly Func<string, Task<Bitmap?>>? load;
    private readonly (string Light, string Dark) files;
    private bool onScreen;

    /// <summary>The file asked for last, whose decode alone is drawn.</summary>
    private string? asked;

    internal MarkdownPicture((string Light, string Dark) files, string words, Func<string, Task<Bitmap?>>? load)
    {
        this.files = files;
        this.load = load;
        Classes.Add(StyleClasses.MdPicture);
        image = new Image
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
        };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        alt = new TextBlock { Classes = { StyleClasses.Muted }, Text = words.Length > 0 ? words : Path.GetFileName(files.Dark), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(this, alt.Text);
        Children.Add(image);
        Children.Add(alt);
        ActualThemeVariantChanged += (_, _) => OnLookChanged();
    }

    /// <summary>The file it draws in the look it is shown in.</summary>
    public string File => ActualThemeVariant == ThemeVariant.Light ? files.Light : files.Dark;

    /// <summary>The picture drawn, null until it is read and when it could not be.</summary>
    public Bitmap? Shown => image.Source as Bitmap;

    /// <summary>The width it is drawn at where HTML gave one, enlarged to it or shrunk to the page; null for the picture's own.</summary>
    public double? Widest
    {
        get => double.IsFinite(image.MaxWidth) ? image.MaxWidth : null;
        init => Fit(value, Layoutable.MaxWidthProperty);
    }

    /// <summary>The height it is drawn at where HTML gave one; null for the picture's own.</summary>
    public double? Tallest
    {
        get => double.IsFinite(image.MaxHeight) ? image.MaxHeight : null;
        init => Fit(value, Layoutable.MaxHeightProperty);
    }

    private void Fit(double? size, AvaloniaProperty<double> limit)
    {
        if (size is { } given)
        {
            image.SetValue(limit, given);
            image.StretchDirection = StretchDirection.Both;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        onScreen = true;
        Ask();
    }

    private void OnLookChanged()
    {
        if (onScreen && asked != File)
        {
            image.Source = null;
            image.IsVisible = false;
            alt.IsVisible = true;
            Ask();
        }
    }

    /// <summary>Asks for the look's file drawn.</summary>
    private void Ask()
    {
        asked = File;
        if (load is not null)
        {
            IdeErrors.Observe(ShowAsync(asked, load(asked)));
        }
    }

    /// <summary>Off screen it is its words again: the page lets its pictures go.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        onScreen = false;
        asked = null;
        image.Source = null;
        image.IsVisible = false;
        alt.IsVisible = true;
    }

    private async Task ShowAsync(string file, Task<Bitmap?> loading)
    {
        if (await loading is { } bitmap && onScreen && file == asked)
        {
            image.Source = bitmap;
            image.IsVisible = true;
            alt.IsVisible = false;
        }
    }
}
