using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>The MSBuild nodes an agent's own Passenger's builds leave end with it, as it ends or is killed, and nothing its
/// project host writes, nor the nodes it or its watchdog asks to end, stays in its temporary folder. MSBuild keeps a node
/// only while the machine's count of nodes allows, which other builds beside it would take, so the class runs alone.</summary>
[Collection(Alone.Name)]
[AboutMacOS]
public sealed class McpBuildNodesTests : McpRig, IClassFixture<RestoredFolders>
{
    /// <summary>The fixture each step of a flow takes for an instance of its own.</summary>
    private readonly RestoredFolders restored;

    public McpBuildNodesTests(RestoredFolders folders)
        : base(folders) => restored = folders;

    /// <summary>The builds' processes as one flow, each step in an instance of its own: the nodes a build leaves for the next
    /// ending with Passenger, each way it ends, nothing a build of Passenger's starts running on under its salt once
    /// Passenger ends, and the project host leaving nothing in Passenger's temporary folder.</summary>
    [Fact]
    public async Task What_Passengers_builds_start_ends_with_Passenger_MSBuilds_nodes_too()
    {
        await Flow.StepApartAsync("the msbuild nodes a build leaves for the next end with passenger (false)", () => new McpBuildNodesTests(restored), t => t.The_msbuild_nodes_a_build_leaves_for_the_next_end_with_passenger(false));
        await Flow.StepApartAsync("the msbuild nodes a build leaves for the next end with passenger (true)", () => new McpBuildNodesTests(restored), t => t.The_msbuild_nodes_a_build_leaves_for_the_next_end_with_passenger(true));
        await Flow.StepApartAsync("nothing a build of passengers starts runs on under its salt once passenger ends", () => new McpBuildNodesTests(restored), t => t.Nothing_a_build_of_passengers_starts_runs_on_under_its_salt_once_passenger_ends());
        await Flow.StepApartAsync("the project host leaves nothing in the temporary folder passenger was given once it ends", () => new McpBuildNodesTests(restored), t => t.The_project_host_leaves_nothing_in_the_temporary_folder_passenger_was_given_once_it_ends());
    }

