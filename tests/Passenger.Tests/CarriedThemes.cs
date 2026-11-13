using System.Text.Json;
using Avalonia;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>Passenger's themes repository as the tests carry it (Themes/, which <see cref="ThemeSource.Variable"/> names
/// for the run in the repository's place), and the themes Passenger carries in its built-in source, each file read through
/// <see cref="ThemeFiles"/> as a source's is.</summary>
public static class CarriedThemes
{
    /// <summary>Passenger's own theme, Tokens.axaml, by the name Settings lists it under.</summary>
    public const string Own = "Passenger";

    /// <summary>The folder the repository's copy is in.</summary>
    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Themes");

    /// <summary>The copy's index, as read.</summary>
    public static ThemeRead<ThemeIndex> Index => ThemeFiles.ReadIndex(ThemeFiles.IndexFile, File.ReadAllBytes(Path.Combine(Folder, ThemeFiles.IndexFile)));

    /// <summary>The names of the themes it lists, each named as its file is, in its order.</summary>
    public static IReadOnlyList<string> ThemeNames => [.. Index.Read!.Themes.Select(Path.GetFileNameWithoutExtension).OfType<string>()];

    /// <summary>The names of the themes Passenger carries beside its own.</summary>
    public static IReadOnlyList<string> BuiltInNames => ThemeSource.Carried;

    /// <summary>A theme of the copy or one Passenger carries, as read, by its file's name.</summary>
    public static ThemeRead<ThemeFile> ReadTheme(string name) => BuiltInNames.Contains(name)
        ? BuiltInThemes.Listed(ThemeResources.Kinds).Listed.Single(l => !l.Code && l.Path == $"{name}.json").Read
        : ThemeFiles.ReadTheme($"{name}.json", File.ReadAllBytes(Path.Combine(Folder, $"{name}.json")), ThemeResources.Kinds);

    /// <summary>A code colour scheme of the copy as read, by its file's name.</summary>
    public static ThemeRead<ThemeFile> ReadScheme(string name) => ThemeFiles.ReadCodeColours($"{name}.json", File.ReadAllBytes(Path.Combine(Folder, $"{name}.json")), ThemeResources.Kinds);

    /// <summary>A theme of the copy or one Passenger carries, whole; null for <see cref="Own"/>.</summary>
    public static ThemeFile? Theme(string name) => name == Own ? null : ReadTheme(name).Read ?? throw new InvalidOperationException($"{name}.json does not read: {ReadTheme(name).Refused}");

    /// <summary>Wears a theme of the copy for a test, laid as Themes lays a kept one, Passenger's own put back after it, pass or fail.</summary>
    public static IDisposable Wearing(string name)
    {
        Themes.Wear(Application.Current!, Theme(name), null);
        return new Worn();
    }

    /// <summary>A copy of the repository's source in a folder of the test's, to change or take away.</summary>
    public static void CopyTo(string folder, params string[] names)
    {
        Directory.CreateDirectory(folder);
        ThemeIndex index = Index.Read!;
        string[] themes = [.. index.Themes.Where(t => names.Length == 0 || names.Contains(Path.GetFileNameWithoutExtension(t)))];
        string[] schemes = [.. index.CodeColours.Where(t => names.Length == 0 || names.Contains(Path.GetFileNameWithoutExtension(t)))];
        foreach (string file in themes.Concat(schemes))
        {
            File.Copy(Path.Combine(Folder, file), Path.Combine(folder, file), overwrite: true);
        }
        File.WriteAllText(Path.Combine(folder, ThemeFiles.IndexFile), JsonSerializer.Serialize(new { format = 1, themes, codeColours = schemes }));
    }

    private sealed class Worn : IDisposable
    {
        public void Dispose() => Themes.Wear(Application.Current!, null, null);
    }
}
