using Passenger.Languages.WinForms;
using Passenger.Xaml.Localization;

namespace Passenger.App.WinForms;

/// <summary>A Windows Forms form's or user control's files, as its designer keeps them: its code file (MainForm.cs), the
/// designer file beside it (MainForm.Designer.cs) that holds its InitializeComponent, and its class's full name.</summary>
public sealed record FormFiles(string Code, string Designer, string Class)
{
    /// <summary>The .resx beside its code (MainForm.resx), which holds what its code cannot (a picture, a tray component's
    /// place), there or not.</summary>
    public string Resources => Path.ChangeExtension(Code, ".resx");

    /// <summary>The .resx of a culture beside its code (MainForm.fr.resx), there or not; its own for the empty culture.</summary>
    public string ResourcesOf(string culture) => culture.Length == 0 ? Resources : Path.ChangeExtension(Code, $".{culture}.resx");

    /// <summary>The cultures it has a .resx of beside its code, as MSBuild takes a .resx's culture from its name.</summary>
    public IReadOnlyList<string> Cultures()
    {
        string stem = Path.GetFileNameWithoutExtension(Code);
        string folder = Path.GetDirectoryName(Code) ?? string.Empty;
        try
        {
            return [.. Directory.EnumerateFiles(folder, $"{stem}.*.resx").Select(p => Path.GetFileNameWithoutExtension(p))
                .Select(s => ResourceFiles.CultureOf(s) is { Length: > 0 } culture && s.Length == stem.Length + 1 + culture.Length ? culture : null)
                .OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be listed gives none: the form is drawn in its own language.
            return [];
        }
    }

    /// <summary>The form a file is one of the files of: a code file with a designer file beside it, or that designer file,
    /// where the designer file gives InitializeComponent to a class the code file declares too; null for any other file.
    /// The texts are read as the editor has them, else from disk (<paramref name="text"/>, null for a file not there).</summary>
    public static FormFiles? Of(string path, Func<string, string?> text)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        bool designer = path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase);
        string stem = path[..^(designer ? ".Designer.cs".Length : ".cs".Length)];
        string code = stem + ".cs";
        string designerPath = stem + ".Designer.cs";
        if (text(designerPath) is not { } designed || text(code) is not { } coded)
        {
            return null;
        }
        return DesignerCode.Designed(designed).FirstOrDefault(c => DesignerCode.Declares(coded, c)) is { } found ? new FormFiles(code, designerPath, found) : null;
    }
}
