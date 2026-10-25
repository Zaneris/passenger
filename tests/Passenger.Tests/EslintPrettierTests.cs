using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>ESLint and Prettier through the window, on a project with its own eslint and prettier in node_modules (the
/// tests' packages, pinned in NodePackages/ and put in place at build, never fetched from npm as the tests run) and its own
/// eslint.config.js, .prettierrc and .prettierignore: problems as typed and their fixes, Fix ESLint Problems, Prettier on
/// Reformat Code and on save, .prettierignore honoured, and the banner offering npm install for what is not installed.
/// Each test opens its own window; one that cannot find Node.js skips, saying so.</summary>
public sealed class EslintPrettierTests : IClassFixture<EslintPrettierTests.Project>
{
    private const string PreferConst = "ESLint: 'total' is never reassigned. Use 'const' instead. (prefer-const)";

    private readonly Project project;

    public EslintPrettierTests(Project project) => this.project = project;

    [AvaloniaFact]
    public void An_ESLint_problem_shows_as_it_is_typed_in_the_editor_the_stripe_and_Problems_with_its_rules_page_and_its_fix_makes_it_go()
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
            Button rule = window.ProblemsWindow.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("link") && (string?)b.Content == "prefer-const");
            Ui.Click(rule);
            Assert.Equal(["https://eslint.org/docs/latest/rules/prefer-const"], window.Browsed.Select(u => u.AbsoluteUri));

            Ui.PlaceAfter(editor, "let tot");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the fixes for the line");
            Assert.Equal("ESLint: Fix 'prefer-const'", window.Assist.Offered[0]);
            Assert.Contains("ESLint: Suppress 'prefer-const' for current line", window.Assist.Offered);
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("const total = items.length;", StringComparison.Ordinal), "the let made a const by ESLint's fix");
            Ui.WaitFor(() => !document.Markers.Any(m => m.Message == PreferConst), "the problem gone once the code is fixed");
            Ui.Run(window, EditorActions.Undo);
            Assert.Contains("let total = items.length;", editor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            project.Reset();
        }
    }

    [AvaloniaFact]
    public void A_project_with_eslint_and_no_configuration_says_so_in_the_status_bar_and_logs_no_error()
    {
        Project.SkipWithoutNode();
        File.Delete(Path.Combine(project.Folder, "eslint.config.js"));
        IdeErrors.Flush();
        int logged = Ui.Shared(IdeErrors.LogPath).Length;
        MainWindow window = project.Open("src/app.js");
        try
        {
            // The server logs what it found before it tells the file's status, which the status bar says.
            Ui.WaitFor(() => window.Status == "ESLint: no configuration found for app.js, so it is not checked", "the status bar to say app.js has no ESLint configuration");
            IdeErrors.Flush();
            string log = Ui.Shared(IdeErrors.LogPath);
            string since = log.Length >= logged ? log[logged..] : log;
            Assert.False(since.Contains("Could not find config file", StringComparison.Ordinal) || since.Contains("An unexpected error occurred", StringComparison.Ordinal),
                $"A project with eslint and no configuration put an ESLint error in passenger.log: {string.Join('\n', since.Split('\n').Where(l => l.Contains("ESLint", StringComparison.Ordinal)).Take(5))}");
        }
        finally
        {
            window.Close();
            project.Reset();
        }
    }

    [AvaloniaFact]
    public void A_file_whose_eslint_needs_a_later_Node_js_says_why_it_is_not_checked_and_the_rest_are_checked_still()
    {
        Project.SkipWithoutNode();
        project.AddFolder("future", ("app.js", "let total = 1;\nconsole.log(total);\n"));
        project.AddFolder("future/node_modules/eslint", ("package.json", "{ \"name\": \"eslint\", \"version\": \"99.0.0\", \"engines\": { \"node\": \">=99\" } }\n"));
        NodeRuntime node = Task.Run(() => NodeRuntime.FindAsync(null)).GetAwaiter().GetResult();
        MainWindow window = project.Open("future/app.js");
        try
        {
            string said = $"ESLint cannot check app.js: ESLint needs Node.js 99.0.0 or later, and {node.Path} is {node.Version}: update it, or name a later one in {NodeRuntime.SettingsPlace}";
            Ui.WaitFor(() => window.Status == said, () => $"the status bar to say the file's eslint needs a later Node.js (it says '{window.Status}')");
            Assert.DoesNotContain(window.EditorArea.Active!.Markers, m => m.Message.StartsWith("ESLint:", StringComparison.Ordinal));

            window.OpenFile(project.PathOf("src/app.js"));
            Ui.Settle();
            OpenDocument checkedStill = window.EditorArea.Active!;
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "console.log(items);");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("let total = items.length;");
            Ui.Press(window, "ENTER");
            window.KeyTextInput("console.log(total);");
            Ui.WaitFor(() => checkedStill.Markers.Any(m => m.Message == PreferConst), "ESLint's problem in the file whose eslint runs on this Node.js");
        }
        finally
        {
            window.Close();
            project.Reset();
        }
    }

    [AvaloniaFact]
    public void Fix_ESLint_Problems_fixes_the_whole_file_and_a_save_fixes_first_when_Settings_ask_and_Disabled_takes_ESLints_problems_away()
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

    [AvaloniaFact]
    public void Prettier_formats_on_Reformat_Code_and_on_save_by_the_projects_own_configuration_as_one_undo_and_leaves_what_prettierignore_names()
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

    [AvaloniaFact]
    public void Each_file_Prettier_formats_gets_its_own_text_back_though_its_configuration_prints_to_standard_output()
    {
        Project.SkipWithoutNode();
        project.AddFolder("src/noisy",
            (".prettierrc.cjs", "console.log(\"the noisy configuration loads\");\nmodule.exports = { semi: false, singleQuote: true };\n"),
            ("first.js", "const  first = {name:\"tea\"};\nlet  more = 1;\n"),
            ("second.js", "const  second = [1,2,3];\nconsole.log( second );\n"));
        MainWindow window = project.Open("src/noisy/first.js");
        try
        {
            ReformatWithPrettier(window);
            Assert.Equal("Prettier: formatted 2 lines", window.Status);
            Assert.Equal("const first = { name: 'tea' }\nlet more = 1\n", window.EditorArea.CodeEditor.Document.Snapshot.ToString());

            window.OpenFile(project.PathOf("src/noisy/second.js"));
            Ui.Settle();
            ReformatWithPrettier(window);
            string second = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
            Assert.True(second == "const second = [1, 2, 3]\nconsole.log(second)\n", $"second.js was not given its own text by Prettier:\n{second}");
            Assert.Contains("the noisy configuration loads", Ui.IdeLog(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            project.Reset();
        }
    }

    [AvaloniaFact]
    public void Prettier_2_formats_as_Prettier_3_does_and_leaves_what_prettierignore_names()
    {
        Project.SkipWithoutNode();
        MainWindow window = project.Open("src/format.js");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.ApplyEdits([new TextEdit(editor.Document.Snapshot.Length, 0, "console.log(label, \"the price of a pot of tea\", \"with milk and sugar on the side\");\n")]);
            // Each puts its own default trailing commas in the call it wraps: Prettier 3 all, Prettier 2 none after arguments.
            const string formatted = "const label = { name: 'tea', price: 3 }\nconsole.log(label)\nconsole.log(\n  label,\n  'the price of a pot of tea',\n  'with milk and sugar on the side'";
            ReformatWithPrettier(window);
            Assert.Equal(formatted + ",\n)\n", editor.Document.Snapshot.ToString());
            Ui.Run(window, EditorActions.Undo);

            window.ApplySettings(window.Settings with { Prettier = ToolConfiguration.Manual, PrettierPackage = project.PathOf("node_modules/prettier2") });
            ReformatWithPrettier(window);
            Assert.True(editor.Document.Snapshot.ToString() == formatted + "\n)\n", $"Prettier 2 did not format the file under the .prettierignore ({window.Status}):\n{editor.Document.Snapshot}");
            window.OpenFile(project.PathOf("src/legacy.js"));
            Ui.Settle();
            ReformatWithPrettier(window);
            Assert.Equal("Prettier: file legacy.js was ignored with .prettierignore", window.Status);
            Assert.Equal(Project.Legacy, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
            project.Reset();
        }
    }

    [AvaloniaFact]
    public void Save_saves_the_file_it_was_pressed_on_though_another_tab_is_chosen_while_Prettier_formats_it()
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

    [AvaloniaFact]
    public void Save_saves_the_file_though_Prettiers_runner_ends_or_the_folder_closes_while_it_formats()
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
            using (Process runner = Process.GetProcessById(Processes.WrittenId(Path.Combine(held, "started"), "Prettier to start formatting first.js")))
            {
                runner.Kill();
            }
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

    [AvaloniaFact]
    public void ESLints_settings_changed_as_it_starts_leave_one_server_for_the_folder_which_ends_with_it()
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

    [AvaloniaFact]
    public void A_package_json_listing_ESLint_and_Prettier_not_installed_says_so_in_the_banner_whose_link_runs_npm_install_there_and_a_Manual_ESLint_checks_it()
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

    [AvaloniaFact]
    public void Settings_has_Riders_ESLint_and_Prettier_pages_at_their_defaults()
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false };
        window.Show();
        try
        {
            SettingsDialog dialog = window.ShowSettings();
            Ui.Settle();
            dialog.Query = "eslint";
            Ui.Settle();
            Assert.Contains(SettingsDialog.EslintPage, dialog.Pages);
            dialog.Query = string.Empty;
            dialog.Page = SettingsDialog.EslintPage;
            Ui.Settle();
            Assert.Equal(["Configuration", "ESLint package", "Run for files", "Run eslint --fix on save"], dialog.Shown);
            Assert.True(Radio(dialog, "Automatic ESLint configuration").IsChecked, "ESLint is not configured automatically by default, as in Rider");
            Assert.Equal(AppSettings.DefaultEslintFiles, Editor<TextBox>(dialog, "Run for files").Text);
            dialog.Page = SettingsDialog.PrettierPage;
            Ui.Settle();
            Assert.Equal(["Configuration", "Prettier package", "Run for files", "Run on 'Reformat Code' action", "Run on save"], dialog.Shown);
            Assert.True(Radio(dialog, "Automatic Prettier configuration").IsChecked, "Prettier is not configured automatically by default");
            Assert.Equal("**/*.{js,ts,jsx,tsx,cjs,cts,mjs,mts,json,vue,astro}", Editor<TextBox>(dialog, "Run for files").Text);
            Assert.True(Editor<CheckBox>(dialog, "Run on 'Reformat Code' action").IsChecked);
            Assert.False(Editor<CheckBox>(dialog, "Run on save").IsChecked);
            TextBox package = Editor<TextBox>(dialog, "Prettier package");
            Assert.False(package.IsEnabled, "Automatic configuration lets a package be named, which only Manual takes");
            Ui.Click(Radio(dialog, "Manual Prettier configuration"));
            Assert.True(package.IsEnabled, "Manual configuration did not let the package be named");
            dialog.Close();
        }
        finally
        {
            window.Close();
        }
    }

    private static RadioButton Radio(SettingsDialog dialog, string words) =>
        dialog.GetVisualDescendants().OfType<RadioButton>().First(r => (string?)r.Content == words);

    /// <summary>What edits an option, beside its label in the page shown.</summary>
    private static T Editor<T>(SettingsDialog dialog, string label) where T : Control =>
        ((Visual)dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == label).Parent!).GetVisualDescendants().OfType<T>().First();

    /// <summary>Reformat with Prettier on the file shown, done.</summary>
    private static void ReformatWithPrettier(MainWindow window)
    {
        Assert.True(window.Perform(AppActions.ReformatWithPrettier), "Reformat with Prettier did not run");
        Ui.Settle();
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
                string packages = Path.Combine(AppContext.BaseDirectory, "node-packages", "node_modules");
                Assert.True(Directory.Exists(Path.Combine(packages, "eslint")), $"The tests' own eslint and prettier are not at {packages}: the build puts them there");
                CopyFolder(packages, Path.Combine(Folder, "node_modules"));
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

        /// <summary>Skips a test where there is no Node.js to run ESLint and Prettier on, or one older than the tests' eslint
        /// needs.</summary>
        public static void SkipWithoutNode()
        {
            Assert.SkipWhen(Programs.Node() is null, "Node.js is not on the PATH, and ESLint and Prettier run on it");
            NodeRuntime node = Task.Run(() => NodeRuntime.FindAsync(null)).GetAwaiter().GetResult();
            string? old = node.Problem ?? node.TooOldFor(Path.Combine(AppContext.BaseDirectory, "node-packages", "node_modules", "eslint", "package.json"), "ESLint");
            Assert.SkipWhen(old is not null, $"ESLint cannot run here: {old}");
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
