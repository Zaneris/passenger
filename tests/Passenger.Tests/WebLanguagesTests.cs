using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>JavaScript, TypeScript, HTML, CSS and JSON through the window, on an ASP.NET Core app (<c>dotnet new blazor</c>)
/// with a script under wwwroot, TypeScript under a tsconfig.json, a stylesheet, a package.json and a component with its
/// own &lt;style&gt; and &lt;script&gt;: each gets completion, a problem, go to declaration and rename from the language
/// servers Passenger carries under web/ (TypeScript's, and VS Code's for HTML, CSS and JSON), run on the machine's
/// Node.js, a Razor file's through Razor's own forwarding to the HTML server. Without a Node.js the files keep their
/// colours and say why. Each test opens its own window, with web servers of its own, on the app as written (<see
/// cref="SharedProject"/>): with no C# language server, which the scripts, stylesheets and JSON ask nothing of, but for the
/// component's, whose Razor is Roslyn's, on the warm one the class shares; a test that cannot find Node.js skips, saying
/// so.</summary>
public sealed class WebLanguagesTests : IClassFixture<BlazorShop>
{
    private readonly BlazorShop app;

    public WebLanguagesTests(BlazorShop app)
    {
        this.app = app;
        app.Reset();
    }

    [AvaloniaFact]
    public void TypeScript_under_a_tsconfig_and_a_script_beside_it_complete_mark_errors_go_to_declarations_and_rename_across_their_files()
    {
        SkipWithoutNode(typescript: true);
        MainWindow window = app.Open("Scripts/items.ts", languageServer: false);
        OpenDocument items = window.EditorArea.Active!;
        window.OpenFile(app.PathOf("Scripts/cart.ts"));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument cart = window.EditorArea.Active!;
        Ui.WaitFor(() => cart.Markers.Any(m => m.Kind == MarkerKind.Error), "the squiggle under the number given to a string");
        Marker error = Assert.Single(cart.Markers, m => m.Kind == MarkerKind.Error);
        Assert.Equal("label", editor.Document.Snapshot.GetText(error.Range.Start, error.Range.Length));
        Assert.StartsWith("TS2322: Type 'number' is not assignable to type 'string'", error.Message, StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "console.log(label);");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("sum");
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("toFixed"), "a number's members after its dot");
        window.KeyTextInput("toF");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "toFixed", "the list narrowed to toFixed");
        Ui.Press(window, "ESCAPE");

        Ui.PlaceAfter(editor, "const sum: number = tot");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active == items, "items.ts opened at the function cart.ts imports");
        Assert.Equal(new TextPosition(5, "export function ".Length), window.EditorArea.CodeEditor.CaretPosition);

        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "total", "the rename box on the function");
        window.KeyTextInput("sumOf");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => cart.Text.Snapshot.ToString().Contains("const sum: number = sumOf(items);", StringComparison.Ordinal),
            "the importing file's call renamed with the function");
        Assert.Contains("import { Item, sumOf } from \"./items\";", cart.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("export function sumOf(items: Item[]): number", items.Text.Snapshot.ToString(), StringComparison.Ordinal);

        CodeEditor shown = window.EditorArea.CodeEditor;
        shown.ApplyEdits([new TextEdit(shown.Document.Snapshot.Length, 0, "export const  spaced=sumOf( []);\n")]);
        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => shown.Document.Snapshot.ToString().Contains("export const spaced = sumOf([]);", StringComparison.Ordinal),
            "the statement laid out by TypeScript's formatter");

        Ui.PlaceAfter(shown, "export function sumO");
        Ui.Run(window, AppActions.FindUsages);
        Passenger.App.ToolWindows.FindView find = window.Find;
        Ui.WaitFor(() => find.Rows.Count(r => r.Usage is not null) >= 4, "the function's usages, in both files, in the Find window");
        string[] usages = [.. find.Rows.Where(r => r.Usage is not null).Select(r => r.Label)];
        Assert.Contains("const sum: number = sumOf(items);", usages);
        Assert.Contains("import { Item, sumOf } from \"./items\";", usages);

        // A script outside the tsconfig, read by the same TypeScript.
        window.OpenFile(app.PathOf("wwwroot/js/site.js"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        editor.Focus();
        Ui.WaitFor(() => window.LanguageShown?.StartsWith("JavaScript · TypeScript ", StringComparison.Ordinal) == true,
            "the status bar to name the TypeScript the script is read by");
        Assert.EndsWith("(Passenger's)", window.LanguageShown, StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "console.log(message);");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("console");
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("log"), "console's members after its dot");
        window.KeyTextInput("war");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "warn", "the list narrowed to warn");
        Ui.Press(window, "ENTER");
        Assert.Equal("console.warn", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line));

        editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "\nlet broken = ;\n")]);
        int semicolon = editor.Document.Snapshot.ToString().LastIndexOf(" = ;", StringComparison.Ordinal) + " = ".Length;
        // The text before's marks (console. lacked its member) stay, carried, until TypeScript has read the text now.
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Error && m.Range.Start == semicolon), "the squiggle under the statement that is not JavaScript");
        Marker broken = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Error);
        Assert.StartsWith("TS1109: Expression expected", broken.Message, StringComparison.Ordinal);
        Assert.Equal(";", editor.Document.Snapshot.GetText(broken.Range.Start, Math.Max(1, broken.Range.Length)));

        Ui.PlaceAfter(editor, "const message = gre");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => editor.CaretPosition.Line == 1, "the caret at the function's declaration");
        Assert.Equal(new TextPosition(1, "function ".Length), editor.CaretPosition);

        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "greet", "the rename box on the function");
        window.KeyTextInput("welcome");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("const message = welcome(\"Ada\");", StringComparison.Ordinal),
            "the call renamed with the function");
        Assert.Contains("function welcome(name)", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void A_stylesheet_completes_a_property_warns_of_an_unknown_one_shows_its_colours_goes_to_a_custom_propertys_declaration_and_renames_it()
    {
        SkipWithoutNode();
        MainWindow window = app.Open("wwwroot/css/site.css", languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => document.ColourMarks.Count == 2, "a swatch beside each line that writes a colour");
        Assert.Equal([1, 6], document.ColourMarks.Select(m => m.Line));
        Assert.Equal(Avalonia.Media.Color.FromRgb(0x1b, 0x6e, 0xc2), document.ColourMarks[0].Colour);

        Ui.PlaceAfter(editor, "background-color: #ffffff;");
        Ui.Press(window, "ENTER");
        TypeEach(window, "bord");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("border-radius"), "CSS's properties as one is typed");
        window.KeyTextInput("er-ra");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "border-radius", "the list narrowed to border-radius");
        Ui.Press(window, "ENTER");
        int line = editor.CaretPosition.Line;
        Assert.StartsWith("    border-radius: ", editor.Document.Snapshot.GetLineText(line), StringComparison.Ordinal);
        window.KeyTextInput("4px");
        Assert.Equal("    border-radius: 4px;", editor.Document.Snapshot.GetLineText(line));

        Ui.Press(window, "END");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("display");
        window.KeyTextInput(":");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("flex"), "display's values after its colon");
        for (int down = window.Assist.CompletionLabels.ToList().IndexOf("flex"); down > 0; down--)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
        window.KeyTextInput(";");
        Assert.Equal("    display:flex;", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line));

        Ui.PlaceAfter(editor, "border-radius: 4px;");
        editor.ApplyEdits([new TextEdit(editor.Selections.Primary.Caret, 0, "\n    colr: red;")]);
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Warning), "the warning under the property CSS has not");
        Marker warning = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Warning);
        Assert.Equal("colr", editor.Document.Snapshot.GetText(warning.Range.Start, warning.Range.Length));
        Assert.Contains("Unknown property: 'colr'", warning.Message, StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "color: var(--acc");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => editor.CaretPosition.Line == 1, "the caret at the custom property's declaration");
        Assert.Equal(new TextPosition(1, 4), editor.CaretPosition);

        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "--accent", "the rename box on the whole custom property's name");
        window.KeyTextInput("--brand");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("color: var(--brand);", StringComparison.Ordinal), "its use renamed with it");
        Assert.Contains("--brand: #1b6ec2;", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, ".card{margin:0;padding:4px;}\n")]);
        Ui.Run(window, AppActions.Reformat);
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains(".card {\n    margin: 0;\n    padding: 4px;\n}", StringComparison.Ordinal),
            "the rule laid out by CSS's formatter, in the file's own indent");
        window.Close();
    }

    /// <summary>An SCSS and a Less stylesheet go to the CSS server as their own languages, the status bar naming each: a
    /// property completes in either, one the server does not know is warned of, and their variables, mixins and nesting
    /// are no error, as CSS's own reading would mark them.</summary>
    [AvaloniaFact]
    public void An_SCSS_and_a_Less_stylesheet_complete_and_are_checked_by_the_CSS_server_as_their_own_languages()
    {
        SkipWithoutNode();
        MainWindow window = app.Open("wwwroot/css/theme.scss", languageServer: false);
        foreach ((string file, string language) in (IEnumerable<(string, string)>)[("wwwroot/css/theme.scss", "SCSS"), ("wwwroot/css/theme.less", "Less")])
        {
            window.OpenFile(app.PathOf(file));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "padding: 12px;");
            Ui.Press(window, "ENTER");
            TypeEach(window, "bord");
            Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("border-radius"), $"CSS's properties as one is typed in {file}");
            Assert.Equal(language, window.LanguageShown);
            Ui.Press(window, "ESCAPE");
            int typed = editor.Selections.Primary.Caret;
            editor.ApplyEdits([new TextEdit(typed - "bord".Length, "bord".Length, "colr: red;")]);
            Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Warning), $"the warning under the property CSS has not in {file}");
            Marker warning = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Warning);
            Assert.Equal("colr", editor.Document.Snapshot.GetText(warning.Range.Start, warning.Range.Length));
            Assert.True(!document.Markers.Any(m => m.Kind == MarkerKind.Error),
                $"{file} was read as plain CSS, its {language} marked as errors: {string.Join("; ", document.Markers.Select(m => m.Message))}");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void Package_json_completes_and_checks_by_its_schema_goes_from_a_dependency_to_its_package_and_renames_a_script_with_its_runs()
    {
        SkipWithoutNode();
        MainWindow window = app.Open("package.json", languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;
        int value = editor.Document.Snapshot.ToString().IndexOf("\"private\": true", StringComparison.Ordinal) + "\"private\": ".Length;
        editor.ApplyEdits([new TextEdit(value, "true".Length, "\"yes\"")]);
        Ui.WaitFor(() => document.Markers.Any(m => m.Kind == MarkerKind.Warning), "the warning where the value is not what the schema says");
        Marker warning = Assert.Single(document.Markers, m => m.Kind == MarkerKind.Warning);
        Assert.Equal("\"yes\"", editor.Document.Snapshot.GetText(warning.Range.Start, warning.Range.Length));
        Assert.Contains("Incorrect type. Expected \"boolean\"", warning.Message, StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "\"version\": \"1.0.0\",");
        Ui.Press(window, "ENTER");
        window.KeyTextInput("\"");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("description"), "package.json's properties, from its schema");
        window.KeyTextInput("descr");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault() == "description", "the list narrowed to description");
        Ui.Press(window, "ENTER");
        Assert.StartsWith("  \"description\": \"", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "\"left-p");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active?.Path == app.Named("node_modules/left-pad/package.json"), "the installed package's own package.json");

        window.OpenFile(app.PathOf("package.json"));
        Ui.Settle();
        editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "\"bui");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "build", "the rename box on the script's name");
        // As typed, a letter outside ASCII and a + included, both of which a JSON serializer's default encoder escapes.
        window.KeyTextInput("compile+über");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("\"tsc -p .\"", StringComparison.Ordinal) && !editor.Document.Snapshot.ToString().Contains("\"build\"", StringComparison.Ordinal),
            "the script renamed");
        string renamed = editor.Document.Snapshot.ToString();
        Assert.True(renamed.Contains("\"compile+über\": \"tsc -p .\"", StringComparison.Ordinal) && renamed.Contains("\"watch\": \"npm run compile+über -- --watch\"", StringComparison.Ordinal),
            $"The script and its npm run were not renamed as typed:\n{renamed}");
        window.Close();
    }

    [AvaloniaFact]
    public void A_components_style_and_script_complete_check_go_to_declaration_and_rename_through_the_HTML_server()
    {
        SkipWithoutNode();
        MainWindow window = app.Open("Components/Pages/Styled.razor");
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument document = window.EditorArea.Active!;

        Ui.PlaceAfter(editor, "color: #1b6ec2;");
        Ui.Press(window, "ENTER");
        TypeEach(window, "bord");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("border-radius"), "CSS's properties in the component's <style>");
        Ui.Press(window, "ESCAPE");
        int typed = editor.Selections.Primary.Caret;
        editor.ApplyEdits([new TextEdit(typed - "bord".Length, "bord".Length, "border: 1px solid;\n        colr: red;")]);
        Ui.WaitFor(() => document.Markers.Any(m => m.Message.Contains("Unknown property: 'colr'", StringComparison.Ordinal)),
            "the warning under the property CSS has not, in the component's <style>");
        Marker warning = document.Markers.First(m => m.Message.Contains("colr", StringComparison.Ordinal));
        Assert.Equal("colr", editor.Document.Snapshot.GetText(warning.Range.Start, warning.Range.Length));

        int call = editor.Document.Snapshot.ToString().IndexOf("    shout(\"hi\")", StringComparison.Ordinal) + 6;
        editor.Selections = SelectionSet.Single(call);
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        int declared = editor.Document.Snapshot.ToString().IndexOf("function shout", StringComparison.Ordinal) + "function ".Length;
        Ui.WaitFor(() => editor.Selections.Primary.Caret == declared, "the caret at the script's function");

        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "shout", "the rename box on the script's function");
        window.KeyTextInput("yell");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("    yell(\"hi\");", StringComparison.Ordinal), "the call renamed with the function");
        Assert.Contains("function yell(text)", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "        return text");
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("toUpperCase"), "a string's members in the component's <script>");
        window.Close();
    }

    [AvaloniaFact]
    public void Without_a_Node_js_a_script_keeps_its_colours_and_its_banner_and_the_status_bar_say_why()
    {
        string missing = Path.Combine(TestRun.Root, "no-node-here", OperatingSystem.IsWindows() ? "node.exe" : "node");
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false };
        window.Show();
        try
        {
            window.ApplySettings(window.Settings with { NodeInterpreter = missing });
            window.OpenFolder(app.Folder);
            window.OpenFile(app.PathOf("wwwroot/js/site.js"));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.WaitFor(() => window.EditorArea.ActiveGroup.BannerText is not null, "the banner over the script");
            Assert.Equal($"JavaScript has its colours only: there is no Node.js at {missing}, which Settings | Languages & Frameworks | Node.js names.",
                window.EditorArea.ActiveGroup.BannerText);
            Assert.Equal("JavaScript · no Node.js", window.LanguageShown);
            Assert.Equal(TokenKind.Keyword, KindOf(editor, "function"));

            Ui.Run(window, AppActions.Rename);
            Assert.Equal($"Rename needs the TypeScript language server: there is no Node.js at {missing}, which Settings | Languages & Frameworks | Node.js names", window.Status);

            // A Node.js named that runs it, the server starts on it and the banner goes.
            if (Programs.Node() is { } node && TooOld() is null)
            {
                window.ApplySettings(window.Settings with { NodeInterpreter = node });
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.BannerText is null, "the banner to go once a Node.js is named");
                Ui.WaitFor(() => window.LanguageShown?.StartsWith("JavaScript · TypeScript ", StringComparison.Ordinal) == true, "the script read by TypeScript");
            }
        }
        finally
        {
            window.ApplySettings(new Passenger.App.Settings.AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_project_with_its_own_typescript_is_read_by_it()
    {
        SkipWithoutNode(typescript: true);
        string folder = TestFolders.Create("own-typescript-");
        CopyFolder(Path.Combine(Passenger.App.Web.WebLanguages.Modules, "typescript"), Path.Combine(folder, "node_modules", "typescript"));
        File.WriteAllText(Path.Combine(folder, "tsconfig.json"), "{ \"compilerOptions\": { \"strict\": true } }\n");
        File.WriteAllText(Path.Combine(folder, "main.ts"), "const n: number = 1;\n");
        MainWindow window = Ui.OpenFolder(folder, autosaves: false);
        try
        {
            window.OpenFile(Path.Combine(PathNames.Canonical(folder), "main.ts"));
            Ui.Settle();
            Ui.WaitFor(() => window.LanguageShown?.StartsWith("TypeScript · TypeScript ", StringComparison.Ordinal) == true, "the status bar to name the TypeScript in use");
            Assert.DoesNotContain("Passenger's", window.LanguageShown, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    private static void CopyFolder(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    /// <summary>Text typed a key at a time, as a person types it: a name's first letter is what opens completion.</summary>
    private static void TypeEach(MainWindow window, string text)
    {
        foreach (char c in text)
        {
            window.KeyTextInput(c.ToString());
        }
    }

    /// <summary>The one kind a stretch of the text is coloured as, its spaces aside; null when its parts differ.</summary>
    private static TokenKind? KindOf(CodeEditor editor, string stretch)
    {
        TextSnapshot text = editor.Document.Snapshot;
        int start = text.ToString().IndexOf(stretch, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{stretch}' is not in the text");
        TokenKind?[] kinds = new TokenKind?[stretch.Length];
        Array.Fill(kinds, TokenKind.Text);
        foreach (SyntaxSpan span in editor.Highlighter!.GetSpans(text, start, stretch.Length))
        {
            for (int i = Math.Max(span.Start, start); i < Math.Min(span.End, start + stretch.Length); i++)
            {
                kinds[i - start] = span.Kind;
            }
        }
        TokenKind?[] seen = [.. kinds.Where((_, i) => !char.IsWhiteSpace(stretch[i])).Distinct()];
        return seen.Length == 1 ? seen[0] : null;
    }

    /// <summary>Skips a test that needs the web language servers where there is no Node.js to run them, or, for one that
    /// needs the TypeScript server (<paramref name="typescript"/>), one older than it needs.</summary>
    internal static void SkipWithoutNode(bool typescript = false)
    {
        Assert.SkipWhen(Programs.Node() is null, "Node.js is not on the PATH, and the web language servers run on it");
        if (typescript)
        {
            string? old = TooOld();
            Assert.SkipWhen(old is not null, $"The TypeScript language server cannot run here: {old}");
        }
    }

    /// <summary>Why the PATH's Node.js cannot run the TypeScript server, or null when it can.</summary>
    private static string? TooOld()
    {
        Passenger.App.Web.NodeRuntime node = Task.Run(() => Passenger.App.Web.NodeRuntime.FindAsync(null)).GetAwaiter().GetResult();
        return node.Problem ?? node.TooOldFor(Path.Combine(Passenger.App.Web.WebLanguages.Modules, "typescript-language-server", "package.json"), "the TypeScript language server");
    }
}
