using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The schemas Passenger carries reach the files they are for that name none themselves, through the JSON server,
/// on files of a folder of the test's own with no C# language server; a test that cannot find Node.js skips, saying so.</summary>
public sealed class JsonSchemaTests
{
    /// <summary>A file-based program's app.run.json beside its app.cs, naming no schema, takes the launch settings' schema a
    /// project's launchSettings.json takes: a value of the wrong type is marked, and a profile completes its properties.</summary>
    [AvaloniaFact]
    public void A_file_based_programs_run_json_is_checked_and_completed_by_the_launch_settings_schema()
    {
        WebLanguagesTests.SkipWithoutNode();
        string folder = PathNames.Canonical(TestFolders.Create("run-json-"));
        TestFolders.Write(folder, "app.cs", "Console.WriteLine(args.Length);\n");
        TestFolders.Write(folder, "app.run.json", """
            {
              "profiles": {
                "greet": {
                  "commandName": "Project",
                  "launchBrowser": "yes"
                }
              }
            }

            """);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(Path.Combine(folder, "app.run.json"));
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Warning), "the warning where launchBrowser is not the boolean the launch settings' schema says");
            Marker warning = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Warning);
            Assert.Equal("\"yes\"", editor.Document.Snapshot.GetText(warning.Range.Start, warning.Range.Length));
            Assert.Contains("Incorrect type. Expected \"boolean\"", warning.Message, StringComparison.Ordinal);

            Ui.PlaceAfter(editor, "\"commandName\": \"Project\",");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("\"");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("commandLineArgs") && window.Assist.CompletionLabels.Contains("environmentVariables"),
                () => $"a launch profile's properties offered from the launch settings' schema, where completion offers [{string.Join(", ", window.Assist.CompletionLabels.Take(10))}]");
        }
        finally
        {
            window.Close();
        }
    }
}
