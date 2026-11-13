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
        (OpenVsxEntry? entry, string? missing) = await OpenVsx.ExtensionAsync(wanted.Id, wanted.Version, cancel);
        if (entry is null)
        {
            return (null, $"{wanted.Id} could not be had from Open VSX: {missing}");
        }
        (byte[]? vsix, string? why) = await OpenVsx.VsixAsync(entry, cancel);
        if (vsix is null)
        {
            return (null, $"{wanted.Id} could not be had from Open VSX: {why}");
        }
        (VsCodeTaken? taken, string? refused) = VsCodeThemes.FromVsix(vsix, wanted.Theme);
        return taken is null ? (null, $"{wanted.Id} {entry.Version}'s {wanted.Theme} is refused: {refused}") : (taken, null);
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
        if (theme.Base is not { } wanted || Kept(source, code, name, wanted, out _) is not null)
        {
            return null;
        }
        (VsCodeTaken? taken, string? why) = await FetchAsync(wanted, cancel);
        return taken is null ? why : KeptThemes.KeepBase(source, code, name, taken.Files);
    }
}
