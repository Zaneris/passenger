using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The end tags typing completes in markup, in the window, as a person types a key at a time: a start tag's '>'
/// in XAML, an MSBuild file, XML and HTML writes its end tag after the caret, which one Undo takes away leaving the '>';
/// "&lt;/" writes the open element's name and its '>', or joins the element's written end tag, right after it or on a line
/// below, which the keys then pass one by one; a self-closed tag, a comment and HTML's empty elements get none; and the
/// setting turns it off.</summary>
public sealed class TagClosingTests
{
    [AvaloniaFact]
    public void A_start_tags_close_writes_its_end_tag_and_an_end_tags_slash_the_open_elements_name_in_xaml_msbuild_xml_and_html()
    {
        string folder = TestFolders.Create("tags-");
        TestFolders.Write(folder, "View.axaml", "<UserControl xmlns=\"https://github.com/avaloniaui\">\n    \n</UserControl>\n");
        TestFolders.Write(folder, "Shared.props", "<Project>\n  \n</Project>\n");
        TestFolders.Write(folder, "data.xml", "<root>\n  \n</root>\n");
        TestFolders.Write(folder, "index.html", "<body>\n    \n</body>\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            CodeEditor editor = Opened(window, Path.Combine(folder, "View.axaml"), "avaloniaui\">\n    ");
            TypeEach(window, "<StackPanel>");
            Assert.Equal("    <StackPanel>|</StackPanel>", XamlTests.CaretLine(editor));
            Ui.Run(window, EditorActions.Undo);
            Ui.Settle();
            Assert.Equal("    <StackPanel>|", XamlTests.CaretLine(editor));
            Ui.Run(window, EditorActions.Redo);
            Ui.Settle();
            TypeEach(window, "<Button Content=\"a > b\" />");
            Assert.Equal("    <StackPanel><Button Content=\"a > b\" />|</StackPanel>", XamlTests.CaretLine(editor));
            TypeEach(window, "<Border></");
            Assert.Equal("    <StackPanel><Button Content=\"a > b\" /><Border></|Border></StackPanel>", XamlTests.CaretLine(editor));
            Ui.Run(window, EditorActions.Undo);
            Ui.Settle();
            Assert.Equal("    <StackPanel><Button Content=\"a > b\" /><Border></|</Border></StackPanel>", XamlTests.CaretLine(editor));

            editor = Opened(window, Path.Combine(folder, "Shared.props"), "<Project>\n  ");
            TypeEach(window, "<PropertyGroup>");
            Assert.Equal("  <PropertyGroup>|</PropertyGroup>", XamlTests.CaretLine(editor));

            editor = Opened(window, Path.Combine(folder, "data.xml"), "<root>\n  ");
            TypeEach(window, "<item><!-- <note> -->");
            Assert.Equal("  <item><!-- <note> -->|</item>", XamlTests.CaretLine(editor));

            editor = Opened(window, Path.Combine(folder, "index.html"), "<body>\n    ");
            TypeEach(window, "<p>One<br>");
            Assert.Equal("    <p>One<br>|</p>", XamlTests.CaretLine(editor));
            TypeEach(window, "<ul><li>Two</li>");
            Assert.Equal("    <p>One<br><ul><li>Two</li>|</ul></p>", XamlTests.CaretLine(editor));

            window.ApplySettings(window.Settings with { CloseTags = false });
            TypeEach(window, "<em>");
            Assert.Equal("    <p>One<br><ul><li>Two</li><em>|</ul></p>", XamlTests.CaretLine(editor));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_end_tag_typed_key_by_key_over_the_one_written_leaves_one_end_tag_in_html_xaml_and_a_project_file()
    {
        string folder = TestFolders.Create("tags-");
        TestFolders.Write(folder, "View.axaml", "<UserControl xmlns=\"https://github.com/avaloniaui\">\n    \n</UserControl>\n");
        TestFolders.Write(folder, "App.csproj", "<Project>\n  \n</Project>\n");
        TestFolders.Write(folder, "index.html", "<body>\n    \n</body>\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            CodeEditor editor = Opened(window, Path.Combine(folder, "index.html"), "<body>\n    ");
            TypeEach(window, "<div>x</div>");
            Assert.Equal("    <div>x</div>|", XamlTests.CaretLine(editor));

            editor = Opened(window, Path.Combine(folder, "View.axaml"), "avaloniaui\">\n    ");
            TypeEach(window, "<Border>x</Border>");
            Assert.Equal("    <Border>x</Border>|", XamlTests.CaretLine(editor));

            editor = Opened(window, Path.Combine(folder, "App.csproj"), "<Project>\n  ");
            TypeEach(window, "<PropertyGroup></PropertyGroup>");
            Assert.Equal("  <PropertyGroup></PropertyGroup>|", XamlTests.CaretLine(editor));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_end_tag_of_another_name_typed_over_the_written_one_is_what_was_typed_and_a_child_element_goes_inside()
    {
        string folder = TestFolders.Create("tags-");
        TestFolders.Write(folder, "index.html", "<body>\n    \n</body>\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            CodeEditor editor = Opened(window, Path.Combine(folder, "index.html"), "<body>\n    ");
            TypeEach(window, "<div></span");
            Assert.Equal("    <div></span|</div>", XamlTests.CaretLine(editor));
            TypeEach(window, "><b>");
            Assert.Equal("    <div></span><b>|</b></div>", XamlTests.CaretLine(editor));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Backspace_and_undo_over_an_end_tag_being_typed_over_leave_the_written_tag_whole()
    {
        string folder = TestFolders.Create("tags-");
        TestFolders.Write(folder, "index.html", "<body>\n    \n</body>\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            CodeEditor editor = Opened(window, Path.Combine(folder, "index.html"), "<body>\n    ");
            TypeEach(window, "<div></d");
            Assert.Equal("    <div></d|iv>", XamlTests.CaretLine(editor));
            Ui.Run(window, EditorActions.Backspace);
            Assert.Equal("    <div></|div>", XamlTests.CaretLine(editor));
            Ui.Run(window, EditorActions.Backspace);
            Assert.Equal("    <div><|</div>", XamlTests.CaretLine(editor));
            Ui.Run(window, EditorActions.Backspace);
            Assert.Equal("    <div>|</div>", XamlTests.CaretLine(editor));
            TypeEach(window, "</div>");
            Assert.Equal("    <div></div>|", XamlTests.CaretLine(editor));
            Ui.Run(window, EditorActions.Undo);
            Ui.Settle();
            Assert.Equal("    <div></|</div>", XamlTests.CaretLine(editor));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_end_tag_typed_on_a_line_above_the_written_one_leaves_one_end_tag_and_undo_backspace_and_enter_put_that_line_back()
    {
        const string Joined = "<body>\n    <div>\n    </div>\n</body>\n";
        string folder = TestFolders.Create("tags-");
        TestFolders.Write(folder, "index.html", "<body>\n    <div>\n        \n    </div>\n</body>\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            CodeEditor editor = Opened(window, Path.Combine(folder, "index.html"), "<div>\n        ");
            TypeEach(window, "</d");
            Assert.Equal("    </d|iv>", XamlTests.CaretLine(editor));
            Assert.Equal(Joined, editor.Document.Snapshot.ToString());
            Ui.Run(window, EditorActions.Backspace);
            Assert.Equal("    </|div>", XamlTests.CaretLine(editor));
            Ui.Run(window, EditorActions.Undo);
            Ui.Settle();
            Assert.Equal("        </|", XamlTests.CaretLine(editor));
            Assert.Equal("<body>\n    <div>\n        </\n    </div>\n</body>\n", editor.Document.Snapshot.ToString());

            Ui.Run(window, EditorActions.Backspace);
            Ui.Run(window, EditorActions.Backspace);
            TypeEach(window, "</");
            Assert.Equal("    </|div>", XamlTests.CaretLine(editor));
            Ui.Run(window, EditorActions.Backspace);
            Assert.Equal("        <|", XamlTests.CaretLine(editor));
            Assert.Equal("<body>\n    <div>\n        <\n    </div>\n</body>\n", editor.Document.Snapshot.ToString());

            TypeEach(window, "/di");
            Ui.Run(window, EditorActions.NewLine);
            Ui.Settle();
            Assert.StartsWith("<body>\n    <div>\n        </di\n", editor.Document.Snapshot.ToString());
            Assert.EndsWith("\n    </div>\n</body>\n", editor.Document.Snapshot.ToString());
            Ui.Run(window, EditorActions.Undo);
            Ui.Settle();
            Ui.Run(window, EditorActions.Backspace);
            Ui.Run(window, EditorActions.Backspace);
            Ui.Run(window, EditorActions.Backspace);
            TypeEach(window, "/div>");
            Assert.Equal("    </div>|", XamlTests.CaretLine(editor));
            Assert.Equal(Joined, editor.Document.Snapshot.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A file opened in the window, its caret just after a piece of its text and the keys its editor's.</summary>
    private static CodeEditor Opened(MainWindow window, string path, string after)
    {
        window.OpenFile(path);
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, after);
        editor.Focus();
        return editor;
    }

    /// <summary>Text typed a key at a time, as a person types it.</summary>
    private static void TypeEach(MainWindow window, string text)
    {
        foreach (char c in text)
        {
            window.KeyTextInput(c.ToString());
        }
        Ui.Settle();
    }
}
