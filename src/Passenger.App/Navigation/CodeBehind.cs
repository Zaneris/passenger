using Passenger.Languages;

namespace Passenger.App.Navigation;

/// <summary>A XAML or Razor file and its code-behind beside it, named after it (MainWindow.axaml.cs, Counter.razor.cs,
/// Index.cshtml.cs): Go to Related File goes from either to the other.</summary>
public static class CodeBehind
{
    private const string Extension = ".cs";

    /// <summary>The other file of the pair a file is in, while it is on disk; null for a file in none.</summary>
    public static string? PartnerOf(string path)
    {
        string partner = path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? path[..^Extension.Length] : path + Extension;
        string markup = partner.Length < path.Length ? partner : path;
        return IsMarkup(markup) && File.Exists(partner) ? partner : null;
    }

    private static bool IsMarkup(string path) => Language.ForPath(path) is var language && (language == Language.Xaml || language == Language.Razor);
}
