using Passenger.Languages.WinForms;

namespace Passenger.App.WinForms;

/// <summary>A Windows Forms form's or user control's files, as its designer keeps them: its code file (MainForm.cs), the
/// designer file beside it (MainForm.Designer.cs) that holds its InitializeComponent, and its class's full name.</summary>
public sealed record FormFiles(string Code, string Designer, string Class)
{
    /// <summary>The .resx beside its code (MainForm.resx), which holds what its code cannot (a picture, a tray component's
    /// place), there or not.</summary>
    public string Resources => Path.ChangeExtension(Code, ".resx");

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
