using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The end tags typing completes in markup, in the window, as a person types a key at a time: a start tag's '>'
/// in XAML, an MSBuild file, XML and HTML writes its end tag after the caret, which one Undo takes away leaving the '>';
/// "&lt;/" writes the open element's name and its '>', over the end tag a '>' wrote; a self-closed tag, a comment and
/// HTML's empty elements get none; and the setting turns it off.</summary>
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
            Assert.Equal("    <StackPanel><Button Content=\"a > b\" /><Border></Border>|</StackPanel>", XamlTests.CaretLine(editor));
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
            TypeEach(window, "<ul><li>Two</");
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
