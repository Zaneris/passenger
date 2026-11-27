using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Migrations;

namespace Passenger.Tests;

/// <summary>The Migrations window over a SQLite project's context, as a person uses it: its migrations listed with
/// which the database has, Add, Update (to the latest and back to one), Script into a tab; and what keeps dotnet ef
/// from working (no tool, no Design package, a context it cannot make) found before the command, fixed in place, and
/// the command then made. EF Core and dotnet-ef come from the package cache through the run's local feed, never
/// nuget.org.</summary>
public sealed class MigrationsTests
{
    /// <summary>The longest a build and a dotnet ef command take together on a developer's machine, scaled by the run.</summary>
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(2);

    /// <summary>The Migrations window adds, applies, rolls back and scripts migrations, and a missing tool, package or
    /// factory is found and made before the command.</summary>
    [AvaloniaFact]
    public void Migrations_run_against_the_database_and_their_tools_are_fixed_in_place()
    {
        Flow.Step(nameof(Migrations_are_added_applied_rolled_back_and_scripted_against_the_runs_database),
            Migrations_are_added_applied_rolled_back_and_scripted_against_the_runs_database);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_missing_tool_and_Design_package_are_found_before_the_command_fixed_in_place_and_the_command_made),
            A_missing_tool_and_Design_package_are_found_before_the_command_fixed_in_place_and_the_command_made);
        OpenWindows.CloseAll();
        Flow.Step(nameof(Typing_in_another_file_while_a_fix_adds_a_package_stays_that_files_own_undo),
            Typing_in_another_file_while_a_fix_adds_a_package_stays_that_files_own_undo);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_context_the_tools_cannot_make_is_said_with_why_and_a_design_time_factory_added_in_place_and_taken_back_by_undo),
            A_context_the_tools_cannot_make_is_said_with_why_and_a_design_time_factory_added_in_place_and_taken_back_by_undo);
    }

    /// <summary>A context's project, startup, environment and migrations folder are those dotnet ef runs with, and EF
    /// Core's packages move as one set with their provider, one step of Undo.</summary>
    [AvaloniaFact]
    public void Ef_cores_projects_and_packages_are_those_dotnet_ef_takes()
    {
        Flow.Step(nameof(The_library_declaring_the_context_is_the_project_and_the_web_app_running_it_the_first_startup),
            The_library_declaring_the_context_is_the_project_and_the_web_app_running_it_the_first_startup);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_provider_behind_EF_Core_is_said_with_both_versions_and_EF_Core_moved_to_what_it_supports_as_one_undo_step),
            A_provider_behind_EF_Core_is_said_with_both_versions_and_EF_Core_moved_to_what_it_supports_as_one_undo_step);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_provider_alignment_whose_restore_fails_puts_the_project_back_as_it_was_and_says_so),
            A_provider_alignment_whose_restore_fails_puts_the_project_back_as_it_was_and_says_so);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_provider_version_within_its_feeds_minimum_publish_age_is_never_what_brings_it_into_step),
            A_provider_version_within_its_feeds_minimum_publish_age_is_never_what_brings_it_into_step);
        OpenWindows.CloseAll();
        Flow.Step(nameof(The_environment_chosen_beside_the_startup_project_is_the_one_dotnet_ef_runs_in_kept_with_the_folder_and_a_typed_one_taken),
            The_environment_chosen_beside_the_startup_project_is_the_one_dotnet_ef_runs_in_kept_with_the_folder_and_a_typed_one_taken);
        OpenWindows.CloseAll();
        Flow.Step(nameof(The_NuGet_windows_update_of_EF_Core_past_its_provider_warns_and_updates_the_set_with_the_provider_together),
            The_NuGet_windows_update_of_EF_Core_past_its_provider_warns_and_updates_the_set_with_the_provider_together);
        OpenWindows.CloseAll();
        Flow.Step(nameof(Under_central_package_management_updating_the_set_moves_the_set_other_projects_reference_too),
            Under_central_package_management_updating_the_set_moves_the_set_other_projects_reference_too);
        OpenWindows.CloseAll();
        Flow.Step(nameof(Updating_one_of_EF_Cores_packages_moves_the_projects_others_with_it_as_one_undo_step_and_an_install_adds_one_alone),
            Updating_one_of_EF_Cores_packages_moves_the_projects_others_with_it_as_one_undo_step_and_an_install_adds_one_alone);
        OpenWindows.CloseAll();
        Flow.Step(nameof(With_EF_Core_11_the_window_takes_what_dotnet_ef_json_names_and_removes_a_migration_offline),
            With_EF_Core_11_the_window_takes_what_dotnet_ef_json_names_and_removes_a_migration_offline);
        OpenWindows.CloseAll();
        Flow.Step(nameof(Each_contexts_migrations_go_into_the_folder_given_are_listed_from_it_and_the_last_removed_from_there),
            Each_contexts_migrations_go_into_the_folder_given_are_listed_from_it_and_the_last_removed_from_there);
    }

    /// <summary>The Migrations window's commands as a person makes them, dotnet ef's lines in the Build window as a program's
    /// output (what it answers, its <c>data:</c> lines, left out of it).</summary>
    private void Migrations_are_added_applied_rolled_back_and_scripted_against_the_runs_database()
    {
        string folder = ShopApp.Make(design: true, tool: ShopApp.Version);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        System.Text.StringBuilder programs = new();
        window.BottomPanel.Build.Written += (_, written) => programs.Append(written.Text);
        using EfRuns runs = new(folder);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Migrations);
            Ui.WaitFor(() => window.Migrations.Listed, $"the shop's migrations listed (status: {window.Status}; gaps: {Gaps(window)})", Slow);
            Assert.True(window.Migrations.Migrations.Count == 0, $"A context with no migration listed {window.Migrations.Migrations.Count}");
            Assert.Equal("ShopContext", window.Migrations.Context);

            runs.Ran("Listing the migrations as the window is first shown", "dbcontext list", "migrations list");
            Add(window, "Initial");
            Assert.Equal([("Initial", (bool?)false)], Rows(window));
            window.BottomPanel.Build.Flush();
            Assert.DoesNotContain(window.BottomPanel.Build.Document.Snapshot.ToString().Split('\n'), line => EfRun.Split(line).Kind == "data");
            runs.Ran("Adding the first migration", "migrations add");

            Tool(window, AppActions.UpdateDatabase);
            Ui.WaitFor(() => Rows(window) is [(_, true)], $"Initial applied to the database ({window.Status})", Slow);
            window.BottomPanel.Build.Flush();
            string console = window.BottomPanel.Build.Document.Snapshot.ToString();
            Assert.True(programs.ToString().Split('\n').Any(line => EfRun.Split(line).Kind is "info" or "warn"),
                $"dotnet ef's lines did not reach the Build window as a program's output, which was:\n{programs}\nThe window's:\n{console}");
            runs.Ran("Updating the database after Add Migration", "database update");

            // The context's database, written in its code, opens in the Database window down to the migrated table.
            Tool(window, AppActions.MigrationsDatabase);
            Ui.WaitFor(() => window.Database.Sources.Any(s => s.Found == "used by ShopContext's migrations") && !window.Database.Busy
                && window.DatabaseExplorer.Rows.Any(r => r.Title == "Tables"), () => $"the context's database opened ({window.Status}; {window.Database.Said}; databases: {string.Join(", ", Directory.GetFiles(folder, "*.db", SearchOption.AllDirectories))}; "
                + $"opened: {string.Join(", ", window.Database.Sources.Select(s => $"{s.Name} {s.Found} {s.Folder}"))})", Slow);
            Passenger.App.Database.DatabaseSource used = window.Database.Sources.Single(s => s.Found == "used by ShopContext's migrations");
            Assert.Equal(("Shop", Passenger.App.Database.DatabaseKind.Sqlite, "shop.db"), (used.Name, used.Kind, used.Database));
            DatabaseTests.Open(window, "Tables");
            Assert.Contains(window.DatabaseExplorer.Rows, r => r.Title == "Products");
            Assert.True(window.Perform(AppActions.DatabaseRefresh), $"Refresh Database Connections did not run: {window.Status}");
            Ui.WaitFor(() => window.Database.Discovered && !window.Database.Busy, () => window.Database.Said);
            Assert.Contains(window.Database.Sources, s => s.Id == used.Id);

            // A model change no migration covers, which the build after it says in Problems at the context.
            ShopApp.AddPrice(folder);
            Ui.Run(window, AppActions.Build);
            string said = "ShopContext's model has changes no migration covers: Add Migration… makes one";
            Passenger.App.ToolWindows.ProblemsModel problems = window.BottomPanel.Problems;
            Ui.WaitFor(() => problems.Problems.Any(p => p.Message == said) && window.Migrations.Busy is null, $"the pending model changes in Problems after the build ({window.Status})", Slow);
            runs.Ran("Reading the migrations again after a build that changed the model", "dbcontext list", "migrations list", "migrations has-pending-model-changes");
            Passenger.App.Run.Problem pending = problems.Problems.Single(p => p.Message == said);
            Assert.Equal(Passenger.Core.PathNames.Canonical(Path.Combine(folder, "Program.cs")), pending.Path);
            window.ShowToolWindow(ToolWindowIds.Problems);
            Ui.Settle();
            ListBox found = window.ProblemsWindow.GetVisualDescendants().OfType<ListBox>().First();
            found.SelectedItem = problems.Rows.Single(r => r.Problem == pending);
            Ui.ChooseFromMenu(found, "Show Context Actions");
            Ui.WaitFor(() => window.Assist.Offered.Contains("Add Migration…"), "Add Migration… offered at the context");
            Assert.Equal("Add Migration…", window.Assist.Offered[0]);
            Ui.Press(window, "ENTER");
            Name(window, "AddPrice");
            Ui.WaitFor(() => window.Migrations.Busy is null && Rows(window) is [(_, true), ("AddPrice", false)], $"AddPrice added and listed ({window.Status})", Slow);
            runs.Ran("Adding a migration from Problems", "migrations add");
            Assert.DoesNotContain(problems.Problems, p => p.Message == said);
            Tool(window, AppActions.UpdateDatabase);
            Ui.WaitFor(() => Rows(window) is [(_, true), (_, true)], $"both applied ({window.Status})", Slow);
            runs.Ran("Updating the database after Add Migration", "database update");

            ListBox list = window.MigrationsView.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "MigrationsList");
            Ui.RightClick(Assert.IsType<ListBoxItem>(list.ContainerFromItem(window.Migrations.Migrations.Single(m => m.Name == "Initial"))));
            Ui.Choose(list.ContextMenu, "Update Database to Initial");
            ChoiceDialog asked = Asked<ChoiceDialog>(window, "the roll back's question");
            Assert.Contains("AddPrice", string.Join(' ', asked.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), StringComparison.Ordinal);
            Ui.ClickButton(asked, "Roll Back");
            Ui.WaitFor(() => Rows(window) is [(_, true), (_, false)], $"AddPrice rolled back ({window.Status})", Slow);
            runs.Ran("Rolling the database back to a listed migration", "database update");

            Tool(window, AppActions.ScriptMigrations);
            MigrationScriptDialog script = Asked<MigrationScriptDialog>(window, "Script Migrations' question");
            Ui.ClickButton(script, "Script");
            Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith(".sql", StringComparison.Ordinal) == true, $"the script open in a tab ({window.Status})", Slow);
            runs.Ran("Scripting the migrations", "migrations script");
            string sql = window.EditorArea.Active!.Text.Snapshot.ToString();
            Assert.Contains("CREATE TABLE \"Products\"", sql, StringComparison.Ordinal);
            Assert.Contains("\"Price\"", sql, StringComparison.Ordinal);

            Tool(window, AppActions.RemoveLastMigration);
            ChoiceDialog remove = Asked<ChoiceDialog>(window, "Remove Last's question");
            Assert.Contains("AddPrice", string.Join(' ', remove.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), StringComparison.Ordinal);
            Ui.ClickButton(remove, "Remove");
            // Not settled: the build and model check after it are for Problems, which this does not read.
            Ui.PumpUntil(() => Rows(window) is [("Initial", true)], $"AddPrice removed and the list read again ({window.Status})", Slow);
            Assert.Empty(Directory.GetFiles(Path.Combine(folder, "Migrations"), "*_AddPrice*.cs"));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The tool's manifest the fix makes is made as the folder's .editorconfig asks, CRLF and a UTF-8 BOM, which
    /// dotnet tool's own write of it keeps.</summary>
    private void A_missing_tool_and_Design_package_are_found_before_the_command_fixed_in_place_and_the_command_made()
    {
        string folder = ShopApp.Make(design: false, tool: null);
        TestFolders.Write(folder, ".editorconfig", "root = true\n\n[dotnet-tools.json]\nend_of_line = crlf\ncharset = utf-8-bom\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        List<string> opened = [];
        window.EditorArea.Opened += (_, document) => opened.Add($"{Path.GetFileName(document.Path)}, opened from:\n"
            + string.Join("\n", new System.Diagnostics.StackTrace().ToString().Split('\n').Where(f => f.Contains(" Passenger.", StringComparison.Ordinal)).Take(12)));
        try
        {
            // Found as the projects are read, the window hidden.
            string found = string.Empty;
            Ui.WaitFor(() => (found = string.Join(" | ", window.Migrations.Gaps.Select(g => g.Said))).Contains("dotnet ef is not installed here", StringComparison.Ordinal)
                && found.Contains("lacks Microsoft.EntityFrameworkCore.Design", StringComparison.Ordinal), () => $"both gaps found with the window hidden ({found})");
            Assert.False(window.DockHost[ToolWindowIds.Migrations].IsBuilt, "Finding what dotnet ef lacks built the hidden Migrations window's view");
            window.ShowToolWindow(ToolWindowIds.Migrations);
            Ui.WaitFor(() => Gaps(window).Contains("dotnet ef is not installed here", StringComparison.Ordinal)
                && Gaps(window).Contains("lacks Microsoft.EntityFrameworkCore.Design", StringComparison.Ordinal), $"both gaps shown before any command ({Gaps(window)})");
            Assert.False(window.Migrations.Listed, "The window ran dotnet ef with the tool missing");

            Tool(window, AppActions.AddMigration);
            Name(window, "Initial");
            ChoiceDialog tool = Asked<ChoiceDialog>(window, "the missing tool's question");
            Ui.ClickButton(tool, $"Pin the Local dotnet-ef to {ShopApp.Version}");
            ChoiceDialog design = Asked<ChoiceDialog>(window, "the missing Design package's question", Slow);
            Ui.ClickButton(design, $"Add Microsoft.EntityFrameworkCore.Design {ShopApp.Version}");
            Ui.WaitFor(() => Directory.Exists(Path.Combine(folder, "Migrations")) && Directory.GetFiles(Path.Combine(folder, "Migrations"), "*_Initial.cs").Length == 1,
                $"the migration made once both were fixed ({window.Status}; gaps: {Gaps(window)})", Slow);

            JsonObject manifest = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "dotnet-tools.json")))!;
            Assert.Equal(ShopApp.Version, manifest["tools"]!["dotnet-ef"]!["version"]!.GetValue<string>());
            InForm(Path.Combine(folder, "dotnet-tools.json"), "its .editorconfig asks for");
            string project = File.ReadAllText(Path.Combine(folder, "Shop.csproj"));
            Assert.Contains($"<PackageReference Include=\"Microsoft.EntityFrameworkCore.Design\" Version=\"{ShopApp.Version}\"", project, StringComparison.Ordinal);
            Assert.Contains("<PrivateAssets>all</PrivateAssets>", project, StringComparison.Ordinal);
            Ui.WaitFor(() => Gaps(window).Length == 0, $"no gap left ({Gaps(window)})");

            UndoFromExplorer(window, Path.Combine(folder, "Shop.csproj"), () => string.Join("\n", opened));
            Assert.DoesNotContain("Microsoft.EntityFrameworkCore.Design", File.ReadAllText(Path.Combine(folder, "Shop.csproj")), StringComparison.Ordinal);
            UndoFromExplorer(window, Path.Combine(folder, "Shop.csproj"), () => string.Join("\n", opened));
            Assert.False(File.Exists(Path.Combine(folder, "dotnet-tools.json")), "Undo left the tool manifest the fix made");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The missing Design package's fix from the window's button while the person types in Program.cs as its install
    /// runs (held): Undo there takes back the typing alone, asking nothing, the package staying, and the fix's own Undo in
    /// the Solution view takes the package out and leaves the typing, where the fix's command took the typing as its own.</summary>
    private void Typing_in_another_file_while_a_fix_adds_a_package_stays_that_files_own_undo()
    {
        string folder = ShopApp.Make(design: false, tool: ShopApp.Version);
        string program = Passenger.Core.PathNames.Canonical(Path.Combine(folder, "Program.cs"));
        string project = Path.Combine(folder, "Shop.csproj");
        TaskCompletionSource installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(program);
            window.ShowToolWindow(ToolWindowIds.Migrations);
            string fix = $"Add Microsoft.EntityFrameworkCore.Design {ShopApp.Version}";
            Ui.WaitFor(() => Gaps(window).Contains("lacks Microsoft.EntityFrameworkCore.Design", StringComparison.Ordinal) && window.Migrations.Busy is null,
                $"the missing Design package found ({Gaps(window)})", Slow);
            Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
            string had = editor.Document.Snapshot.ToString();

            Task held = window.Session.Packages.HoldNextChange(installed.Task);
            Ui.ClickButton(window.MigrationsView, fix);
            ChoiceDialog asked = Asked<ChoiceDialog>(window, "the fix's question, before it reaches the feeds");
            Button add = asked.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content as string == fix);
            // Pumped, not settled: the package change holds the window's background work while its install is held.
            Ui.Click(add, settle: false);
            Ui.PumpUntil(() => held.IsCompleted, $"the Design package's install to be held ({window.Status})", Slow);
            Assert.Equal(program, window.EditorArea.Active?.Path);
            Ui.PlaceAfter(editor, "Console.WriteLine(context.Products.Count());");
            editor.Focus();
            window.KeyTextInput(" // typed meanwhile");
            string typed = editor.Document.Snapshot.ToString();
            Assert.Contains("Count()); // typed meanwhile", typed, StringComparison.Ordinal);
            installed.SetResult();
            Ui.WaitFor(() => File.ReadAllText(project).Contains("Microsoft.EntityFrameworkCore.Design", StringComparison.Ordinal) && window.Migrations.Busy is null,
                $"the Design package added once its install went on ({window.Status}; gaps: {Gaps(window)})", Slow);

            List<string> questions = [];
            window.Confirm = (_, question, _) =>
            {
                questions.Add(question);
                return Task.FromResult(true);
            };
            window.OpenFile(program);
            Ui.Settle();
            Ui.Run(window, Passenger.Editor.EditorActions.Undo);
            Assert.True(questions.Count == 0, $"Undo in Program.cs asked {string.Join(" | ", questions)}, as though the typing were the package add's");
            Assert.Equal(had, editor.Document.Snapshot.ToString());
            Assert.Contains("Microsoft.EntityFrameworkCore.Design", File.ReadAllText(project), StringComparison.Ordinal);
            Ui.Run(window, Passenger.Editor.EditorActions.Redo);
            Assert.Equal(typed, editor.Document.Snapshot.ToString());

            UndoFromExplorer(window, project);
            Assert.DoesNotContain("Microsoft.EntityFrameworkCore.Design", File.ReadAllText(project), StringComparison.Ordinal);
            Assert.Equal(typed, window.EditorArea.Find(program)?.Text.Snapshot.ToString());
        }
        finally
        {
            installed.TrySetResult();
            window.Close();
        }
    }

    /// <summary>An API beside the library holding its context, as a solution is often laid out (#2011): Project offers the
    /// library alone, chosen, never the web app that has only the Design package; Startup offers the web app that runs it
    /// first, then the library itself and the services library referencing it, never the domain library it references.
    /// Every project's C# files are read only once the window is shown.</summary>
    private void The_library_declaring_the_context_is_the_project_and_the_web_app_running_it_the_first_startup()
    {
        string folder = ShopApp.MakeLayered();
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Session.Projects is { Count: 4 } all && all.All(p => p.IsEvaluated),
                () => $"the four projects evaluated ({string.Join(", ", window.Session.Projects.Select(p => $"{p.Name}: {(p.IsEvaluated ? "evaluated" : p.Problem ?? "not yet")}"))})");
            Ui.Settle();
            Assert.True(window.Session.EfProjects.Whole is null,
                "Every project's C# files were read with the Migrations window never shown: each folder EF Core reaches would pay for a window nobody opened");
            window.ShowToolWindow(ToolWindowIds.Migrations);
            Ui.WaitFor(() => window.Session.EfProjects.IsWholeOf(window.Session.Projects), () => "what every project declares read once the window was shown");
            Ui.Settle();
            MigrationsController migrations = window.Migrations;
            Assert.True(Names(migrations.Projects) == "MyApp.Data" && migrations.Project?.Name == "MyApp.Data",
                $"Project offered [{Names(migrations.Projects)}], {migrations.Project?.Name ?? "none"} chosen: the library holding the context would not be where migrations are made");
            Assert.True(Names(migrations.Startups) == "MyApp.Api, MyApp.Data, MyApp.Services" && migrations.Startup?.Name == "MyApp.Api",
                $"Startup offered [{Names(migrations.Startups)}], {migrations.Startup?.Name ?? "none"} chosen: dotnet ef would not start the web app that runs the context");
            Assert.True(migrations.Declared is [{ Context: "AppDbContext" } declared] && Path.GetFileName(declared.File) == "AppDbContext.cs",
                $"The context was found declared as [{string.Join(", ", migrations.Declared)}]: Show Context Actions there would offer none of the window's fixes");
        }
        finally
        {
            window.Close();
        }
    }

    private static string Names(IEnumerable<Passenger.Workspace.ProjectModel> projects) => string.Join(", ", projects.Select(p => p.Name));

    private void A_provider_behind_EF_Core_is_said_with_both_versions_and_EF_Core_moved_to_what_it_supports_as_one_undo_step()
    {
        string folder = ShopApp.Make(design: true, tool: ShopApp.Version, provider: "1.0.0", providers: [("1.0.0", $"[{ShopApp.Older}]")]);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Migrations);
            string said = $"{ShopApp.Provider} 1.0.0 supports EF Core {ShopApp.Older}; Shop has EF Core {ShopApp.Version}";
            Ui.WaitFor(() => Gaps(window).Contains(said, StringComparison.Ordinal) && window.Migrations.Busy is null, $"the provider behind said with both versions ({Gaps(window)})", Slow);

            // No version of the provider supports EF Core's newest: EF Core's packages go to the newest it supports.
            string fix = $"Bring {ShopApp.Provider} and EF Core into Step";
            Ui.ClickButton(window.MigrationsView, fix);
            ChoiceDialog asked = Asked<ChoiceDialog>(window, "the fix's question, before it reads the feeds");
            Ui.ClickButton(asked, fix);
            string project = Path.Combine(folder, "Shop.csproj");
            Ui.WaitFor(() => File.ReadAllText(project).Contains($"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"{ShopApp.Older}\"", StringComparison.Ordinal)
                && File.ReadAllText(project).Contains($"Microsoft.EntityFrameworkCore.Design\" Version=\"{ShopApp.Older}\"", StringComparison.Ordinal)
                && !Gaps(window).Contains(said, StringComparison.Ordinal) && window.Migrations.Busy is null,
                $"EF Core's packages moved to {ShopApp.Older} and the gap gone ({window.Status}; gaps: {Gaps(window)})", Slow);
            Assert.Contains($"{ShopApp.Provider}\" Version=\"1.0.0\"", File.ReadAllText(project), StringComparison.Ordinal);

            UndoFromExplorer(window, Path.Combine(folder, "Shop.csproj"));
            Assert.Contains($"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"{ShopApp.Version}\"", File.ReadAllText(project), StringComparison.Ordinal);
            Assert.Contains($"Microsoft.EntityFrameworkCore.Design\" Version=\"{ShopApp.Version}\"", File.ReadAllText(project), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The provider's 5.0.0 supports the project's EF Core, but its feed published it within the 72 hours its
    /// minPublishAgeHours asks (#2341): bringing the provider into step leaves it at 1.0.0 and moves EF Core to what that
    /// supports, as with no newer provider at all, where without the age it would have taken the provider to 5.0.0.</summary>
    private void A_provider_version_within_its_feeds_minimum_publish_age_is_never_what_brings_it_into_step()
    {
        string folder = ShopApp.Make(design: true, tool: ShopApp.Version, provider: "1.0.0", providers: [("1.0.0", $"[{ShopApp.Older}]"), ("5.0.0", $"[{ShopApp.Version}]")],
            young: "5.0.0");
        string project = Path.Combine(folder, "Shop.csproj");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Migrations);
            string said = $"{ShopApp.Provider} 1.0.0 supports EF Core {ShopApp.Older}";
            Ui.WaitFor(() => Gaps(window).Contains(said, StringComparison.Ordinal) && window.Migrations.Busy is null, $"the provider behind ({Gaps(window)})", Slow);
            EfFix align = window.Migrations.Gaps.SelectMany(g => g.Fixes).First(f => f.Kind == EfFixKind.AlignProvider);
            Task<string?> fixing = window.Migrations.FixAsync(align);
            Ui.ClickButton(Asked<ChoiceDialog>(window, "the fix's question"), align.Title);
            string? why = Ui.Await(fixing, "the alignment's end", Slow);
            string written = File.ReadAllText(project);
            Assert.True(why is null && written.Contains($"{ShopApp.Provider}\" Version=\"1.0.0\"", StringComparison.Ordinal)
                && written.Contains($"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"{ShopApp.Older}\"", StringComparison.Ordinal),
                $"Bringing the provider into step with 5.0.0 too new for its feed said \"{why}\" and left the project as:\n{written}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The provider's version that supports the project's EF Core needs a package no feed holds, so the alignment's
    /// restore fails: the project is put back as it was and restored again, the fix saying it failed and that the project
    /// was put back, the provider still behind and nothing left for Undo.</summary>
    private void A_provider_alignment_whose_restore_fails_puts_the_project_back_as_it_was_and_says_so()
    {
        // A version no other test makes: the package cache keeps a provider by its id and version.
        string folder = ShopApp.Make(design: true, tool: ShopApp.Version, provider: "1.0.0",
            providers: [("1.0.0", $"[{ShopApp.Older}]"), ("3.0.0", $"[{ShopApp.Version}]")], unrestorable: "3.0.0");
        string project = Path.Combine(folder, "Shop.csproj");
        byte[] before = File.ReadAllBytes(project);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Migrations);
            string said = $"{ShopApp.Provider} 1.0.0 supports EF Core {ShopApp.Older}";
            Ui.WaitFor(() => Gaps(window).Contains(said, StringComparison.Ordinal) && window.Migrations.Busy is null, $"the provider behind ({Gaps(window)})", Slow);
            EfFix align = window.Migrations.Gaps.SelectMany(g => g.Fixes).First(f => f.Kind == EfFixKind.AlignProvider);
            Task<string?> fixing = window.Migrations.FixAsync(align);
            Ui.ClickButton(Asked<ChoiceDialog>(window, "the fix's question"), align.Title);
            string? why = Ui.Await(fixing, "the alignment's end", Slow);

            Assert.True(File.ReadAllBytes(project).AsSpan().SequenceEqual(before), $"The alignment whose restore failed left the project as:\n{File.ReadAllText(project)}\n(it said: {why})");
            Assert.True(why is not null && why.EndsWith("; Shop.csproj put back as before", StringComparison.Ordinal),
                $"The alignment whose restore failed said \"{why}\", not that it failed and put the project back");
            Ui.WaitFor(() => Gaps(window).Contains(said, StringComparison.Ordinal) && window.Migrations.Busy is null,
                $"the provider still behind once the project was restored again ({Gaps(window)})", Slow);
            Assert.True(window.EditorArea.Commands.ToUndo(null) is null, $"The failed alignment left {window.EditorArea.Commands.ToUndo(null)?.Name} for Undo");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A context dotnet ef cannot make is said with why, and the design-time factory added for it reads the
    /// context's own connection from the configuration by its name: with Reporting's string (a production password in it)
    /// configured before Shop's, ShopContextFactory reads Shop and holds no connection string, Reporting's or another, made
    /// with the CRLF and UTF-8 BOM its .editorconfig asks; dotnet ef then makes the context through it, and Undo takes the
    /// factory back.</summary>
    private void A_context_the_tools_cannot_make_is_said_with_why_and_a_design_time_factory_added_in_place_and_taken_back_by_undo()
    {
        string folder = ShopApp.Make(design: true, tool: ShopApp.Version, program: Unregistered);
        TestFolders.Write(folder, ".editorconfig", "root = true\n\n[*Factory.cs]\nend_of_line = crlf\ncharset = utf-8-bom\n");
        const string Production = "Server=reporting.example;Database=reports;User Id=sa;Password=Pr0duction-Secret";
        TestFolders.Write(folder, "appsettings.json", $$"""
            {
              "ConnectionStrings": {
                "Reporting": "{{Production}}",
                "Shop": "Data Source=shop.db"
              }
            }

            """);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Migrations);
            string said = "dotnet ef cannot make ShopContext: no host in Shop registers it, and it has no parameterless constructor or design-time factory";
            Ui.WaitFor(() => Gaps(window).Contains(said, StringComparison.Ordinal) && window.Migrations.Busy is null, $"why the context cannot be made ({window.Status}; gaps: {Gaps(window)})", Slow);

            Ui.ClickButton(window.MigrationsView, "Add a Design-Time Factory for ShopContext");
            string factory = Path.Combine(folder, "ShopContextFactory.cs");
            Ui.WaitFor(() => File.Exists(factory), "the factory written");
            Assert.Contains("IDesignTimeDbContextFactory<ShopContext>", File.ReadAllText(factory), StringComparison.Ordinal);
            InForm(factory, "its .editorconfig asks for");
            string made = File.ReadAllText(factory);
            Assert.True(made.Contains("options.UseSqlite(connection)", StringComparison.Ordinal) && made.Contains("ConnectionString(\"Shop\")", StringComparison.Ordinal),
                $"The factory does not read ShopContext's own connection, Shop, from the configuration:\n{made}");
            Assert.True(!made.Contains("Reporting", StringComparison.Ordinal) && !made.Contains("Password", StringComparison.Ordinal) && !made.Contains("Data Source", StringComparison.Ordinal),
                $"The factory holds a connection string, Reporting's or another, where it should read Shop's from the configuration:\n{made}");
            Assert.True(window.Status?.Contains("reads the connection string Shop", StringComparison.Ordinal) == true, $"Adding the factory said \"{window.Status}\"");

            Tool(window, AppActions.MigrationsRefresh);
            Ui.WaitFor(() => window.Migrations.Listed && window.Migrations.Busy is null, $"the migrations listed through the factory ({window.Status}; gaps: {Gaps(window)})", Slow);
            Assert.DoesNotContain("cannot make", Gaps(window), StringComparison.Ordinal);

            UndoFromExplorer(window, Path.Combine(folder, "Shop.csproj"));
            Assert.False(File.Exists(factory), "Undo left the factory it added");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A shop whose ShopContext no host registers and which has no parameterless constructor, so dotnet ef cannot
    /// make it without a design-time factory.</summary>
    private const string Unregistered = """
        using Microsoft.EntityFrameworkCore;

        using ShopContext context = new(new DbContextOptionsBuilder<ShopContext>().UseSqlite("Data Source=shop.db").Options);
        Console.WriteLine(context.Products.Count());

        public sealed class Product
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }

        public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
        {
            public DbSet<Product> Products => Set<Product>();
        }

        """;

    /// <summary>The Environment chooser offers what the startup project's settings files and launch profiles name, the run
    /// configuration's first, said as such. Staging chosen is what every dotnet ef runs in, given in its environment alone:
    /// the factory added reads the connection appsettings.Staging.json alone names, and Add and Update make Staging's
    /// database through it. Kept with the folder, its migrations are listed from that database once opened again. A name
    /// typed is taken, one that names no environment put back.</summary>
    private void The_environment_chosen_beside_the_startup_project_is_the_one_dotnet_ef_runs_in_kept_with_the_folder_and_a_typed_one_taken()
    {
        string folder = ShopApp.Make(design: true, tool: ShopApp.Version, program: Unregistered);
        TestFolders.Write(folder, "Properties/launchSettings.json", """
            {
              "profiles": {
                "Shop": {
                  "commandName": "Project",
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development"
                  }
                }
              }
            }

            """);
        TestFolders.Write(folder, "appsettings.Development.json", "{\n  \"ConnectionStrings\": {\n    \"Audit\": \"Data Source=audit.db\"\n  }\n}\n");
        TestFolders.Write(folder, "appsettings.Staging.json", "{\n  \"ConnectionStrings\": {\n    \"ShopContext\": \"Data Source=staging.db\"\n  }\n}\n");
        string settings = TestFolders.Create("migrations-settings-");
        // The environment each dotnet ef command was given, and whether its command line named one.
        List<string> given = [];
        string under = Passenger.Core.PathNames.Canonical(folder);
        EventHandler<Passenger.Core.ChildStart> heard = (_, child) =>
        {
            if (child.Start is { } start && start.ArgumentList.Any(a => a.Contains(under, StringComparison.Ordinal))
                && start.ArgumentList.Any(a => a is "--startup-project" or "--startup-assembly"))
            {
                lock (given)
                {
                    string? Variable(string name) => start.Environment.TryGetValue(name, out string? value) ? value : null;
                    given.Add($"{Variable(Passenger.Core.AspNetCoreVariables.AspNetCoreEnvironment)}/{Variable(Passenger.Core.AspNetCoreVariables.DotnetEnvironment)}"
                        + (start.ArgumentList.Any(a => a is "--environment" or "-e") ? " on its command line" : string.Empty));
                }
            }
        };
        Passenger.Core.ChildProcesses.Started += heard;
        try
        {
            MainWindow window = Keeping(settings, folder);
            try
            {
                window.ShowToolWindow(ToolWindowIds.Migrations);
                Ui.WaitFor(() => Gaps(window).Contains("dotnet ef cannot make ShopContext", StringComparison.Ordinal) && window.Migrations.Busy is null,
                    () => $"why the context cannot be made ({window.Status}; gaps: {Gaps(window)})", Slow);
                ComboBox chooser = Environments(window);
                Ui.WaitFor(() => Offered(chooser) == "Development (the run configuration's), Staging", () => $"Development and Staging offered, where the chooser offers {Offered(chooser)}");
                Assert.Equal("Development", chooser.Text);
                Assert.True(Taken(given).All(g => g == "Development/Development"), $"Listing in the run configuration's environment gave dotnet ef {string.Join(", ", Taken(given))}");

                Open(chooser);
                ComboBoxItem first = Assert.IsType<ComboBoxItem>(chooser.ContainerFromIndex(0));
                string said = string.Join(" ", first.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
                Assert.True(said == "Development the run configuration's", $"The default's row says \"{said}\", not that it is the run configuration's");
                Ui.Click(Assert.IsType<ComboBoxItem>(chooser.ContainerFromIndex(1)));
                Ui.WaitFor(() => window.Migrations.Environment?.Name == "Staging" && window.Migrations.Busy is null && Gaps(window).Contains("dotnet ef cannot make ShopContext", StringComparison.Ordinal),
                    () => $"Staging chosen and the context listed again in it ({window.Status}; gaps: {Gaps(window)})", Slow);
                Assert.Equal("Staging", chooser.Text);

                Ui.ClickButton(window.MigrationsView, "Add a Design-Time Factory for ShopContext");
                string factory = Path.Combine(folder, "ShopContextFactory.cs");
                Ui.WaitFor(() => File.Exists(factory), "the factory written");
                Assert.True(File.ReadAllText(factory).Contains("ConnectionString(\"ShopContext\")", StringComparison.Ordinal),
                    $"The factory does not read ShopContext, the connection appsettings.Staging.json alone names:\n{File.ReadAllText(factory)}");
                // Through the factory, which finds ShopContext's connection in Staging alone: in Development it throws.
                Add(window, "Initial");
                Tool(window, AppActions.UpdateDatabase);
                Ui.WaitFor(() => Rows(window) is [("Initial", true)], () => $"Initial applied to Staging's database ({window.Status}; gaps: {Gaps(window)})", Slow);
                Assert.True(File.Exists(Path.Combine(folder, "staging.db")), "Update Database made no staging.db, the database appsettings.Staging.json names");
                Assert.True(Taken(given).All(g => g == "Staging/Staging"), $"dotnet ef was given {string.Join(", ", Taken(given))} with Staging chosen");
            }
            finally
            {
                window.Close();
            }

            MainWindow again = Keeping(settings, folder);
            try
            {
                Assert.Equal("Staging", again.Migrations.Environment?.Name);
                again.ShowToolWindow(ToolWindowIds.Migrations);
                Ui.WaitFor(() => again.Migrations.Listed && again.Migrations.Busy is null, () => $"the migrations listed in the folder opened again ({again.Status}; gaps: {Gaps(again)})", Slow);
                Assert.Equal([("Initial", (bool?)true)], Rows(again));
                ComboBox chooser = Environments(again);
                Assert.Equal("Staging", chooser.Text);
                Assert.True(Taken(given).All(g => g == "Staging/Staging"), $"dotnet ef was given {string.Join(", ", Taken(given))} in the folder opened again");

                Type(again, chooser, "Staging Two");
                Ui.WaitFor(() => again.Status?.Contains("\"Staging Two\" names no environment", StringComparison.Ordinal) == true, () => $"the name typed refused ({again.Status})");
                Assert.Equal(("Staging", "Staging"), (again.Migrations.Environment?.Name, chooser.Text));
                Type(again, chooser, "QA");
                Ui.WaitFor(() => again.Migrations.Environment?.Name == "QA" && again.Migrations.Busy is null, () => $"QA taken and listed in ({again.Status}; gaps: {Gaps(again)})", Slow);
                Assert.True(Taken(given) is { Count: > 0 } ran && ran.All(g => g == "QA/QA"), $"dotnet ef was given {string.Join(", ", Taken(given))} with QA typed");
                Ui.Press(again, "TAB");
                Ui.WaitFor(() => Offered(chooser) == "Development (the run configuration's), Staging, QA", () => $"QA offered once typed, where the chooser offers {Offered(chooser)}");
            }
            finally
            {
                again.Close();
            }
        }
        finally
        {
            Passenger.Core.ChildProcesses.Started -= heard;
            TestFolders.Delete(settings);
        }
    }

    /// <summary>The environments a chooser offers, as its rows say them.</summary>
    private static string Offered(ComboBox chooser) =>
        string.Join(", ", chooser.Items.OfType<EfEnvironment>().Select(e => e.Note is { } note ? $"{e.Name} ({note})" : e.Name));

    private static ComboBox Environments(MainWindow window)
    {
        Ui.Settle();
        return window.MigrationsView.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "MigrationsEnvironment");
    }

    /// <summary>The environments dotnet ef was given since last asked, each as ASPNETCORE_ENVIRONMENT/DOTNET_ENVIRONMENT.</summary>
    private static List<string> Taken(List<string> given)
    {
        lock (given)
        {
            List<string> taken = [.. given];
            given.Clear();
            return taken;
        }
    }

    /// <summary>An editable drop-down's list opened by a click on its glyph, beside its field, as a person opens it.</summary>
    private static void Open(ComboBox chooser)
    {
        Ui.Click(chooser, new Avalonia.Point(chooser.Bounds.Width - 12, chooser.Bounds.Height / 2));
        Ui.WaitFor(() => chooser.IsDropDownOpen, "the environments' list to open");
    }

    /// <summary>A name typed into an editable drop-down's field, over what it held, and Enter.</summary>
    private static void Type(MainWindow window, ComboBox chooser, string name)
    {
        TextBox field = chooser.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible);
        Ui.Click(field);
        field.SelectAll();
        window.KeyTextInput(name);
        Ui.Press(window, "ENTER");
    }

    /// <summary>A window that keeps its state in a folder of its own, on the folder given.</summary>
    private static MainWindow Keeping(string settings, string folder)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    private void The_NuGet_windows_update_of_EF_Core_past_its_provider_warns_and_updates_the_set_with_the_provider_together()
    {
        string folder = ShopApp.Make(design: true, tool: null, provider: "1.0.0", ef: ShopApp.Older,
            providers: [("1.0.0", $"[{ShopApp.Older}]"), ("2.0.0", $"[{ShopApp.Version}]")]);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects is [{ IsEvaluated: true }], "the shop's project read");
            window.ShowToolWindow(ToolWindowIds.NuGet);
            Passenger.App.ToolWindows.NuGetView nuget = window.NuGet;
            nuget.Query = "Microsoft.EntityFrameworkCore.Sqlite";
            Task searched = nuget.RefreshAsync();
            Ui.Landed(() => searched.IsCompleted && nuget.Selected?.Id == "Microsoft.EntityFrameworkCore.Sqlite", "the SQLite provider found and chosen");
            nuget.ChosenVersion = ShopApp.Version;
            Task<bool> update = nuget.InstallAsync(window.Projects[0]);

            ChoiceDialog asked = Asked<ChoiceDialog>(window, "the update's warning");
            string words = string.Join(' ', asked.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
            Assert.Contains($"{ShopApp.Provider} 1.0.0 supports EF Core {ShopApp.Older}; Microsoft.EntityFrameworkCore.Sqlite {ShopApp.Version} leaves it behind", words, StringComparison.Ordinal);
            Ui.ClickButton(asked, $"Update the Set to {ShopApp.Version}");
            Ui.Landed(() => update.IsCompleted && !nuget.IsBusy, "the set's update");
            string project = File.ReadAllText(Path.Combine(folder, "Shop.csproj"));
            Assert.Contains($"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"{ShopApp.Version}\"", project, StringComparison.Ordinal);
            Assert.Contains($"Microsoft.EntityFrameworkCore.Design\" Version=\"{ShopApp.Version}\"", project, StringComparison.Ordinal);
            Assert.Contains($"{ShopApp.Provider}\" Version=\"2.0.0\"", project, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Under central package management, Update the Set moves the central versions of EF Core's packages another
    /// project references (Reports' relational package) with the shop's, never leaving the folder's set half at the new
    /// version.</summary>
    private void Under_central_package_management_updating_the_set_moves_the_set_other_projects_reference_too()
    {
        string folder = ShopApp.Make(design: true, tool: null, provider: "1.0.0", ef: ShopApp.Older,
            providers: [("1.0.0", $"[{ShopApp.Older}]"), ("2.0.0", $"[{ShopApp.Version}]")], central: true);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects is [{ IsEvaluated: true }, { IsEvaluated: true }], "the shop's and the reports' projects read");
            window.ShowToolWindow(ToolWindowIds.NuGet);
            Passenger.App.ToolWindows.NuGetView nuget = window.NuGet;
            nuget.Query = "Microsoft.EntityFrameworkCore.Sqlite";
            Task searched = nuget.RefreshAsync();
            Ui.Landed(() => searched.IsCompleted && nuget.Selected?.Id == "Microsoft.EntityFrameworkCore.Sqlite", "the SQLite provider found and chosen");
            nuget.ChosenVersion = ShopApp.Version;
            Task<bool> update = nuget.InstallAsync(window.Projects.Single(p => p.Name == "Shop"));
            Ui.ClickButton(Asked<ChoiceDialog>(window, "the update's warning"), $"Update the Set to {ShopApp.Version}");
            Ui.Landed(() => update.IsCompleted && !nuget.IsBusy, "the set's update");
            string[] versions = [.. System.Xml.Linq.XDocument.Load(Path.Combine(folder, "Directory.Packages.props")).Descendants("PackageVersion")
                .Select(v => $"{(string?)v.Attribute("Include")} {(string?)v.Attribute("Version")}")];
            string[] expected = [$"Microsoft.EntityFrameworkCore.Sqlite {ShopApp.Version}", $"Microsoft.EntityFrameworkCore.Design {ShopApp.Version}",
                $"Microsoft.EntityFrameworkCore.Relational {ShopApp.Version}", $"{ShopApp.Provider} 2.0.0"];
            Assert.True(versions.SequenceEqual(expected), $"Update the Set left the central versions [{string.Join(", ", versions)}]: {nuget.Log}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The NuGet window's Update of one of EF Core's packages moves the project's others with it, the status naming
    /// them, and Undo takes them back together; an Install of one the project lacks adds that one alone.</summary>
    private void Updating_one_of_EF_Cores_packages_moves_the_projects_others_with_it_as_one_undo_step_and_an_install_adds_one_alone()
    {
        string folder = ShopApp.Make(design: true, tool: null, ef: ShopApp.Older);
        string project = Path.Combine(folder, "Shop.csproj");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects is [{ IsEvaluated: true }], "the shop's project read");
            Passenger.App.ToolWindows.NuGetView nuget = Chosen(window, Sqlite, ShopApp.Version);
            Ui.Click(Offered(nuget, "Update"), settle: false);
            Ui.WaitFor(() => !nuget.IsBusy && Has(project, Sqlite, ShopApp.Version) && Has(project, Design, ShopApp.Version),
                () => $"the update of {Sqlite} to {ShopApp.Version} with {Design} ({window.Status}):\n{File.ReadAllText(project)}\n{nuget.Log}", Slow);
            string said = $"Update {Sqlite} to {ShopApp.Version} in Shop: {Design} {ShopApp.Version} moved with it";
            Assert.True(window.Status == said, $"The update moving the Design package with it said \"{window.Status}\", not \"{said}\"");

            UndoFromExplorer(window, project);
            Assert.True(Has(project, Sqlite, ShopApp.Older) && Has(project, Design, ShopApp.Older),
                $"One Undo left EF Core's set at two versions, which does not build together:\n{File.ReadAllText(project)}");

            nuget = Chosen(window, Relational, ShopApp.Version);
            Ui.Click(Offered(nuget, "Install"), settle: false);
            Ui.WaitFor(() => !nuget.IsBusy && Has(project, Relational, ShopApp.Version), () => $"{Relational} installed ({window.Status}):\n{File.ReadAllText(project)}\n{nuget.Log}", Slow);
            Assert.True(Has(project, Sqlite, ShopApp.Older) && Has(project, Design, ShopApp.Older),
                $"Installing {Relational}, which the project lacked, moved the project's other EF Core packages:\n{File.ReadAllText(project)}");
        }
        finally
        {
            window.Close();
        }
    }

    private const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";

    private const string Design = "Microsoft.EntityFrameworkCore.Design";

    private const string Relational = "Microsoft.EntityFrameworkCore.Relational";

    /// <summary>The NuGet window shown, a package found and chosen, and a version picked in its picker as a person picks it.</summary>
    private static Passenger.App.ToolWindows.NuGetView Chosen(MainWindow window, string id, string version)
    {
        window.ShowToolWindow(ToolWindowIds.NuGet);
        Passenger.App.ToolWindows.NuGetView nuget = window.NuGet;
        nuget.Query = id;
        Task searched = nuget.RefreshAsync();
        Ui.Landed(() => searched.IsCompleted && nuget.Selected?.Id == id && nuget.Versions.Contains(version),
            $"{id} found and chosen, {version} among its versions [{string.Join(", ", nuget.Versions)}] ({nuget.Status})");
        NuGetTests.Choose(nuget, version);
        return nuget;
    }

    /// <summary>The button the NuGet window offers on the shop's line for the version chosen.</summary>
    private static Button Offered(Passenger.App.ToolWindows.NuGetView nuget, string label) => NuGetTests.Action(nuget, "Shop", label)
        ?? throw new Xunit.Sdk.XunitException($"The NuGet window offers no {label} on Shop's line for {nuget.ChosenVersion}");

    /// <summary>Whether the project file references a package at a version.</summary>
    private static bool Has(string project, string id, string version) =>
        File.ReadAllText(project).Contains($"Include=\"{id}\" Version=\"{version}\"", StringComparison.Ordinal);

    /// <summary>Undo from the Solution view, a row chosen there, its question answered yes: the last command taken back;
    /// <paramref name="opened"/> says where the files open in editors were opened from.</summary>
    internal static void UndoFromExplorer(MainWindow window, string path, Func<string>? opened = null)
    {
        List<string> asked = [];
        window.Confirm = (_, question, _) =>
        {
            asked.Add(question);
            return Task.FromResult(true);
        };
        window.ExplorerView.Reveal(path);
        Ui.Settle();
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Name.StartsWith(Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal)));
        string? next = window.EditorArea.Commands.ToUndo(null)?.Name;
        string open = string.Join(", ", window.EditorArea.Documents.Select(d => Path.GetFileName(d.Path)));
        string? active = window.EditorArea.Active?.Path is { } shown ? Path.GetFileName(shown) : null;
        string? focused = window.FocusManager?.GetFocusedElement()?.GetType().Name;
        Ui.Run(window, Passenger.Editor.EditorActions.Undo);
        Assert.True(asked.Count == 1, $"Undo asked {asked.Count} questions: {string.Join(" | ", asked)} (the command to undo was {next ?? "none"}; "
            + $"open: {open}; active: {active ?? "none"}; the keys with: {focused ?? "nothing"}; the status bar said {window.Status}){(opened is null ? "" : $"\n{opened()}")}");
    }

    /// <summary>EF Core 11's dotnet ef takes its options' defaults from .config/dotnet-ef.json: of a library's two apps that
    /// can start its contexts, the window lists and adds migrations through the startup project the file names and the
    /// context it names, never the first app (which lacks the Design package dotnet ef needs) nor the first context; and
    /// with the database out of reach, Remove Last Migration removes the migration offline, never asking the database.</summary>
    private void With_EF_Core_11_the_window_takes_what_dotnet_ef_json_names_and_removes_a_migration_offline()
    {
        string folder = ShopApp.MakeEf11();
        string data = Path.Combine(folder, "Data");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        // Each dotnet ef command line the Build window shows, which the build before the next command clears.
        List<string> commands = [];
        window.BottomPanel.Build.Document.Changed += (_, _) => commands.AddRange(window.BottomPanel.Build.Document.Snapshot.ToString().Split('\n')
            .Where(l => l.Contains("--startup-project", StringComparison.Ordinal) && !commands.Contains(l)));
        try
        {
            window.ShowToolWindow(ToolWindowIds.Migrations);
            Ui.WaitFor(() => window.Migrations.Listed, () => $"the migrations listed (status: {window.Status}; gaps: {Gaps(window)})", Slow);
            Assert.Equal(("Data", "Web", "SalesContext"), (window.Migrations.Project?.Name, window.Migrations.Startup?.Name, window.Migrations.Context));

            Add(window, "Initial");
            Assert.True(File.Exists(Path.Combine(data, "Migrations", "SalesContextModelSnapshot.cs")),
                $"Add Migration made no snapshot of SalesContext: [{string.Join(", ", Directory.GetFiles(Path.Combine(data, "Migrations")).Select(Path.GetFileName))}]");
            Assert.Equal([("Initial", (bool?)null)], Rows(window));

            Tool(window, AppActions.RemoveLastMigration);
            ChoiceDialog remove = Asked<ChoiceDialog>(window, "Remove Last's question");
            Assert.Contains("could not be read", string.Join(' ', remove.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), StringComparison.Ordinal);
            Ui.ClickButton(remove, "Remove");
            // Not settled: the build and model check after it are for Problems, which this does not read.
            Ui.PumpUntil(() => Rows(window).Count == 0, $"Initial removed with the database out of reach ({window.Status})", Slow);
            Assert.Empty(Directory.GetFiles(Path.Combine(data, "Migrations"), "*_Initial*.cs"));

            Assert.True(commands.Count > 0 && commands.All(c => c.Contains("Web.csproj", StringComparison.Ordinal) && !c.Contains("Api.csproj", StringComparison.Ordinal)),
                $"dotnet ef was not run through Web alone:\n{string.Join("\n", commands)}");
            Assert.Contains(commands, c => c.Contains("migrations remove", StringComparison.Ordinal) && c.Contains("--offline", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A migration set per context, each in a folder of its own (#2047): Add Migration's output folder takes one typed
    /// and one picked inside the project, dotnet ef writing each context's migrations and snapshot there; each context's list
    /// is read from its own folder; the folder given is kept for the context and filled in again beside the folders holding
    /// its migrations, dotnet ef's own named where it is empty; and Remove Last Migration removes the last from its folder,
    /// the snapshot there kept. A folder picked outside the project is refused, saying why.</summary>
    private void Each_contexts_migrations_go_into_the_folder_given_are_listed_from_it_and_the_last_removed_from_there()
    {
        string folder = ShopApp.MakeSets();
        string data = Passenger.Core.PathNames.Canonical(Path.Combine(folder, "Data"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        List<string?> starts = [];
        string? picked = null;
        window.PickFolder = (_, start) =>
        {
            starts.Add(start);
            return Task.FromResult(picked);
        };
        try
        {
            window.ShowToolWindow(ToolWindowIds.Migrations);
            Ui.WaitFor(() => window.Migrations.Listed, () => $"the contexts listed (status: {window.Status}; gaps: {Gaps(window)})", Slow);
            MigrationsController migrations = window.Migrations;
            Assert.Equal(("Data", "Api"), (migrations.Project?.Name, migrations.Startup?.Name));
            Assert.Equal(["PostgresContext", "SqlServerContext"], migrations.Contexts.Select(c => c.Name).Order(StringComparer.Ordinal));
            migrations.ChooseContext("PostgresContext");

            AddMigrationDialog first = Open(window);
            Assert.True(first.FolderBox.Text is null or "" && first.Into == "Empty: dotnet ef's default, Data/Migrations",
                $"A context with no migrations opened Add Migration with its folder '{first.FolderBox.Text}' and the note '{first.Into}'");
            first.KeyTextInput("Initial");
            first.FolderBox.Focus();
            first.KeyTextInput("Migrations/Postgres");
            Assert.True(first.Into == "Into Data/Migrations/Postgres", $"The folder typed was taken as '{first.FolderBox.Text}', the note saying '{first.Into}'");
            Ui.ClickButton(first, "Add");
            Ui.WaitFor(() => migrations.Busy is null && Rows(window) is [("Initial", _)], () => $"PostgresContext's Initial added and listed ({window.Status})", Slow);
            Assert.True(Files(data, "Migrations/Postgres") is [.., "PostgresContextModelSnapshot.cs"] postgres && postgres.Any(f => f.EndsWith("_Initial.cs", StringComparison.Ordinal)),
                $"Data/Migrations/Postgres holds [{string.Join(", ", Files(data, "Migrations/Postgres"))}]: the migration went elsewhere");
            Assert.False(Directory.Exists(Path.Combine(data, "Migrations", "SqlServer")) || File.Exists(Path.Combine(data, "Migrations", "PostgresContextModelSnapshot.cs")),
                "PostgresContext's migration reached Migrations itself, which no output folder named");

            migrations.ChooseContext("SqlServerContext");
            AddMigrationDialog second = Open(window);
            second.KeyTextInput("Initial");
            picked = Path.Combine(folder, "Elsewhere");
            Ui.Click(second.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Browse…"));
            Assert.True(second.Problem.Contains("outside Data", StringComparison.Ordinal) && second.FolderBox.Text is null or "",
                $"A folder picked outside the project was taken as '{second.FolderBox.Text}', saying '{second.Problem}'");
            Directory.CreateDirectory(Path.Combine(data, "Migrations", "SqlServer"));
            picked = Path.Combine(data, "Migrations", "SqlServer");
            Ui.Click(second.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Browse…"));
            Assert.True(starts.All(s => s == data), $"The folder picker started in [{string.Join(", ", starts)}], not in Data's folder");
            Assert.Equal("Migrations/SqlServer", second.FolderBox.Text);
            Ui.ClickButton(second, "Add");
            Ui.WaitFor(() => migrations.Busy is null && migrations.Context == "SqlServerContext" && Rows(window) is [("Initial", _)],
                () => $"SqlServerContext's Initial added and listed ({window.Status})", Slow);
            Assert.Contains("SqlServerContextModelSnapshot.cs", Files(data, "Migrations/SqlServer"));

            // Each context's list is its own folder's.
            migrations.ChooseContext("PostgresContext");
            Tool(window, AppActions.MigrationsRefresh);
            Ui.WaitFor(() => migrations.Busy is null && migrations.Listed, () => $"PostgresContext listed again ({window.Status})", Slow);
            Assert.Equal([("Initial", (bool?)false)], Rows(window));

            AddMigrationDialog again = Open(window);
            Assert.True(again.FolderBox.Text == "Migrations/Postgres" && again.FolderBox.ItemsSource is IEnumerable<string> offered && offered.SequenceEqual(["Migrations/Postgres"]),
                $"Add Migration opened again with '{again.FolderBox.Text}' filled in, offering [{string.Join(", ", (again.FolderBox.ItemsSource as IEnumerable<string>) ?? [])}]: "
                + "the folder given last and the one holding the context's migrations were to be");
            again.FolderBox.Focus();
            Ui.Press(again, "meta A");
            Ui.Press(again, "BACK_SPACE");
            Assert.True(again.Into == "Empty: beside its last migration, in Data/Migrations/Postgres", $"The folder emptied left '{again.FolderBox.Text}', the note saying '{again.Into}'");
            again.NameBox.Focus();
            again.KeyTextInput("Second");
            Ui.ClickButton(again, "Add");
            Ui.WaitFor(() => migrations.Busy is null && Rows(window) is [("Initial", _), ("Second", _)], () => $"Second added and listed ({window.Status})", Slow);
            Assert.Contains(Files(data, "Migrations/Postgres"), f => f.EndsWith("_Second.cs", StringComparison.Ordinal));
            Assert.True(window.Session.MigrationFolders.Of(migrations.Project!, "PostgresContext") is null
                && window.Session.MigrationFolders.Of(migrations.Project!, "SqlServerContext") == "Migrations/SqlServer",
                "The folders kept were not the last each context was given: the next Add Migration would fill in another");

            Tool(window, AppActions.RemoveLastMigration);
            Ui.ClickButton(Asked<ChoiceDialog>(window, "Remove Last's question"), "Remove");
            // Not settled: the build and model check after it are for Problems, which this does not read.
            Ui.PumpUntil(() => Rows(window) is [("Initial", false)], $"Second removed and the list read again ({window.Status})", Slow);
            string[] left = Files(data, "Migrations/Postgres");
            Assert.True(!left.Any(f => f.Contains("_Second", StringComparison.Ordinal)) && left.Contains("PostgresContextModelSnapshot.cs")
                && !File.ReadAllText(Path.Combine(data, "Migrations", "Postgres", "PostgresContextModelSnapshot.cs")).Contains("Second", StringComparison.Ordinal),
                $"After Remove Last Migration Data/Migrations/Postgres holds [{string.Join(", ", left)}]");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Add Migration… from the window's button, its question waited for.</summary>
    private static AddMigrationDialog Open(MainWindow window)
    {
        Tool(window, AppActions.AddMigration);
        return Asked<AddMigrationDialog>(window, "Add Migration's question");
    }

    /// <summary>The C# files in a folder under a project's, by name, in order.</summary>
    private static string[] Files(string project, string folder) =>
        Directory.Exists(Path.Combine(project, folder)) ? [.. Directory.GetFiles(Path.Combine(project, folder), "*.cs").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)] : [];

    private static string Gaps(MainWindow window) => string.Join(" | ", window.MigrationsView.GapsShown);

    /// <summary>Checks a file holds a UTF-8 BOM and only CRLF breaks, as <paramref name="why"/>.</summary>
    private static void InForm(string path, string why)
    {
        byte[] bytes = File.ReadAllBytes(path);
        string text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.True(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), $"{Path.GetFileName(path)} has no UTF-8 BOM, which {why}:\n{text}");
        Assert.True(text.Contains("\r\n", StringComparison.Ordinal) && text.Replace("\r\n", "", StringComparison.Ordinal).IndexOf('\n', StringComparison.Ordinal) < 0,
            $"{Path.GetFileName(path)} has '\\n' breaks where {why} CRLF:\n{text}");
    }

    private static IReadOnlyList<(string Name, bool? Applied)> Rows(MainWindow window) => [.. window.Migrations.Migrations.Select(m => (m.Name, m.Applied))];

    /// <summary>Add Migration… from the window's button, its name typed into its question, waited for until it is listed.</summary>
    private static void Add(MainWindow window, string name)
    {
        Tool(window, AppActions.AddMigration);
        Name(window, name);
        Ui.WaitFor(() => window.Migrations.Busy is null && window.Migrations.Migrations.Any(m => m.Name == name), $"{name} added and listed ({window.Status})", Slow);
    }

    private static void Name(MainWindow window, string name)
    {
        AddMigrationDialog dialog = Asked<AddMigrationDialog>(window, "Add Migration's question");
        dialog.KeyTextInput(name);
        Ui.ClickButton(dialog, "Add");
    }

    /// <summary>A click on one of the Migrations window's buttons, by the action it runs, the window shown first.</summary>
    private static void Tool(MainWindow window, string action)
    {
        window.ShowToolWindow(ToolWindowIds.Migrations);
        Ui.Settle();
        Button button = window.MigrationsView.GetVisualDescendants().OfType<Button>().Single(b => b.Name == action);
        Ui.Click(button, settle: false);
    }

    private static T Asked<T>(MainWindow window, string what, TimeSpan? limit = null)
        where T : Window
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<T>().Any(d => d.IsVisible), $"{what} (status: {window.Status}; gaps: {Gaps(window)})", limit);
        return DialogButtonsTests.Check(window.OwnedWindows.OfType<T>().Single(d => d.IsVisible));
    }
}

/// <summary>The dotnet ef commands the IDE starts on a folder's projects, by their verbs ("migrations list"), and its
/// builds there, each a process a person waits on: what each step of a test ran held to what that step needs.</summary>
internal sealed class EfRuns : IDisposable
{
    private static readonly string[] Verbs = ["migrations", "dbcontext", "database"];
    private readonly List<string> started = [];
    private readonly EventHandler<Passenger.Core.ChildStart> heard;

    public EfRuns(string folder)
    {
        string under = Passenger.Core.PathNames.Canonical(folder);
        heard = (_, child) =>
        {
            if (child.Start is not { } start || !start.ArgumentList.Any(a => a.Contains(under, StringComparison.Ordinal)))
            {
                return;
            }
            List<string> words = [.. start.ArgumentList];
            int verb = words.FindIndex(w => Verbs.Contains(w));
            string? ran = words is ["build", ..] ? "build"
                : verb >= 0 && verb + 1 < words.Count && words.Any(w => w is "--startup-project" or "--startup-assembly") ? $"{words[verb]} {words[verb + 1]}" : null;
            if (ran is not null)
            {
                lock (started)
                {
                    started.Add(ran);
                }
            }
        };
        Passenger.Core.ChildProcesses.Started += heard;
    }

    /// <summary>Holds what ran since the last step to the dotnet ef commands given, in any order; builds are told, not held.</summary>
    public void Ran(string step, params string[] needed)
    {
        string[] ran;
        lock (started)
        {
            ran = [.. started];
            started.Clear();
        }
        string[] ef = [.. ran.Where(r => r != "build").Order(StringComparer.Ordinal)];
        Assert.True(ef.SequenceEqual(needed.Order(StringComparer.Ordinal)),
            $"{step} ran dotnet ef {ef.Length} times ({string.Join(", ", ef)}) where it needs {string.Join(", ", needed)}: "
            + $"a person waits on each. Everything it started, in order: {string.Join(", ", ran)}");
    }

    public void Dispose() => Passenger.Core.ChildProcesses.Started -= heard;
}

/// <summary>A console app with a SQLite context, as a test of migrations writes it: EF Core's SQLite provider at the
/// version the tests pin, the Design package or not, the local tool at a version or none, restored from the run's local
/// feed, which holds the tool and the Design package copied from the package cache.</summary>
internal static class ShopApp
{
    public static string Version { get; } = AvaloniaApp.Metadata("EfCoreVersion");

    public static string Older { get; } = AvaloniaApp.Metadata("EfCoreOlderVersion");

    /// <summary>The stand-in provider's id: a provider of EF Core of another maker, as the feed's metadata describes it.
    /// Named for the EF Core versions its ranges name, since the package cache keeps it by id and version: one made for
    /// the versions pinned before a move would be taken for it, its ranges naming those.</summary>
    public static string Provider { get; } = $"Passenger.EntityFrameworkCore.StandIn.Ef{Older.Replace('.', '_')}.Ef{Version.Replace('.', '_')}";

    /// <summary>The app in a folder of its own: the Design package and the relational one or not, the local tool at a version or
    /// none, the stand-in provider at a version (in a feed of the test's own, with the <paramref name="fed"/> packages of the cache,
    /// each version with the range of EF Core it supports, the <paramref name="unrestorable"/> one needing a package no feed holds,
    /// the feed asking 72 hours of age, <paramref name="young"/> alone younger), and the test's Program.cs; restored from the UI thread,
    /// its loop running. <paramref name="central"/> keeps the versions in a Directory.Packages.props, with a Reports library beside.</summary>
    public static string Make(bool design, string? tool, string? provider = null, IReadOnlyList<(string Version, string Range)>? providers = null, string? program = null,
        string? ef = null, string? unrestorable = null, bool central = false, bool relational = false, IReadOnlyList<(string Id, string Version)>? fed = null,
        string? young = null)
    {
        string folder = Write(design, tool, provider, providers, program, ef, unrestorable, central, relational, fed, young);
        Restore.Run(Path.Combine(folder, "Shop.csproj"));
        return folder;
    }

    /// <inheritdoc cref="Make"/>
    public static async Task<string> MakeAsync(bool design, string? tool, string? provider = null, IReadOnlyList<(string Version, string Range)>? providers = null,
        string? ef = null, bool relational = false)
    {
        string folder = Write(design, tool, provider, providers, null, ef, null, false, relational, null);
        await Restore.RunAsync(Path.Combine(folder, "Shop.csproj"));
        return folder;
    }

    private static string Write(bool design, string? tool, string? provider, IReadOnlyList<(string Version, string Range)>? providers, string? program, string? ef,
        string? unrestorable, bool central, bool relational, IReadOnlyList<(string Id, string Version)>? fed, string? young = null)
    {
        ef ??= Version;
        string folder = TestFolders.Create("shop");
        Feed();
        string standIns = TestFolders.Create("standins");
        foreach ((string version, string range) in providers ?? [])
        {
            StandIn(standIns, version, range, version == unrestorable);
            if (young is not null && version != young)
            {
                File.SetLastWriteTimeUtc(Path.Combine(standIns, $"{Provider}.{version}.nupkg"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            }
        }
        CopyInto(standIns, [.. fed ?? []]);
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="feed" value="{System.Security.SecurityElement.Escape(AvaloniaApp.Feed)}" />
                <add key="standins" value="{System.Security.SecurityElement.Escape(standIns)}"{(young is null ? string.Empty : " minPublishAgeHours=\"72\"")} />
              </packageSources>
            </configuration>
            """);
        string Versioned(string version) => central ? string.Empty : $" Version=\"{version}\"";
        string packages = $"    <PackageReference Include=\"Microsoft.EntityFrameworkCore.Sqlite\"{Versioned(ef)} />\n"
            + (design ? $"    <PackageReference Include=\"Microsoft.EntityFrameworkCore.Design\"{Versioned(ef)} PrivateAssets=\"all\" />\n" : string.Empty)
            + (relational ? $"    <PackageReference Include=\"Microsoft.EntityFrameworkCore.Relational\"{Versioned(ef)} />\n" : string.Empty)
            + (provider is null ? string.Empty : $"    <PackageReference Include=\"{Provider}\"{Versioned(provider)} />\n");
        if (central)
        {
            TestFolders.Write(folder, "Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n"
                + $"  <ItemGroup>\n    <PackageVersion Include=\"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"{ef}\" />\n"
                + $"    <PackageVersion Include=\"Microsoft.EntityFrameworkCore.Design\" Version=\"{ef}\" />\n"
                + $"    <PackageVersion Include=\"Microsoft.EntityFrameworkCore.Relational\" Version=\"{ef}\" />\n"
                + (provider is null ? string.Empty : $"    <PackageVersion Include=\"{Provider}\" Version=\"{provider}\" />\n") + "  </ItemGroup>\n</Project>\n");
            TestFolders.Write(folder, "Reports/Reports.csproj", TestProjects.Library().With("  <ItemGroup>\n    <PackageReference Include=\"Microsoft.EntityFrameworkCore.Relational\" />\n  </ItemGroup>\n"));
        }
        TestFolders.Write(folder, "Shop.csproj", TestProjects.Console("<Nullable>enable</Nullable>", "<ImplicitUsings>enable</ImplicitUsings>")
            .With($"  <ItemGroup>\n{packages}  </ItemGroup>\n"));
        TestFolders.Write(folder, "Program.cs", program ?? """
            using Microsoft.EntityFrameworkCore;

            using ShopContext context = new();
            Console.WriteLine(context.Products.Count());

            public sealed class Product
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public sealed class ShopContext : DbContext
            {
                public DbSet<Product> Products => Set<Product>();

                protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite("Data Source=shop.db");
            }

            """);
        if (tool is not null)
        {
            TestFolders.Write(folder, "dotnet-tools.json", $$"""
                {
                  "version": 1,
                  "isRoot": true,
                  "tools": {
                    "dotnet-ef": {
                      "version": "{{tool}}",
                      "commands": [ "dotnet-ef" ],
                      "rollForward": false
                    }
                  }
                }

                """);
        }
        return folder;
    }

    public static string Ef11 { get; } = AvaloniaApp.Metadata("EfCore11Version");

    /// <summary>A library with two SQLite contexts on EF Core 11 and the two apps that reference it, Api (first, without the
    /// Design package) and Web, with the local dotnet-ef 11 and a .config/dotnet-ef.json naming the library, Web and the
    /// second context; the database a file that is none, out of reach. Restored from the run's local feed and the
    /// package cache (tests/EfGraphs), from the UI thread.</summary>
    public static string MakeEf11()
    {
        string folder = TestFolders.Create("shop11");
        Copy(("dotnet-ef", Ef11));
        AvaloniaApp.UseFeed(folder);
        string Ef(string package, bool design = false) =>
            $"    <PackageReference Include=\"Microsoft.EntityFrameworkCore.{package}\" Version=\"{Ef11}\"{(design ? " PrivateAssets=\"all\"" : string.Empty)} />\n";
        string App(string packages) =>
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n"
            + $"  <ItemGroup>\n    <ProjectReference Include=\"../Data/Data.csproj\" />\n{packages}  </ItemGroup>\n</Project>\n";
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Api/Api.csproj\" />\n  <Project Path=\"Data/Data.csproj\" />\n  <Project Path=\"Web/Web.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "Data/Data.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n    <Nullable>enable</Nullable>\n"
            + $"  </PropertyGroup>\n  <ItemGroup>\n{Ef("Sqlite")}  </ItemGroup>\n</Project>\n");
        // A file of the database's name that is no database: the tool finds it there and cannot read it.
        string database = Path.Combine(folder, "sales.db");
        TestFolders.Write(folder, "sales.db", "not a database\n");
        TestFolders.Write(folder, "Data/Contexts.cs", $$"""
            using Microsoft.EntityFrameworkCore;

            namespace Data;

            public sealed class Product
            {
                public int Id { get; set; }
                public string Name { get; set; } = "";
            }

            public sealed class ShopContext : DbContext
            {
                public DbSet<Product> Products => Set<Product>();

                protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(@"Data Source={{database}}");
            }

            public sealed class SalesContext : DbContext
            {
                public DbSet<Product> Sold => Set<Product>();

                protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(@"Data Source={{database}}");
            }

            """);
        TestFolders.Write(folder, "Api/Api.csproj", App(string.Empty));
        TestFolders.Write(folder, "Api/Program.cs", "System.Console.WriteLine(\"Api\");\n");
        TestFolders.Write(folder, "Web/Web.csproj", App(Ef("Design", design: true)));
        TestFolders.Write(folder, "Web/Program.cs", "System.Console.WriteLine(\"Web\");\n");
        TestFolders.Write(folder, ".config/dotnet-ef.json", "{\n  \"project\": \"Data\",\n  \"startupProject\": \"Web/Web.csproj\",\n  \"context\": \"SalesContext\"\n}\n");
        TestFolders.Write(folder, "dotnet-tools.json", $$"""
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "dotnet-ef": {
                  "version": "{{Ef11}}",
                  "commands": [ "dotnet-ef" ],
                  "rollForward": false
                }
              }
            }

            """);
        // By its real path: NuGet reaches Data by that too, and two spellings of one project restored at once collide.
        Restore.Run(Passenger.Core.PathNames.Canonical(Path.Combine(folder, "Shop.slnx")));
        return folder;
    }

    /// <summary>An API and the library holding its context, as #2011 lays them out: MyApp.Api, a web app with only the Design
    /// package, referencing MyApp.Data, a library declaring AppDbContext with SQLite's provider, and MyApp.Services, a library
    /// referencing it; MyApp.Domain, which the data library references, beside. No local tool, so nothing lists by itself.
    /// Restored from the run's local feed and the package cache (tests/EfGraphs), from the UI thread.</summary>
    public static string MakeLayered()
    {
        string folder = TestFolders.Create("layered");
        Feed();
        AvaloniaApp.UseFeed(folder);
        string Ef(string package, bool design = false) =>
            $"    <PackageReference Include=\"Microsoft.EntityFrameworkCore.{package}\" Version=\"{Version}\"{(design ? " PrivateAssets=\"all\"" : string.Empty)} />\n";
        string Referencing(params string[] projects) => string.Concat(projects.Select(p => $"    <ProjectReference Include=\"../{p}/{p}.csproj\" />\n"));
        string[] names = ["MyApp.Api", "MyApp.Data", "MyApp.Domain", "MyApp.Services"];
        TestFolders.Write(folder, "MyApp.slnx", $"<Solution>\n{string.Concat(names.Select(n => $"  <Project Path=\"{n}/{n}.csproj\" />\n"))}</Solution>\n");
        TestFolders.Write(folder, "MyApp.Api/MyApp.Api.csproj", TestProjects.Web().With($"  <ItemGroup>\n{Referencing("MyApp.Data", "MyApp.Services")}{Ef("Design", design: true)}  </ItemGroup>\n"));
        TestFolders.Write(folder, "MyApp.Api/Program.cs", "Microsoft.AspNetCore.Builder.WebApplication.Create(args).Run();\n");
        TestFolders.Write(folder, "MyApp.Data/MyApp.Data.csproj", TestProjects.Library().With($"  <ItemGroup>\n{Referencing("MyApp.Domain")}{Ef("Sqlite")}  </ItemGroup>\n"));
        TestFolders.Write(folder, "MyApp.Data/AppDbContext.cs", """
            using Microsoft.EntityFrameworkCore;
            using MyApp.Domain;

            namespace MyApp.Data;

            public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
            {
                public DbSet<Product> Products => Set<Product>();
            }

            """);
        TestFolders.Write(folder, "MyApp.Domain/MyApp.Domain.csproj", TestProjects.Library());
        TestFolders.Write(folder, "MyApp.Domain/Product.cs", "namespace MyApp.Domain;\n\npublic sealed class Product\n{\n    public int Id { get; set; }\n}\n");
        TestFolders.Write(folder, "MyApp.Services/MyApp.Services.csproj", TestProjects.Library().With($"  <ItemGroup>\n{Referencing("MyApp.Data")}  </ItemGroup>\n"));
        TestFolders.Write(folder, "MyApp.Services/Catalog.cs", "namespace MyApp.Services;\n\npublic sealed class Catalog(MyApp.Data.AppDbContext context)\n{\n}\n");
        // By its real path: NuGet reaches the libraries by that too, and two spellings of one project restored at once collide.
        Restore.Run(Passenger.Core.PathNames.Canonical(Path.Combine(folder, "MyApp.slnx")));
        return folder;
    }

    /// <summary>A data library with two SQLite contexts, PostgresContext and SqlServerContext, each over its own database file,
    /// as a solution keeping a migration set per provider declares them (#2047), and Api, a console app referencing it with
    /// the Design package; the local dotnet-ef. No migration yet. Restored from the run's local feed and the package cache
    /// (tests/EfGraphs), from the UI thread.</summary>
    public static string MakeSets()
    {
        string folder = TestFolders.Create("sets");
        Feed();
        AvaloniaApp.UseFeed(folder);
        string Ef(string package, bool design = false) =>
            $"    <PackageReference Include=\"Microsoft.EntityFrameworkCore.{package}\" Version=\"{Version}\"{(design ? " PrivateAssets=\"all\"" : string.Empty)} />\n";
        TestFolders.Write(folder, "Sets.slnx", "<Solution>\n  <Project Path=\"Api/Api.csproj\" />\n  <Project Path=\"Data/Data.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "Data/Data.csproj", TestProjects.Library().With($"  <ItemGroup>\n{Ef("Sqlite")}  </ItemGroup>\n"));
        string Database(string name) => Path.Combine(Passenger.Core.PathNames.Canonical(folder), name);
        TestFolders.Write(folder, "Data/Contexts.cs", $$"""
            using Microsoft.EntityFrameworkCore;

            namespace Data;

            public sealed class Product
            {
                public int Id { get; set; }
            }

            public sealed class PostgresContext : DbContext
            {
                public DbSet<Product> Products => Set<Product>();

                protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(@"Data Source={{Database("postgres.db")}}");
            }

            public sealed class SqlServerContext : DbContext
            {
                public DbSet<Product> Products => Set<Product>();

                protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite(@"Data Source={{Database("sqlserver.db")}}");
            }

            """);
        TestFolders.Write(folder, "Api/Api.csproj", TestProjects.Console().With($"  <ItemGroup>\n    <ProjectReference Include=\"../Data/Data.csproj\" />\n{Ef("Design", design: true)}  </ItemGroup>\n"));
        TestFolders.Write(folder, "Api/Program.cs", "System.Console.WriteLine(\"Api\");\n");
        TestFolders.Write(folder, "dotnet-tools.json", $$"""
            {
              "version": 1,
              "isRoot": true,
              "tools": {
                "dotnet-ef": {
                  "version": "{{Version}}",
                  "commands": [ "dotnet-ef" ],
                  "rollForward": false
                }
              }
            }

            """);
        // By its real path: NuGet reaches Data by that too, and two spellings of one project restored at once collide.
        Restore.Run(Passenger.Core.PathNames.Canonical(Path.Combine(folder, "Sets.slnx")));
        return folder;
    }

    /// <summary>The model changed: a product has a price, which no migration covers yet.</summary>
    public static void AddPrice(string folder)
    {
        string program = Path.Combine(folder, "Program.cs");
        File.WriteAllText(program, File.ReadAllText(program).Replace("    public string Name { get; set; } = \"\";\n", "    public string Name { get; set; } = \"\";\n    public decimal Price { get; set; }\n", StringComparison.Ordinal));
    }

    /// <summary>A stand-in provider's package in a feed: a nuspec alone, depending on EF Core's relational layer within the
    /// range given, as a provider that trails EF Core's releases does; <paramref name="unrestorable"/>, on a package no
    /// feed holds too, so no restore takes it.</summary>
    private static void StandIn(string feed, string version, string range, bool unrestorable)
    {
        string missing = unrestorable ? "\n                    <dependency id=\"Passenger.Missing\" version=\"1.0.0\" />" : string.Empty;
        using System.IO.Compression.ZipArchive package = System.IO.Compression.ZipFile.Open(Path.Combine(feed, $"{Provider}.{version}.nupkg"), System.IO.Compression.ZipArchiveMode.Create);
        using StreamWriter nuspec = new(package.CreateEntry($"{Provider}.nuspec").Open());
        nuspec.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{Provider}</id>
                <version>{version}</version>
                <authors>Passenger's tests</authors>
                <description>A stand-in provider of EF Core that supports {range}</description>
                <dependencies>
                  <group targetFramework="{TestProjects.Framework}">
                    <dependency id="Microsoft.EntityFrameworkCore.Relational" version="{range}" />{missing}
                  </group>
                </dependencies>
              </metadata>
            </package>
            """);
    }

    /// <summary>The tool, the Design package, EF Core, its relational layer and SQLite's provider at both versions, copied from
    /// the package cache into the run's local feed, whose metadata the window reads for the versions that fit, and whose
    /// versions say which of EF Core's packages move with another.</summary>
    internal static void Feed() => Copy(
        ("dotnet-ef", Version), ("dotnet-ef", Older), ("microsoft.entityframeworkcore.design", Version), ("microsoft.entityframeworkcore.design", Older),
        ("microsoft.entityframeworkcore", Version), ("microsoft.entityframeworkcore", Older),
        ("microsoft.entityframeworkcore.relational", Version), ("microsoft.entityframeworkcore.relational", Older),
        ("microsoft.entityframeworkcore.sqlite", Version), ("microsoft.entityframeworkcore.sqlite", Older));

    /// <summary>Packages copied from the package cache into the run's local feed.</summary>
    private static void Copy(params (string Id, string Version)[] packages) => CopyInto(AvaloniaApp.Feed, packages);

    /// <summary>Packages copied from the package cache into a feed's folder.</summary>
    private static void CopyInto(string feed, IReadOnlyList<(string Id, string Version)> packages)
    {
        string cache = AvaloniaApp.Metadata("NuGetPackageRoot");
        foreach ((string id, string version) in packages)
        {
            string package = Path.Combine(cache, id, version, $"{id}.{version}.nupkg");
            Assert.True(File.Exists(package), $"{id} {version} is not in the package cache at {package}: restore the tests first");
            string fed = Path.Combine(feed, Path.GetFileName(package));
            if (!File.Exists(fed))
            {
                File.Copy(package, fed, overwrite: true);
            }
        }
    }
}
