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

    /// <summary>In a window at its smallest with the default layout, the Explorer open leaving the editor narrower than the
    /// picker's own width and the navigation bar over it leaving too little room for the picker of itself under the
    /// swatch's line, the picker opened on that swatch shows whole in the editor under its line, narrowed and lowered to
    /// the room there: its hex whole, and its Choose and Cancel inside it, where Choose writes the colour typed.</summary>
    [AvaloniaFact]
    public void The_colour_picker_in_an_editor_at_the_windows_smallest_width_shows_whole_under_its_line_and_chooses_there()
    {
        (MainWindow window, CodeEditor editor) = Open(MainWindow.SmallestWidth, MainWindow.SmallestHeight);
        Rect layer = CodeAssistTests.Layer(window);
        Assert.True(layer.Width < PopupFrame.Narrow,
            $"The default layout at the window's smallest width was to leave the editor narrower than the picker's own width, {PopupFrame.Narrow}; it is {layer.Width} wide");
        Pick(window, editor);
        Ui.Settle();
        Passenger.App.Xaml.ColourPicker picker = window.GetVisualDescendants().OfType<Passenger.App.Xaml.ColourPicker>().Single(p => p.IsEffectivelyVisible);
        Rect at = In(window, picker);
        Assert.True(layer.Inflate(0.5).Contains(at), $"The picker runs past the editor's edge, cut there: it lies at {at} in the editor at {layer}");
        double line = editor.TranslatePoint(new Point(0, editor.PointOf(editor.Document.Snapshot.GetLineStart(2)).Y + editor.LineHeight), window)!.Value.Y;
        Assert.True(at.Y >= line - 0.5, $"The picker at {at} covers the swatch's line, whose foot is at {line}, where it was to hang under it");

        TextPresenter hex = picker.GetVisualDescendants().OfType<TextPresenter>().Single(t => t.IsEffectivelyVisible);
        Rect typed = In(window, hex);
        Assert.True(at.Contains(typed) && hex.TextLayout.Width <= hex.Bounds.Width + 0.5,
            $"The picker at {at} cuts its hex '{hex.Text}' at {typed}, {hex.TextLayout.Width:0.#} wide");
        Button[] answers = [.. picker.GetVisualDescendants().OfType<Button>().Where(b => b.Content is "Choose" or "Cancel")];
        Assert.True(answers.Length == 2 && answers.All(b => at.Contains(In(window, b))),
            $"The picker at {at} cuts its answers: {string.Join(", ", answers.Select(b => $"{b.Content} at {In(window, b)}"))}");

        window.KeyTextInput("#AA0000");
        Ui.Click(answers.Single(b => b.Content is "Choose"));
        Ui.Settle();
        string now = editor.Document.Snapshot.ToString();
        Assert.True(now.Contains("FromRgb(170, 0, 0)", StringComparison.Ordinal), $"Choose in the narrowed picker did not write the colour typed ({window.Status}):\n{now}");
        window.Close();
    }

    /// <summary>A popup placed over its line keeps below the bars over the editor: in a window at its smallest, a swatch low
    /// in the editor, with too little room under it, opens the picker over its line, lowered to the room between the
    /// navigation bar's foot and the line, so it covers neither the bar nor the tabs nor the line, and shows whole.</summary>
    [AvaloniaFact]
    public void A_picker_over_its_line_keeps_below_the_navigation_bar_lowered_to_the_room_there()
    {
        string low = TestFolders.Write(folder, "Low.cs", "class Low\n{\n" + new string('\n', 11) + "    static object Shade = Avalonia.Media.Color.FromRgb(0x11, 0x22, 0x33);\n}\n");
        (MainWindow window, CodeEditor editor) = Open(MainWindow.SmallestWidth, MainWindow.SmallestHeight);
        window.OpenFile(low);
        OpenDocument shown = window.EditorArea.Active!;
        Ui.WaitFor(() => shown.ColourMarks.Any(m => m.Line == 13), "the swatch of Low's colour");
        NavigationBar bar = window.EditorArea.ActiveGroup.Navigation;
        Ui.WaitFor(() => bar.Bar.IsVisible, () => $"the navigation bar over the editor, naming {string.Join(" › ", bar.Shown)}");
        Ui.Click(editor, editor.GutterColumnPoint(editor.Gutter!.Marks, 13));
        Assert.Equal("#112233", window.PickingColour);
        Ui.Settle();
        Passenger.App.Xaml.ColourPicker picker = window.GetVisualDescendants().OfType<Passenger.App.Xaml.ColourPicker>().Single(p => p.IsEffectivelyVisible);
        Rect at = In(window, picker);
        Rect barAt = In(window, bar.Bar);
        double line = editor.TranslatePoint(new Point(0, editor.PointOf(editor.Document.Snapshot.GetLineStart(13)).Y), window)!.Value.Y;
        Assert.True(at.Bottom <= line + 0.5, $"The picker at {at} was to stand over the swatch's line, whose top is at {line}, the room under it too short");
        Assert.True(at.Y >= barAt.Bottom - 0.5, $"The picker at {at} covers the navigation bar at {barAt}, over the editor it was to keep to");
        Assert.True(CodeAssistTests.Layer(window).Inflate(0.5).Contains(at), $"The picker at {at} runs past the editor's edge");
        Button[] answers = [.. picker.GetVisualDescendants().OfType<Button>().Where(b => b.Content is "Choose" or "Cancel")];
        Assert.True(answers.Length == 2 && answers.All(b => at.Contains(In(window, b))),
            $"The picker at {at}, lowered, cuts its answers: {string.Join(", ", answers.Select(b => $"{b.Content} at {In(window, b)}"))}");
        window.Close();
    }

    /// <summary>A picker open in the main window closes once an editor in a window of its own becomes the active one, as the
    /// popups placed at a line do, and the picker opened there is that window's own: its Choose writes its colour over its
    /// own call and closes it there, the main window's file left as it was.</summary>
    [AvaloniaFact]
    public void A_picker_closes_as_an_editor_window_becomes_active_and_the_one_opened_there_writes_its_own_colour()
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

    /// <summary>Where a control lies in its window.</summary>
    private static Rect In(MainWindow window, Control control) => new(control.TranslatePoint(default, window) ?? default, control.Bounds.Size);

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
