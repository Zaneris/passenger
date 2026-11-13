using Avalonia.Platform;

namespace Passenger.App;

/// <summary>The themes and code colour schemes Passenger carries: a source like any other, its index and files under
/// Theme/BuiltIn carried as the app's resources and read through <see cref="ThemeFiles"/>, each file once. Passenger's own
/// theme is Tokens.axaml, under every other, so this lists only themes beside it. Called on any thread.</summary>
public static class BuiltInThemes
{
    private static readonly Uri Root = new("avares://Passenger/Theme/BuiltIn/");
    private static readonly Lazy<ThemeRead<ThemeIndex>> index = new(() => ThemeFiles.ReadIndex(ThemeFiles.IndexFile, Read(ThemeFiles.IndexFile)));

    /// <summary>The built-in source's index, as read.</summary>
    public static ThemeRead<ThemeIndex> Index => index.Value;

    /// <summary>The built-in source as a source read: every file it lists, its tokens read against <paramref name="kinds"/>.</summary>
    public static SourceRead Listed(IReadOnlyDictionary<string, ThemeTokenKind> kinds)
    {
        if (Index.Read is not { } listed)
        {
            return new SourceRead(ThemeSource.BuiltIn, Index.Refused, [], Index.Problems);
        }
        return new SourceRead(ThemeSource.BuiltIn, null, [.. listed.Themes.Select(p => File(p, false, kinds)).Concat(listed.CodeColours.Select(p => File(p, true, kinds)))], Index.Problems);
    }

    /// <summary>The file of a built-in theme or scheme of a name, or null where none is carried.</summary>
    public static byte[]? Bytes(bool code, string name) =>
        Listed(ThemeResources.Kinds).Listed.FirstOrDefault(l => l.Code == code && l.Read.Read?.Name == name)?.Bytes;

    private static ListedTheme File(string path, bool code, IReadOnlyDictionary<string, ThemeTokenKind> kinds)
    {
        byte[] bytes = Read(path);
        ThemeRead<ThemeFile> read = code ? ThemeFiles.ReadCodeColours(path, bytes, kinds) : ThemeFiles.ReadTheme(path, bytes, kinds);
        return new ListedTheme(ThemeSource.BuiltIn, path, code, read, read.Read is null ? null : bytes);
    }

    private static byte[] Read(string path)
    {
        using Stream stream = AssetLoader.Open(new Uri(Root, path));
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }
}
