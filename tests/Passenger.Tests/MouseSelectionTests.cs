using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A selection made with the mouse, in the editor and in the Run and Debug consoles, as a person sees and uses it:
/// drawn in the selection's colour wherever a drag or a key grows it, and copied by every key the keymap in force gives
/// Copy, in each keymap.</summary>
public sealed class MouseSelectionTests : IDisposable
{
    private const string Lines = "first line of the notes\nalpha beta gamma delta epsilon zeta eta theta\niota kappa lambda mu nu xi omicron pi\nrho sigma tau upsilon phi chi psi omega\n";

    private readonly string folder = TestFolders.Create("mouse-selection-");

    public void Dispose() => TestFolders.Delete(folder);

    internal void A_selection_grown_forward_along_a_line_is_drawn_in_the_selections_colour_in_the_editor_and_a_console()
    {
        string notes = Path.Combine(folder, "notes.txt");
        File.WriteAllText(notes, Lines);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(notes);
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            TextSnapshot text = editor.Document.Snapshot;
            int from = text.GetLineStart(1) + "alpha ".Length;
            int to = from + "beta gamma delta epsilon".Length;
            Ui.DragDrawn(window, Place(window, editor, from), Place(window, editor, to));
            AssertDrawnSelected(window, editor, from, to, "A drag forward along line 2 of the editor");

            // Shift+Right grows a selection forward along its line as a drag does, a key at a time.
            int caret = text.GetLineStart(2) + "iota ".Length;
            editor.Selections = SelectionSet.Single(caret);
            Ui.Settle();
            for (int i = 0; i < "kappa lambda".Length; i++)
            {
                Ui.Press(window, "shift RIGHT");
            }
            AssertDrawnSelected(window, editor, caret, caret + "kappa lambda".Length, "Shift+Right pressed along line 3 of the editor");

            OutputView run = window.BottomPanel.Run;
            run.Append(Lines);
            window.DockHost.Show(ToolWindowIds.Run);
            Ui.Settle();
            TextSnapshot output = run.Document.Snapshot;
            int start = output.GetLineStart(3) + "rho ".Length;
            int end = start + "sigma tau upsilon phi".Length;
            Ui.DragDrawn(window, Place(window, run.Editor, start), Place(window, run.Editor, end));
            AssertDrawnSelected(window, run.Editor, start, end, "A drag forward along line 4 of the Run console");
        }
        finally
        {
            window.Close();
        }
    }

    internal void Every_key_a_keymap_gives_Copy_copies_what_the_mouse_selected_in_the_Run_and_Debug_consoles()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        ToolViews bottom = window.BottomPanel;
        try
        {
            bottom.Run.Append(Lines);
            window.DockHost.Show(ToolWindowIds.Run);
            Ui.Settle();
            CopiesInEveryKeymap(window, bottom.Run, "the Run console");

            List<string> sent = [];
            bottom.Run.Input = new ConsoleInput(sent.Add, () => { });
            Ui.Click(bottom.Run.Editor, Place(bottom.Run.Editor, bottom.Run.Document.Snapshot.Length));
            window.KeyTextInput("typed");
            Ui.Press(window, "ENTER");
            Assert.True(sent is ["typed\n"], $"Enter in the Run console, its program reading its input, sent [{string.Join(", ", sent)}], not the line typed");
            CopiesInEveryKeymap(window, bottom.Run, "the Run console, its program reading its input");
            bottom.Run.Input = null;

            bottom.Debug.Sessions.Add(new DebugTab("Hello", bottom.Debug.FirstConsole));
            bottom.Debug.FirstConsole.Append(Lines);
            bottom.Debug.ShowTab(console: true);
            window.DockHost.Show(ToolWindowIds.Debug);
            Ui.Settle();
            CopiesInEveryKeymap(window, bottom.Debug.FirstConsole, "the Debug window's console");
        }
        finally
        {
            // The keymap in force is the process's; the next test starts on the platform's own.
            window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VisualStudio });
            window.Close();
        }
    }

    /// <summary>Selects a stretch of a console's text with the mouse, then presses each key the keymap gives Copy, in each
    /// keymap, the clipboard emptied before each: each must leave the stretch there.</summary>
    private static void CopiesInEveryKeymap(MainWindow window, OutputView console, string where)
    {
        CodeEditor editor = console.Editor;
        TextSnapshot text = console.Document.Snapshot;
        int start = text.GetLineStart(1) + "alpha ".Length;
        int end = start + "beta gamma".Length;
        Ui.Drag(window, Place(window, editor, start), Place(window, editor, end));
        Assert.True(editor.Selections.Primary.Range == TextRange.FromBounds(start, end),
            $"A drag in {where} selected {editor.Selections.Primary.Range}, not \"beta gamma\" at {TextRange.FromBounds(start, end)}");
        foreach (KeymapPreset preset in Enum.GetValues<KeymapPreset>())
        {
            window.ApplySettings(window.Settings with { Keymap = preset });
            Ui.Settle();
            foreach (Shortcut copy in AppActions.Keymap().ShortcutsFor(EditorActions.Copy))
            {
                Ui.Await(window.Clipboard!.SetTextAsync("nothing copied"), "the clipboard emptied");
                Ui.Press(window, copy.Notation());
                string? copied = Ui.Clipboard(window);
                Assert.True(copied == "beta gamma",
                    $"{copy.Display()}, Copy's key in {DefaultKeymaps.NameOf(preset)}'s keymap, left \"{copied}\" on the clipboard with \"beta gamma\" selected in {where}: a console's selection cannot be copied by its keys");
            }
        }
        window.ApplySettings(window.Settings with { Keymap = KeymapPreset.VisualStudio });
        Ui.Settle();
    }

    /// <summary>Fails unless the editor draws every character of a stretch it has selected in the selection's colour, read
    /// from the window's last frame at the top of each character's cell, where no glyph reaches.</summary>
    private static void AssertDrawnSelected(MainWindow window, CodeEditor editor, int start, int end, string what)
    {
        Ui.Settle();
        Assert.True(editor.Selections.Primary.Range == TextRange.FromBounds(start, end),
            $"{what} selected {editor.Selections.Primary.Range}, not {TextRange.FromBounds(start, end)}");
        Color selection = Application.Current!.TryFindResource(TokenKeys.Editor_Selection, window.ActualThemeVariant, out object? brush) && brush is ISolidColorBrush solid
            ? solid.Color
            : throw new InvalidOperationException($"The {window.ActualThemeVariant} theme has no {TokenKeys.Editor_Selection}");
        Point[] cells = [.. Enumerable.Range(start, end - start).Select(at =>
        {
            Point left = editor.PointOf(at);
            return editor.TranslatePoint(new Point((left.X + editor.PointOf(at + 1).X) / 2, left.Y + 1), window)!.Value;
        })];
        Color[] drawn = Ui.Pixels(window, cells);
        int[] unselected = [.. Enumerable.Range(0, drawn.Length).Where(i => drawn[i] != selection)];
        Assert.True(unselected.Length == 0,
            $"{what} selected \"{editor.Document.Snapshot.GetText(start, end - start)}\", but {unselected.Length} of its {drawn.Length} characters are drawn "
            + $"without the selection's colour {selection} (the first, character {unselected.FirstOrDefault() + 1}, in {drawn[unselected.FirstOrDefault()]}): "
            + "a person sees no selection where the text is selected");
    }

    /// <summary>Where a press lands on an offset's character, in the window: its left edge, the middle of its row.</summary>
    private static Point Place(MainWindow window, CodeEditor editor, int offset) =>
        editor.TranslatePoint(Place(editor, offset), window)!.Value;

    private static Point Place(CodeEditor editor, int offset) => editor.PointOf(offset) + new Vector(1, editor.LineHeight / 2);
}
