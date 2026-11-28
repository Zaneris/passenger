using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>JavaScript, TypeScript, HTML, CSS and JSON through the window, on an ASP.NET Core app (<c>dotnet new
/// blazor</c>) with a script under wwwroot, TypeScript under a tsconfig.json, a stylesheet, a package.json and a
/// component with its own &lt;style&gt; and &lt;script&gt;: each gets completion, a problem, go to declaration and
/// rename from the language servers Passenger carries under web/ (TypeScript's, and VS Code's for HTML, CSS and JSON),
/// run on the machine's Node.js, a Razor file's through Razor's own forwarding to the HTML server. A server that ends
/// as it starts is started again. Each test opens its own window, with web servers of its own, on the app as written
/// (<see cref="SharedProject"/>): with no C# language server, which the scripts, stylesheets and JSON ask nothing of,
/// but for the component's, whose Razor is Roslyn's, on the warm one the class shares; a test that cannot find Node.js
/// skips, saying so.</summary>
public sealed class WebLanguagesTests : IClassFixture<BlazorShop>
{
    private readonly BlazorShop app;

    public WebLanguagesTests(BlazorShop app)
    {
        this.app = app;
        app.Reset();
    }

    /// <summary>TypeScript and a script beside a tsconfig complete, check, refactor and rename through the project's
    /// own TypeScript.</summary>
    [AvaloniaFact]
    public void TypeScript_completes_checks_refactors_and_renames_by_its_own_server()
    {
        app.Reset();
        Flow.Step(nameof(TypeScript_under_a_tsconfig_and_a_script_beside_it_complete_mark_errors_go_to_declarations_and_rename_across_their_files),
            TypeScript_under_a_tsconfig_and_a_script_beside_it_complete_mark_errors_go_to_declarations_and_rename_across_their_files);
        OpenWindows.CloseAll();
        app.Reset();
        Flow.Step(nameof(TypeScripts_refactorings_extract_a_function_offering_its_name_and_move_a_declaration_to_a_new_file_that_undo_takes_back),
            TypeScripts_refactorings_extract_a_function_offering_its_name_and_move_a_declaration_to_a_new_file_that_undo_takes_back);
        OpenWindows.CloseAll();
        app.Reset();
        app.Reset();
        Flow.Step(nameof(A_TypeScript_rename_at_an_imported_name_or_a_use_of_one_renames_it_where_it_is_declared_and_everywhere_it_is_used),
            A_TypeScript_rename_at_an_imported_name_or_a_use_of_one_renames_it_where_it_is_declared_and_everywhere_it_is_used);
        OpenWindows.CloseAll();
        app.Reset();
        Flow.Step(nameof(A_project_with_its_own_typescript_is_read_by_it), A_project_with_its_own_typescript_is_read_by_it);
    }

    /// <summary>Stylesheets, SCSS, Less, a component's style and script and JSON files complete, check and rename
    /// through their servers, one ending as it starts started again.</summary>
    [AvaloniaFact]
    public void Stylesheets_and_json_are_served_by_their_language_servers()
    {
        Flow.Step(nameof(A_stylesheet_completes_a_property_warns_of_an_unknown_one_shows_its_colours_goes_to_a_custom_propertys_declaration_and_renames_it),
            A_stylesheet_completes_a_property_warns_of_an_unknown_one_shows_its_colours_goes_to_a_custom_propertys_declaration_and_renames_it);
        OpenWindows.CloseAll();
        app.Reset();
        app.Reset();
        Flow.Step(nameof(An_SCSS_and_a_Less_stylesheet_complete_and_are_checked_by_the_CSS_server_as_their_own_languages),
            An_SCSS_and_a_Less_stylesheet_complete_and_are_checked_by_the_CSS_server_as_their_own_languages);
        OpenWindows.CloseAll();
        app.Reset();
        Flow.Step(nameof(A_stylesheets_class_and_variables_renamed_by_the_name_shown_keep_their_dot_and_sigil),
            A_stylesheets_class_and_variables_renamed_by_the_name_shown_keep_their_dot_and_sigil);
        OpenWindows.CloseAll();
        app.Reset();
        Flow.Step(nameof(A_web_server_still_being_configured_as_its_folder_closes_ends),
            A_web_server_still_being_configured_as_its_folder_closes_ends);
        OpenWindows.CloseAll();
        app.Reset();
        Flow.Step(nameof(File_structure_lists_a_JSON_files_keys_and_goes_to_the_one_chosen),
            File_structure_lists_a_JSON_files_keys_and_goes_to_the_one_chosen);
        OpenWindows.CloseAll();
        app.Reset();
        app.Reset();
        Flow.Step(nameof(Package_json_completes_and_checks_by_its_schema_goes_from_a_dependency_to_its_package_and_renames_a_script_with_its_runs),
            Package_json_completes_and_checks_by_its_schema_goes_from_a_dependency_to_its_package_and_renames_a_script_with_its_runs);
        OpenWindows.CloseAll();
        app.Reset();
        Flow.Step(nameof(A_components_style_and_script_complete_check_go_to_declaration_and_rename_through_the_HTML_server),
            A_components_style_and_script_complete_check_go_to_declaration_and_rename_through_the_HTML_server);
        OpenWindows.CloseAll();
        app.Reset();
        Flow.Step(nameof(A_stylesheets_language_server_that_ends_as_it_starts_is_started_again_saying_so_and_checks_the_file),
            A_stylesheets_language_server_that_ends_as_it_starts_is_started_again_saying_so_and_checks_the_file);
        OpenWindows.CloseAll();
        app.Reset();
    }

    private void TypeScript_under_a_tsconfig_and_a_script_beside_it_complete_mark_errors_go_to_declarations_and_rename_across_their_files()
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

    /// <summary>TypeScript's refactorings, which its server makes by commands of its own that hand the window their edits:
    /// Extract to function writes the function, its new name then offered to rename, and Move to a new file makes the file
    /// and imports from it in both files, one command that Undo takes back whole, the new file with it. C#'s own
    /// refactorings grey there, saying the file is not C#.</summary>
    private void TypeScripts_refactorings_extract_a_function_offering_its_name_and_move_a_declaration_to_a_new_file_that_undo_takes_back()
    {
        SkipWithoutNode(typescript: true);
        MainWindow window = app.Open("Scripts/cart.ts", languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument cart = window.EditorArea.Active!;
        Ui.WaitFor(() => window.Session.Web?.For(cart) is { Lsp: not null }, "TypeScript's document of cart.ts");
        ActionContext context = window.Actions.Context();
        string[] offered = [.. ((string[])[AppActions.ChangeSignature, AppActions.ExtractInterface, AppActions.Move, AppActions.SafeDelete, AppActions.PullMembersUp, AppActions.PushMembersDown])
            .Where(id => window.Actions.WhyNot(id, context)?.Shown != Refusals.NotCSharp.Shown).Select(id => $"{id} ({window.Actions.WhyNot(id, context)?.Shown ?? "enabled"})")];
        Assert.True(offered.Length == 0, $"In a TypeScript file these refactorings of C# alone, which fail there, are not greyed as C#'s: {string.Join(", ", offered)}");
        int call = BlazorShop.Cart.IndexOf("total(items)", StringComparison.Ordinal);
        editor.Selections = SelectionSet.Single(new Selection(call, call + "total(items)".Length));
        Ui.Run(window, AppActions.QuickFix);
        ChooseOffered(window, "Extract to function in module scope");
        Ui.WaitFor(() => window.Assist.Renaming == "newFunction", () => $"the rename box on the function TypeScript wrote; the status bar said: {window.Status}\n{cart.Text.Snapshot}");
        window.KeyTextInput("totalOfItems");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => cart.Text.Snapshot.ToString().Contains("function totalOfItems()", StringComparison.Ordinal), () => $"the function renamed as typed:\n{cart.Text.Snapshot}");
        string extracted = cart.Text.Snapshot.ToString();
        Assert.True(extracted.Contains("const sum: number = totalOfItems();", StringComparison.Ordinal) && extracted.Contains("    return total(items);\n", StringComparison.Ordinal),
            $"The call was not extracted into the function:\n{extracted}");

        window.OpenFile(app.PathOf("Scripts/items.ts"));
        Ui.Settle();
        OpenDocument items = window.EditorArea.Active!;
        Ui.WaitFor(() => window.Session.Web?.For(items) is { Lsp: not null }, "TypeScript's document of items.ts");
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "export interface It");
        Ui.Run(window, AppActions.QuickFix);
        ChooseOffered(window, "Move to a new file");
        string moved = app.PathOf("Scripts/Item.ts");
        Ui.WaitFor(() => File.Exists(moved) && File.ReadAllText(moved).Contains("export interface Item", StringComparison.Ordinal),
            () => $"the interface in a file of its own; the status bar said: {window.Status}");
        Ui.WaitFor(() => cart.Text.Snapshot.ToString().Contains("from \"./Item\"", StringComparison.Ordinal), () => $"cart.ts importing the interface from its new file:\n{cart.Text.Snapshot}");
        Assert.DoesNotContain("export interface Item", items.Text.Snapshot.ToString(), StringComparison.Ordinal);
        Assert.Contains("from \"./Item\"", items.Text.Snapshot.ToString(), StringComparison.Ordinal);

        List<string> asked = [];
        window.Confirm = (_, question, _) =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        Ui.Settle();
        Ui.Run(window, EditorActions.Undo);
        Ui.WaitFor(() => !File.Exists(moved), () => $"Undo to take the new file away; it asked [{string.Join(" | ", asked)}], the status bar said: {window.Status}");
        Assert.True(asked is [{ } question] && question.StartsWith("Undo Move to a new file? It changes ", StringComparison.Ordinal)
            && new[] { "items.ts", "cart.ts", "Item.ts" }.All(f => question.Contains(f, StringComparison.Ordinal)), $"Undo asked: {string.Join(" | ", asked)}");
        Assert.Equal(BlazorShop.Items, items.Text.Snapshot.ToString());
        Assert.Equal(extracted, cart.Text.Snapshot.ToString());
        window.Close();
    }

    /// <summary>A TypeScript rename at an imported name, or at a use of one, renames the name where it is declared and
    /// everywhere it is used, as a rename from its declaration does: the file that declares it changed on disk while it is
    /// not open, and in its editor once it is, the importing file never given an alias in its place.</summary>
    private void A_TypeScript_rename_at_an_imported_name_or_a_use_of_one_renames_it_where_it_is_declared_and_everywhere_it_is_used()
    {
        SkipWithoutNode(typescript: true);
        MainWindow window = app.Open("Scripts/cart.ts", languageServer: false);
        CodeEditor editor = window.EditorArea.CodeEditor;
        OpenDocument cart = window.EditorArea.Active!;
        Ui.WaitFor(() => window.Session.Web?.For(cart) is { Lsp: not null }, "TypeScript's document of cart.ts");
        string items = app.PathOf("Scripts/items.ts");

        Ui.PlaceAfter(editor, "import { Item, tot");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "total", "the rename box on the imported function");
        window.KeyTextInput("sumOf");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => File.ReadAllText(items).Contains("export function sumOf(items: Item[]): number", StringComparison.Ordinal),
            () => $"the function renamed where items.ts declares it; the status bar said: {window.Status}\n{cart.Text.Snapshot}");
        string renamed = cart.Text.Snapshot.ToString();
        Assert.True(renamed.Contains("import { Item, sumOf } from \"./items\";", StringComparison.Ordinal) && renamed.Contains("const sum: number = sumOf(items);", StringComparison.Ordinal),
            $"The importing file was not renamed with the function:\n{renamed}");

        window.OpenFile(items);
        Ui.Settle();
        OpenDocument declaring = window.EditorArea.Active!;
        window.OpenFile(app.PathOf("Scripts/cart.ts"));
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "const items: It");
        Ui.Run(window, AppActions.Rename);
        Ui.WaitFor(() => window.Assist.Renaming == "Item", "the rename box on a use of the imported interface");
        window.KeyTextInput("Product");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => declaring.Text.Snapshot.ToString().Contains("export interface Product {", StringComparison.Ordinal),
            () => $"the interface renamed where items.ts declares it; the status bar said: {window.Status}\n{declaring.Text.Snapshot}");
        Assert.Contains("export function sumOf(items: Product[]): number", declaring.Text.Snapshot.ToString(), StringComparison.Ordinal);
        renamed = cart.Text.Snapshot.ToString();
        Assert.True(renamed.Contains("import { Product, sumOf } from \"./items\";", StringComparison.Ordinal) && renamed.Contains("const items: Product[] = ", StringComparison.Ordinal),
            $"The importing file was not renamed with the interface:\n{renamed}");
        window.Close();
    }

    /// <summary>Chooses one of the actions offered by its words, with the keys a person would press.</summary>
    private static void ChooseOffered(MainWindow window, string title)
    {
        Ui.WaitFor(() => window.Assist.Offered.Contains(title), () => $"'{title}' among the actions offered: {string.Join("; ", window.Assist.Offered)}; the status bar said: {window.Status}");
        for (int down = window.Assist.Offered.ToList().IndexOf(title); down > 0; down--)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
    }

    private void A_stylesheet_completes_a_property_warns_of_an_unknown_one_shows_its_colours_goes_to_a_custom_propertys_declaration_and_renames_it()
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
    private void An_SCSS_and_a_Less_stylesheet_complete_and_are_checked_by_the_CSS_server_as_their_own_languages()
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

    /// <summary>A stylesheet's class and an SCSS and a Less variable are renamed by the name the rename box shows, without
    /// its '.', '$' or '@', and keep it where declared and used: the rule stays a class's, the variable a variable.</summary>
    private void A_stylesheets_class_and_variables_renamed_by_the_name_shown_keep_their_dot_and_sigil()
    {
        SkipWithoutNode();
        MainWindow window = app.Open("wwwroot/css/site.css", languageServer: false);
        foreach ((string file, string at, string shown, string name, string[] renamed) in (IEnumerable<(string, string, string, string, string[])>)[
            ("wwwroot/css/site.css", ".btn-pri", "btn-primary", "btn-main", [".btn-main {"]),
            ("wwwroot/css/theme.scss", "solid $acc", "accent", "brand", ["$brand: #1b6ec2;", "solid $brand;", "darken($brand, 10%)"]),
            ("wwwroot/css/theme.less", "solid @acc", "accent", "brand", ["@brand: #1b6ec2;", "solid @brand;", "darken(@brand, 10%)"])])
        {
            window.OpenFile(app.PathOf(file));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, at);
            Ui.Run(window, AppActions.Rename);
            Ui.WaitFor(() => window.Assist.Renaming == shown, $"the rename box on {shown} in {file}");
            window.KeyTextInput(name);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => renamed.All(r => editor.Document.Snapshot.ToString().Contains(r, StringComparison.Ordinal)),
                () => $"{shown} renamed to {name} keeping what goes before it in {file}; the status bar said: {window.Status}\n{editor.Document.Snapshot}");
        }
        window.Close();
    }

    /// <summary>A web language server still being configured as its folder closes is never installed for it: the JSON
    /// server's configuring held while package.json opens, the window closed, then let go, its process ends.</summary>
    private void A_web_server_still_being_configured_as_its_folder_closes_ends()
    {
        SkipWithoutNode();
        MainWindow window = app.Open("Program.cs", languageServer: false);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int server;
        try
        {
            Task<int> held = window.Session.Web!.HoldNextConfigure(release.Task);
            window.OpenFile(app.PathOf("package.json"));
            server = Ui.AwaitPumped(held, "the JSON server's configuring to be held", TimeSpan.FromMinutes(1));
            Assert.True(Processes.IsRunning(server), "The JSON server held at its configuring is not running");
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
        Ui.PumpUntil(() => !Processes.IsRunning(server), "the JSON server, configured after its folder closed, to end", TimeSpan.FromSeconds(30));
    }

    /// <summary>File Structure on a JSON file lists its keys, nested under the objects they are in, as its server outlines
    /// them, each with the badge of its value's kind, and a key chosen is where the caret goes.</summary>
    private void File_structure_lists_a_JSON_files_keys_and_goes_to_the_one_chosen()
    {
        SkipWithoutNode();
        MainWindow window = app.Open("package.json", languageServer: false);
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => window.Session.Web?.For(document) is { Lsp: not null }, "the JSON server's document of package.json");
        Ui.Run(window, AppActions.FileStructure);
        SymbolPopup structure = window.Popup as SymbolPopup
            ?? throw new Xunit.Sdk.XunitException($"File Structure on package.json opened {window.Popup?.GetType().Name ?? "nothing"}; the status bar said: {window.Status}");
        Ui.WaitFor(() => structure.IsSettled && structure.Rows.Any(r => r.Name == "build"), "package.json's outline, its scripts' keys among it");
        SymbolRow scripts = structure.Rows.First(r => r.Name == "scripts");
        Assert.Equal(0, scripts.Depth);
        SymbolRow build = structure.Rows.First(r => r.Name == "build");
        Assert.Equal(1, build.Depth);
        Assert.True(SymbolPopup.Badge(build.Kind) != SymbolPopup.Badge(SymbolRow.HeadingKind),
            $"The build script's key, a string, wears a Markdown heading's badge ({SymbolPopup.Badge(build.Kind).Letter}) in File Structure and the navigation bar");
        window.KeyTextInput("build");
        Ui.WaitFor(() => structure.IsSettled && structure.Rows.Select(r => r.Name).SequenceEqual(["scripts", "build"]), () => $"the outline filtered to the build script in its object, not [{string.Join(", ", structure.Rows.Select(r => r.Name))}]");
        Ui.Press(window, "DOWN");
        Assert.Equal("build", structure.Selected?.Name);
        Ui.Press(window, "ENTER");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Contains("\"build\"", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line), StringComparison.Ordinal);
        window.Close();
    }

    private void Package_json_completes_and_checks_by_its_schema_goes_from_a_dependency_to_its_package_and_renames_a_script_with_its_runs()
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

    private void A_components_style_and_script_complete_check_go_to_declaration_and_rename_through_the_HTML_server()
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

    /// <summary>A web language server whose process ends during its own start, before it has answered, is started again as
    /// one that ends later is, the status bar saying so, and its language comes back for the file open.</summary>
    private void A_stylesheets_language_server_that_ends_as_it_starts_is_started_again_saying_so_and_checks_the_file()
    {
        SkipWithoutNode();
        string folder = TestFolders.Create("css-ending-");
        File.WriteAllText(Path.Combine(folder, "site.css"), Unknown);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false, autosaves: false);
        List<string> said = [];
        using IDisposable following = Ui.FollowStatus(window, said);
        try
        {
            using (EndingAsTheyStart ending = new(CssServer, times: 1))
            {
                window.OpenFile(Path.Combine(PathNames.Canonical(folder), "site.css"));
                OpenDocument document = window.EditorArea.Active!;
                Ui.WaitFor(() => document.Markers.Any(Warned), () => $"the CSS server's warning once it was started again ({window.LanguageShown}; the status bar says {window.Status})");
                Assert.True(ending.Ended.Count == 1, $"The CSS language server was ended {ending.Ended.Count} times as it started, not once");
            }
            Assert.True(said.Contains("The CSS language server ended as it started; starting it again"),
                $"The status bar never said the server that ended as it started was started again; it said: {string.Join(" | ", said)}");
            Assert.Contains($"The CSS language server ended as it started for {window.Folder}", Ui.IdeLog());
            Assert.Equal("CSS", window.LanguageShown);
            Assert.Null(window.EditorArea.ActiveGroup.BannerText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>What the CSS language server's command line names it by.</summary>
    private const string CssServer = "cssServerMain.js";

    /// <summary>A stylesheet with a property CSS has not, which its server warns of.</summary>
    private const string Unknown = ".card {\n    colr: red;\n}\n";

    private static bool Warned(Marker marker) => marker.Kind == MarkerKind.Warning && marker.Message.Contains("Unknown property: 'colr'", StringComparison.Ordinal);

    private void A_project_with_its_own_typescript_is_read_by_it()
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
            // The language said once, its TypeScript's version beside it.
            Ui.WaitFor(() => window.LanguageShown is { } shown && System.Text.RegularExpressions.Regex.IsMatch(shown, @"^TypeScript \d"),
                () => $"the status bar to name the TypeScript in use, saying the language once ({window.LanguageShown})");
            Assert.DoesNotContain("Passenger's", window.LanguageShown, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A folder's files copied into another, its folders made as they come.</summary>
    internal static void CopyFolder(string from, string to)
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

    /// <summary>Skips a test that needs the web language servers where there is no Node.js to run them, or, for one that
    /// needs the TypeScript server (<paramref name="typescript"/>), one older than it needs.</summary>
    internal static void SkipWithoutNode(bool typescript = false)
    {
        Assert.SkipWhen(Programs.Node() is null, NoNode);
        if (typescript)
        {
            string? old = TooOld();
            Assert.SkipWhen(old is not null, $"The TypeScript language server cannot run here: {old}");
        }
    }

    /// <inheritdoc cref="SkipWithoutNode"/>
    internal static async Task SkipWithoutNodeAsync(bool typescript = false)
    {
        Assert.SkipWhen(Programs.Node() is null, NoNode);
        if (typescript)
        {
            string? old = await TooOldAsync();
            Assert.SkipWhen(old is not null, $"The TypeScript language server cannot run here: {old}");
        }
    }

    private const string NoNode = "Node.js is not on the PATH, and the web language servers run on it";

    /// <summary><see cref="TooOldAsync"/> from the UI thread, the window's loop running meanwhile.</summary>
    private static string? TooOld() => Ui.Await(Waiting.Apart(() => TooOldAsync()), "the PATH's Node.js to say its version");

    /// <summary>Why the PATH's Node.js cannot run the TypeScript server, or null when it can.</summary>
    private static async Task<string?> TooOldAsync()
    {
        Passenger.App.Web.NodeRuntime node = await Passenger.App.Web.NodeRuntime.FindAsync(null);
        return node.Problem ?? node.TooOldFor(Path.Combine(Passenger.App.Web.WebLanguages.Modules, "typescript-language-server", "package.json"), "the TypeScript language server");
    }
}
