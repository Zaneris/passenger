using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Mcp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A C# colour's swatch picked while its file changes: the colour chosen is written where the call is as the
/// file now stands, a line an agent added above it kept, and refused, writing nothing, where the call's own text was
/// changed meanwhile; and picked in an editor at the window's smallest, where the picker shows whole under its line, or
/// over it below the navigation bar.</summary>
[SameOnEverySystem]
public sealed class ColourSwatchesTests : IDisposable
{
    private const string Palette = "class Palette\n{\n    static object Brand = Avalonia.Media.Color.FromRgb(0x33, 0x66, 0x99);\n}\n";

    private readonly string folder = TestFolders.Create("swatches-");

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>A colour chosen in the picker changes its call where a line added above moved it, writes nothing where
    /// the call's text changed, saying so, and a picker closes as an editor window becomes active, the one opened there
    /// writing its own colour.</summary>
    [AvaloniaFact]
    public void A_colour_chosen_writes_its_own_call_or_nothing_saying_why()
    {
        Flow.StepApart<ColourSwatchesTests>("a colour chosen after a line was added above its call changes the call and keeps the line", t => t.A_colour_chosen_after_a_line_was_added_above_its_call_changes_the_call_and_keeps_the_line());
        Flow.StepApart<ColourSwatchesTests>("a colour chosen after its call's text was changed writes nothing and says so", t => t.A_colour_chosen_after_its_calls_text_was_changed_writes_nothing_and_says_so());
        Flow.StepApart<ColourSwatchesTests>("a picker closes as an editor window becomes active and the one opened there writes its own colour", t => t.A_picker_closes_as_an_editor_window_becomes_active_and_the_one_opened_there_writes_its_own_colour());
    }

    private void A_colour_chosen_after_a_line_was_added_above_its_call_changes_the_call_and_keeps_the_line()
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

    private void A_colour_chosen_after_its_calls_text_was_changed_writes_nothing_and_says_so()
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

    /// <summary>A picker open in the main window closes once an editor in a window of its own becomes the active one, as the
    /// popups placed at a line do, and the picker opened there is that window's own: its Choose writes its colour over its
    /// own call and closes it there, the main window's file left as it was.</summary>
    private void A_picker_closes_as_an_editor_window_becomes_active_and_the_one_opened_there_writes_its_own_colour()
    {
        string accent = TestFolders.Write(folder, "Accent.cs", "class Accent\n{\n    static object Shade = Avalonia.Media.Color.FromRgb(0x11, 0x22, 0x33);\n}\n");
        (MainWindow window, CodeEditor editor) = Open();
        List<Window> opened = [];
        using IDisposable watching = Window.WindowOpenedEvent.AddClassHandler<Window>((shown, _) => opened.Add(shown));
        window.OpenFile(accent);
        OpenDocument accentShown = window.EditorArea.Active!;
        Ui.WaitFor(() => accentShown.ColourMarks.Any(m => m.Line == 2), "the swatch of Accent's colour");
        Ui.Press(window, "shift F4");
        Ui.Settle();
        Window editors = opened.LastOrDefault(w => w is not MainWindow && w.IsVisible) ?? throw new Xunit.Sdk.XunitException("Open Tab in New Window opened no window of its own");
        CodeEditor there = editors.GetVisualDescendants().OfType<EditorGroup>().Single().Editor;
        Assert.Same(accentShown.Text, there.Document);

        Pick(window, editor);
        Passenger.App.Xaml.ColourPicker first = window.GetVisualDescendants().OfType<Passenger.App.Xaml.ColourPicker>().Single(p => p.IsEffectivelyVisible);
        Ui.Click(there, there.GutterColumnPoint(there.Gutter!.Marks, 2));
        Assert.Equal("#112233", window.PickingColour);
        Assert.False(first.IsAttachedToVisualTree(), "The main window's picker stayed open once an editor in a window of its own became the active one and opened its own");
        Passenger.App.Xaml.ColourPicker second = editors.GetVisualDescendants().OfType<Passenger.App.Xaml.ColourPicker>().Single(p => p.IsEffectivelyVisible);

        editors.KeyTextInput("#AA0000");
        Ui.Click(second.GetVisualDescendants().OfType<Button>().Single(b => b.Content is "Choose"));
        Ui.Settle();
        Assert.False(second.IsAttachedToVisualTree(), "The editor window's picker stayed open once its Choose was pressed");
        Assert.Null(window.PickingColour);
        Assert.True(there.Document.Snapshot.ToString().Contains("FromRgb(170, 0, 0)", StringComparison.Ordinal),
            $"Choose in the editor window's picker did not write its colour over its own call ({window.Status}):\n{there.Document.Snapshot}");
        Assert.Equal(Palette, editor.Document.Snapshot.ToString());
        window.Close();
    }

    /// <summary>The folder open with no C# language server, which swatches ask nothing of, the palette shown with its
    /// call's swatch; in a window of the size given.</summary>
    private (MainWindow Window, CodeEditor Editor) Open(double width = 1200, double height = 800)
    {
        string path = TestFolders.Write(folder, "Palette.cs", Palette);
        MainWindow window = Ui.OpenFolder(folder, width, height, languageServer: false);
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
