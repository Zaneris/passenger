using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>ESLint and Prettier through the window, on a project with its own eslint and prettier in node_modules (the
/// tests' packages, pinned in NodePackages/ and put in place at build, never fetched from npm as the tests run) and its own
/// eslint.config.js, .prettierrc and .prettierignore: problems as typed and their fixes, Fix ESLint Problems, Prettier on
/// Reformat Code and on save, .prettierignore honoured, the banner offering npm install for what is not installed,
/// packages in a node_modules another user made above passed over, a Yarn Plug'n'Play project's packages found through its
/// .pnp.cjs, a Bun or Deno folder's servers run on its runtime, and Oxlint with Oxfmt, and Biome, in folders that name
/// them. Each test opens its own window; one that cannot find Node.js skips, saying so.</summary>
public sealed class EslintPrettierTests : IClassFixture<EslintPrettierTests.Project>
{
    private const string PreferConst = "ESLint: 'total' is never reassigned. Use 'const' instead. (prefer-const)";

    private readonly Project project;

    public EslintPrettierTests(Project project) => this.project = project;

    /// <summary>ESLint's problems show as typed and its fixes apply, one server a folder, the person's own ESLint alone
    /// running, and one not installed is installed from its banner.</summary>
    [AvaloniaFact]
    public void Eslint_checks_and_fixes_as_its_server_says()
    {
        Flow.Step(nameof(An_ESLint_problem_shows_as_it_is_typed_in_the_editor_the_stripe_and_Problems_with_its_rules_page_and_its_fix_makes_it_go),
            An_ESLint_problem_shows_as_it_is_typed_in_the_editor_the_stripe_and_Problems_with_its_rules_page_and_its_fix_makes_it_go);
        OpenWindows.CloseAll();
        Flow.Step(nameof(Fix_ESLint_Problems_fixes_the_whole_file_and_a_save_fixes_first_when_Settings_ask_and_Disabled_takes_ESLints_problems_away),
            Fix_ESLint_Problems_fixes_the_whole_file_and_a_save_fixes_first_when_Settings_ask_and_Disabled_takes_ESLints_problems_away);
        OpenWindows.CloseAll();
        Flow.Step(nameof(ESLints_settings_changed_as_it_starts_leave_one_server_for_the_folder_which_ends_with_it),
            ESLints_settings_changed_as_it_starts_leave_one_server_for_the_folder_which_ends_with_it);
        OpenWindows.CloseAll();
        Flow.Step(nameof(ESLint_and_Prettier_in_a_node_modules_another_user_made_above_are_passed_over_and_in_the_persons_own_check_the_script),
            ESLint_and_Prettier_in_a_node_modules_another_user_made_above_are_passed_over_and_in_the_persons_own_check_the_script);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_package_json_listing_ESLint_and_Prettier_not_installed_says_so_in_the_banner_whose_link_runs_npm_install_there_and_a_Manual_ESLint_checks_it),
            A_package_json_listing_ESLint_and_Prettier_not_installed_says_so_in_the_banner_whose_link_runs_npm_install_there_and_a_Manual_ESLint_checks_it);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_Yarn_Plug_n_Play_projects_ESLint_and_Prettier_are_found_through_its_pnp_cjs_with_no_node_modules_and_check_and_format_its_script),
            A_Yarn_Plug_n_Play_projects_ESLint_and_Prettier_are_found_through_its_pnp_cjs_with_no_node_modules_and_check_and_format_its_script);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_folder_naming_Oxlint_and_Oxfmt_gets_Oxlints_problems_and_fixes_and_Oxfmts_layout_on_Reformat_Code_and_on_save),
            A_folder_naming_Oxlint_and_Oxfmt_gets_Oxlints_problems_and_fixes_and_Oxfmts_layout_on_Reformat_Code_and_on_save);
        OpenWindows.CloseAll();
        // Last, as its skip where neither Bun nor Deno is installed ends the flow.
        Flow.Step(nameof(A_Bun_or_Deno_folder_runs_TypeScript_and_ESLint_on_its_runtime_with_no_Node_js_on_the_PATH),
            A_Bun_or_Deno_folder_runs_TypeScript_and_ESLint_on_its_runtime_with_no_Node_js_on_the_PATH);
    }

    /// <summary>Prettier formats on Reformat Code and on save by the project's configuration, and a save lands whatever
    /// happens as it formats.</summary>
    [AvaloniaFact]
    public void Prettier_formats_and_saves_land_as_asked()
    {
        Flow.Step(nameof(Prettier_formats_on_Reformat_Code_and_on_save_by_the_projects_own_configuration_as_one_undo_and_leaves_what_prettierignore_names),
            Prettier_formats_on_Reformat_Code_and_on_save_by_the_projects_own_configuration_as_one_undo_and_leaves_what_prettierignore_names);
        OpenWindows.CloseAll();
        Flow.Step(nameof(Save_saves_the_file_it_was_pressed_on_though_another_tab_is_chosen_while_Prettier_formats_it),
            Save_saves_the_file_it_was_pressed_on_though_another_tab_is_chosen_while_Prettier_formats_it);
        OpenWindows.CloseAll();
        Flow.Step(nameof(Save_saves_the_file_though_Prettiers_runner_ends_or_the_folder_closes_while_it_formats),
            Save_saves_the_file_though_Prettiers_runner_ends_or_the_folder_closes_while_it_formats);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_folder_naming_Biome_gets_its_problems_and_fixes_and_its_layout_on_Reformat_Code_and_on_save_rather_than_an_installed_Prettiers),
            A_folder_naming_Biome_gets_its_problems_and_fixes_and_its_layout_on_Reformat_Code_and_on_save_rather_than_an_installed_Prettiers);
    }

    /// <summary>A folder whose package.json lists oxlint and oxfmt, with an .oxlintrc.json and an .oxfmtrc.json: Oxlint's
    /// problem shows as typed, in Problems too, its fix makes it go, and Oxfmt lays the file out on Reformat Code and, with
    /// Reformat on save, on save, each by the folder's own configuration.</summary>
    private void A_folder_naming_Oxlint_and_Oxfmt_gets_Oxlints_problems_and_fixes_and_Oxfmts_layout_on_Reformat_Code_and_on_save()
    {
        Project.SkipWithoutNode();
        Project.SkipWithout("Oxlint", "@oxlint");
        string folder = TestFolders.Create("oxc-");
        Project.Write(folder,
            ("package.json", "{\n  \"name\": \"oxc-client\",\n  \"private\": true,\n  \"devDependencies\": {\n"
                + $"    \"oxfmt\": \"{Project.VersionOf("oxfmt")}\",\n    \"oxlint\": \"{Project.VersionOf("oxlint")}\"\n  }}\n}}\n"),
            (".oxlintrc.json", "{\n  \"rules\": { \"prefer-const\": \"error\" }\n}\n"),
            (".oxfmtrc.json", "{ \"semi\": false, \"singleQuote\": true }\n"),
            ("src/app.js", Project.App));
        Project.CopyPackages(folder, "oxlint", "@oxlint", "oxfmt", "@oxfmt", "tinypool");
        MainWindow window = Ui.OpenFolder(folder, autosaves: false);
        try
        {
            string path = Path.Combine(PathNames.Canonical(folder), "src", "app.js");
            window.OpenFile(path);
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "console.log(items);");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("let total = items.length;");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("console.log(total);");
            static bool Oxlints(Marker m) => m.Message.StartsWith("Oxlint: `total` is never reassigned.", StringComparison.Ordinal) && m.Message.EndsWith("(eslint(prefer-const))", StringComparison.Ordinal);
            Ui.WaitFor(() => document.Markers.Any(Oxlints), "Oxlint's problem under the let never assigned again, as it is typed");
            Marker problem = Assert.Single(document.Markers, Oxlints);
            Assert.Equal(MarkerKind.Error, problem.Kind);
            Assert.Equal("total", editor.Document.Snapshot.GetText(problem.Range.Start, problem.Range.Length));
            window.Perform(AppActions.ShowProblems);
            Ui.Settle();
            Assert.Contains(window.BottomPanel.Problems.FileProblems, p => p.Message == problem.Message);

            Ui.PlaceAfter(editor, "let tot");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes for the line");
            Assert.StartsWith("Oxlint: Replace `let total = items.length;` with `const total", window.Assist.Offered[0], StringComparison.Ordinal);
            Assert.Contains("Oxlint: Fix current file", window.Assist.Offered);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("const total = items.length;", StringComparison.Ordinal), "the let made a const by Oxlint's fix");
            Ui.WaitFor(() => !document.Markers.Any(Oxlints), "the problem gone once the code is fixed");

            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "const  label = {name:\"tea\"};\n")]);
            Ui.Run(window, AppActions.Reformat);
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().EndsWith("console.log(total)\nconst label = { name: 'tea' }\n", StringComparison.Ordinal),
                "the file laid out by Oxfmt as its .oxfmtrc.json asks, with no semicolons and single quotes");

            window.ApplySettings(window.Settings with { ReformatOnSave = true });
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "const  more = [1,2];\n")]);
            Assert.True(window.Perform(AppActions.Save), "Save did not run");
            Ui.WaitFor(() => !document.IsModified, "app.js saved");
            Assert.EndsWith("const label = { name: 'tea' }\nconst more = [1, 2]\n", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>A folder with a biome.json whose package.json lists Biome, with Prettier installed beside it but named
    /// nowhere: Biome's problem shows as typed, its fix (which its server gives only once chosen) makes it go, and Biome,
    /// not Prettier, lays the file out on Reformat Code and on save by biome.json.</summary>
    private void A_folder_naming_Biome_gets_its_problems_and_fixes_and_its_layout_on_Reformat_Code_and_on_save_rather_than_an_installed_Prettiers()
    {
        Project.SkipWithoutNode();
        Project.SkipWithout("Biome", "@biomejs", "cli-");
        string folder = TestFolders.Create("biome-");
        Project.Write(folder,
            ("package.json", "{\n  \"name\": \"biome-client\",\n  \"private\": true,\n  \"devDependencies\": {\n"
                + $"    \"@biomejs/biome\": \"{Project.VersionOf("@biomejs/biome")}\"\n  }}\n}}\n"),
            ("biome.json", "{\n  \"linter\": { \"rules\": { \"style\": { \"useConst\": \"error\" } } },\n  \"formatter\": { \"indentStyle\": \"space\" },\n"
                + "  \"javascript\": { \"formatter\": { \"semicolons\": \"asNeeded\", \"quoteStyle\": \"single\" } }\n}\n"),
            ("src/app.js", Project.App));
        Project.CopyPackages(folder, "@biomejs", "prettier");
        MainWindow window = Ui.OpenFolder(folder, autosaves: false);
        try
        {
            string path = Path.Combine(PathNames.Canonical(folder), "src", "app.js");
            window.OpenFile(path);
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "console.log(items);");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("let total = items.length;");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("console.log(total);");
            const string UseConst = "Biome: This let declares a variable that is only assigned once. (lint/style/useConst)";
            Ui.WaitFor(() => document.Markers.Any(m => m.Message == UseConst), "Biome's problem under the let never assigned again, as it is typed");
            Marker problem = Assert.Single(document.Markers, m => m.Message == UseConst);
            Assert.Equal(MarkerKind.Error, problem.Kind);
            Assert.Equal("https://biomejs.dev/linter/rules/use-const", problem.Link?.AbsoluteUri);

            Ui.PlaceAfter(editor, "let tot");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes for the line");
            Assert.StartsWith("Biome: ", window.Assist.Offered[0], StringComparison.Ordinal);
            Assert.Contains("Biome: Fix current file", window.Assist.Offered);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("const total = items.length", StringComparison.Ordinal), "the let made a const by Biome's fix");
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message == UseConst), "the problem gone once the code is fixed");

            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "const  label = {name:\"tea\"};\n")]);
            Ui.Run(window, AppActions.Reformat);
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().EndsWith("console.log(total)\nconst label = { name: 'tea' }\n", StringComparison.Ordinal),
                "the file laid out by Biome as its biome.json asks, with no semicolons and single quotes, not by the Prettier installed beside it");

            window.ApplySettings(window.Settings with { ReformatOnSave = true, PrettierOnSave = true });
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "const  more = [1,2];\n")]);
            Assert.True(window.Perform(AppActions.Save), "Save did not run");
            Ui.WaitFor(() => !document.IsModified, "app.js saved");
            Assert.EndsWith("const label = { name: 'tea' }\nconst more = [1, 2]\n", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>ESLint's problem shows as typed, with its rule's page, and its fix, which ESLint's server makes by handing
    /// the window its edit, makes it go, the server told the edit was made, so it logs no failure.</summary>
    private void An_ESLint_problem_shows_as_it_is_typed_in_the_editor_the_stripe_and_Problems_with_its_rules_page_and_its_fix_makes_it_go()
    {
        Project.SkipWithoutNode();
        MainWindow window = project.Open("src/app.js");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "console.log(items);");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("let total = items.length;");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("console.log(total);");
            Ui.WaitFor(() => document.Markers.Any(m => m.Message == PreferConst), "ESLint's problem under the let never assigned again, as it is typed");
            Marker problem = Assert.Single(document.Markers, m => m.Message.StartsWith("ESLint:", StringComparison.Ordinal));
            Assert.Equal(MarkerKind.Error, problem.Kind);
            Assert.Equal("total", editor.Document.Snapshot.GetText(problem.Range.Start, problem.Range.Length));
            Assert.Equal("https://eslint.org/docs/latest/rules/prefer-const", problem.Link?.AbsoluteUri);
            Assert.Contains(editor.StripeMarks, m => m.Kind == StripeMarkKind.Error && m.Message == PreferConst);

            window.Perform(AppActions.ShowProblems);
            Ui.Settle();
            Problem listed = Assert.Single(window.BottomPanel.Problems.FileProblems, p => p.Message == PreferConst);
            Assert.Equal(ProblemSeverity.Error, listed.Severity);
            Button rule = window.ProblemsWindow.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains(StyleClasses.Link) && (string?)b.Content == "prefer-const");
            Ui.Click(rule);
            Assert.Equal(["https://eslint.org/docs/latest/rules/prefer-const"], window.Browsed.Select(u => u.AbsoluteUri));

            Ui.PlaceAfter(editor, "let tot");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes for the line");
            Assert.Equal("ESLint: Fix 'prefer-const'", window.Assist.Offered[0]);
            Assert.Contains("ESLint: Suppress 'prefer-const' for current line", window.Assist.Offered);
            IdeErrors.Flush();
            int logged = Ui.Shared(IdeErrors.LogPath).Length;
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("const total = items.length;", StringComparison.Ordinal), "the let made a const by ESLint's fix");
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message == PreferConst), "the problem gone once the code is fixed");
            // The fix's command answered, after what the server logged of its edit's answer.
            Ui.Settle();
            IdeErrors.Flush();
            string log = Ui.Shared(IdeErrors.LogPath);
            string since = log.Length >= logged ? log[logged..] : log;
            Assert.False(since.Contains("Failed to apply command", StringComparison.Ordinal),
                $"ESLint was told its fix was not made though the window made it, so it logged a failure: {string.Join('\n', since.Split('\n').Where(l => l.Contains("ESLint", StringComparison.Ordinal)).Take(5))}");
            Ui.Run(window, EditorActions.Undo);
            Assert.Contains("let total = items.length;", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            project.Reset();
        }
    }

    private void Fix_ESLint_Problems_fixes_the_whole_file_and_a_save_fixes_first_when_Settings_ask_and_Disabled_takes_ESLints_problems_away()
    {
        Project.SkipWithoutNode();
        MainWindow window = project.Open("src/app.js");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            Ui.PlaceAfter(editor, "console.log(items);");
            editor.ApplyEdits([new TextEdit(editor.Selections.Primary.Caret, 0, "\nlet first = 1;\nlet second = 2;\nconsole.log(first + second);")]);
            Ui.WaitFor(() => document.Markers.Count(m => m.Message.EndsWith("(prefer-const)", StringComparison.Ordinal)) == 2, "a problem under each let");
            Ui.ChooseFromEditorMenu(window, "Fix ESLint Problems");
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("const first = 1;\nconst second = 2;", StringComparison.Ordinal), "both lets fixed at once");
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message.StartsWith("ESLint:", StringComparison.Ordinal)), "no ESLint problem left");

            window.ApplySettings(window.Settings with { EslintFixOnSave = true });
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "let third = 3;\nconsole.log(third);\n")]);
            Ui.WaitFor(() => document.Markers.Any(m => m.Message.EndsWith("(prefer-const)", StringComparison.Ordinal)), "the problem under the new let");
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => !document.IsModified, "the file saved");
            Assert.Contains("const third = 3;", File.ReadAllText(project.PathOf("src/app.js")), StringComparison.Ordinal);

            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "let fourth = 4;\nconsole.log(fourth);\n")]);
            Ui.WaitFor(() => document.Markers.Any(m => m.Message.EndsWith("(prefer-const)", StringComparison.Ordinal)), "the problem under the last let");
            window.ApplySettings(window.Settings with { Eslint = ToolConfiguration.Disabled });
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message.StartsWith("ESLint:", StringComparison.Ordinal)), "ESLint's problems gone once it is disabled");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
            project.Reset();
        }
    }

    private void Prettier_formats_on_Reformat_Code_and_on_save_by_the_projects_own_configuration_as_one_undo_and_leaves_what_prettierignore_names()
    {
        Project.SkipWithoutNode();
        MainWindow window = project.Open("src/format.js");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "pri");
            Ui.Run(window, AppActions.Reformat);
            Ui.WaitFor(() => editor.Document.Snapshot.ToString() == "const label = { name: 'tea', price: 3 }\nconsole.log(label)\n",
                "the file laid out by the project's Prettier, its .prettierrc's single quotes and no semicolons");
            Assert.Equal("Prettier: formatted 2 lines", window.Status);
            int caret = editor.Selections.Primary.Caret;
            Assert.Equal("pri", editor.Document.Snapshot.GetText(caret - 3, 3));
            Ui.Run(window, EditorActions.Undo);
            Assert.True(editor.Document.Snapshot.ToString() == Project.Format, $"One Undo did not take the whole format back:\n{editor.Document.Snapshot}");

            window.ApplySettings(window.Settings with { PrettierOnSave = true });
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "const  more = [1,2,3];\n")]);
            Ui.Run(window, AppActions.SaveAll);
            Ui.WaitFor(() => !window.EditorArea.Active!.IsModified, "the file saved");
            Assert.Equal("const label = { name: 'tea', price: 3 }\nconsole.log(label)\nconst more = [1, 2, 3]\n", File.ReadAllText(project.PathOf("src/format.js")));

            window.ApplySettings(window.Settings with { PrettierFiles = "**/*.ts" });
            Ui.WaitFor(() => window.LanguageShown?.StartsWith("JavaScript · TypeScript ", StringComparison.Ordinal) == true, "the TypeScript server reading the script");
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "const  other = {a:\"b\"};\n")]);
            Ui.Run(window, AppActions.Reformat);
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().EndsWith("console.log(label)\nconst more = [1, 2, 3]\nconst other = { a: \"b\" };\n", StringComparison.Ordinal),
                "a file outside Prettier's Run for files laid out by its language server, its double quotes and semicolon kept");

            window.OpenFile(project.PathOf("src/legacy.js"));
            Ui.Settle();
            editor = window.EditorArea.CodeEditor;
            Ui.WaitFor(() => window.LanguageShown?.StartsWith("JavaScript · TypeScript ", StringComparison.Ordinal) == true, "the TypeScript server reading the script");
            Ui.Run(window, AppActions.Reformat);
            Ui.WaitFor(() => editor.Document.Snapshot.ToString() == "const kept = \"as written\";\nconsole.log(kept);\n",
                "the file .prettierignore names laid out by its language server, its double quotes and semicolons kept");
            Ui.ChooseFromEditorMenu(window, "Reformat with Prettier");
            Assert.Equal("Prettier: file legacy.js was ignored with .prettierignore", window.Status);
            Assert.Equal("const kept = \"as written\";\nconsole.log(kept);\n", editor.Document.Snapshot.ToString());
            Assert.Contains($"Prettier {Project.VersionOf("prettier")} formatted", Ui.IdeLog(), StringComparison.Ordinal);
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
            project.Reset();
        }
    }

    private void Save_saves_the_file_it_was_pressed_on_though_another_tab_is_chosen_while_Prettier_formats_it()
    {
        Project.SkipWithoutNode();
        string held = project.AddFolder("src/held", (".prettierrc.cjs", Project.Holding), ("first.js", "const  first = {name:\"tea\"};\n"));
        MainWindow window = project.Open("src/format.js");
        try
        {
            window.ApplySettings(window.Settings with { PrettierOnSave = true });
            OpenDocument other = window.EditorArea.Active!;
            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(0, 0, "// Not saved.\n")]);
            window.OpenFile(project.PathOf("src/held/first.js"));
            Ui.Settle();
            OpenDocument first = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "let  more = 1;\n")]);

            Assert.True(window.Perform(AppActions.Save), "Save did not run");
            Processes.WrittenId(Path.Combine(held, "started"), "Prettier to start formatting first.js");
            window.OpenFile(project.PathOf("src/format.js"));
            Assert.Same(other, window.EditorArea.Active);
            File.WriteAllText(Path.Combine(held, "go"), string.Empty);
            Ui.WaitFor(() => !first.IsModified, "first.js, which Save was pressed on, saved once Prettier formatted it");
            Assert.Equal("const first = { name: 'tea' }\nlet more = 1\n", File.ReadAllText(project.PathOf("src/held/first.js")));
            Assert.True(other.IsModified, "format.js was saved, the tab chosen while Prettier formatted the file Save was pressed on");
            Assert.Equal(Project.Format, File.ReadAllText(project.PathOf("src/format.js")));
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
            project.Reset();
        }
    }

    private void Save_saves_the_file_though_Prettiers_runner_ends_or_the_folder_closes_while_it_formats()
    {
        Project.SkipWithoutNode();
        string held = project.AddFolder("src/held", (".prettierrc.cjs", Project.Holding), ("first.js", "const first = 1;\n"));
        string path = project.PathOf("src/held/first.js");
        MainWindow window = project.Open("src/held/first.js");
        try
        {
            window.ApplySettings(window.Settings with { PrettierOnSave = true });
            OpenDocument first = window.EditorArea.Active!;
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "let  more = 2;\n")]);
            Assert.True(window.Perform(AppActions.Save), "Save did not run");
            OwnProcess.Of(Processes.WrittenId(Path.Combine(held, "started"), "Prettier to start formatting first.js")).Kill(alone: true);
            Ui.WaitFor(() => !first.IsModified, "first.js saved, though Prettier's runner ended as it formatted it");
            Assert.Equal("const first = 1;\nlet  more = 2;\n", File.ReadAllText(path));
            Assert.StartsWith("Prettier: Prettier's runner ended", window.Status, StringComparison.Ordinal);

            File.Delete(Path.Combine(held, "started"));
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "let  last = 3;\n")]);
            Assert.True(window.Perform(AppActions.Save), "Save did not run");
            Processes.WrittenId(Path.Combine(held, "started"), "Prettier to start formatting first.js again, in a runner of its own");
            Assert.Null(window.CloseSolution());
            // The close saved the file; the format in flight ends with its runner, and Save after it throws nothing.
            Ui.Settle();
            Assert.Equal("const first = 1;\nlet  more = 2;\nlet  last = 3;\n", File.ReadAllText(path));
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
            project.Reset();
        }
    }

    private void ESLints_settings_changed_as_it_starts_leave_one_server_for_the_folder_which_ends_with_it()
    {
        Project.SkipWithoutNode();
        List<int> servers = [];
        void Heard(object? sender, ChildStart start)
        {
            if (start.Command.Contains("eslintServer.js", StringComparison.Ordinal))
            {
                lock (servers)
                {
                    servers.Add(start.ProcessId);
                }
            }
        }
        int[] Started()
        {
            lock (servers)
            {
                return [.. servers];
            }
        }
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        ChildProcesses.Started += Heard;
        try
        {
            window.OpenFolder(project.Folder);
            Ui.Settle();
            window.OpenFile(project.PathOf("src/app.js"));
            // ESLint's files changed while its server starts, and another file opened before the start in its place is done.
            window.ApplySettings(window.Settings with { EslintFiles = "**/*.js" });
            Ui.PumpUntil(() => Started().Length > 0, "an ESLint server to start");
            window.OpenFile(project.PathOf("src/format.js"));
            Ui.Settle();
            // A start given up ends its server at once, which the system may take a moment to tell.
            int[] running = [];
            Ui.WaitFor(() => (running = [.. Started().Where(Processes.IsRunning)]).Length == 1, "one ESLint server, and no other, to run for the folder");
            Assert.Null(window.CloseSolution());
            Ui.WaitFor(() => !Processes.IsRunning(running[0]), "the folder's ESLint server to end with it");
        }
        finally
        {
            ChildProcesses.Started -= Heard;
            window.ApplySettings(new AppSettings());
            window.Close();
            project.Reset();
        }
    }

    private void A_package_json_listing_ESLint_and_Prettier_not_installed_says_so_in_the_banner_whose_link_runs_npm_install_there_and_a_Manual_ESLint_checks_it()
    {
        Project.SkipWithoutNode();
        Assert.SkipWhen(Programs.Node() is not { } node || Npm.Cli(node) is null, "npm is not beside the PATH's Node.js, and Run 'npm install' runs it");
        string folder = TestFolders.Create("uninstalled-");
        File.WriteAllText(Path.Combine(folder, "package.json"), Project.Package);
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        File.WriteAllText(Path.Combine(folder, "src", "app.js"), Project.App);
        string canonical = PathNames.Canonical(folder);
        List<ChildStart> started = [];
        void Heard(object? sender, ChildStart start)
        {
            lock (started)
            {
                started.Add(start);
            }
        }
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        ChildProcesses.Started += Heard;
        try
        {
            window.OpenFolder(folder);
            window.OpenFile(Path.Combine(canonical, "src", "app.js"));
            Ui.Settle();
            Ui.WaitFor(() => window.EditorArea.ActiveGroup.BannerText is not null, "the banner over the script");
            Assert.Equal("ESLint and Prettier are not installed: package.json lists eslint and prettier, which are not in node_modules.",
                window.EditorArea.ActiveGroup.BannerText);
            Assert.Equal("Run 'npm install'", window.EditorArea.ActiveGroup.BannerLink);

            Ui.Click(window.EditorArea.ActiveGroup.BannerLinkControl);
            Ui.WaitFor(() => window.Status?.StartsWith("npm install failed in ", StringComparison.Ordinal) == true,
                "npm install to end, which the run's npm, offline with an empty cache, cannot finish");
            ChildStart npm;
            lock (started)
            {
                npm = Assert.Single(started, s => s.Command.Contains("npm-cli.js", StringComparison.Ordinal) && s.Command.EndsWith("install", StringComparison.Ordinal));
            }
            Assert.Equal(canonical, PathNames.Canonical(npm.Folder));
            Assert.Equal("Run 'npm install'", window.EditorArea.ActiveGroup.BannerLink);

            // Manual configuration: the eslint package Settings names checks the files, the project having none of its own.
            File.WriteAllText(Path.Combine(folder, "eslint.config.js"), Project.Config);
            window.ApplySettings(window.Settings with { Eslint = ToolConfiguration.Manual, EslintPackage = project.PathOf("node_modules/eslint") });
            Ui.WaitFor(() => window.EditorArea.ActiveGroup.BannerText?.StartsWith("Prettier is not installed", StringComparison.Ordinal) == true,
                "the banner naming only what Settings leaves to the project");
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "let total = items.length;\nconsole.log(total);\n")]);
            Ui.WaitFor(() => document.Markers.Any(m => m.Message == PreferConst), "the problem from the eslint package Settings names");
        }
        finally
        {
            ChildProcesses.Started -= Heard;
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>A folder whose package.json lists ESLint and Prettier, installed only in the node_modules of the folder above
    /// it: while another user owns that node_modules (as one made at a drive's root may be) they are passed over, the
    /// banner saying they are not installed, and once it is the person's the parent's ESLint checks the folder's script.</summary>
    private void ESLint_and_Prettier_in_a_node_modules_another_user_made_above_are_passed_over_and_in_the_persons_own_check_the_script()
    {
        Project.SkipWithoutNode();
        string app = PathNames.Canonical(project.AddFolder("app", ("package.json", Project.Package), ("app.js", Project.App)));
        string modules = Path.Combine(PathNames.Canonical(project.Folder), "node_modules");
        MainWindow window;
        using (FoldersUp.HeldByAnother(modules))
        {
            window = Opened();
            try
            {
                Ui.WaitFor(() => window.EditorArea.ActiveGroup.BannerText is not null, "the banner over the script");
                Assert.Equal("ESLint and Prettier are not installed: package.json lists eslint and prettier, which are not in node_modules.",
                    window.EditorArea.ActiveGroup.BannerText);
            }
            finally
            {
                window.Close();
            }
        }

        window = Opened();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "let total = items.length;\nconsole.log(total);\n")]);
            Ui.WaitFor(() => document.Markers.Any(m => m.Message == PreferConst), "the problem from the ESLint in the person's own parent folder");
            Assert.Null(window.EditorArea.ActiveGroup.BannerText);
        }
        finally
        {
            window.Close();
            // The window's servers worked in the folder it opened, which Windows deletes only once they have ended.
            Ui.Ended(window);
            project.Reset();
        }

        MainWindow Opened()
        {
            MainWindow opened = Ui.OpenFolder(app, autosaves: false);
            opened.OpenFile(Path.Combine(app, "app.js"));
            Ui.Settle();
            return opened;
        }
    }

    /// <summary>A Yarn Plug'n'Play project, its packages laid out by Yarn with no registry and no node_modules anywhere: the
    /// ESLint its .pnp.cjs places checks its script and its Prettier formats it, the servers running with its .pnp.cjs, and
    /// no banner says they are missing.</summary>
    private void A_Yarn_Plug_n_Play_projects_ESLint_and_Prettier_are_found_through_its_pnp_cjs_with_no_node_modules_and_check_and_format_its_script()
    {
        Project.SkipWithoutNode();
        string folder = PathNames.Canonical(TestFolders.Create("pnp-"));
        MainWindow? window = null;
        try
        {
            Project.LaidOutByYarn(folder);
            Assert.True(File.Exists(Path.Combine(folder, YarnPnp.ManifestName)) && !Directory.Exists(Path.Combine(folder, "node_modules")),
                $"Yarn laid {folder} out with no .pnp.cjs, or a node_modules, so nothing is left to Plug'n'Play");
            window = Ui.OpenFolder(folder, autosaves: false);
            window.OpenFile(Path.Combine(folder, "src", "app.js"));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "let total = items.length;\nconsole.log(total);\n")]);
            Ui.WaitFor(() => document.Markers.Any(m => m.Message == PreferConst), "the problem from the ESLint the project's .pnp.cjs places");
            Assert.Null(window.EditorArea.ActiveGroup.BannerText);

            window.OpenFile(Path.Combine(folder, "src", "format.js"));
            Ui.Settle();
            editor = window.EditorArea.CodeEditor;
            Ui.Run(window, AppActions.Reformat);
            Ui.WaitFor(() => editor.Document.Snapshot.ToString() == "const label = { name: \"tea\", price: 3 };\nconsole.log(label);\n",
                "the file laid out by the Prettier the project's .pnp.cjs places");
            Assert.Null(window.EditorArea.ActiveGroup.BannerText);
        }
        finally
        {
            window?.Close();
            if (window is not null)
            {
                // The window's servers worked in the folder, which Windows deletes only once they have ended.
                Ui.Ended(window);
            }
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A folder whose lockfile is Bun's, and one whose lockfile is Deno's, run TypeScript's server and ESLint on that
    /// runtime with no Node.js on the PATH the IDE has: a TypeScript error is marked and ESLint's problem shown, the log
    /// saying the runtime each server started on, and nothing the folder's own bunfig.toml preloads run. A runtime not
    /// installed is passed over, said in the test's output, and the step skips where neither is.</summary>
    private void A_Bun_or_Deno_folder_runs_TypeScript_and_ESLint_on_its_runtime_with_no_Node_js_on_the_PATH()
    {
        (ScriptRuntime Kind, string Program, string Lockfile, string Lock)[] runtimes =
        [
            (ScriptRuntime.Bun, "bun", "bun.lock", "{\n  \"lockfileVersion\": 1,\n  \"workspaces\": {\n    \"\": {\n      \"name\": \"shop-client\"\n    }\n  },\n  \"packages\": {}\n}\n"),
            (ScriptRuntime.Deno, "deno", "deno.lock", "{\n  \"version\": \"5\"\n}\n"),
        ];
        List<string> missing = [];
        foreach ((ScriptRuntime kind, string program, string lockfile, string written) in runtimes)
        {
            if (Programs.Command(program) is not { } found)
            {
                missing.Add($"{NodeRuntime.NameOf(kind)} is not on the PATH, so its folder was not run");
                continue;
            }
            RunsOn(kind, found, lockfile, written);
        }
        foreach (string said in missing)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(said);
        }
        Assert.SkipWhen(missing.Count == runtimes.Length, string.Join("; ", missing));
    }

    /// <summary>A folder with a runtime's lockfile opened while the PATH holds that runtime and no Node.js: its TypeScript
    /// error and ESLint problem shown by servers on that runtime.</summary>
    private void RunsOn(ScriptRuntime kind, string found, string lockfile, string written)
    {
        string name = NodeRuntime.NameOf(kind);
        string app = PathNames.Canonical(project.AddFolder(name.ToLowerInvariant(), ("package.json", Project.Package), (lockfile, written),
            ("eslint.config.js", Project.Config), ("bunfig.toml", "preload = [\"./preload.cjs\"]\n"),
            ("preload.cjs", "require(\"fs\").writeFileSync(require(\"path\").join(__dirname, \"preloaded\"), \"\");\n")));
        project.AddFolder(Path.Combine(name.ToLowerInvariant(), "src"), ("app.js", Project.App), ("sum.ts", "const label: string = 5;\nconsole.log(label);\n"));
        string bin = TestFolders.Create("runtime-");
        string? path = Environment.GetEnvironmentVariable("PATH");
        string node = OperatingSystem.IsWindows() ? "node.exe" : "node";
        MainWindow? window = null;
        try
        {
            // The runtime alone in a folder of its own, where Node.js stands beside it (Homebrew's bin).
            string runtime = Path.Combine(bin, Path.GetFileName(found));
            if (OperatingSystem.IsWindows())
            {
                File.Copy(found, runtime);
            }
            else
            {
                File.CreateSymbolicLink(runtime, found);
            }
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator,
                [bin, .. (path ?? string.Empty).Split(Path.PathSeparator).Where(at => at.Length > 0 && !File.Exists(Path.Combine(at, node)))]));
            Assert.True(Programs.Node() is null, $"Node.js is still on the PATH the test gives, at {Programs.Node()}");

            window = Ui.OpenFolder(app, autosaves: false);
            window.OpenFile(Path.Combine(app, "src", "sum.ts"));
            Ui.Settle();
            OpenDocument sum = window.EditorArea.Active!;
            Ui.WaitFor(() => sum.Markers.Any(m => m.Kind == MarkerKind.Error && m.Message.StartsWith("TS2322:", StringComparison.Ordinal)),
                $"TypeScript's error under the number given to a string, from its server on {name}");
            Assert.Equal(kind, window.Session.Web!.Node?.Kind);

            window.OpenFile(Path.Combine(app, "src", "app.js"));
            Ui.Settle();
            CodeEditor editor = window.EditorArea.CodeEditor;
            OpenDocument document = window.EditorArea.Active!;
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "let total = items.length;\nconsole.log(total);\n")]);
            Ui.WaitFor(() => document.Markers.Any(m => m.Message == PreferConst), $"ESLint's problem from its server on {name}");
            string log = Ui.IdeLog();
            foreach (string server in (string[])["TypeScript", "ESLint"])
            {
                Assert.True(System.Text.RegularExpressions.Regex.IsMatch(log, $@"{server} language server \(process \d+\) started for {System.Text.RegularExpressions.Regex.Escape(app)} on {name} "),
                    $"The IDE's log does not say the {server} server started on {name} for {app}");
            }
            Assert.False(File.Exists(Path.Combine(app, "preloaded")), $"{name} ran what the folder's own bunfig.toml preloads as it ran Passenger's servers");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            window?.Close();
            if (window is not null)
            {
                Ui.Ended(window);
            }
            project.Reset();
            TestFolders.Delete(bin);
        }
    }

    /// <summary>A JavaScript project with its own ESLint and Prettier installed (the tests' packages copied into its
    /// node_modules once), a flat ESLint configuration, a .prettierrc and a .prettierignore; its files are put back as
    /// written after each test.</summary>
    public sealed class Project : IDisposable
    {
        public static readonly string Package = "{\n  \"name\": \"shop-client\",\n  \"version\": \"1.0.0\",\n  \"private\": true,\n  \"type\": \"module\",\n"
            + $"  \"devDependencies\": {{\n    \"eslint\": \"{VersionOf("eslint")}\",\n    \"prettier\": \"{VersionOf("prettier")}\"\n  }}\n}}\n";

        /// <summary>The version of one of the tests' own npm packages, which NodePackages/package.json pins.</summary>
        public static string VersionOf(string package)
        {
            string file = Path.Combine(AppContext.BaseDirectory, "node-packages", "node_modules", package, "package.json");
            using System.Text.Json.JsonDocument read = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            return read.RootElement.GetProperty("version").GetString() ?? throw new InvalidOperationException($"{file} names no version");
        }

        public const string Config = "export default [\n  {\n    files: [\"**/*.js\"],\n    rules: {\n      \"prefer-const\": \"error\",\n    },\n  },\n];\n";

        public const string App = "// The cart's count.\nconst items = [1, 2, 3];\nconsole.log(items);\n";

        public const string Format = "const  label = {name:\"tea\",price:3};\nconsole.log(label);\n";

        public const string Legacy = "const  kept   =   \"as written\";\nconsole.log(kept);\n";

        /// <summary>A Prettier configuration that holds its runner as it loads, until the test writes <c>go</c> beside it,
        /// having written the runner's process id to <c>started</c>: so a test acts while Prettier formats.</summary>
        public const string Holding = "const fs = require(\"fs\");\nconst path = require(\"path\");\n"
            + "fs.writeFileSync(path.join(__dirname, \"started\"), String(process.pid));\n"
            + "const pause = new Int32Array(new SharedArrayBuffer(4));\nconst until = Date.now() + 60000;\n"
            + "while (!fs.existsSync(path.join(__dirname, \"go\")) && Date.now() < until) {\n  Atomics.wait(pause, 0, 0, 20);\n}\n"
            + "module.exports = { semi: false, singleQuote: true };\n";

        private readonly List<string> added = [];

        /// <summary>The tests' packages a project of Oxlint, Oxfmt or Biome takes, which the ESLint and Prettier project leaves out.</summary>
        private static readonly string[] Others = ["oxlint", "@oxlint", "oxfmt", "@oxfmt", "tinypool", "@biomejs"];

        private static string Packages => Path.Combine(AppContext.BaseDirectory, "node-packages", "node_modules");

        private static readonly (string Path, string Text)[] Files =
        [
            ("package.json", Package),
            ("eslint.config.js", Config),
            (".prettierrc", "{ \"semi\": false, \"singleQuote\": true }\n"),
            (".prettierignore", "src/legacy.js\n"),
            ("src/app.js", App),
            ("src/format.js", Format),
            ("src/legacy.js", Legacy),
        ];

        public Project()
        {
            Folder = TestFolders.Create("lint-");
            TestFolders.Prepare(() =>
            {
                Assert.True(Directory.Exists(Path.Combine(Packages, "eslint")), $"The tests' own eslint and prettier are not at {Packages}: the build puts them there");
                foreach (string package in Directory.EnumerateDirectories(Packages).Where(d => !Others.Contains(Path.GetFileName(d))))
                {
                    CopyFolder(package, Path.Combine(Folder, "node_modules", Path.GetFileName(package)));
                }
                Reset();
            }, Folder);
        }

        public string Folder { get; }

        /// <summary>A file's path as the IDE names it: canonical, its '/' Windows' '\' there, so it compares with an open file's.</summary>
        public string PathOf(string relative) => Path.GetFullPath(Path.Combine(PathNames.Canonical(Folder), relative));

        /// <summary>A folder of a test's own in the project, with its files, gone again at <see cref="Reset"/>; its path.</summary>
        public string AddFolder(string relative, params (string Name, string Text)[] files)
        {
            string folder = Path.Combine(Folder, relative);
            Directory.CreateDirectory(folder);
            added.Add(folder);
            foreach ((string name, string text) in files)
            {
                File.WriteAllText(Path.Combine(folder, name), text);
            }
            return folder;
        }

        /// <summary>Every file as written, whatever a test saved, and the folders a test added gone.</summary>
        public void Reset()
        {
            foreach (string folder in added)
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            added.Clear();
            foreach ((string path, string text) in Files)
            {
                string full = Path.Combine(Folder, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
        }

        /// <summary>A window on the project with a file open, its servers started.</summary>
        public MainWindow Open(string file)
        {
            MainWindow window = Ui.OpenFolder(Folder, autosaves: false);
            window.OpenFile(PathOf(file));
            Ui.Settle();
            return window;
        }

        /// <summary>A Yarn Plug'n'Play project written in a folder and laid out by the tests' own Yarn, offline: its every
        /// package one of the tests' own, linked where it stands (Yarn's fallback giving each the others), its eslint
        /// configuration, a script and one to format.</summary>
        public static void LaidOutByYarn(string folder)
        {
            string packages = PathNames.Canonical(Path.Combine(AppContext.BaseDirectory, "node-packages", "node_modules"));
            IEnumerable<string> names = Directory.EnumerateDirectories(packages)
                .SelectMany(d => Path.GetFileName(d).StartsWith('@') ? Directory.EnumerateDirectories(d).Select(s => $"{Path.GetFileName(d)}/{Path.GetFileName(s)}") : [Path.GetFileName(d)])
                .Where(n => !n.StartsWith('.') && !n.StartsWith("@yarnpkg/", StringComparison.Ordinal) && !n.StartsWith("@xterm/", StringComparison.Ordinal) && n != "prettier2")
                .Order(StringComparer.Ordinal);
            string links = string.Join(",\n", names.Select(n => $"    {System.Text.Json.JsonSerializer.Serialize(n)}: {System.Text.Json.JsonSerializer.Serialize("link:" + Path.Combine(packages, n).Replace('\\', '/'))}"));
            File.WriteAllText(Path.Combine(folder, "package.json"), $"{{\n  \"name\": \"pnp-shop\",\n  \"private\": true,\n  \"type\": \"module\",\n  \"dependencies\": {{\n{links}\n  }}\n}}\n");
            File.WriteAllText(Path.Combine(folder, ".yarnrc.yml"), "nodeLinker: pnp\nenableNetwork: false\nenableTelemetry: false\nenableGlobalCache: false\n");
            File.WriteAllText(Path.Combine(folder, "yarn.lock"), string.Empty);
            File.WriteAllText(Path.Combine(folder, "eslint.config.js"), Config);
            Directory.CreateDirectory(Path.Combine(folder, "src"));
            File.WriteAllText(Path.Combine(folder, "src", "app.js"), App);
            File.WriteAllText(Path.Combine(folder, "src", "format.js"), Format);
            string yarn = Path.Combine(packages, "@yarnpkg", "cli-dist", "bin", "yarn.js");
            System.Diagnostics.ProcessStartInfo install = ChildProcesses.Command(Programs.Node()!, [yarn, "install"], folder,
                [new("YARN_GLOBAL_FOLDER", Path.Combine(folder, ".yarn", "global")), new("YARN_ENABLE_NETWORK", "0")]);
            ChildResult laid = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(install, limit: TimeSpan.FromMinutes(2).Scaled())), "Yarn to lay the project out");
            Assert.True(laid.Succeeded, $"Yarn could not lay {folder} out ({ExitCodes.Said(laid.ExitCode)}): {laid.Output}{laid.Error}");
        }

        /// <summary>Skips a test where there is no Node.js to run ESLint and Prettier on, or one older than the tests' eslint
        /// needs.</summary>
        public static void SkipWithoutNode()
        {
            Assert.SkipWhen(Programs.Node() is null, NoNode);
            string? old = Ui.Await(Waiting.Apart(() => TooOldAsync()), "the PATH's Node.js to say its version");
            Assert.SkipWhen(old is not null, $"ESLint cannot run here: {old}");
        }

        /// <inheritdoc cref="SkipWithoutNode"/>
        public static async Task SkipWithoutNodeAsync()
        {
            Assert.SkipWhen(Programs.Node() is null, NoNode);
            string? old = await TooOldAsync();
            Assert.SkipWhen(old is not null, $"ESLint cannot run here: {old}");
        }

        private const string NoNode = "Node.js is not on the PATH, and ESLint and Prettier run on it";

        /// <summary>Why the PATH's Node.js cannot run the tests' ESLint, or null when it can.</summary>
        private static async Task<string?> TooOldAsync()
        {
            NodeRuntime node = await NodeRuntime.FindAsync(null);
            return node.Problem ?? node.TooOldFor(Path.Combine(AppContext.BaseDirectory, "node-packages", "node_modules", "eslint", "package.json"), "ESLint");
        }

        /// <summary>Skips a test where the tests' packages hold no build of a tool for this system (a folder under
        /// <paramref name="scope"/>, its name starting <paramref name="prefix"/>), which the build fetches only where npm has one.</summary>
        public static void SkipWithout(string tool, string scope, string prefix = "binding-") =>
            Assert.SkipWhen(!Directory.Exists(Path.Combine(Packages, scope)) || !Directory.EnumerateDirectories(Path.Combine(Packages, scope), prefix + "*").Any(),
                $"{tool} has no build for this system among the tests' packages ({Path.Combine(Packages, scope)}), so it cannot run here");

        /// <summary>Files written into a test's folder, by their paths from it.</summary>
        public static void Write(string folder, params (string Path, string Text)[] files)
        {
            foreach ((string path, string text) in files)
            {
                string full = Path.Combine(folder, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
        }

        /// <summary>Some of the tests' packages (top-level folders of node_modules, a scope's whole) copied into a folder's
        /// node_modules.</summary>
        public static void CopyPackages(string folder, params string[] packages)
        {
            foreach (string package in packages)
            {
                CopyFolder(Path.Combine(Packages, package), Path.Combine(folder, "node_modules", package));
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

        public void Dispose() => TestFolders.Delete(Folder);
    }
}