    /// <summary>The MSBuild nodes a build of Passenger's leaves for its next build, which no other build takes up, end with
    /// Passenger, as it ends or is killed, as MSBuild's shutdown asks (killed, its watchdog asks; Windows has none), leaving
    /// no folder of theirs in its temporary folder. The two projects build at once, each with a step of a few seconds, so
    /// MSBuild runs one in a node the test sees; restored first, with no language server, whose restore would leave a node
    /// no process's child. MSBuild sometimes ends a node it meant to keep (<see cref="NodeBuilds"/>): it builds until one stays.</summary>
    private async Task The_msbuild_nodes_a_build_leaves_for_the_next_end_with_passenger(bool killed)
    {
        Nice.SkipUnlessMSBuildKeepsNodes();
        await TakeFolderAsync();
        string two = await WriteTwoAsync();
        string given = GivenTemporary();
        Dictionary<string, string?> environment = TemporaryVariables(given);
        // No compiler server among what the build starts, which is the machine's to share.
        environment["UseSharedCompilation"] = "false";
        Process passenger = StartPassenger(["--mcp", two], environment: environment, languageServer: false);
        List<int> left = [];
        List<OwnProcess> own = [];
        McpClient? client = null;
        try
        {
            client = await Connect(passenger, ["--mcp", two]);
            for (int build = 1; left.Count == 0; build++)
            {
                await Processes.UntilMSBuildKeepsNodesAsync();
                // The ids a build's steps write are that build's, so what the test sees running is too.
                File.Delete(Path.Combine(two, "A", "step.pid"));
                File.Delete(Path.Combine(two, "B", "step.pid"));
                HashSet<int> before = [.. Processes.Descendants(passenger.Id)];
                Task<JsonElement> building = Call(client, McpTools.Build);
                await WrittenId("two/A/step.pid", "project A's step");
                await WrittenId("two/B/step.pid", "project B's step");
                int[] during = [.. Processes.Descendants(passenger.Id).Where(id => !before.Contains(id))];
                IReadOnlyDictionary<int, string> listed = Processes.Listed();
                JsonElement built = await building;
                Assert.True(built.GetProperty("succeeded").GetBoolean(), built.GetProperty("outcome").GetString());
                left.AddRange(during.Where(Processes.IsRunning));
                own.AddRange(left.Select(OwnProcess.Of));
                if (left.Count == 0 && build == NodeBuilds)
                {
                    Assert.Fail($"{NodeBuilds} builds left nothing for the next build to use, so there is nothing to see end with Passenger.{NoNodeLeft(passenger.Id, during, listed)}");
                }
            }

            if (killed)
            {
                OwnProcess.Of(passenger).Kill(alone: true);
            }
            else
            {
                await client.DisposeAsync();
                client = null;
                Assert.True(passenger.WaitForExit(MainWindow.EndLimit + TimeSpan.FromSeconds(5).Scaled()), "Passenger did not end once its agent left");
            }
            string[] running = [.. left.Where(id => !Processes.Gone(id)).Select(Processes.Describe)];
            Assert.True(running.Length == 0, $"After Passenger {(killed ? "was killed" : "ended")}, {running.Length} of the {left.Count} processes its build left went on:\n{string.Join('\n', running)}");
            if (!killed || !OperatingSystem.IsWindows())
            {
                string[] folders = [.. Directory.EnumerateDirectories(given, "MSBuildTemp*").Select(Path.GetFileName).OfType<string>()];
                Assert.True(folders.Length == 0, $"After Passenger {(killed ? "was killed" : "ended")}, the MSBuild nodes its build left had left {string.Join(", ", folders)} "
                    + "in the temporary folder it was given, as a node killed rather than asked to end leaves its folder for good");
            }
        }
        finally
        {
            ChildProcesses.Kill(passenger);
            own.ForEach(process => process.Kill());
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (Exception gone) when (gone is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    // The server is gone, as the test made it.
                }
            }
        }
    }

    /// <summary>Nothing a build of Passenger's starts goes on under its handshake salt once Passenger ends: no MSBuild server
    /// at all (its builds turn it off), and none of the worker nodes left for its next build, nor a compiler or Razor server
    /// one of them started, which the build is let start as any build does. Counted by the salt each takes from Passenger
    /// (<see cref="Processes.SaltedBuilds"/>), so other builds on the machine are not.</summary>
    private async Task Nothing_a_build_of_passengers_starts_runs_on_under_its_salt_once_passenger_ends()
    {
        Nice.SkipUnlessMSBuildKeepsNodes();
        await TakeFolderAsync();
        string two = await WriteTwoAsync();
        Process passenger = StartPassenger(["--mcp", two], languageServer: false);
        string salt = $"passenger-{passenger.Id}";
        IReadOnlyDictionary<int, string> during = new Dictionary<int, string>();
        McpClient? client = null;
        try
        {
            client = await Connect(passenger, ["--mcp", two]);
            for (int build = 1; !during.Values.Any(IsNode); build++)
            {
                await Processes.UntilMSBuildKeepsNodesAsync();
                JsonElement built = await Call(client, McpTools.Build);
                Assert.True(built.GetProperty("succeeded").GetBoolean(), built.GetProperty("outcome").GetString());
                during = Processes.SaltedBuilds(salt);
                Assert.False(during.Values.Any(c => c.Contains("/nodemode:8", StringComparison.Ordinal)),
                    $"Passenger's build ran in MSBuild's server, which outlives every build and keeps its nodes:\n{string.Join('\n', during.Values)}");
                if (!during.Values.Any(IsNode) && build == NodeBuilds)
                {
                    Assert.Fail($"{NodeBuilds} builds left no node under {salt}, so there is nothing to see end with Passenger.{NoNodeLeft(passenger.Id, [], Processes.Listed())}");
                }
            }
            await client.DisposeAsync();
            client = null;
            Assert.True(passenger.WaitForExit(MainWindow.EndLimit + TimeSpan.FromSeconds(5).Scaled()), "Passenger did not end once its agent left");
            IReadOnlyDictionary<int, string> left = during;
            await Waiting.UntilAsync(() => (left = Processes.SaltedBuilds(salt)).Count == 0, TimeSpan.FromSeconds(10).Scaled(), TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(250));
            Assert.True(left.Count == 0, $"After Passenger ended, {left.Count} of what its builds started went on under its salt {salt}, each holding its memory until "
                + $"MSBuild's idle timeout or for good:\n{string.Join('\n', left.Select(p => $"{p.Key} {p.Value}"))}");
        }
        finally
        {
            ChildProcesses.Kill(passenger);
            foreach (int id in Processes.SaltedBuilds(salt).Keys)
            {
                OwnProcess.Of(id).Kill();
            }
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (Exception gone) when (gone is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    // The server is gone.
                }
            }
        }

        static bool IsNode(string command) => command.Contains("/nodemode:1", StringComparison.Ordinal);
    }

    /// <summary>The project host, whose design-time builds for the language server write in MSBuild's temporary folder and
    /// which Passenger ends as it ends, leaves nothing in the temporary folder Passenger was given.</summary>
    private async Task The_project_host_leaves_nothing_in_the_temporary_folder_passenger_was_given_once_it_ends()
    {
        string folder = await TakeFolderAsync();
        string given = GivenTemporary();
        Process passenger = StartPassenger(["--mcp", folder], environment: TemporaryVariables(given));
        McpClient? client = null;
        try
        {
            client = await Connect(passenger, ["--mcp", folder]);
            // Answered once the language server has the project loaded, which the host built at design time for it.
            await Call(client, McpTools.Diagnostics, new() { ["path"] = "Program.cs" });
            await client.DisposeAsync();
            client = null;
            Assert.True(passenger.WaitForExit(MainWindow.EndLimit + TimeSpan.FromSeconds(5).Scaled()), "Passenger did not end once its agent left");
            await LeftNothingInAsync(given, passenger.Id, killed: false);
        }
        finally
        {
            ChildProcesses.Kill(passenger);
            if (client is not null)
            {
                try
                {
                    await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
                }
                catch (Exception gone) when (gone is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
                {
                    // The server is gone.
                }
            }
        }
    }

    /// <summary>A temporary folder of the test's own for a Passenger, short as the run's is for the runtime's socket in it.</summary>
    private string GivenTemporary()
    {
        string given = Directory.CreateDirectory(Path.Combine(TestRun.Temporary, $"tmp-{Guid.NewGuid():N}"[..12])).FullName;
        DeleteAtEnd(given);
        return given;
    }

    /// <summary>The variables naming a temporary folder: TMPDIR on macOS and Linux, TMP and TEMP on Windows.</summary>
    private static Dictionary<string, string?> TemporaryVariables(string folder) => new() { ["TMPDIR"] = folder, ["TMP"] = folder, ["TEMP"] = folder };

    /// <summary>What a Passenger ended or killed left in the temporary folder it was given, NuGet's scratch aside, which every
    /// restore shares and keeps: nothing, what its project host and language servers write being in folders of its own in
    /// its state's (<see cref="UserTemporary.OfThisProcess"/>), which the watchdog takes once Passenger and its children have
    /// gone. On Windows, with no watchdog, the next Passenger takes them, and they lie in the temporary folder itself (the
    /// user's own there), as do the language server's: there no folder of MSBuild's is left in it.</summary>
    private async Task LeftNothingInAsync(string given, int passenger, bool killed)
    {
        string scratch = Path.GetFileName(NuGetScratch.In(given, OperatingSystem.IsLinux(), Environment.UserName));
        string id = passenger.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string[] kept = [UserTemporary.Of(OwnState, SdkTemporary.Kind), UserTemporary.Of(Path.Combine(OwnState, "cache"), global::Passenger.Lsp.RoslynServerFolder.TemporariesKind)];
        string[] left = [];
        string[] own = [];
        // The watchdog's work, and the runtime's own as each process ends, may still be under way.
        await Waiting.UntilAsync(() => (left = [.. Directory.EnumerateFileSystemEntries(given).Select(Path.GetFileName).OfType<string>()
                .Where(name => name != scratch && (!OperatingSystem.IsWindows() || name.StartsWith("MSBuildTemp", StringComparison.Ordinal)))]).Length == 0
            && (own = OperatingSystem.IsWindows() ? [] : [.. kept.Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories)
                .Where(folder => Path.GetFileName(folder) is var name && (name == id || name.StartsWith(id + "-", StringComparison.Ordinal)))]).Length == 0,
            TimeSpan.FromSeconds(10).Scaled(), TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(250));
        Assert.True(left.Length == 0, $"After Passenger {(killed ? "was killed" : "ended")}, what it started left {string.Join(", ", left)} "
            + "in the temporary folder it was given, a folder for each MSBuild process it ended");
        Assert.True(own.Length == 0, $"After Passenger {(killed ? "was killed" : "ended")}, its own temporary folders stayed: {string.Join(", ", own)}");
    }

    /// <summary>Two projects of one solution, restored, each with a step of a few seconds, so a build of both runs one in a
    /// node; the step writes its process's id to step.pid.</summary>
    private async Task<string> WriteTwoAsync()
    {
        string two = Path.Combine(Folder, "two");
        Directory.CreateDirectory(Path.Combine(two, "A"));
        Directory.CreateDirectory(Path.Combine(two, "B"));
        Write("two/Two.slnx", "<Solution>\n  <Project Path=\"A/A.csproj\" />\n  <Project Path=\"B/B.csproj\" />\n</Solution>\n");
        foreach (string project in new[] { "two/A/A.csproj", "two/B/B.csproj" })
        {
            Write(project, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>{TestProjects.Framework}</TargetFramework>
                  </PropertyGroup>
                  <Target Name="Wait" BeforeTargets="Build" Condition="'$(DesignTimeBuild)' != 'true'">
                    <Exec Condition="'$(OS)' != 'Windows_NT'" Command="echo $$ &gt; step.pid; sleep 3" />
                    <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Set-Content -Path step.pid -Value $PID; Start-Sleep -Seconds 3&quot;" />
                  </Target>
                </Project>
                """);
        }
        await Restore.RunAsync(two);
        return two;
    }

    /// <summary>The builds the test makes for one to leave a node: rarely, on a loaded machine, MSBuild ends a node it meant
    /// to keep, and the next build starts one again.</summary>
    private const int NodeBuilds = 3;

    /// <summary>Why the last build may have left no node, for its failure to say: what it ran (id, parent, nice value,
    /// command) and Passenger's MSBuild nodes now, with their MSBuild variables.</summary>
    private static string NoNodeLeft(int passenger, IReadOnlyList<int> ran, IReadOnlyDictionary<int, string> listed)
    {
        IReadOnlyDictionary<int, string> now = Processes.Listed();
        IReadOnlyDictionary<int, string> nodes = Processes.MSBuildNodes();
        string[] ours = [.. nodes.Where(n => $"{n.Value} ".Contains($"passenger-{passenger} ", StringComparison.Ordinal)).Select(n => $"{Seen(n.Key)} {n.Value}")];
        (int counted, int most) = Processes.MSBuildNodesCounted();
        return $"\nPassenger: {Seen(passenger)}\nWhat its build ran while both steps did:\n{string.Join('\n', ran.Select(Seen))}" +
            $"\nPassenger's MSBuild nodes now:\n{string.Join('\n', ours)}" +
            $"\nMSBuild keeps no node at a nice value other than 0, nor while it counts more than {most} nodes (the MSBuild executable's): {counted} of the machine's {nodes.Count} run;" +
            " and a node it meant to keep ends when its build closes their pipe first, now and then";

        string Seen(int id) => listed.TryGetValue(id, out string? line) || now.TryGetValue(id, out line) ? line : Processes.Describe(id);
    }
}
