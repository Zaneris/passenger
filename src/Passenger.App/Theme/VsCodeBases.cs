namespace Passenger.App;

/// <summary>The VS Code colour themes Passenger themes are laid over (<see cref="VsCodeBase"/>): one taken from a
/// <c>.vsix</c> by its label (<see cref="VsCodeThemes.FromVsix"/>), fetched from Open VSX by its extension's id (at a pinned version, else the newest) and kept
/// beside its theme (<see cref="KeptThemes.BaseFiles"/>), and read from there as the theme is laid, so it draws offline.
/// Called on any thread.</summary>
public static class VsCodeBases
{
    /// <summary>A base fetched from Open VSX: its extension at the version pinned (else its newest), its <c>.vsix</c> and the
    /// theme in it; why not, saying which.</summary>
    public static async Task<(VsCodeTaken? Taken, string? Why)> FetchAsync(VsCodeBase wanted, CancellationToken cancel)
    {
        if (wanted is not { Id: { } id, Theme: { } label })
        {
            return (null, $"{wanted.File} is a file beside its theme's, fetched with it rather than from Open VSX");
        }
        wanted = wanted with { Id = id, Theme = label };
        (OpenVsxEntry? entry, string? missing) = await OpenVsx.ExtensionAsync(id, wanted.Version, cancel);
        if (entry is null)
        {
            return (null, $"{wanted.Id} could not be had from Open VSX: {missing}");
        }
        (byte[]? vsix, string? why) = await OpenVsx.VsixAsync(entry, cancel);
        if (vsix is null)
        {
            return (null, $"{wanted.Id} could not be had from Open VSX: {why}");
        }
        (VsCodeTaken? taken, string? refused) = VsCodeThemes.FromVsix(vsix, label);
        return taken is null ? (null, $"{wanted.Id} {entry.Version}'s {wanted.Theme} is refused: {refused}") : (taken, null);
    }

    /// <summary>The files of a theme's base named as a file beside it, the files it includes too, each read by its path
    /// there through <paramref name="read"/>, so they are kept with the theme; none for a base by its id, and why not where
    /// one cannot be read or the theme refused.</summary>
    public static async Task<(Dictionary<string, byte[]> Files, string? Why)> BesideAsync(ThemeFile theme, Func<string, Task<(byte[]? Bytes, string? Why)>> read)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        string? next = theme.Base?.File;
        while (next is not null)
        {
            if (files.ContainsKey(next) || files.Count == VsCodeThemes.MostIncludes)
            {
                return (files, $"{theme.Base!.File} includes more than the {VsCodeThemes.MostIncludes} files Passenger reads of one theme");
            }
            (byte[]? bytes, string? why) = await read(next);
            if (bytes is null)
            {
                return (files, $"{theme.Name}'s VS Code theme {next} could not be read: {why}");
            }
            files[next] = bytes;
            next = VsCodeThemes.Included(next, bytes);
        }
        if (theme.Base?.File is { } file)
        {
            ThemeRead<VsCodeTheme> whole = VsCodeThemes.ReadTheme(file, null, null, path => files.TryGetValue(path, out byte[]? bytes) ? (bytes, null) : (null, $"no {path}"));
            if (whole.Read is null)
            {
                return (files, $"{theme.Name}'s VS Code theme is refused: {whole.Refused}");
            }
        }
        return (files, null);
    }

    /// <summary>The version of a theme's base kept beside it, or null where none is kept.</summary>
    public static string? KeptVersion(ThemeSource source, bool code, string name)
    {
        (byte[]? manifest, _) = KeptThemes.BaseFiles(source, code, name)(VsCodeThemes.Manifest);
        return manifest is null ? null : VsCodeThemes.ReadManifest(VsCodeThemes.Manifest, manifest).Read?.Version;
    }

    /// <summary>A theme's base as kept beside it: null with no why where none is kept, and with why where what is kept does
    /// not read.</summary>
    public static VsCodeTheme? Kept(ThemeSource source, bool code, string name, VsCodeBase wanted, out string? why)
    {
        why = null;
        if (wanted.File is { } beside)
        {
            VsCodeThemes.ExtensionFile besides = KeptThemes.BesideFiles(source, code, name);
            if (besides(beside).Bytes is null)
            {
                return null;
            }
            ThemeRead<VsCodeTheme> read = VsCodeThemes.ReadTheme(beside, null, null, besides);
            why = read.Refused;
            return read.Read;
        }
        VsCodeThemes.ExtensionFile files = KeptThemes.BaseFiles(source, code, name);
        (byte[]? manifest, _) = files(VsCodeThemes.Manifest);
        if (manifest is null)
        {
            return null;
        }
        ThemeRead<VsCodeExtension> extension = VsCodeThemes.ReadManifest(VsCodeThemes.Manifest, manifest);
        if (extension.Read is not { } named || named.Id != wanted.Id || (wanted.Version is { } pinned && named.Version != pinned))
        {
            // Another extension or version kept than the theme now names is fetched again.
            return null;
        }
        if (named.Themes.FirstOrDefault(t => t.Label == wanted.Theme) is not { } contributed)
        {
            return null;
        }
        ThemeRead<VsCodeTheme> theme = VsCodeThemes.ReadContributed(contributed, files);
        why = theme.Refused;
        return theme.Read;
    }

    /// <summary>A theme file's base fetched and kept beside it, where it names one not kept; why not.</summary>
    public static async Task<string?> KeepAsync(ThemeSource source, bool code, string name, ThemeFile theme, CancellationToken cancel)
    {
        // A file beside the theme's is kept with its pictures (BesideAsync), not fetched.
        if (theme.Base is not { Id: not null } wanted || Kept(source, code, name, wanted, out _) is not null)
        {
            return null;
        }
        (VsCodeTaken? taken, string? why) = await FetchAsync(wanted, cancel);
        return taken is null ? why : KeptThemes.KeepBase(source, code, name, taken.Files);
    }
}
