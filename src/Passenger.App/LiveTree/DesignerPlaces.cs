using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Languages.WinForms;
using Passenger.Text;

namespace Passenger.App.LiveTree;

/// <summary>Where a running Windows Forms app's control or item is written: the designer code of the class that makes it
/// (<see cref="DesignedIn"/>, as its inspector names it), its creation and the lines setting each of its properties, read
/// as the designer writes them (<see cref="DesignerCode"/>) from the file's text as the editor has it.</summary>
public static class DesignerPlaces
{
    /// <summary>The designer file declaring a class's InitializeComponent: of the project's folder, else of the folder holding
    /// it (a library's beside the app's); null where none does. Read off the window's thread.</summary>
    public static string? FileOf(string projectFolder, string fullName)
    {
        string name = fullName[(fullName.LastIndexOfAny(['.', '+']) + 1)..];
        foreach (string? folder in (string?[])[projectFolder, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(projectFolder))])
        {
            if (folder is null || !Directory.Exists(folder))
            {
                continue;
            }
            foreach (string file in PathNames.FilesUnder(folder, ProjectFolders.Enters, Designer).SelectMany(f => f.Files))
            {
                if (EditorArea.ReadText(file) is { } text && text.Contains(name, StringComparison.Ordinal) && DesignerCode.Read(text, fullName) is not null)
                {
                    return file;
                }
            }
        }
        return null;
    }

    /// <summary>A file of a folder by its path where it is designer code (a .Designer.cs file); null passes it over.</summary>
    private static string? Designer(string folder, ReadOnlySpan<char> name) =>
        name.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ? Path.Join(folder, name) : null;

    /// <summary>A Windows Forms element's details with its designer code's places, at the editor's lines: its creation (its
    /// class's InitializeComponent for a root), and each property's line that sets it; else why it has none.</summary>
    public static ElementDetails Placed(ElementDetails details, Func<string, TextSnapshot?> texts)
    {
        if (details.Designed is not { } designed)
        {
            return details;
        }
        if (designed.File is not { } file || texts(file) is not { } text || DesignerCode.Read(text.ToString(), designed.Class) is not { } source)
        {
            return details with { NoSource = $"No designer code of {designed.Class} was found under the project's folder" };
        }
        SourcePlace? At(int? offset)
        {
            if (offset is not int at)
            {
                return null;
            }
            TextPosition position = text.GetPosition(at);
            return new SourcePlace(file, position.Line + 1, position.Column + 1);
        }
        SourcePlace? made = At(source.MadeAt(designed.Field));
        return details with
        {
            Source = made,
            NoSource = made is null ? $"Made in code: {Path.GetFileName(file)} does not make {designed.Field}" : null,
            Properties = [.. details.Properties.Select(p => p.Place is null && At(source.SetAt(designed.Field, p.Name)) is { } set ? p with { Place = set } : p)],
        };
    }
}
