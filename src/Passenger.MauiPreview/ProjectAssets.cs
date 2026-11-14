using Avalonia.Controls.Maui.Controls;
using Avalonia.Controls.Maui.Handlers;
using Avalonia.Controls.Maui.Services;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using Microsoft.Maui;

namespace Passenger.MauiPreview;

/// <summary>The project's MauiFont files, which only a platform's build packs, read from where the project keeps them:
/// each face in a font collection of the preview's own, named by its file's name and by the alias the template's naming
/// gives it (OpenSans-Regular.ttf, OpenSansRegular), so a FontFamily the XAML names finds it.</summary>
internal sealed class ProjectFonts : IFontRegistrar
{
    private const string Key = "fonts:PassengerPreview";
    private readonly Dictionary<string, string> families = new(StringComparer.Ordinal);

    private ProjectFonts()
    {
    }

    /// <summary>The fonts in the folders given, each a TrueType or OpenType file.</summary>
    public static ProjectFonts Load(IEnumerable<string> folders)
    {
        ProjectFonts fonts = new();
        Collection collection = new();
        IEnumerable<string> files = folders.Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "*.ttf").Concat(Directory.EnumerateFiles(f, "*.otf")));
        foreach (string file in files)
        {
            using FileStream stream = File.OpenRead(file);
            if (collection.TryAddGlyphTypeface(stream, out GlyphTypeface? face))
            {
                string family = $"{Key}#{face.FamilyName}";
                fonts.families[Path.GetFileName(file)] = family;
                fonts.families[Path.GetFileNameWithoutExtension(file).Replace("-", string.Empty, StringComparison.Ordinal)] = family;
            }
        }
        Avalonia.Media.FontManager.Current.AddFontCollection(collection);
        return fonts;
    }

    public void Register(string filename, string? alias, System.Reflection.Assembly assembly) => Register(filename, alias);

    public void Register(string filename, string? alias)
    {
        if (alias is not null && families.TryGetValue(Path.GetFileName(filename), out string? family))
        {
            families[alias] = family;
        }
    }

    public string? GetFont(string font) => families.GetValueOrDefault(font);

    private sealed class Collection : FontCollectionBase
    {
        public override Uri Key { get; } = new(ProjectFonts.Key);
    }
}

/// <summary>An image the XAML names by file (dotnet_bot.png) found where the project keeps its MauiImage files: the file
/// itself, or the PNG of the same name an SVG's is made into by a platform's build where the build left one.</summary>
internal sealed class ProjectImages(IReadOnlyList<string> folders) : IImageSourceService<IFileImageSource>, IAvaloniaImageSourceService
{
    private readonly AvaloniaFileImageSourceService files = new();

    public Task<IImageSourceServiceResult<Avalonia.Media.Imaging.Bitmap>?> GetImageAsync(IImageSource imageSource, float scale = 1, CancellationToken cancellationToken = default) =>
        imageSource is IFileImageSource file && Find(file.File) is { } path
            ? files.GetImageAsync(new Microsoft.Maui.Controls.FileImageSource { File = path }, scale, cancellationToken)
            : files.GetImageAsync(imageSource, scale, cancellationToken);

    private string? Find(string name) => folders.Select(f => Path.Combine(f, name)).FirstOrDefault(File.Exists);
}

/// <summary>A view whose handler the preview has not (a platform view MauiProgram registers, a library's): drawn as the
/// backend's placeholder naming its type, at the size its layout gives it, so the rest of the page still draws.</summary>
internal sealed class UnhandledViewHandler() : ViewHandler<IView, PlaceholderControl>(Mapper, null!)
{
    public static readonly IPropertyMapper<IView, UnhandledViewHandler> Mapper = new PropertyMapper<IView, UnhandledViewHandler>(Microsoft.Maui.Handlers.ViewHandler.ViewMapper);

    protected override PlaceholderControl CreatePlatformView() => new(VirtualView.GetType().Name);
}
