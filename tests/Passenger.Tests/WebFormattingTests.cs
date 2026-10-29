using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Reformat Code through the JSON, CSS and HTML servers ends a file as it ended, or as its .editorconfig's
/// <c>insert_final_newline</c> asks, on files of a folder of the test's own with no C# language server; a test that
/// cannot find Node.js skips, saying so.</summary>
public sealed class WebFormattingTests
{
    /// <summary>A JSON file, appsettings.Development.json, a stylesheet and an HTML page, each ending in a line break, keep it
    /// as their servers lay them out; a JSON file whose .editorconfig says <c>insert_final_newline = false</c> ends without
    /// one, and a stylesheet ending without one whose .editorconfig says true ends with one.</summary>
    [AvaloniaFact]
    public void Reformat_code_keeps_the_final_line_break_of_json_css_and_html_and_ends_a_file_as_its_editorconfig_asks()
    {
        WebLanguagesTests.SkipWithoutNode();
        string folder = PathNames.Canonical(TestFolders.Create("web-format-"));
        (string File, string Text, string Ending)[] files =
        [
            ("ugly.json", "{\"a\":1,\n   \"b\": [1,2,\n3]}\n", "]\n}\n"),
            ("appsettings.Development.json", "{\n\"Logging\": {\"LogLevel\": {\"Default\": \"Information\"}}\n}\n", "}\n}\n"),
            ("styles/ugly.css", ".a{color:red;margin:0}\n", "0\n}\n"),
            ("ugly.html", "<div><p>hi</p>\n</div>\n", "</p>\n</div>\n"),
            ("strict/settings.json", "{\"a\":1,\n   \"b\": 2}\n", "2\n}"),
            ("wanting/bare.css", ".b{color:blue}", "blue\n}\n"),
        ];
        foreach ((string file, string text, _) in files)
        {
            TestFolders.Write(folder, file, text);
        }
        TestFolders.Write(folder, "strict/.editorconfig", "[*.json]\ninsert_final_newline = false\n");
        TestFolders.Write(folder, "wanting/.editorconfig", "[*.css]\ninsert_final_newline = true\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            foreach ((string file, string text, string ending) in files)
            {
                window.OpenFile(Path.Combine(folder, file));
                Ui.Settle();
                OpenDocument document = window.EditorArea.Active!;
                Ui.WaitFor(() => window.Session.Web?.For(document) is { Lsp: not null }, $"the language server's document of {file}");
                Ui.Run(window, AppActions.Reformat);
                Ui.WaitFor(() => document.Text.Snapshot.ToString() != text, $"{file} laid out by its server: {window.Status}");
                string formatted = document.Text.Snapshot.ToString();
                Assert.True(formatted.EndsWith(ending, StringComparison.Ordinal) && !formatted.EndsWith("\n" + ending, StringComparison.Ordinal),
                    $"Reformat Code ended {file} otherwise than the file and its .editorconfig ask, with '{ending.ReplaceLineEndings("\\n")}':\n{formatted}");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
