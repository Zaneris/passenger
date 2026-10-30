using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The schemas Passenger carries reach the files they are for that name none themselves, through the JSON server
/// (a compose file through the YAML server), and what the tools they describe take is never marked; on files of a folder
/// of the test's own with no C# language server; a test that cannot find Node.js skips, saying so.</summary>
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

    /// <summary>TypeScript reads a compiler option's named value in any case, so tsconfig.json marks none of them.</summary>
    [AvaloniaFact]
    public void Tsconfig_takes_compiler_option_values_in_any_case_as_typescript_reads_them()
    {
        List<(string Text, string Message)> marked = Marked("tsconfig.json", """
            {
              "compilerOptions": {
                "moduleResolution": "BUNDLER",
                "jsx": "REACT-JSX",
                "module": "NODE16",
                "newLine": "Lf",
                "moduleDetection": "Force",
                "strict": "yes"
              }
            }

            """, "\"yes\"");
        Assert.True(marked.Count == 0, $"Option values TypeScript reads are marked as wrong, where the file has {Said(marked)}");
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

    /// <summary>ESLint runs a plugin's rule written as its severity alone in an array, ["error"], as it runs "error", so
    /// package.json's eslintConfig marks none of them.</summary>
    [AvaloniaFact]
    public void Package_jsons_eslint_config_takes_a_plugin_rule_of_its_severity_alone_in_an_array_as_eslint_runs_it()
    {
        List<(string Text, string Message)> marked = Marked("package.json", """
            {
              "name": "lint",
              "private": "yes",
              "eslintConfig": {
                "rules": {
                  "@typescript-eslint/no-explicit-any": ["error"],
                  "import/no-unresolved": ["warn"],
                  "vue/max-len": [2]
                }
              }
            }

            """, "\"yes\"");
        Assert.True(marked.Count == 0, $"Rules ESLint runs are marked as wrong, where the file has {Said(marked)}");
    }

    /// <summary>typescript-eslint's consistent-generic-constructors takes constructor or type-annotation as its option, and
    /// a plugin's rule whose options the schema does not describe takes what ESLint gives it, so package.json's eslintConfig
    /// marks none of them.</summary>
    [AvaloniaFact]
    public void Package_jsons_eslint_config_takes_a_rules_string_options_as_eslint_runs_them()
    {
        List<(string Text, string Message)> marked = Marked("package.json", """
            {
              "name": "lint",
              "private": "yes",
              "eslintConfig": {
                "rules": {
                  "@typescript-eslint/consistent-generic-constructors": ["error", "constructor"],
                  "@typescript-eslint/method-signature-style": ["error", "method"],
                  "vue/html-indent": ["error", "tab", { "baseIndent": 1 }],
                  "@typescript-eslint/no-unused-vars": ["warn", "all"]
                }
              }
            }

            """, "\"yes\"");
        Assert.True(marked.Count == 0, $"Rules ESLint runs are marked as wrong, where the file has {Said(marked)}");
    }

    /// <summary>stylelint runs a rule that takes a number written with its options, [2, { "severity": "warning" }], so a
    /// stylelint configuration in package.json marks none of them.</summary>
    [AvaloniaFact]
    public void Package_jsons_stylelint_takes_a_numeric_rule_with_its_options_as_stylelint_runs_it()
    {
        List<(string Text, string Message)> marked = Marked("package.json", """
            {
              "name": "styles",
              "private": "yes",
              "stylelint": {
                "rules": {
                  "number-max-precision": [2, { "severity": "warning" }],
                  "max-nesting-depth": [3, { "message": "Nested too deep" }]
                }
              }
            }

            """, "\"yes\"");
        Assert.True(marked.Count == 0, $"Rules stylelint runs are marked as wrong, where the file has {Said(marked)}");
    }

    /// <summary>stylelint applies an override whose files is one glob as it does one whose files is a list, so a stylelint
    /// configuration in package.json marks neither.</summary>
    [AvaloniaFact]
    public void Package_jsons_stylelint_takes_an_overrides_files_as_one_glob_as_stylelint_applies_it()
    {
        List<(string Text, string Message)> marked = Marked("package.json", """
            {
              "name": "styles",
              "private": "yes",
              "stylelint": {
                "overrides": [
                  { "files": "**/*.css", "rules": {} },
                  { "files": ["**/*.scss"], "rules": {} }
                ]
              }
            }

            """, "\"yes\"");
        Assert.True(marked.Count == 0, $"Overrides stylelint applies are marked as wrong, where the file has {Said(marked)}");
    }

    /// <summary>Compose replaces a variable in a value before it reads the value, so a compose file marks none where a
    /// field takes only certain words (pull_policy, cgroup, a dependency's condition) and still marks a word it has not.</summary>
    [AvaloniaFact]
    public void A_compose_file_takes_a_variable_where_a_field_takes_certain_words_as_compose_replaces_it_first()
    {
        DockerFilesTests.Shop.SkipWithoutNode(yaml: true);
        List<(string Text, string Message)> marked = Marked("compose.yaml", """
            services:
              web:
                image: nginx
                pull_policy: ${PULL_POLICY:-always}
                cgroup: ${CGROUP:-host}
                init: [true]
                depends_on:
                  db:
                    condition: ${DB_CONDITION:-service_started}
              db:
                image: postgres
                pull_policy: sometimes

            """, "[true]");
        Assert.True(marked.Count == 1 && marked[0].Text == "sometimes", $"Values Compose replaces are marked as wrong, or a policy it has not is not, where the file has {Said(marked)}");
    }

    /// <summary>A compose file named in another letter case or after a '-' (Compose.yaml, compose-dev.yml) is checked by the
    /// Compose specification's schema, as compose.yaml is.</summary>
    [AvaloniaTheory]
    [InlineData("Compose.yaml")]
    [InlineData("compose-dev.yml")]
    public void A_compose_file_of_any_letter_case_or_named_with_a_dash_is_checked_by_the_compose_schema(string name)
    {
        DockerFilesTests.Shop.SkipWithoutNode(yaml: true);
        List<(string Text, string Message)> marked = Marked(name, """
            services:
              web:
                image: nginx
                init: [true]

            """, "[true]");
        Assert.True(marked.Count == 0, $"{name} marks more than the value its schema refuses: {Said(marked)}");
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
