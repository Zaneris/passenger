using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The line separator and encoding, changed from the status bar's widgets and from File: a line separator
/// chosen converts every break and writes the file; an encoding chosen reloads the file's bytes in it or converts the
/// text to it, as the person answers, and warns before losing a character. Each is one undoable step, undone on disk
/// too.</summary>
public sealed class FilePropertiesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("properties-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void An_LF_file_converted_to_CRLF_and_back_is_written_so_and_each_undo_writes_the_file_as_it_was()
    {
        string path = Write("One.cs", "class One\n{\n}\n");
        MainWindow window = Open("One.cs");

        Click(window, "LineSeparatorButton");
        Assert.Equal(["CRLF - Windows (\\r\\n)", "LF - Unix and macOS (\\n) (checked)", "CR - Classic Mac OS (\\r)"], Items(window.OpenContextMenu!));
        Choose(window, "CRLF - Windows (\\r\\n)");
        Assert.Equal("class One\r\n{\r\n}\r\n", File.ReadAllText(path));
        Assert.Equal("CRLF", Shown(window, "LineEndingText"));
        Assert.False(window.EditorArea.Active!.IsModified, "The converted file was written, yet its tab says it has unsaved changes");
        Assert.Equal("Changed Line Separators from LF to CRLF", window.Status);

        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single("class One".Length);
        Ui.Press(window, "ENTER");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Equal("class One\r\n\r\n{\r\n}\r\n", File.ReadAllText(path));

        NativeMenu separators = Ui.Submenu(Ui.Submenu(window.MenuBar, "File"), "Line Separators");
        Assert.Equal(["CRLF - Windows (\\r\\n)", "LF - Unix and macOS (\\n)", "CR - Classic Mac OS (\\r)"], separators.Items.OfType<NativeMenuItem>().Select(i => i.Header));
        NativeMenuItem own = separators.Items.OfType<NativeMenuItem>().First();
        Assert.True(own.IsChecked && own.IsEnabled, "File, Line Separators does not check CRLF, the file's own, or greys it where Rider checks it");
        separators.Items.OfType<NativeMenuItem>().Single(i => i.Header == "LF - Unix and macOS (\\n)").Command!.Execute(null);
        Ui.Settle();
        Assert.Equal("class One\n\n{\n}\n", File.ReadAllText(path));
        Assert.Equal("LF", Shown(window, "LineEndingText"));

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal("class One\r\n\r\n{\r\n}\r\n", File.ReadAllText(path));
        Assert.Equal("CRLF", Shown(window, "LineEndingText"));
        Assert.False(window.EditorArea.Active.IsModified, "Undoing the conversion wrote the file back, yet its tab says it has unsaved changes");
        Ui.Run(window, EditorActions.Undo);
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal("class One\n{\n}\n", File.ReadAllText(path));
        Assert.Equal("class One\n{\n}\n", Text(window));
        Assert.Equal("LF", Shown(window, "LineEndingText"));

        Ui.Run(window, EditorActions.Redo);
        Assert.Equal("class One\r\n{\r\n}\r\n", File.ReadAllText(path));
        Assert.Equal("CRLF", Shown(window, "LineEndingText"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_classic_Mac_file_shows_its_lines_keeps_its_breaks_through_a_save_and_converts_to_LF()
    {
        string path = Write("Mac.txt", "one\rtwo\rthree\r");
        MainWindow window = Open("Mac.txt");
        Assert.Equal(4, window.EditorArea.CodeEditor.Document.Snapshot.LineCount);
        Assert.Equal("CR", Shown(window, "LineEndingText"));

        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single("one".Length);
        Ui.Press(window, "ENTER");
        window.KeyTextInput("and a half");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Equal("one\rand a half\rtwo\rthree\r", File.ReadAllText(path));

        Click(window, "LineSeparatorButton");
        Choose(window, "LF - Unix and macOS (\\n)");
        Assert.Equal("one\nand a half\ntwo\nthree\n", File.ReadAllText(path));
        Click(window, "LineSeparatorButton");
        Choose(window, "CR - Classic Mac OS (\\r)");
        Assert.Equal("one\rand a half\rtwo\rthree\r", File.ReadAllText(path));
        Assert.Equal("CR", Shown(window, "LineEndingText"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_Windows_1252_file_opens_showing_its_letters_and_shows_control_characters_only_once_reloaded_as_ISO_8859_1()
    {
        // Windows-1252, not UTF-8, so read in the 8-bit encoding a file of no other is read in.
        string path = Path.Combine(folder, "Menu.txt");
        byte[] bytes = [0x8C, .. "uvre: "u8, 0x93, .. "caf"u8, 0xE9, 0x94, .. " for 5 "u8, 0x80, .. "\n"u8];
        File.WriteAllBytes(path, bytes);
        MainWindow window = Open("Menu.txt");
        Assert.Equal("windows-1252", Shown(window, "EncodingText"));
        Assert.Equal("Œuvre: “café” for 5 €\n", Text(window));

        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single("Œuvre".Length);
        window.KeyTextInput(" ž");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Empty(window.OwnedWindows);
        byte[] saved = [0x8C, .. "uvre "u8, 0x9E, .. ": "u8, 0x93, .. "caf"u8, 0xE9, 0x94, .. " for 5 "u8, 0x80, .. "\n"u8];
        Assert.Equal(saved, File.ReadAllBytes(path));

        Click(window, "EncodingButton");
        Assert.Contains("ISO-8859-1", Items(window.OpenContextMenu!));
        Assert.Contains("windows-1252 (checked)", Items(window.OpenContextMenu!));
        Choose(window, "ISO-8859-1");
        Answer(window, "Menu.txt: Reload or Convert to ISO-8859-1", "Reload");
        Assert.Equal("\u008Cuvre \u009E: \u0093café\u0094 for 5 \u0080\n", Text(window));
        Assert.Equal("ISO-8859-1", Shown(window, "EncodingText"));
        Assert.False(window.EditorArea.Active!.IsModified, "The file was only read again, yet its tab says it has unsaved changes");
        Assert.Equal(saved, File.ReadAllBytes(path));

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal("windows-1252", Shown(window, "EncodingText"));
        Assert.Equal("Œuvre ž: “café” for 5 €\n", Text(window));
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_converted_to_UTF_16_is_written_with_its_byte_order_mark_and_undo_writes_it_as_UTF_8_again()
    {
        string path = Write("Cafe.cs", "class Café { }\n");
        MainWindow window = Open("Cafe.cs");
        Assert.Equal("UTF-8", Shown(window, "EncodingText"));

        Click(window, "EncodingButton");
        Choose(window, "UTF-8 BOM");
        Assert.Empty(window.OwnedWindows);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("class Café { }\n")], File.ReadAllBytes(path));

        Click(window, "EncodingButton");
        Choose(window, "UTF-16 LE");
        Answer(window, "Cafe.cs: Reload or Convert to UTF-16 LE", "Convert");
        Assert.Equal([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("class Café { }\n")], File.ReadAllBytes(path));
        Assert.Equal("UTF-16 LE", Shown(window, "EncodingText"));
        Assert.False(window.EditorArea.Active!.IsModified);

        Ui.Submenu(window.MenuBar, "File").Items.OfType<NativeMenuItem>().Single(i => i.Header == "File Encoding").Command!.Execute(null);
        Ui.Settle();
        Choose(window, "UTF-16 BE");
        Answer(window, "Cafe.cs: Reload or Convert to UTF-16 BE", "Convert");
        Assert.Equal([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("class Café { }\n")], File.ReadAllBytes(path));

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("class Café { }\n")], File.ReadAllBytes(path));
        Ui.Run(window, EditorActions.Undo);
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(Encoding.UTF8.GetBytes("class Café { }\n"), File.ReadAllBytes(path));
        Assert.Equal("UTF-8", Shown(window, "EncodingText"));
        Assert.False(window.EditorArea.Active.IsModified);
        window.Close();
    }

    [AvaloniaFact]
    public void A_conversion_that_would_lose_characters_warns_first_and_converted_anyway_writes_question_marks_undo_brings_back()
    {
        string path = Write("Price.cs", "// 5 € → 🚀\n");
        MainWindow window = Open("Price.cs");

        Click(window, "EncodingButton");
        Choose(window, "ISO-8859-1");
        Answer(window, "Price.cs: Reload or Convert to ISO-8859-1", "Convert");
        ConfirmDialog warning = Assert.Single(window.OwnedWindows.OfType<ConfirmDialog>());
        Assert.Equal("Incompatible Encoding: ISO-8859-1", warning.Title);
        Ui.ClickButton(warning, "Cancel");
        Assert.Equal("// 5 € → 🚀\n", File.ReadAllText(path));
        Assert.Equal("UTF-8", Shown(window, "EncodingText"));

        Click(window, "EncodingButton");
        Choose(window, "ISO-8859-1");
        Answer(window, "Price.cs: Reload or Convert to ISO-8859-1", "Convert");
        Ui.ClickButton(Assert.Single(window.OwnedWindows.OfType<ConfirmDialog>()), "Convert Anyway");
        Assert.Equal(Encoding.Latin1.GetBytes("// 5 ? ? ?\n"), File.ReadAllBytes(path));
        Assert.Equal("// 5 ? ? ?\n", Text(window));
        Assert.Equal("ISO-8859-1", Shown(window, "EncodingText"));

        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal("// 5 € → 🚀\n", Text(window));
        Assert.Equal(Encoding.UTF8.GetBytes("// 5 € → 🚀\n"), File.ReadAllBytes(path));
        Assert.Equal("UTF-8", Shown(window, "EncodingText"));
        window.Close();
    }

    [AvaloniaFact]
    public void An_editorconfigs_end_of_line_and_charset_show_on_the_widgets_and_a_choice_against_them_outlasts_a_save()
    {
        Write(".editorconfig", "root = true\n\n[*]\nend_of_line = lf\ncharset = utf-8\n\n[Three.cs]\ncharset = latin1\n");
        string path = Write("One.cs", "class One\n{\n}\n");
        File.WriteAllBytes(Path.Combine(folder, "Two.cs"), Encoding.Latin1.GetBytes("// Café\r\n"));
        File.WriteAllBytes(Path.Combine(folder, "Three.cs"), [.. "// Caf"u8, 0xE9, 0x80, .. "\n"u8]);
        MainWindow window = Open("Two.cs");
        Assert.StartsWith("Line Separator: \\r\\n\nThe next save makes it LF, as the .editorconfig asks: end_of_line = lf in ", Tip(window, "LineSeparatorButton"), StringComparison.Ordinal);
        Assert.StartsWith("File Encoding: windows-1252\nThe next save makes it UTF-8, as the .editorconfig asks: charset = utf-8 in ", Tip(window, "EncodingButton"), StringComparison.Ordinal);

        window.OpenFile("Three.cs");
        Ui.Settle();
        Assert.Equal("// Café\u0080\n", Text(window));
        Assert.StartsWith("File Encoding: ISO-8859-1\nAs the .editorconfig asks: charset = latin1 in ", Tip(window, "EncodingButton"), StringComparison.Ordinal);

        window.OpenFile("One.cs");
        Ui.Settle();
        Assert.StartsWith("Line Separator: \\n\nAs the .editorconfig asks: end_of_line = lf in ", Tip(window, "LineSeparatorButton"), StringComparison.Ordinal);
        Assert.StartsWith("File Encoding: UTF-8\nAs the .editorconfig asks: charset = utf-8 in ", Tip(window, "EncodingButton"), StringComparison.Ordinal);

        Click(window, "LineSeparatorButton");
        Choose(window, "CRLF - Windows (\\r\\n)");
        Assert.StartsWith("Line Separator: \\r\\n\nChosen over the .editorconfig's end_of_line = lf", Tip(window, "LineSeparatorButton"), StringComparison.Ordinal);

        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single(0);
        window.KeyTextInput("public ");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Equal("public class One\r\n{\r\n}\r\n", File.ReadAllText(path));
        window.Close();
    }

    private string Write(string name, string text) => TestFolders.Write(folder, name, text);

    /// <summary>A window on the folder with a file of it open; it saves only when asked.</summary>
    private MainWindow Open(string file)
    {
        MainWindow window = Ui.OpenFolder(folder, autosaves: false);
        window.OpenFile(file);
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
        return window;
    }

    /// <summary>A click on a status bar widget, where the pointer finds it.</summary>
    internal static void Click(MainWindow window, string name)
    {
        Ui.Click(window.FindControl<Button>(name)!);
        Assert.True(window.OpenContextMenu is not null, $"A click on the status bar's {name} opened no menu");
    }

    /// <summary>The open menu's items, a disabled one and the checked one marked so.</summary>
    private static string[] Items(ContextMenu menu) =>
        [.. menu.Items.OfType<MenuItem>().Select(i => $"{i.Header}{(i.IsEnabled ? "" : " (disabled)")}{(i.IsChecked ? " (checked)" : "")}")];

    /// <summary>Chooses an item of the open menu, or of its More.</summary>
    internal static void Choose(MainWindow window, string item)
    {
        ContextMenu menu = window.OpenContextMenu ?? throw new InvalidOperationException("No menu is open");
        IEnumerable<MenuItem> items = menu.Items.OfType<MenuItem>();
        if (items.Any(i => (string?)i.Header == item))
        {
            Ui.Choose(menu, item);
            return;
        }
        MenuItem more = items.FirstOrDefault(m => m.Items.OfType<MenuItem>().Any(i => (string?)i.Header == item))
            ?? throw new InvalidOperationException($"The menu has no {item}: [{string.Join(", ", Items(menu))}]");
        Ui.Choose(menu, (string)more.Header!, item);
    }

    /// <summary>Answers the Reload or Convert question, failing unless it was asked as named.</summary>
    internal static void Answer(MainWindow window, string title, string button)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(), $"the question '{title}'");
        ChoiceDialog asked = DialogButtonsTests.Check(window.OwnedWindows.OfType<ChoiceDialog>().Single());
        Assert.Equal(title, asked.Title);
        Ui.ClickButton(asked, button);
    }

    internal static string? Shown(MainWindow window, string name) => window.FindControl<TextBlock>(name)!.Text;

    private static string? Tip(MainWindow window, string name) => ToolTip.GetTip(window.FindControl<Button>(name)!) as string;

    private static string Text(MainWindow window) => window.EditorArea.CodeEditor.Document.Snapshot.ToString();
}
