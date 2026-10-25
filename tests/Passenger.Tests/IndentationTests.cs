using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Passenger.App;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>How a file indents, and what its save does to it: the nearest .editorconfig sections that match the file,
/// else the file's own lines, else its language; Enter, Tab, Shift+Tab and a pasted block follow it, the status bar
/// names it and where it came from, a save trims, ends and writes the file as the .editorconfig asks, and an
/// .editorconfig changed on disk is followed at once.</summary>
public sealed class IndentationTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("indent-"));

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_file_of_two_spaces_with_no_editorconfig_keeps_two_spaces_for_enter_tab_shift_tab_and_a_paste()
    {
        Write("Two.cs", "class Two\n{\n  void Run()\n  {\n    int a = 1;\n    if (a > 0)\n    {\n      a++;\n    }\n  }\n}\n");
        MainWindow window = Open("Two.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal("2 spaces, detected", IndentShown(window));

        Ui.PlaceAfter(editor, "if (a > 0)\n    {");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("a--;");
        Assert.Contains("    {\n      a--;\n      a++;", Text(editor), StringComparison.Ordinal);
        Ui.Press(window, "TAB");
        Assert.Contains("\n      a--;  \n", Text(editor), StringComparison.Ordinal);
        PlaceBefore(editor, "a--;");
        Ui.Press(window, "TAB");
        Assert.Contains("\n        a--;  \n", Text(editor), StringComparison.Ordinal);
        Ui.Press(window, "shift TAB");
        Ui.Press(window, "shift TAB");
        Assert.Contains("\n    a--;  \n", Text(editor), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "int a = 1;");
        Ui.Press(window, "ENTER");
        Copy(window, "if (a < 0)\n\t\t{\n\t\t\ta = 0;\n\t\t}");
        Ui.Press(window, Ui.Shortcut(EditorActions.Paste));
        Ui.Settle();
        Assert.True(Text(editor).Contains("    int a = 1;\n    if (a < 0)\n    {\n      a = 0;\n    }\n", StringComparison.Ordinal),
            $"The pasted block did not take the file's two spaces:\n{Text(editor)}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_tab_editorconfig_over_a_file_of_spaces_has_enter_tab_and_a_paste_write_tabs_and_the_status_bar_say_so()
    {
        Write(".editorconfig", "root = true\n\n[*]\nindent_style = tab\n");
        Write("Spaced.cs", "class Spaced\n{\n    void Run()\n    {\n        int a = 1;\n    }\n}\n");
        MainWindow window = Open("Spaced.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal("Tab, from .editorconfig", IndentShown(window));
        Assert.Equal($"Indent: Tab, from {Path.Combine(folder, ".editorconfig")}", ToolTip.GetTip(window.FindControl<TextBlock>("IndentText")!));

        Ui.PlaceAfter(editor, "void Run()\n    {");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("a();");
        Assert.Contains("    {\n    \ta();\n", Text(editor), StringComparison.Ordinal);
        PlaceBefore(editor, "a();");
        Ui.Press(window, "TAB");
        Assert.Contains("\n    \t\ta();\n", Text(editor), StringComparison.Ordinal);
        Ui.Press(window, "shift TAB");
        Assert.Contains("\n\t\ta();\n", Text(editor), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "int a = 1;");
        Ui.Press(window, "ENTER");
        Copy(window, "if (a < 0)\n        {\n            a = 0;\n        }");
        Ui.Press(window, Ui.Shortcut(EditorActions.Paste));
        Ui.Settle();
        Assert.True(Text(editor).Contains("        int a = 1;\n        if (a < 0)\n\t\t{\n\t\t\ta = 0;\n\t\t}\n", StringComparison.Ordinal),
            $"The pasted block did not take the .editorconfig's tabs:\n{Text(editor)}");
        window.Close();
    }

    [AvaloniaFact]
    public void The_nearest_matching_section_decides_and_a_nested_root_editorconfig_stops_the_search()
    {
        // Within a file the later section wins; a nested file wins over its parent's; a root one never reads its parent.
        Write(".editorconfig", "root = true\n\n[*]\nindent_style = space\nindent_size = 4\n\n[*.cs]\nindent_style = tab\n");
        Write("Top.cs", "class Top { }\n");
        Write("top.json", "{ }\n");
        Write("inner/.editorconfig", "[*.cs]\nindent_style = space\nindent_size = 2\n");
        Write("inner/Inner.cs", "class Inner { }\n");
        Write("inner/inner.json", "{ }\n");
        Write("own/.editorconfig", "root = true\n\n[*.{cs,json}]\nindent_size = 3\n\n[case{1..3}.cs]\nindent_style = tab\n\n[deep/**/[!x]*.cs]\nindent_size = 5\n");
        Write("own/Own.cs", "class Own { }\n");
        Write("own/case2.cs", "class Case2 { }\n");
        Write("own/case7.cs", "class Case7 { }\n");
        Write("own/deep/a/b/Deep.cs", "class Deep { }\n");
        Write("own/deep/a/b/xDeep.cs", "class XDeep { }\n");
        MainWindow window = Open(null);

        (string File, string Shown, string Tab)[] expected =
        [
            ("Top.cs", "Tab, from .editorconfig", "\t"),
            ("top.json", "4 spaces, from .editorconfig", "    "),
            ("inner/Inner.cs", "2 spaces, from .editorconfig", "  "),
            ("inner/inner.json", "4 spaces, from .editorconfig", "    "),
            ("own/Own.cs", "3 spaces, from .editorconfig", "   "),
            ("own/case2.cs", "Tab, from .editorconfig", "\t"),
            ("own/case7.cs", "3 spaces, from .editorconfig", "   "),
            ("own/deep/a/b/Deep.cs", "5 spaces, from .editorconfig", "     "),
            ("own/deep/a/b/xDeep.cs", "3 spaces, from .editorconfig", "   "),
        ];
        foreach ((string file, string shown, string tab) in expected)
        {
            window.OpenFile(file);
            Ui.Settle();
            Assert.True(shown == IndentShown(window), $"{file}'s indentation shows as {IndentShown(window)}, not {shown}");
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Selections = SelectionSet.Single(0);
            Ui.Press(window, "TAB");
            Assert.True(Text(editor).StartsWith(tab + "class", StringComparison.Ordinal) || Text(editor).StartsWith(tab + "{", StringComparison.Ordinal),
                $"Tab at the start of {file} did not put in '{tab.Replace("\t", "→", StringComparison.Ordinal)}': {Text(editor)}");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void A_save_trims_trailing_whitespace_but_the_carets_ends_the_file_with_a_break_and_writes_the_editorconfigs_breaks_and_charset()
    {
        Write(".editorconfig", "root = true\n\n[*]\ntrim_trailing_whitespace = true\ninsert_final_newline = true\nend_of_line = crlf\ncharset = utf-8-bom\n");
        Write("Notes.cs", "class Notes  \n{\n    int a;   \n    int b;\t\n}");
        MainWindow window = Open("Notes.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal("LF", window.FindControl<TextBlock>("LineEndingText")!.Text);

        Ui.PlaceAfter(editor, "int b;\t");
        window.KeyTextInput(" ");
        Ui.Press(window, Ui.Shortcut(AppActions.Save));

        byte[] saved = File.ReadAllBytes(Path.Combine(folder, "Notes.cs"));
        Assert.Equal([0xEF, 0xBB, 0xBF], saved[..3]);
        string written = Encoding.UTF8.GetString(saved, 3, saved.Length - 3);
        Assert.Equal("class Notes\r\n{\r\n    int a;\r\n    int b;\t \r\n}\r\n", written);
        Assert.Equal(written, Text(editor));
        Assert.False(window.EditorArea.Active!.IsModified);
        Assert.Equal("CRLF", window.FindControl<TextBlock>("LineEndingText")!.Text);
        Assert.Equal("UTF-8 BOM", window.FindControl<TextBlock>("EncodingText")!.Text);

        Ui.Press(window, "ENTER");
        Assert.Contains("    int b;\t \r\n    \r\n}", Text(editor), StringComparison.Ordinal);
        Assert.Null(window.CreateClass(folder, "Made"));
        byte[] made = File.ReadAllBytes(Path.Combine(folder, "Made.cs"));
        Assert.Equal([0xEF, 0xBB, 0xBF], made[..3]);
        string madeText = Encoding.UTF8.GetString(made, 3, made.Length - 3);
        Assert.EndsWith(";\r\n\r\npublic class Made\r\n{\r\n}\r\n", madeText, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", madeText.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void An_editorconfig_written_or_deleted_on_disk_changes_how_the_open_file_indents()
    {
        Write("Open.cs", "class Open\n{\n    void Run()\n    {\n        int a = 1;\n    }\n}\n");
        MainWindow window = Open("Open.cs");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal("4 spaces, detected", IndentShown(window));

        Write(".editorconfig", "root = true\n\n[*.cs]\nindent_style = tab\n");
        Ui.WaitFor(() => IndentShown(window) == "Tab, from .editorconfig", "the open file to take the new .editorconfig's tabs");
        editor.Selections = SelectionSet.Single(0);
        Ui.Press(window, "TAB");
        Assert.StartsWith("\tclass Open", Text(editor), StringComparison.Ordinal);

        File.Delete(Path.Combine(folder, ".editorconfig"));
        Ui.WaitFor(() => IndentShown(window) == "4 spaces, detected", "the open file to go back to its own indentation");
        Ui.Press(window, "TAB");
        Assert.StartsWith("\t    class Open", Text(editor), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_xaml_window_whose_attributes_line_up_under_its_tags_is_read_as_its_elements_four_spaces_and_reformat_keeps_them()
    {
        // Avalonia's MVVM template's window, as dotnet new writes it: its elements four spaces a level, each tag's
        // attributes aligned under its first, and a comment's second line aligned under its first's words.
        const string Template = "<Window xmlns=\"https://github.com/avaloniaui\"\n        xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"\n"
            + "        xmlns:vm=\"using:AvApp.ViewModels\"\n        xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"\n"
            + "        xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"\n        mc:Ignorable=\"d\" d:DesignWidth=\"800\" d:DesignHeight=\"450\"\n"
            + "        x:Class=\"AvApp.Views.MainWindow\"\n        x:DataType=\"vm:MainViewModel\"\n        Icon=\"/Assets/avalonia-logo.ico\"\n        Title=\"AvApp\">\n\n"
            + "    <Design.DataContext>\n        <!-- This only sets the DataContext for the previewer in an IDE,\n"
            + "             to set the actual DataContext for runtime, set the DataContext property in code (look at App.axaml.cs) -->\n"
            + "        <vm:MainViewModel />\n    </Design.DataContext>\n\n    <TextBox Text=\"{Binding Greeting}\"\n             HorizontalAlignment=\"Center\"\n"
            + "             VerticalAlignment=\"Center\" />\n\n</Window>\n";
        Write("MainWindow.axaml", Template);
        MainWindow window = Open("MainWindow.axaml");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.True(IndentShown(window) == "4 spaces, detected",
            $"The template's four spaces a level were not found among its aligned attributes, so XAML's default stands: {IndentShown(window)}");
        Ui.Run(window, AppActions.Reformat);
        Ui.Settle();
        Assert.True(Text(editor).Replace("\n\n</Window>", "\n</Window>", StringComparison.Ordinal) == Template.Replace("\n\n</Window>", "\n</Window>", StringComparison.Ordinal),
            $"Reformat Code indented the template's window otherwise than its own four spaces:\n{Text(editor)}");
        window.Close();
    }

    [AvaloniaFact]
    public void Enter_in_xaml_and_html_indents_by_the_tags_a_level_in_from_a_start_tag_back_after_a_closed_one_and_under_a_first_attribute()
    {
        Write("Entering.axaml", "<Window xmlns=\"https://github.com/avaloniaui\"\n        Title=\"Enter\">\n    <StackPanel>\n"
            + "        <Button Content=\"One\"\n                Width=\"80\" />\n        <Border></Border>\n    </StackPanel>\n</Window>\n");
        MainWindow window = Open("Entering.axaml");
        CodeEditor editor = window.EditorArea.CodeEditor;

        Ui.PlaceAfter(editor, "<Border>");
        Ui.Press(window, "ENTER");
        Assert.True(Text(editor).Contains("        <Border>\n            \n        </Border>\n", StringComparison.Ordinal),
            $"Enter between <Border> and </Border> did not open a line a level in with the end tag below:\n{Text(editor)}");
        Assert.Equal(new TextPosition(6, 12), editor.CaretPosition);

        Ui.PlaceAfter(editor, "Width=\"80\" />");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<Separator />");
        Assert.True(Text(editor).Contains("                Width=\"80\" />\n        <Separator />\n", StringComparison.Ordinal),
            $"Enter after a self-closing tag lined the next element up under its attributes:\n{Text(editor)}");

        Ui.PlaceAfter(editor, "<Button Content=\"One\"");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("Height=\"20\"");
        Assert.True(Text(editor).Contains("        <Button Content=\"One\"\n                Height=\"20\"\n                Width=\"80\" />", StringComparison.Ordinal),
            $"Enter after a tag's attribute did not line the next up under the first:\n{Text(editor)}");
        Ui.PlaceAfter(editor, "    <Stack");
        Ui.Press(window, "shift ENTER");
        window.KeyTextInput("<TextBlock />");
        Assert.True(Text(editor).Contains("    <StackPanel>\n        <TextBlock />\n        <Button", StringComparison.Ordinal),
            $"Shift+Enter on <StackPanel>'s line did not open a line a level in:\n{Text(editor)}");
        window.Close();

        // HTML's void elements never close, so the line after one stays beside it.
        Write("page.html", "<html>\n<head>\n    <meta charset=\"utf-8\">\n</head>\n</html>\n");
        window = Open("page.html");
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "<head>");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<base href=\"/\">");
        Ui.PlaceAfter(editor, "<meta charset=\"utf-8\">");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<title>Page</title>");
        Assert.True(Text(editor).Contains("<head>\n    <base href=\"/\">\n    <meta charset=\"utf-8\">\n    <title>Page</title>\n</head>", StringComparison.Ordinal),
            $"Enter in the HTML's head did not indent its elements a level in from <head>, each beside the last:\n{Text(editor)}");
        window.Close();
    }

    /// <summary>An li, p or td whose end tag is left out ends where HTML's parser ends it (at the next li, a block, the
    /// next cell, its list's end tag), so Enter after it indents as beside it, not inside it.</summary>
    [AvaloniaFact]
    public void Enter_in_html_after_an_element_whose_end_tag_is_left_out_indents_beside_it()
    {
        Write("list.html", "<ul>\n    <li>One\n    <li>Two</li>\n    <li>Four</ul>\n<section>\n    <p>Intro\n    <div>Block</div>\n</section>\n");
        MainWindow window = Open("list.html");
        CodeEditor editor = window.EditorArea.CodeEditor;

        // One ended at Two, so after Two's end tag the next item goes beside it.
        Ui.PlaceAfter(editor, "<li>Two</li>");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<li>Three</li>");
        Assert.True(Text(editor).Contains("    <li>Two</li>\n    <li>Three</li>\n", StringComparison.Ordinal),
            $"Enter after an li that follows one with its end tag left out went inside that one:\n{Text(editor)}");

        // The list's end tag after Four ends Four too, so it goes to the list's own indentation.
        Ui.PlaceAfter(editor, "<li>Four");
        Ui.Press(window, "ENTER");
        Assert.True(Text(editor).Contains("    <li>Four\n</ul>\n", StringComparison.Ordinal),
            $"Enter before </ul> after an li with its end tag left out put </ul> at the li's indentation:\n{Text(editor)}");

        // A div ends the p before it, so the next line is the section's, beside both.
        Ui.PlaceAfter(editor, "<div>Block</div>");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("<hr>");
        Assert.True(Text(editor).Contains("    <div>Block</div>\n    <hr>\n</section>", StringComparison.Ordinal),
            $"Enter after a div that follows a p with its end tag left out went inside the p:\n{Text(editor)}");
        window.Close();
    }

    /// <summary>Enter in a large XAML file reads back from the caret as far as the elements that hold it, not the whole
    /// file before it, so a key there costs what an Enter in a small one does.</summary>
    [AvaloniaFact]
    public void Enter_near_the_end_of_a_large_xaml_file_reads_only_the_elements_around_the_caret()
    {
        StringBuilder large = new("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\">\n");
        for (int i = 0; i < 20_000; i++)
        {
            large.Append("    <SolidColorBrush x:Key=\"Brush").Append(i).Append("\" Color=\"#FF").Append((i % 256).ToString("X2", System.Globalization.CultureInfo.InvariantCulture)).Append("0000\" />\n");
        }
        Write("Large.axaml", large.Append("    <StackPanel x:Key=\"Last\">\n        <Border />\n    </StackPanel>\n</ResourceDictionary>\n").ToString());
        MainWindow window = Open("Large.axaml");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.Settle();
        Ui.PlaceAfter(editor, "        <Border />");
        // The jump to the file's end drawn first, its lines laid out and coloured, so what is counted is the Enter's alone.
        Ui.Settle();
        long before = GC.GetAllocatedBytesForCurrentThread();
        window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        window.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
        Ui.Settle();
        window.KeyTextInput("<Separator />");
        Assert.True(Text(editor).Contains("        <Border />\n        <Separator />\n    </StackPanel>", StringComparison.Ordinal),
            $"Enter after the last element in the large file's StackPanel did not indent beside it:\n{Text(editor)[^200..]}");
        // The 20,000 brushes before the caret are 1.2 million characters, which a read of all of them copies whole (5 MB).
        Assert.True(allocated < 1_000_000, $"Enter near the end of a {editor.Document.Snapshot.Length:N0}-character XAML file allocated {allocated:N0} bytes, as a read of all of it does");
        window.Close();
    }

    private void Write(string name, string text) => TestFolders.Write(folder, name, text);

    /// <summary>A window on the folder, with a file of it open when one is named; it saves only when asked.</summary>
    private MainWindow Open(string? file)
    {
        MainWindow window = Ui.OpenFolder(folder, autosaves: false);
        if (file is not null)
        {
            window.OpenFile(file);
        }
        Ui.Settle();
        window.EditorArea.CodeEditor.Focus();
        return window;
    }

    private static void PlaceBefore(CodeEditor editor, string text)
    {
        int at = Text(editor).IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{text}' is not in the file");
        editor.Selections = SelectionSet.Single(at);
    }

    private static void Copy(MainWindow window, string text) => Ui.Await(window.Clipboard!.SetTextAsync(text), "the clipboard to take the text");

    private static string Text(CodeEditor editor) => editor.Document.Snapshot.ToString();

    private static string? IndentShown(MainWindow window) => window.FindControl<TextBlock>("IndentText")!.Text;
}
