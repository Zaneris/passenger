using Passenger.Core;

namespace Passenger.App;

/// <summary>The themes and code colour schemes a person chose from a source or added from a file, each kept as its file was
/// read, so it draws at every start and offline without its source: under the state's <c>themes/</c>, a folder for each
/// source (<see cref="ThemeSource.KeptFolder"/>), in it <c>themes/</c> and <c>codeColours/</c>, in each a folder for each
/// name holding <see cref="FileName"/> (and later the images its file names beside it). A copy is taken only when one is
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

    /// <summary>Keeps a theme's file as read, over any copy kept before; why not where it cannot be written.</summary>
    public static string? Keep(ThemeSource source, bool code, string name, byte[] bytes)
    {
        string path = PathOf(source, code, name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Write(path, bytes);
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return $"{name} could not be kept in {Path.GetDirectoryName(path)}: {error.Message}";
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
