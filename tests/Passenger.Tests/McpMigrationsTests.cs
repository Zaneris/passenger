using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>An agent's migrations, through the tools it has: the Migrations actions by ide_action with what they would ask the
/// person as input, answering the context's migrations; and what keeps dotnet ef from working refused with its fixes, which
/// code_actions lists on the startup project's file and apply_code_action makes.</summary>
public sealed class McpMigrationsTests : McpRig
{
    [Fact]
    public async Task An_agent_fixes_what_dotnet_ef_lacks_through_code_actions_then_adds_and_applies_a_migration()
    {
        string folder = await ShopApp.MakeAsync(design: false, tool: null);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);

        CallToolResult refused = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.AddMigration, ["input"] = "Initial" });
        string said = Text(refused);
        Assert.True(refused.IsError == true && said.Contains("dotnet ef is not installed here", StringComparison.Ordinal)
            && said.Contains("code_actions on Shop.csproj:1:1", StringComparison.Ordinal), $"Add Migration with no tool answered: {said}");

        string pin = $"Pin the Local dotnet-ef to {ShopApp.Version}";
        string design = $"Add Microsoft.EntityFrameworkCore.Design {ShopApp.Version}";
        string[] offered = [.. (await Call(client, McpTools.CodeActions, new() { ["path"] = "Shop.csproj", ["line"] = 1, ["column"] = 1 })).EnumerateArray().Select(a => a.GetString()!)];
        Assert.Contains($"{pin} [quickfix]", offered);
        Assert.Contains($"{design} [quickfix]", offered);
        Assert.Contains("dotnet-tools.json", Text(await Call(client, McpTools.ApplyCodeAction, new() { ["path"] = "Shop.csproj", ["line"] = 1, ["column"] = 1, ["title"] = pin })),
            StringComparison.Ordinal);
        Assert.Contains("Shop.csproj", Text(await Call(client, McpTools.ApplyCodeAction, new() { ["path"] = "Shop.csproj", ["line"] = 1, ["column"] = 1, ["title"] = design })),
            StringComparison.Ordinal);

        string added = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.AddMigration, ["input"] = "Initial" }));
        Assert.True(added.Contains("Added migration Initial", StringComparison.Ordinal) && added.Contains("_Initial: pending", StringComparison.Ordinal),
            $"Add Migration answered: {added}");
        string updated = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.UpdateDatabase }));
        Assert.Contains("_Initial: applied", updated, StringComparison.Ordinal);
        CallToolResult nameless = await Result(client, McpTools.IdeAction, new() { ["action"] = AppActions.AddMigration });
        Assert.True(nameless.IsError == true && Text(nameless).Contains("takes the migration's name as input", StringComparison.Ordinal),
            $"Add Migration with no name answered: {Text(nameless)}");
        string script = Text(await Call(client, McpTools.IdeAction, new() { ["action"] = AppActions.ScriptMigrations, ["input"] = "..Initial" }));
        string file = script.Split(" into ", 2)[1].Split('\n')[0];
        Assert.Contains("CREATE TABLE \"Products\"", File.ReadAllText(file), StringComparison.Ordinal);
    }

    /// <summary>An agent's nuget_install of one of EF Core's packages is never asked about: it moves the project's others with
    /// it, as the window's update does, and leaves the provider it leaves behind as it was, its answer naming what moved and
    /// the provider's version that supports the EF Core asked.</summary>
    [Fact]
    public async Task An_agents_update_of_one_of_EF_Cores_packages_moves_the_set_and_says_which_provider_it_left_behind()
    {
        string folder = await ShopApp.MakeAsync(design: true, tool: null, provider: "1.0.0",
            providers: [("1.0.0", $"[{ShopApp.Older}]"), ("2.0.0", $"[{ShopApp.Version}]")], ef: ShopApp.Older, relational: true);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        const string Sqlite = "Microsoft.EntityFrameworkCore.Sqlite";
        JsonElement installed = await Call(client, McpTools.NugetInstall, new() { ["project"] = "Shop", ["id"] = Sqlite, ["version"] = ShopApp.Version });
        string[] with = installed.TryGetProperty("with", out JsonElement moved) && moved.ValueKind == JsonValueKind.Array ? [.. moved.EnumerateArray().Select(m => m.GetString()!)] : [];
        Assert.True(with.Order(StringComparer.Ordinal).SequenceEqual([$"Microsoft.EntityFrameworkCore.Design {ShopApp.Version}", $"Microsoft.EntityFrameworkCore.Relational {ShopApp.Version}"]),
            $"nuget_install of {Sqlite} answered that [{string.Join(", ", with)}] moved with it, not the rest of EF Core's set: {installed}");
        string said = installed.TryGetProperty("said", out JsonElement words) ? words.GetString() ?? string.Empty : string.Empty;
        Assert.True(said.Contains($"{ShopApp.Provider} 1.0.0 left behind, supporting EF Core {ShopApp.Older}: {ShopApp.Provider} 2.0.0 supports EF Core {ShopApp.Version}", StringComparison.Ordinal),
            $"nuget_install's answer does not tell the agent the provider it left behind and the version of it that fits: {installed}");
        string project = File.ReadAllText(Path.Combine(folder, "Shop.csproj"));
        Assert.True(project.Contains($"Include=\"{Sqlite}\" Version=\"{ShopApp.Version}\"", StringComparison.Ordinal)
            && project.Contains($"Include=\"Microsoft.EntityFrameworkCore.Design\" Version=\"{ShopApp.Version}\"", StringComparison.Ordinal)
            && project.Contains($"Include=\"{ShopApp.Provider}\" Version=\"1.0.0\"", StringComparison.Ordinal),
            $"nuget_install left the project's EF Core set at two versions, or moved the provider no one asked to move:\n{project}");
    }

    /// <summary>An agent giving up on a Migrations call held behind a run's build stops that call alone: the run whose
    /// build it waits behind goes on, built and run once its hold is let go.</summary>
    [Fact]
    public async Task An_agent_giving_up_on_a_migrations_call_stops_that_call_alone_and_the_run_under_way_goes_on()
    {
        string folder = await ShopApp.MakeAsync(design: true, tool: ShopApp.Version);
        string held = Path.Combine(folder, "held");
        string released = Path.Combine(folder, "released");
        File.WriteAllText(Path.Combine(folder, "Program.cs"), """
            using Microsoft.EntityFrameworkCore;

            Console.WriteLine("the shop ran");

            public sealed class Product
            {
                public int Id { get; set; }
            }

            public sealed class ShopContext : DbContext
            {
                public DbSet<Product> Products => Set<Product>();

                protected override void OnConfiguring(DbContextOptionsBuilder options) => options.UseSqlite("Data Source=shop.db");
            }

            """);
        // Every real build waits at its compile, saying so, until the test lets it go.
        File.WriteAllText(Path.Combine(folder, "Directory.Build.targets"), """
            <Project>
              <Target Name="Hold" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true' and !Exists('$(MSBuildThisFileDirectory)released')">
                <Exec Condition="'$(OS)' != 'Windows_NT'" WorkingDirectory="$(MSBuildThisFileDirectory)" Command="echo held &gt; held; while [ ! -f released ]; do sleep 0.1; done" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" WorkingDirectory="$(MSBuildThisFileDirectory)"
                      Command="powershell -NoProfile -Command &quot;Set-Content held held; while (-not (Test-Path released)) { Start-Sleep -Milliseconds 100 }&quot;" />
              </Target>
            </Project>
            """);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);
        Task<CallToolResult> running = Result(client, McpTools.Run, new() { ["hotReload"] = false, ["waitSeconds"] = 60 });
        try
        {
            Assert.True(await Waiting.UntilAsync(() => File.Exists(held) || running.IsCompleted, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken) && !running.IsCompleted,
                $"The agent's run never reached its build's hold: {(running.IsCompleted ? Text(await running) : "it is still under way")}{Said()}");

            using CancellationTokenSource giveUp = new();
            Heard heard = new();
            await using IAsyncDisposable hearing = Heard.All(client, heard);
            Task<CallToolResult> listing = CallGivingUp(client, McpTools.IdeAction, new() { ["action"] = AppActions.MigrationsRefresh }, giveUp.Token, progress: true);
            Assert.True(await Waiting.UntilAsync(() => heard.Told.Any(t => t.Message?.Contains("Waiting for the build under way to end", StringComparison.Ordinal) == true)
                || listing.IsCompleted, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken) && !listing.IsCompleted,
                $"The Migrations call never said it waits behind the run's build: it was told {heard}{(listing.IsCompleted ? $", and answered {Text(await listing)}" : string.Empty)}{Said()}");
            await giveUp.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing);

            Assert.False(await Waiting.UntilAsync(() => running.IsCompleted, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken),
                $"Giving up on the Migrations call ended the run whose build it waited behind: {(running.IsCompleted ? Text(await running) : string.Empty)}{Said()}");
        }
        finally
        {
            File.WriteAllText(released, "released\n");
        }
        CallToolResult ran = await running;
        Assert.True(ran.IsError != true && Text(ran).Contains("the shop ran", StringComparison.Ordinal),
            $"The run the given-up Migrations call waited behind answered, once let go: {Text(ran)}{Said()}");
    }
}
