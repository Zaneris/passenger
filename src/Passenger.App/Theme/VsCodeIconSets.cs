using Passenger.Core;

namespace Passenger.App;

/// <summary>The VS Code file icon themes Passenger themes draw their files and folders with (<c>iconTheme</c>): one fetched
/// from Open VSX by its extension's id (at a pinned version, else the newest) and read from its <c>.vsix</c>, or read
/// beside the theme's file; each kept beside its theme under <see cref="Folder"/> with every SVG it draws, so it draws
/// offline, and read from there as the theme is laid. Called on any thread.</summary>
public static class VsCodeIconSets
{
    /// <summary>The folder a theme's icon themes are kept in beside its file: one of an extension under its id, at their
    /// paths in the extension, and one beside the theme's file under <see cref="BesideFolder"/>, at their paths there.</summary>
    public const string Folder = "iconTheme";

    /// <summary>Where an icon theme named as a file beside its theme is kept under <see cref="Folder"/>, never an id.</summary>
    private const string BesideFolder = "beside";

    /// <summary>Where an icon theme's files are kept beside its theme's file, its folders joined by /.</summary>
    public static string KeptUnder(VsCodeBase wanted) => $"{Folder}/{wanted.Id ?? BesideFolder}";

    /// <summary>An icon theme fetched from Open VSX: its extension at the version pinned (else its newest), its <c>.vsix</c>
    /// and the theme in it with its SVGs; why not, saying which.</summary>
    public static async Task<(VsCodeIconTaken? Taken, string? Why)> FetchAsync(VsCodeBase wanted, CancellationToken cancel)
    {
        if (wanted is not { Id: { } id, Theme: { } label })
        {
            return (null, $"{wanted.File} is a file beside its theme's, fetched with it rather than from Open VSX");
        }
        (OpenVsxEntry? entry, string? missing) = await OpenVsx.ExtensionAsync(id, wanted.Version, cancel);
        if (entry is null)
        {
            return (null, $"{id} could not be had from Open VSX: {missing}");
        }
        (byte[]? vsix, string? why) = await OpenVsx.VsixAsync(entry, cancel);
        if (vsix is null)
        {
            return (null, $"{id} could not be had from Open VSX: {why}");
        }
        (VsCodeIconTaken? taken, string? refused) = VsCodeIconThemes.FromVsix(vsix, label);
        return taken is null ? (null, $"{id} {entry.Version}'s file icon theme {label} is refused: {refused}") : (taken, null);
    }

    /// <summary>The files of each icon theme a theme names as a file beside it, its JSON and every SVG it draws, each read by
    /// its path there through <paramref name="read"/> and named by where it is kept beside the theme's file (an SVG that
    /// cannot be read passed over, its files keeping Passenger's icons); why not where a JSON cannot be read or is refused.</summary>
    public static async Task<(Dictionary<string, byte[]> Files, string? Why)> BesideAsync(ThemeFile theme, Func<string, Task<(byte[]? Bytes, string? Why)>> read)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (VsCodeBase wanted in theme.IconThemes.Where(t => t.File is not null))
        {
            string json = wanted.File!;
            (byte[]? bytes, string? why) = await read(json);
            if (bytes is null)
            {
                return (files, $"{theme.Name}'s file icon theme {json} could not be read: {why}");
            }
            ThemeRead<VsCodeIconTheme> whole = VsCodeIconThemes.ReadTheme(json, NameOf(json), bytes);
            if (whole.Read is not { } icons)
            {
                return (files, $"{theme.Name}'s file icon theme is refused: {whole.Refused}");
            }
            files[$"{KeptUnder(wanted)}/{json}"] = bytes;
            foreach (string svg in icons.Icons.Values.Distinct(StringComparer.Ordinal))
            {
                (byte[]? drawn, string? unread) = await read(svg);
                if (drawn is not null && ThemeImages.IconWhyNot(svg, drawn) is null)
                {
                    files[$"{KeptUnder(wanted)}/{svg}"] = drawn;
                }
                else
                {
                    IdeErrors.Write($"Themes: {theme.Name}'s icon {svg} is passed over, so the files it stands for keep Passenger's icon: {unread ?? ThemeImages.IconWhyNot(svg, drawn!)}");
                }
            }
        }
        return (files, null);
    }

    /// <summary>Each icon theme a theme names by its extension's id fetched and kept beside it where it is not kept as named;
    /// why not.</summary>
    public static async Task<string?> KeepAsync(ThemeSource source, bool code, string name, ThemeFile theme, CancellationToken cancel)
    {
        foreach (VsCodeBase wanted in theme.IconThemes.Where(t => t.Id is not null))
        {
            if (Kept(wanted, path => KeptThemes.Beside(source, code, name, path), out _) is not null)
            {
                continue;
            }
            (VsCodeIconTaken? taken, string? why) = await FetchAsync(wanted, cancel);
            if ((taken is null ? why : KeptThemes.KeepUnder(source, code, name, KeptUnder(wanted), taken.Files)) is { } refused)
            {
                return refused;
            }
        }
        return null;
    }

    /// <summary>The version of an icon theme kept beside its theme by its extension's id, or null where none is kept.</summary>
    public static string? KeptVersion(VsCodeBase wanted, Func<string, byte[]?> beside) =>
        beside($"{KeptUnder(wanted)}/{VsCodeThemes.Manifest}") is { } manifest ? VsCodeThemes.ReadManifest(VsCodeThemes.Manifest, manifest).Read?.Version : null;

    /// <summary>An icon theme as kept beside its theme, its files read through <paramref name="beside"/> by their paths beside
    /// the theme's file, and what reads each SVG it draws by its path in the extension: null with no why where none is kept
    /// as named (another extension, label or version), and with why where what is kept does not read.</summary>
    public static (VsCodeIconTheme Theme, Func<string, byte[]?> Svg)? Kept(VsCodeBase wanted, Func<string, byte[]?> beside, out string? why)
    {
        why = null;
        string under = KeptUnder(wanted);
        string? json = wanted.File;
        string name = json is null ? wanted.Theme! : NameOf(json);
        if (json is null)
        {
            if (beside($"{under}/{VsCodeThemes.Manifest}") is not { } manifest
                || VsCodeThemes.ReadManifest(VsCodeThemes.Manifest, manifest).Read is not { } extension || extension.Id != wanted.Id
                || (wanted.Version is { } pinned && extension.Version != pinned)
                || (extension.IconThemes.FirstOrDefault(t => t.Label == wanted.Theme) ?? extension.IconThemes.FirstOrDefault(t => t.Id == wanted.Theme)) is not { } contributed)
            {
                return null;
            }
            (json, name) = (contributed.Path, contributed.Label);
        }
        if (beside($"{under}/{json}") is not { } bytes)
        {
            return null;
        }
        ThemeRead<VsCodeIconTheme> read = VsCodeIconThemes.ReadTheme(json, name, bytes);
        why = read.Refused;
        return read.Read is { } theme ? (theme, svg => beside($"{under}/{svg}")) : null;
    }

    /// <summary>An icon theme's name where it is a file beside its theme: the file's name without its extension.</summary>
    private static string NameOf(string json) => Path.GetFileNameWithoutExtension(json);
}
