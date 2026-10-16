using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Rider's line separator and encoding, changed from the status bar's widgets and from File: a line separator
/// chosen converts every break and writes the file; an encoding chosen reloads the file's bytes in it or converts the text
/// to it, as the person answers, and warns before losing a character. Each is one undoable step, undone on disk too.</summary>
public sealed class FilePropertiesTests : IDisposable
{
    private readonly string folder = TestFolders.Create("properties-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void An_LF_file_converted_to_CRLF_and_back_is_written_so_and_each_undo_writes_the_file_as_it_was()
    {
        string path = Write("One.cs", "class One\n{\n}\n");
        MainWindow window = Open("One.cs");

        // Current values are checked, including in the native menu, and selecting one again leaves edits unsaved.
        Click(window, "LineSeparatorButton");
        MenuTests.CurrentValue(window.OpenContextMenu!.Items.OfType<MenuItem>(), "LF - Unix and macOS (\\n)");
        Choose(window, "CRLF - Windows (\\r\\n)");
        Assert.Equal("class One\r\n{\r\n}\r\n", File.ReadAllText(path));
        Assert.Equal("CRLF", Shown(window, "LineEndingText"));
        Assert.False(window.EditorArea.Active!.IsModified, "The converted file was written, yet its tab says it has unsaved changes");
        Assert.Equal("Changed Line Separators from LF to CRLF", window.Status);

        // Enter now writes the file's break.
        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single("class One".Length);
        Ui.Press(window, "ENTER");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Equal("class One\r\n\r\n{\r\n}\r\n", File.ReadAllText(path));

        // File, Line Separators makes it LF again.
        NativeMenu separators = Ui.Submenu(Ui.Submenu(window.MenuBar, "File"), "Line Separators");
        Assert.Equal(["CRLF - Windows (\\r\\n)", "LF - Unix and macOS (\\n)", "CR - Classic Mac OS (\\r)"], separators.Items.OfType<NativeMenuItem>().Select(i => i.Header));
        NativeMenuItem current = Assert.Single(separators.Items.OfType<NativeMenuItem>(), i => i.IsChecked);
        Assert.Equal("CRLF - Windows (\\r\\n)", current.Header);
        Assert.True(current.IsEnabled, "File, Line Separators greys the current value instead of checking it");
        separators.Items.OfType<NativeMenuItem>().Single(i => i.Header == "LF - Unix and macOS (\\n)").Command!.Execute(null);
        Ui.Settle();
        Assert.Equal("class One\n\n{\n}\n", File.ReadAllText(path));
        Assert.Equal("LF", Shown(window, "LineEndingText"));

        // Undo puts CRLF back, on disk as in the editor; another undo the Enter; another the first conversion.
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

        // Redo converts it again.
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
    public void A_Windows_1252_file_reloaded_as_windows_1252_shows_its_letters_and_is_saved_in_it()
    {
        // "Œuvre: “café” for 5 €" in Windows-1252, which is not UTF-8 and so first reads as Latin-1.
        string path = Path.Combine(folder, "Menu.txt");
        byte[] bytes = [0x8C, .. "uvre: "u8, 0x93, .. "caf"u8, 0xE9, 0x94, .. " for 5 "u8, 0x80, .. "\n"u8];
        File.WriteAllBytes(path, bytes);
        MainWindow window = Open("Menu.txt");
        Assert.Equal("ISO-8859-1", Shown(window, "EncodingText"));
        Assert.Equal("\u008Cuvre: \u0093café\u0094 for 5 \u0080\n", Text(window));

        Click(window, "EncodingButton");
        Assert.Contains("windows-1252", Items(window.OpenContextMenu!));
        MenuTests.CurrentValue(window.OpenContextMenu!.Items.OfType<MenuItem>().Where(i => i.Header as string != "More"), "ISO-8859-1");
        Choose(window, "windows-1252");
        Answer(window, "Menu.txt: Reload or Convert to windows-1252", "Reload");
        Assert.Equal("Œuvre: “café” for 5 €\n", Text(window));
        Assert.Equal("windows-1252", Shown(window, "EncodingText"));
        Assert.False(window.EditorArea.Active!.IsModified, "The file was only read again, yet its tab says it has unsaved changes");
        Assert.Equal(bytes, File.ReadAllBytes(path));

        // Saved, it stays Windows-1252, with no question of a change on disk.
        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single("Œuvre".Length);
        window.KeyTextInput(" ž");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Empty(window.OwnedWindows);
        Assert.Equal([0x8C, .. "uvre "u8, 0x9E, .. ": "u8, 0x93, .. "caf"u8, 0xE9, 0x94, .. " for 5 "u8, 0x80, .. "\n"u8], File.ReadAllBytes(path));
        Assert.Equal("windows-1252", Shown(window, "EncodingText"));

        // Undo, twice, reads it as it was before: Latin-1.
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal("ISO-8859-1", Shown(window, "EncodingText"));
        Assert.StartsWith("\u008Cuvre: ", Text(window), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_file_converted_to_UTF_16_is_written_with_its_byte_order_mark_and_undo_writes_it_as_UTF_8_again()
    {
        string path = Write("Cafe.cs", "class Café { }\n");
        MainWindow window = Open("Cafe.cs");
        Assert.Equal("UTF-8", Shown(window, "EncodingText"));

        // A BOM comes with no question, as Rider's Add BOM.
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

        // Big-endian from File, File Encoding, which opens the same list over the widget.
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
        Press(warning, "Cancel");
        Assert.Equal("// 5 € → 🚀\n", File.ReadAllText(path));
        Assert.Equal("UTF-8", Shown(window, "EncodingText"));

        Click(window, "EncodingButton");
        Choose(window, "ISO-8859-1");
        Answer(window, "Price.cs: Reload or Convert to ISO-8859-1", "Convert");
        Press(Assert.Single(window.OwnedWindows.OfType<ConfirmDialog>()), "Convert Anyway");
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
        Write(".editorconfig", "root = true\n\n[*]\nend_of_line = lf\ncharset = utf-8\n");
        string path = Write("One.cs", "class One\n{\n}\n");
        File.WriteAllBytes(Path.Combine(folder, "Two.cs"), Encoding.Latin1.GetBytes("// Café\r\n"));
        MainWindow window = Open("Two.cs");
        Assert.StartsWith("Line Separator: \\r\\n\nThe next save makes it LF, as the .editorconfig asks: end_of_line = lf in ", Tip(window, "LineSeparatorButton"), StringComparison.Ordinal);
        Assert.StartsWith("File Encoding: ISO-8859-1\nThe next save makes it UTF-8, as the .editorconfig asks: charset = utf-8 in ", Tip(window, "EncodingButton"), StringComparison.Ordinal);

        window.OpenFile("One.cs");
        Ui.Settle();
        Assert.StartsWith("Line Separator: \\n\nAs the .editorconfig asks: end_of_line = lf in ", Tip(window, "LineSeparatorButton"), StringComparison.Ordinal);
        Assert.StartsWith("File Encoding: UTF-8\nAs the .editorconfig asks: charset = utf-8 in ", Tip(window, "EncodingButton"), StringComparison.Ordinal);

        Click(window, "LineSeparatorButton");
        Choose(window, "CRLF - Windows (\\r\\n)");
        Assert.StartsWith("Line Separator: \\r\\n\nChosen over the .editorconfig's end_of_line = lf", Tip(window, "LineSeparatorButton"), StringComparison.Ordinal);

        // A save after an edit keeps what the person chose, as Rider leaves a converted file's separator.
        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single(0);
        window.KeyTextInput("public ");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));
        Assert.Equal("public class One\r\n{\r\n}\r\n", File.ReadAllText(path));
        window.Close();
    }

    [AvaloniaFact]
    public void Choosing_the_current_separator_or_encoding_keeps_unsaved_edits_and_creates_no_undo_step()
    {
        string path = Write("One.cs", "class One { }\n");
        MainWindow window = Open("One.cs");
        window.EditorArea.CodeEditor.Selections = Passenger.Text.SelectionSet.Single(0);
        window.KeyTextInput("public ");
        Click(window, "LineSeparatorButton");
        Choose(window, "LF - Unix and macOS (\\n)");
        Click(window, "EncodingButton");
        MenuTests.CurrentValue(window.OpenContextMenu!.Items.OfType<MenuItem>().Where(i => i.Header as string != "More"), "UTF-8");
        Choose(window, "UTF-8");
        Assert.True(window.EditorArea.Active!.IsModified);
        Assert.Equal("class One { }\n", File.ReadAllText(path));
        Assert.Empty(window.OwnedWindows);
        window.EditorArea.CodeEditor.Focus();
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal("class One { }\n", Text(window));
        Assert.False(window.EditorArea.Active.IsModified);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Long_status_breadcrumbs_leave_storage_widgets_and_messages_readable_and_remain_reachable(bool light)
    {
        const string Name = "A long file name that must not overlap the status caret separator encoding indentation or language widgets.cs";
        string relative = Path.Combine("Long parent folder", "Nested source folder", Name);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(folder, relative))!);
        string path = Write(relative, "class LongName { }\n");
        Write("Single.cs", "class Single { }\n");
        MainWindow window = Open("Single.cs");
        window.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        window.SetRenderScaling(2);
        try
        {
            for (int opened = 0; opened < 2; opened++)
            {
                Ui.Settle();
                Button single = Assert.Single(window.FindControl<Control>("BreadcrumbHost")!.GetVisualDescendants().OfType<Button>(), button => button.Classes.Contains("crumb"));
                Assert.True(single.IsEffectivelyVisible && single.Bounds.Width > 24, "A plain root-level file must bootstrap a readable breadcrumb after the hidden empty state");
                Assert.Equal("Single.cs", single.Content);
                UiAudit.Capture(window, $"status-path-single-{opened}-{(light ? "light" : "dark")}");
                Ui.Run(window, AppActions.CloseTab);
                Assert.Empty(window.Breadcrumbs);
                if (opened == 0)
                {
                    window.OpenFile("Single.cs");
                }
            }
            window.OpenFile(relative);
            Ui.Settle();
            Assert.Equal(["Long parent folder", "Nested source folder", Name], window.Breadcrumbs);
            Control host = window.FindControl<Control>("BreadcrumbHost")!;
            Control bar = host.GetVisualAncestors().OfType<DockPanel>().First();
            StackPanel widgets = window.FindControl<TextBlock>("LanguageText")!.GetVisualAncestors().OfType<StackPanel>().First();
            TextBlock message = window.FindControl<TextBlock>("StatusText")!;
            Button[] crumbs = [.. host.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("crumb"))];
            foreach (double width in new double[] { 1200, 683, 640, 1200 })
            {
                window.Width = width;
                Ui.Settle();
                Check("path");
            }
            window.Width = 640;
            Ui.Settle();
            Click(window, "LineSeparatorButton");
            Choose(window, "CRLF - Windows (\\r\\n)");
            Assert.Equal("Changed Line Separators from LF to CRLF", window.Status);
            Assert.Contains("\r\n", File.ReadAllText(path), StringComparison.Ordinal);
            foreach (double width in new double[] { 640, 683, 1200, 640 })
            {
                window.Width = width;
                Ui.Settle();
                Check("message");
                Assert.Equal(window.Status, ToolTip.GetTip(message));
            }

            // Every folder and the long filename stay reachable without sacrificing the status widgets.
            for (int index = 0; index < crumbs.Length; index++)
            {
                Button crumb = crumbs[index];
                for (int attempt = 0; attempt < 30 && !Inside(crumb, host); attempt++)
                {
                    double left = crumb.TranslatePoint(default, host)!.Value.X;
                    double clipped = left < 0 ? left : left + crumb.Bounds.Width - host.Bounds.Width;
                    Point wheel = host.TranslatePoint(new Point(host.Bounds.Width / 2, host.Bounds.Height / 2), window)!.Value;
                    window.MouseWheel(wheel, new Vector(-Math.Clamp(clipped / 50, -3, 3), 0));
                    Ui.Settle();
                }
                Assert.True(Inside(crumb, host), $"Breadcrumb '{crumb.Content}' cannot be reached in the finite status path");
                Assert.IsType<string>(ToolTip.GetTip(crumb));
                TextBlock label = Assert.Single(crumb.GetVisualDescendants().OfType<TextBlock>());
                Assert.True(label.TextLayout.Width <= label.Bounds.Width + 1, "Long breadcrumb names must ellipsize inside their own reachable button");
                Point at = crumb.TranslatePoint(new Point(crumb.Bounds.Width / 2, crumb.Bounds.Height / 2), window)!.Value;
                window.MouseMove(at);
                window.MouseDown(at, MouseButton.Left);
                window.MouseUp(at, MouseButton.Left);
                Ui.Settle();
                string expected = index == 0 ? Path.Combine(folder, "Long parent folder")
                    : index == 1 ? Path.Combine(folder, "Long parent folder", "Nested source folder") : path;
                Assert.Contains(window.ExplorerView.SelectedNodes, node => node.Path == Passenger.Core.PathNames.Canonical(expected));
                Assert.Equal(Name, window.EditorArea.Active?.Name);
            }

            Assert.True(crumbs[0].Focus());
            Ui.Settle();
            Ui.Press(window, "tab");
            Assert.Same(crumbs[1], window.FocusManager?.GetFocusedElement());
            foreach (double width in new double[] { 1200, 683, 640 })
            {
                window.Width = width;
                Ui.Settle();
                Assert.Same(crumbs[1], window.FocusManager?.GetFocusedElement());
                Assert.True(Inside(crumbs[1], host), "Resizing must preserve the focused breadcrumb in its viewport");
                Check("focused");
            }

            void Check(string state)
            {
                Point hostAt = host.TranslatePoint(default, bar)!.Value;
                Point widgetsAt = widgets.TranslatePoint(default, bar)!.Value;
                bool separated = hostAt.X >= 0 && hostAt.X + host.Bounds.Width <= widgetsAt.X + 0.5;
                bool widgetsFit = widgets.Children.Where(control => control.IsEffectivelyVisible).All(control => Inside(control, bar));
                List<string> missed = [];
                bool actionsHit = widgets.Children.OfType<Button>().Where(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Bounds.Width > 0).All(button =>
                {
                    Point at = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(at);
                    Ui.Settle();
                    IInputElement? hit = window.InputHitTest(at);
                    bool reached = button.IsPointerOver && (hit == button || hit is Visual visual && visual.GetVisualAncestors().Contains(button));
                    if (!reached)
                    {
                        missed.Add($"{button.Name} at {at}, hit {hit}");
                    }
                    return reached;
                });
                bool messageFits = Inside(message, bar) && message.TranslatePoint(default, bar)!.Value.X >= hostAt.X + host.Bounds.Width - 0.5
                    && message.TextLayout.Width <= message.Bounds.Width + 1;
                if (!string.IsNullOrEmpty(message.Text))
                {
                    Point messageAt = message.TranslatePoint(default, bar)!.Value;
                    messageFits &= messageAt.X >= hostAt.X + host.Bounds.Width + 11.5
                        && messageAt.X + message.Bounds.Width <= widgetsAt.X - 11.5;
                }
                UiAudit.Capture(window, $"status-path-{state}-{window.Width:0}-{(light ? "light" : "dark")}");
                Assert.True(separated && widgetsFit && messageFits && actionsHit, $"The status path overlaps its widgets/message at {window.Width}px: path={hostAt} {host.Bounds}, widgets={widgetsAt} {widgets.Bounds}, message={message.Bounds}, missed actions: {string.Join("; ", missed)}");
            }
        }
        finally
        {
            window.Close();
        }

        static bool Inside(Control control, Control parent) => control.TranslatePoint(default, parent) is { } at
            && at.X >= -0.5 && at.X + control.Bounds.Width <= parent.Bounds.Width + 0.5;
    }

