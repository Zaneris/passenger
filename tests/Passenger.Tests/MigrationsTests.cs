using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Migrations;

namespace Passenger.Tests;

/// <summary>The Migrations window over a SQLite project's context, as a person uses it: its migrations listed with which
/// the database has, Add, Update (to the latest and back to one), Script into a tab; and what keeps dotnet ef from working
/// (no tool, a tool of another version, no Design package) found before the command, fixed in place, and the command then
/// made. EF Core and dotnet-ef come from the package cache through the run's local feed, never nuget.org.</summary>
public sealed class MigrationsTests
{
    /// <summary>The longest a build and a dotnet ef command take together on a developer's machine, scaled by the run.</summary>
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(2);

    /// <summary>The Migrations window's commands as a person makes them, dotnet ef's lines in the Build window as a program's
    /// output (what it answers, its <c>data:</c> lines, left out of it).</summary>
    [AvaloniaFact]
    public void Migrations_are_added_applied_rolled_back_and_scripted_against_the_runs_database()
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
    [AvaloniaFact]
    public void A_missing_tool_and_Design_package_are_found_before_the_command_fixed_in_place_and_the_command_made()
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
    [AvaloniaFact]
    public void Typing_in_another_file_while_a_fix_adds_a_package_stays_that_files_own_undo()
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

    [AvaloniaFact]
    public void Only_the_projects_own_contexts_are_taken_for_its_own_not_a_nested_projects_nor_a_dependencys()
    {
        string folder = ShopApp.Make(design: false, tool: null);
        TestFolders.Write(folder, "Audit/Audit.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Audit/AuditContext.cs", "public sealed class AuditContext : Microsoft.EntityFrameworkCore.DbContext\n{\n}\n");
        TestFolders.Write(folder, "node_modules/kit/KitContext.cs", "public sealed class KitContext : Microsoft.EntityFrameworkCore.DbContext\n{\n}\n");
        TestFolders.Write(folder, "src/obj/Old/OldContext.cs", "public sealed class OldContext : Microsoft.EntityFrameworkCore.DbContext\n{\n}\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Migrations.Gaps.Count > 0 && window.Migrations.Declared.Count > 0, () => $"the shop's gaps and its context's declaration found (gaps: {window.Migrations.Gaps.Count})");
            string[] declared = [.. window.Migrations.Declared.Select(d => $"{d.Context} in {Path.GetRelativePath(Passenger.Core.PathNames.Canonical(folder), d.File!)}")];
            Assert.True(declared.SequenceEqual(["ShopContext in Program.cs"]),
                $"The shop's contexts were found as [{string.Join(", ", declared)}]: Show Context Actions on another project's or a dependency's context would offer the shop's fixes");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The manifest, stored with CRLF and a UTF-8 BOM, keeps them through dotnet tool's write of it.</summary>
    [AvaloniaFact]
    public void A_tool_of_another_version_is_said_with_both_versions_and_pinned_to_the_runtimes_before_the_command()
    {
        string folder = ShopApp.Make(design: true, tool: ShopApp.Older);
        string tools = Path.Combine(folder, "dotnet-tools.json");
        File.WriteAllBytes(tools, [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(tools).Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal))]);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Migrations);
            string said = $"The local dotnet-ef {ShopApp.Older} does not match Shop's EF Core {ShopApp.Version}";
            Ui.WaitFor(() => Gaps(window).Contains(said, StringComparison.Ordinal), $"the mismatch said with both versions ({Gaps(window)})", Slow);

