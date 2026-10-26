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
        window.BottomPanel.Build.Written += (_, text) => programs.Append(text);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Migrations);
            Ui.WaitFor(() => window.Migrations.Listed, $"the shop's migrations listed (status: {window.Status}; gaps: {Gaps(window)})", Slow);
            Assert.True(window.Migrations.Migrations.Count == 0, $"A context with no migration listed {window.Migrations.Migrations.Count}");
            Assert.Equal("ShopContext", window.Migrations.Context);

            Add(window, "Initial");
            Assert.Equal([("Initial", (bool?)false)], Rows(window));
            window.BottomPanel.Build.Flush();
            string console = window.BottomPanel.Build.Document.Snapshot.ToString();
            Assert.True(programs.ToString().Split('\n').Any(line => EfRun.Split(line).Kind is "info" or "warn"),
                $"dotnet ef's lines did not reach the Build window as a program's output, which was:\n{programs}\nThe window's:\n{console}");
            Assert.DoesNotContain(console.Split('\n'), line => EfRun.Split(line).Kind == "data");

            Tool(window, AppActions.UpdateDatabase);
            Ui.WaitFor(() => Rows(window) is [(_, true)], $"Initial applied to the database ({window.Status})", Slow);

            // A model change no migration covers, which the build after it says in Problems at the context.
            ShopApp.AddPrice(folder);
            Ui.Run(window, AppActions.Build);
            string said = "ShopContext's model has changes no migration covers: Add Migration… makes one";
            Passenger.App.ToolWindows.ProblemsModel problems = window.BottomPanel.Problems;
            Ui.WaitFor(() => problems.Problems.Any(p => p.Message == said) && window.Migrations.Busy is null, $"the pending model changes in Problems after the build ({window.Status})", Slow);
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
            Assert.DoesNotContain(problems.Problems, p => p.Message == said);
            Tool(window, AppActions.UpdateDatabase);
            Ui.WaitFor(() => Rows(window) is [(_, true), (_, true)], $"both applied ({window.Status})", Slow);

            ListBox list = window.MigrationsView.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "MigrationsList");
            Ui.RightClick(Assert.IsType<ListBoxItem>(list.ContainerFromItem(window.Migrations.Migrations.Single(m => m.Name == "Initial"))));
            Ui.Choose(list.ContextMenu, "Update Database to Initial");
            ChoiceDialog asked = Asked<ChoiceDialog>(window, "the roll back's question");
            Assert.Contains("AddPrice", string.Join(' ', asked.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), StringComparison.Ordinal);
            Ui.ClickButton(asked, "Roll Back");
            Ui.WaitFor(() => Rows(window) is [(_, true), (_, false)], $"AddPrice rolled back ({window.Status})", Slow);

            Tool(window, AppActions.ScriptMigrations);
            MigrationScriptDialog script = Asked<MigrationScriptDialog>(window, "Script Migrations' question");
            Ui.ClickButton(script, "Script");
            Ui.WaitFor(() => window.EditorArea.Active?.Path.EndsWith(".sql", StringComparison.Ordinal) == true, $"the script open in a tab ({window.Status})", Slow);
            string sql = window.EditorArea.Active!.Text.Snapshot.ToString();
            Assert.Contains("CREATE TABLE \"Products\"", sql, StringComparison.Ordinal);
            Assert.Contains("\"Price\"", sql, StringComparison.Ordinal);

            Tool(window, AppActions.RemoveLastMigration);
            ChoiceDialog remove = Asked<ChoiceDialog>(window, "Remove Last's question");
            Assert.Contains("AddPrice", string.Join(' ', remove.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text)), StringComparison.Ordinal);
            Ui.ClickButton(remove, "Remove");
            Ui.WaitFor(() => Rows(window) is [("Initial", true)], $"AddPrice removed and the list read again ({window.Status})", Slow);
            Assert.Empty(Directory.GetFiles(Path.Combine(folder, "Migrations"), "*_AddPrice*.cs"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_missing_tool_and_Design_package_are_found_before_the_command_fixed_in_place_and_the_command_made()
    {
        string folder = ShopApp.Make(design: false, tool: null);
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

    [AvaloniaFact]
    public void A_tool_of_another_version_is_said_with_both_versions_and_pinned_to_the_runtimes_before_the_command()
    {
        string folder = ShopApp.Make(design: true, tool: ShopApp.Older);
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
            Assert.Contains("options.UseSqlite(", File.ReadAllText(factory), StringComparison.Ordinal);

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
    private static void UndoFromExplorer(MainWindow window, string path, Func<string>? opened = null)
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

    private static string Gaps(MainWindow window) => string.Join(" | ", window.MigrationsView.GapsShown);

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
    /// provider at a version (its versions in a feed of the test's own, each with the range of EF Core it supports), and a
    /// Program.cs of the test's in place of the shop's.</summary>
    public static string Make(bool design, string? tool, string? provider = null, IReadOnlyList<(string Version, string Range)>? providers = null, string? program = null,
        string? ef = null)
    {
        ef ??= Version;
        string folder = TestFolders.Create("shop");
        Feed();
        string standIns = TestFolders.Create("standins");
        foreach ((string version, string range) in providers ?? [])
        {
            StandIn(standIns, version, range);
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
        Restore.Run(Path.Combine(folder, "Shop.csproj"));
        return folder;
    }

    /// <summary>The model changed: a product has a price, which no migration covers yet.</summary>
    public static void AddPrice(string folder)
    {
        string program = Path.Combine(folder, "Program.cs");
        File.WriteAllText(program, File.ReadAllText(program).Replace("    public string Name { get; set; } = \"\";\n", "    public string Name { get; set; } = \"\";\n    public decimal Price { get; set; }\n", StringComparison.Ordinal));
    }

    /// <summary>A stand-in provider's package in a feed: a nuspec alone, depending on EF Core's relational layer within the
    /// range given, as a provider that trails EF Core's releases does.</summary>
    private static void StandIn(string feed, string version, string range)
    {
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
                    <dependency id="Microsoft.EntityFrameworkCore.Relational" version="{range}" />
                  </group>
                </dependencies>
              </metadata>
            </package>
            """);
    }

    /// <summary>The tool at both versions, the Design package and EF Core at both versions, copied from the package cache into
    /// the run's local feed, whose metadata the window reads for the versions that fit.</summary>
    internal static void Feed()
    {
        string cache = AvaloniaApp.Metadata("NuGetPackageRoot");
        foreach ((string id, string version) in new[]
        {
            ("dotnet-ef", Version), ("dotnet-ef", Older), ("microsoft.entityframeworkcore.design", Version),
            ("microsoft.entityframeworkcore", Version), ("microsoft.entityframeworkcore", Older),
            ("microsoft.entityframeworkcore.sqlite", Version), ("microsoft.entityframeworkcore.sqlite", Older),
        })
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