    private string Write(string name, string text)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A window on the folder with a file of it open; it saves only when asked.</summary>
    private MainWindow Open(string file)
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(file);
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
        return window;
    }

    /// <summary>A click on a status bar widget, where the pointer finds it.</summary>
    private static void Click(MainWindow window, string name)
    {
        Button widget = window.FindControl<Button>(name)!;
        Assert.True(widget.IsVisible, $"The status bar's {name} is hidden");
        Point at = widget.TranslatePoint(new Point(widget.Bounds.Width / 2, widget.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Ui.Settle();
        Assert.True(window.OpenContextMenu is not null, $"A click on the status bar's {name} opened no menu");
    }

    /// <summary>The open menu's items, a disabled one marked so.</summary>
    private static string[] Items(ContextMenu menu) =>
        [.. menu.Items.OfType<MenuItem>().Select(i => $"{i.Header}{(i.IsEnabled ? "" : " (disabled)")}")];

    /// <summary>Chooses an item of the open menu, or of its More.</summary>
    private static void Choose(MainWindow window, string item)
    {
        ContextMenu menu = window.OpenContextMenu ?? throw new InvalidOperationException("No menu is open");
        IEnumerable<MenuItem> items = menu.Items.OfType<MenuItem>();
        MenuItem chosen = items.Concat(items.SelectMany(i => i.Items.OfType<MenuItem>())).FirstOrDefault(i => (string?)i.Header == item)
            ?? throw new InvalidOperationException($"The menu has no {item}: [{string.Join(", ", Items(menu))}]");
        Assert.True(chosen.IsEnabled, $"{item} is disabled");
        chosen.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Ui.Settle();
    }

    /// <summary>Answers Rider's Reload or Convert question, failing unless it was asked as named.</summary>
    private static void Answer(MainWindow window, string title, string button)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<ChoiceDialog>().Any(), $"the question '{title}'");
        ChoiceDialog asked = DialogButtonsTests.Check(window.OwnedWindows.OfType<ChoiceDialog>().Single());
        Assert.Equal(title, asked.Title);
        Press(asked, button);
    }

    private static void Press(Window dialog, string button)
    {
        Button pressed = dialog.GetVisualDescendants().OfType<Button>().First(b => (string?)b.Content == button);
        pressed.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Ui.Settle();
    }

    private static string? Shown(MainWindow window, string name) => window.FindControl<TextBlock>(name)!.Text;

    private static string? Tip(MainWindow window, string name) => ToolTip.GetTip(window.FindControl<Button>(name)!) as string;

    private static string Text(MainWindow window) => window.EditorArea.CodeEditor.Document.Snapshot.ToString();
}
