using Avalonia.Platform;

namespace Passenger.App;

/// <summary>The themes and code colour schemes Passenger carries: a source like any other, its index and files under
/// Theme/BuiltIn carried as the app's resources and read through <see cref="ThemeFiles"/>, each file once. A built-in look's
/// file is named as its <see cref="Look"/> or <see cref="ColourScheme"/> is, and so is the theme or scheme in it. UI thread
/// only.</summary>
public static class BuiltInThemes
{
    private static readonly Uri Root = new("avares://Passenger/Theme/BuiltIn/");
    private static readonly Dictionary<string, ThemeRead<ThemeFile>> read = new(StringComparer.Ordinal);
    private static ThemeRead<ThemeIndex>? index;

    /// <summary>The built-in source's index, as read.</summary>
    public static ThemeRead<ThemeIndex> Index => index ??= ThemeFiles.ReadIndex(ThemeFiles.IndexFile, Bytes(ThemeFiles.IndexFile));

    /// <summary>A theme the index lists, as read.</summary>
    public static ThemeRead<ThemeFile> ReadTheme(string path) => Read(path, code: false);

    /// <summary>A code colour scheme the index lists, as read.</summary>
    public static ThemeRead<ThemeFile> ReadCodeColours(string path) => Read(path, code: true);

    /// <summary>The theme of a built-in look other than Passenger's own, which Tokens.axaml is.</summary>
    public static ThemeFile Theme(Look look) => Whole(look.ToString(), code: false);

    /// <summary>The code colour scheme of a built-in choice other than the theme's own.</summary>
    public static ThemeFile CodeColours(ColourScheme scheme) => Whole(scheme.ToString(), code: true);

    private static ThemeFile Whole(string name, bool code)
    {
        string path = $"{name}.json";
        ThemeIndex listed = Index.Read ?? throw new InvalidOperationException($"Passenger's built-in themes cannot be read: {Index.Refused}");
        if (!(code ? listed.CodeColours : listed.Themes).Contains(path, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"Passenger's built-in themes list no {path}");
        }
        ThemeRead<ThemeFile> file = Read(path, code);
        if (file.Read is not { } theme || file.Problems.Count > 0 || theme.Name != name)
        {
            throw new InvalidOperationException($"Passenger's built-in {path} does not read whole as {name}: {file.Refused ?? string.Join("; ", file.Problems)}");
        }
        return theme;
    }

    private static ThemeRead<ThemeFile> Read(string path, bool code)
    {
        if (!read.TryGetValue(path, out ThemeRead<ThemeFile>? file))
        {
            byte[] bytes = Bytes(path);
            file = code ? ThemeFiles.ReadCodeColours(path, bytes, ThemeResources.Kinds) : ThemeFiles.ReadTheme(path, bytes, ThemeResources.Kinds);
            read[path] = file;
        }
        return file;
    }

    private static byte[] Bytes(string path)
    {
        using Stream stream = AssetLoader.Open(new Uri(Root, path));
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
