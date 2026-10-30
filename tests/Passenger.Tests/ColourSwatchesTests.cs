using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Mcp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A C# colour's swatch picked while its file changes: the colour chosen is written where the call is as the
/// file now stands, a line an agent added above it kept, and refused, writing nothing, where the call's own text was
/// changed meanwhile.</summary>
public sealed class ColourSwatchesTests : IDisposable
{
    private const string Palette = "class Palette\n{\n    static object Brand = Avalonia.Media.Color.FromRgb(0x33, 0x66, 0x99);\n}\n";

    private readonly string folder = TestFolders.Create("swatches-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_colour_chosen_after_a_line_was_added_above_its_call_changes_the_call_and_keeps_the_line()
    {
        (MainWindow window, CodeEditor editor) = Open();
        PassengerTools agent = new(window);
        Pick(window, editor);
        Ui.Await(agent.Edit("Palette.cs", "class Palette\n{\n", "class Palette\n{\n    // The brand's own blue, as the style guide gives it.\n"), "the agent's line above the call");
        Assert.Equal("#336699", window.PickingColour);
        window.KeyTextInput("#AA0000");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        string now = editor.Document.Snapshot.ToString();
        Assert.True(now == "class Palette\n{\n    // The brand's own blue, as the style guide gives it.\n    static object Brand = Avalonia.Media.Color.FromRgb(170, 0, 0);\n}\n",
            $"The colour was not written over the call where it is now ({window.Status}):\n{now}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_colour_chosen_after_its_calls_text_was_changed_writes_nothing_and_says_so()
    {
        (MainWindow window, CodeEditor editor) = Open();
        PassengerTools agent = new(window);
        Pick(window, editor);
        Ui.Await(agent.Edit("Palette.cs", "FromRgb(0x33,", "FromRgb(0x44,"), "the agent's change of the call");
        string changed = editor.Document.Snapshot.ToString();
        window.KeyTextInput("#AA0000");
        Ui.Press(window, "ENTER");
        Ui.Settle();
        Assert.Equal("The colour was edited meanwhile: choose it again", window.Status);
        Assert.Equal(changed, editor.Document.Snapshot.ToString());
        window.Close();
    }

    /// <summary>The folder open with no C# language server, which swatches ask nothing of, the palette shown with its
    /// call's swatch.</summary>
    private (MainWindow Window, CodeEditor Editor) Open()
    {
        string path = TestFolders.Write(folder, "Palette.cs", Palette);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(path);
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        OpenDocument shown = window.EditorArea.Active!;
        Ui.WaitFor(() => shown.ColourMarks.Any(m => m.Line == 2), "the swatch of the colour FromRgb makes");
        return (window, editor);
    }

    /// <summary>Opens the picker on the call's swatch, as a click on it does.</summary>
    private static void Pick(MainWindow window, CodeEditor editor)
    {
        Ui.Click(editor, editor.GutterColumnPoint(editor.Gutter!.Marks, 2));
        Assert.Equal("#336699", window.PickingColour);
    }
}
