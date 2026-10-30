using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The schemas Passenger carries reach the files they are for that name none themselves, through the JSON server,
/// and what the tools they describe take is never marked; on files of a folder of the test's own with no C# language
/// server; a test that cannot find Node.js skips, saying so.</summary>
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

    /// <summary>.NET's configuration reads keys in any case, so Serilog's and Kestrel's do in appsettings.json: each is
    /// checked as its own spelling is (an argument's name that is none marked under lower-case keys), a key required in
    /// any case.</summary>
    [AvaloniaFact]
    public void Appsettings_takes_serilogs_keys_in_any_case_as_dotnet_configuration_reads_them()
    {
        List<(string Text, string Message)> marked = Marked("appsettings.json", """
            {
              "AllowedHosts": 5,
              "Serilog": {
                "minimumlevel": "Debug",
                "using": ["Serilog.Sinks.Console"],
                "enrich": ["FromLogContext"],
                "writeto": [{ "name": "Console", "args": { "outputTemplate": "{Message}", "1st": "x" } }]
              },
              "Kestrel": { "Endpoints": { "Http": { "url": "http://localhost:5000" } } }
            }

            """, "5");
        Assert.True(marked.Count == 1 && marked[0].Text == "\"1st\"", $"Keys Serilog and Kestrel read are marked as wrong, or an argument's name under "
            + $"lower-case keys is not, where the file has {Said(marked)}");
    }

    /// <summary>Node.js resolves an export or import condition of any name that is no subpath and no array index, a digit
    /// or a dot in it too, so package.json marks none of them.</summary>
    [AvaloniaFact]
    public void Package_json_takes_export_and_import_conditions_with_digits_and_dots_as_node_resolves_them()
    {
        List<(string Text, string Message)> marked = Marked("package.json", """
            {
              "name": "conditions",
              "private": "yes",
              "exports": { "node20": "./a.js", "foo.bar": "./b.js" },
              "imports": { "#lib": { "deno.land": "./deno.js", "node20": "./a.js", "default": "./b.js" } }
            }

            """, "\"yes\"");
        Assert.True(marked.Count == 0, $"Conditions Node.js resolves are marked as wrong, where the file has {Said(marked)}");
    }

    /// <summary>ESLint's configuration names a plugin's rule by the plugin's short name (import/, vue/, unicorn/), so the rules
    /// package.json's eslintConfig offers are named so, never by the plugin's package.</summary>
    [AvaloniaFact]
    public void Package_jsons_eslint_config_offers_plugin_rules_by_the_names_eslint_finds_them_by()
    {
        WebLanguagesTests.SkipWithoutNode();
        string folder = PathNames.Canonical(TestFolders.Create("eslint-rules-"));
        TestFolders.Write(folder, "package.json", """
            {
              "name": "lint",
              "eslintConfig": {
                "plugins": ["import"],
                "rules": {
                  "eqeqeq": "error"
                }
              }
            }

            """);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(Path.Combine(folder, "package.json"));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "\"eqeqeq\": \"error\"");
            window.KeyTextInput(",");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("\"");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Count > 0, "the rules eslintConfig takes offered");
            foreach (char typed in "imp")
            {
                window.KeyTextInput(typed.ToString());
                Ui.Settle();
            }
            Ui.WaitFor(() => window.Assist.CompletionNarrowedBy == "\"imp", "the rules narrowed to those of imp");
            Assert.True(window.Assist.CompletionLabels.Contains("import/no-unresolved"),
                $"import's no-unresolved is not offered by the name ESLint finds it by, where completion offers [{string.Join(", ", window.Assist.CompletionLabels.Take(10))}]");
            Assert.DoesNotContain(window.Assist.CompletionLabels, l => l.StartsWith("eslint-plugin-", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The file written alone in a folder of its own and opened with no C# server, read once its server has checked
    /// it against its schema: what it marks, as the text marked and the message, but the one value the test gives it of the
    /// wrong type, <paramref name="wrong"/>, whose mark says the check is in.</summary>
    private static List<(string Text, string Message)> Marked(string name, string text, string wrong)
    {
        WebLanguagesTests.SkipWithoutNode();
        string folder = PathNames.Canonical(TestFolders.Create("schema-"));
        TestFolders.Write(folder, name, text);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(Path.Combine(folder, name));
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;
            string Text(Marker marker) => editor.Document.Snapshot.GetText(marker.Range.Start, marker.Range.Length);
            Ui.WaitFor(() => document.Markers.Any(m => Text(m) == wrong), () => $"the mark on {wrong}, which the schema refuses, where {name} has {Said([.. document.Markers.Select(m => (Text(m), m.Message))])}");
            List<(string Text, string Message)> marked = [.. document.Markers.Select(m => (Text(m), m.Message))];
            Assert.Single(marked, m => m.Text == wrong);
            return [.. marked.Where(m => m.Text != wrong)];
        }
        finally
        {
            window.Close();
        }
    }

    private static string Said(List<(string Text, string Message)> marked) =>
        marked.Count == 0 ? "nothing marked" : $"{marked.Count} marked: {string.Join("; ", marked.Select(m => $"{m.Text}: {m.Message}"))}";
}
