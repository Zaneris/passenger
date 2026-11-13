using Passenger.Core;

namespace Passenger.App;

/// <summary>The themes and code colour schemes a person chose from a source or added from a file, each kept as its file was
/// read, so it draws at every start and offline without its source: under the state's <c>themes/</c>, a folder for each
/// source (<see cref="ThemeSource.KeptFolder"/>), in it <c>themes/</c> and <c>codeColours/</c>, in each a folder for each
/// name holding <see cref="FileName"/> and the pictures its file names beside it, at the paths it names them by. A copy is taken only when one is
/// chosen and none is kept, or when a refresh is asked for; written whole through <see cref="AtomicFile"/>. Called on any
/// thread.</summary>
public static class KeptThemes
{
    /// <summary>A kept theme's file in its folder.</summary>
    public const string FileName = "theme.json";

    /// <summary>The folder every kept theme is under.</summary>
    public static string Folder => Path.Combine(State.StateStore.DefaultRoot, "themes");

    /// <summary>Where a theme of a source and a name is kept.</summary>
    public static string PathOf(ThemeSource source, bool code, string name) =>
        Path.Combine(Folder, source.KeptFolder, code ? "codeColours" : "themes", ThemeSource.KeptName(name), FileName);

    /// <summary>A kept theme's file, or null where none is kept or it cannot be read.</summary>
    public static byte[]? Bytes(ThemeSource source, bool code, string name)
    {
        string path = PathOf(source, code, name);
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Themes: the kept {path} could not be read: {error.Message}");
            return null;
        }
    }

    /// <summary>Whether a theme of a source and a name is kept.</summary>
    public static bool Holds(ThemeSource source, bool code, string name) => File.Exists(PathOf(source, code, name));

    /// <summary>A picture a kept theme names, by its path beside the theme's file, or null where none is kept or it cannot be
    /// read or is no longer one a background may be.</summary>
    public static byte[]? Image(ThemeSource source, bool code, string name, string image)
    {
        string path = Path.Combine(Path.GetDirectoryName(PathOf(source, code, name))!, image.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > ThemeFiles.MostImageBytes)
            {
                return null;
            }
            byte[] bytes = File.ReadAllBytes(path);
            return ThemeImages.WhyNot(path, bytes) is null ? bytes : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Themes: the kept {path} could not be read: {error.Message}");
            return null;
        }
    }

    /// <summary>Keeps a theme's file as read, with the pictures it names beside it (each by its path there), over any copy kept
    /// before; why not where it cannot be written.</summary>
    public static string? Keep(ThemeSource source, bool code, string name, byte[] bytes, IReadOnlyDictionary<string, byte[]>? images = null)
    {
        string path = PathOf(source, code, name);
        try
        {
            string folder = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);
            // The pictures first, so a kept file never names one not kept yet.
            foreach ((string image, byte[] picture) in images ?? new Dictionary<string, byte[]>())
            {
                string beside = Path.Combine(folder, image.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(beside)!);
                AtomicFile.Write(beside, picture);
            }
            AtomicFile.Write(path, bytes);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{name} could not be kept in {Path.GetDirectoryName(path)}: {error.Message}";
        }
    }

    /// <summary>The folder a theme's VS Code base is kept in beside its file: the extension's manifest and the theme's files at
    /// their paths in the extension.</summary>
    public const string BaseFolder = "vscode";

    /// <summary>The files of a kept theme's VS Code base, by their paths in its extension, each held to the most a theme file
    /// may be.</summary>
    public static VsCodeThemes.ExtensionFile BaseFiles(ThemeSource source, bool code, string name) => FilesIn(source, code, name, BaseFolder);

    /// <summary>The files kept beside a theme's file, by their paths there, each held to the most a theme file may be.</summary>
    public static VsCodeThemes.ExtensionFile BesideFiles(ThemeSource source, bool code, string name) => FilesIn(source, code, name, string.Empty);

    /// <summary>A file kept beside a theme's, by its path there, as it was written, or null where none is.</summary>
    public static byte[]? Beside(ThemeSource source, bool code, string name, string path)
    {
        string file = Path.Combine(Path.GetDirectoryName(PathOf(source, code, name))!, path.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.Exists(file) && new FileInfo(file).Length <= ThemeFiles.MostImageBytes ? File.ReadAllBytes(file) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Themes: the kept {file} could not be read: {error.Message}");
            return null;
        }
    }

    private static VsCodeThemes.ExtensionFile FilesIn(ThemeSource source, bool code, string name, string under)
    {
        string folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(PathOf(source, code, name))!, under));
        return path =>
        {
            string file = Path.GetFullPath(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
            try
            {
                if (!PathNames.IsUnder(file, folder) || !File.Exists(file))
                {
                    return (null, $"{name}'s VS Code theme is not kept here ({path})");
                }
                if (new FileInfo(file).Length > ThemeFiles.MostBytes)
                {
                    return (null, $"the kept {file} is more than the {ThemeFiles.MostBytes} bytes a theme file may hold");
                }
                return (File.ReadAllBytes(file), null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return (null, $"the kept {file} could not be read: {error.Message}");
            }
        };
    }

    /// <summary>Whether a kept theme's VS Code base is kept beside it.</summary>
    public static bool HoldsBase(ThemeSource source, bool code, string name) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(PathOf(source, code, name))!, BaseFolder, VsCodeThemes.Manifest));

    /// <summary>Keeps a theme's VS Code base beside it, each file by its path in the extension, the manifest last, so a kept
    /// manifest never names a theme not kept yet; why not where it cannot be written.</summary>
    public static string? KeepBase(ThemeSource source, bool code, string name, IReadOnlyDictionary<string, byte[]> files)
    {
        string folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(PathOf(source, code, name))!, BaseFolder));
        try
        {
            foreach ((string path, byte[] bytes) in files.OrderBy(f => f.Key == VsCodeThemes.Manifest ? 1 : 0))
            {
                string file = Path.GetFullPath(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
                if (!PathNames.IsUnder(file, folder))
                {
                    return $"{name}'s VS Code theme names {path}, outside its extension";
                }
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                AtomicFile.Write(file, bytes);
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{name}'s VS Code theme could not be kept in {folder}: {error.Message}";
        }
    }

    /// <summary>The file of every theme or scheme of a source kept here, for the list Settings shows without the source.</summary>
    public static IReadOnlyList<byte[]> Of(ThemeSource source, bool code)
    {
        string folder = Path.Combine(Folder, source.KeptFolder, code ? "codeColours" : "themes");
        List<byte[]> kept = [];
        try
        {
            if (!Directory.Exists(folder))
            {
                return kept;
            }
            foreach (string named in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal))
            {
                string file = Path.Combine(named, FileName);
                if (File.Exists(file) && new FileInfo(file).Length <= ThemeFiles.MostBytes)
                {
                    kept.Add(File.ReadAllBytes(file));
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Themes: the themes kept in {folder} could not be listed: {error.Message}");
        }
        return kept;
    }
}