            Tool(window, AppActions.AddMigration);
            Name(window, "Initial");
            ChoiceDialog asked = Asked<ChoiceDialog>(window, "the mismatched tool's question", Slow);
            Ui.ClickButton(asked, $"Pin the Local dotnet-ef to {ShopApp.Version}");
            Ui.WaitFor(() => window.Migrations.Migrations.Any(m => m.Name == "Initial"), $"the migration made with the pinned tool ({window.Status})", Slow);
            JsonObject manifest = (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "dotnet-tools.json")))!;
            Assert.Equal(ShopApp.Version, manifest["tools"]!["dotnet-ef"]!["version"]!.GetValue<string>());
            InForm(tools, "it was stored with");
            Assert.DoesNotContain("does not match", Gaps(window), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_provider_behind_EF_Core_is_said_with_both_versions_and_EF_Core_moved_to_what_it_supports_as_one_undo_step()
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

    [AvaloniaFact]
    public void A_provider_behind_EF_Core_moves_up_to_the_version_of_it_that_supports_the_projects_EF_Core()
    {
        string folder = ShopApp.Make(design: true, tool: ShopApp.Version, provider: "1.0.0",
            providers: [("1.0.0", $"[{ShopApp.Older}]"), ("2.0.0", $"[{ShopApp.Version}]")]);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Migrations);
            Ui.WaitFor(() => Gaps(window).Contains($"{ShopApp.Provider} 1.0.0 supports", StringComparison.Ordinal) && window.Migrations.Busy is null, $"the provider behind ({Gaps(window)})", Slow);
            string fix = $"Bring {ShopApp.Provider} and EF Core into Step";
            Ui.ClickButton(window.MigrationsView, fix);
            Ui.ClickButton(Asked<ChoiceDialog>(window, "the fix's question"), fix);
            string project = Path.Combine(folder, "Shop.csproj");
            Ui.WaitFor(() => File.ReadAllText(project).Contains($"{ShopApp.Provider}\" Version=\"2.0.0\"", StringComparison.Ordinal) && Gaps(window).Length == 0
                && window.Migrations.Busy is null, $"the provider moved up to 2.0.0 ({window.Status}; gaps: {Gaps(window)})", Slow);
            Assert.Contains($"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"{ShopApp.Version}\"", File.ReadAllText(project), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The provider's version that supports the project's EF Core needs a package no feed holds, so the alignment's
    /// restore fails: the project is put back as it was and restored again, the fix saying it failed and that the project
    /// was put back, the provider still behind and nothing left for Undo.</summary>
    [AvaloniaFact]
    public void A_provider_alignment_whose_restore_fails_puts_the_project_back_as_it_was_and_says_so()
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
    [AvaloniaFact]
    public void A_context_the_tools_cannot_make_is_said_with_why_and_a_design_time_factory_added_in_place_and_taken_back_by_undo()
    {
        string folder = ShopApp.Make(design: true, tool: ShopApp.Version, program: """
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

            """);
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

    [AvaloniaFact]
    public void The_NuGet_windows_update_of_EF_Core_past_its_provider_warns_and_updates_the_set_with_the_provider_together()
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
    [AvaloniaFact]
    public void With_EF_Core_11_the_window_takes_what_dotnet_ef_json_names_and_removes_a_migration_offline()
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
        ConfirmDialog dialog = Asked<ConfirmDialog>(window, "Add Migration's question");
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

    /// <summary>The app in a folder of its own: the Design package or not, the local tool at a version or none, the stand-in
    /// provider at a version (its versions in a feed of the test's own, each with the range of EF Core it supports, the
    /// <paramref name="unrestorable"/> one needing a package no feed holds), and a Program.cs of the test's in place of the
    /// shop's; restored from the UI thread (a window test's body), the window's loop running meanwhile.</summary>
    public static string Make(bool design, string? tool, string? provider = null, IReadOnlyList<(string Version, string Range)>? providers = null, string? program = null,
        string? ef = null, string? unrestorable = null)
    {
        string folder = Write(design, tool, provider, providers, program, ef, unrestorable);
        Restore.Run(Path.Combine(folder, "Shop.csproj"));
        return folder;
    }

    /// <inheritdoc cref="Make"/>
    public static async Task<string> MakeAsync(bool design, string? tool)
    {
        string folder = Write(design, tool, null, null, null, null, null);
        await Restore.RunAsync(Path.Combine(folder, "Shop.csproj"));
        return folder;
    }

    private static string Write(bool design, string? tool, string? provider, IReadOnlyList<(string Version, string Range)>? providers, string? program, string? ef,
        string? unrestorable)
    {
        ef ??= Version;
        string folder = TestFolders.Create("shop");
        Feed();
        string standIns = TestFolders.Create("standins");
        foreach ((string version, string range) in providers ?? [])
        {
            StandIn(standIns, version, range, version == unrestorable);
        }
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="feed" value="{System.Security.SecurityElement.Escape(AvaloniaApp.Feed)}" />
                <add key="standins" value="{System.Security.SecurityElement.Escape(standIns)}" />
              </packageSources>
            </configuration>
            """);
        string packages = $"    <PackageReference Include=\"Microsoft.EntityFrameworkCore.Sqlite\" Version=\"{ef}\" />\n"
            + (design ? $"    <PackageReference Include=\"Microsoft.EntityFrameworkCore.Design\" Version=\"{ef}\" PrivateAssets=\"all\" />\n" : string.Empty)
            + (provider is null ? string.Empty : $"    <PackageReference Include=\"{Provider}\" Version=\"{provider}\" />\n");
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

    /// <summary>The tool at both versions, the Design package and EF Core at both versions, copied from the package cache into
    /// the run's local feed, whose metadata the window reads for the versions that fit.</summary>
    internal static void Feed() => Copy(
        ("dotnet-ef", Version), ("dotnet-ef", Older), ("microsoft.entityframeworkcore.design", Version),
        ("microsoft.entityframeworkcore", Version), ("microsoft.entityframeworkcore", Older),
        ("microsoft.entityframeworkcore.sqlite", Version), ("microsoft.entityframeworkcore.sqlite", Older));

    /// <summary>Packages copied from the package cache into the run's local feed.</summary>
    private static void Copy(params (string Id, string Version)[] packages)
    {
        string cache = AvaloniaApp.Metadata("NuGetPackageRoot");
        foreach ((string id, string version) in packages)
        {
            string package = Path.Combine(cache, id, version, $"{id}.{version}.nupkg");
            Assert.True(File.Exists(package), $"{id} {version} is not in the package cache at {package}: restore the tests first");
            string fed = Path.Combine(AvaloniaApp.Feed, Path.GetFileName(package));
            if (!File.Exists(fed))
            {
                File.Copy(package, fed, overwrite: true);
            }
        }
    }
}
