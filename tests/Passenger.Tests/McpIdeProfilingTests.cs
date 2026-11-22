using System.Diagnostics;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.App;
using Passenger.Core;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>Attach Profiler for an agent, and why a memory profile has no heap, McpIdeTests' where what it tests is held to a
/// time: a profile's samples, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class McpIdeProfilingTests : McpRig, IClassFixture<RestoredFolders>
{
    /// <summary>The fixture each step of a flow takes for an instance of its own.</summary>
    private readonly RestoredFolders restored;

    public McpIdeProfilingTests(RestoredFolders folders)
        : base(folders) => restored = folders;

    /// <summary>An agent's profiles as one flow, each step one test's check in an instance of its own: a program
    /// already running profiled and left running, a memory profile saying why it has no heap, and a program the
    /// debugger holds answered at once, leaving the next free.</summary>
    [Fact]
    public async Task An_agent_profiles_a_running_program_and_says_why_one_has_no_heap_or_is_held_by_the_debugger()
    {
        await Flow.StepApartAsync("an agent profiles a program already running and leaves it running", () => new McpIdeProfilingTests(restored), t => t.An_agent_profiles_a_program_already_running_and_leaves_it_running());
        await Flow.StepApartAsync("an agents memory profile says why it has no heap", () => new McpIdeProfilingTests(restored), t => t.An_agents_memory_profile_says_why_it_has_no_heap());
        await Flow.StepApartAsync("an agents profile of a program the debugger holds answers at once and leaves the next free", () => new McpIdeProfilingTests(restored), t => t.An_agents_profile_of_a_program_the_debugger_holds_answers_at_once_and_leaves_the_next_free());
    }

    /// <summary>Attach Profiler for an agent: the program Run started, profiled from now for a while, left running after. Its
    /// hot spot is named at the editor's line, below a comment whose breaks the compiler counted into the PDB's lines.</summary>
    private async Task An_agent_profiles_a_program_already_running_and_leaves_it_running()
    {
        await TakeFolderAsync();
        Write("Program.cs", $"File.WriteAllText(Path.Combine(@\"{Folder}\", \"spin.pid\"), Environment.ProcessId.ToString());\n"
            + "long turns = 0;\nwhile (true)\n{\n    turns = Spin.Turn(turns);\n}\n\n"
            + $"/* Pasted:{(char)0x2028}a{(char)0x2029}b{(char)0x85}c\rd */\nstatic class Spin\n{{\n    public static long Turn(long turns) => turns + 1;\n}}\n");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        CallToolResult unattached = await Result(client, McpTools.Profile, new() { ["attach"] = true, ["seconds"] = 1 });
        Assert.True(unattached.IsError == true, $"Attach Profiler with nothing running answered: {Text(unattached)}");

        Assert.True((await Call(client, McpTools.Run, new() { ["waitSeconds"] = 0 })).GetProperty("stillRunning").GetBoolean());
        int spinning = await WrittenId("spin.pid", "the program to start");
        JsonElement profiled = await Call(client, McpTools.Profile, new() { ["attach"] = true, ["seconds"] = 2 });
        Assert.True(profiled.GetProperty("samples").GetInt32() > 0, $"Attach Profiler sampled nothing: {profiled.GetRawText()}");
        // The editor's line 11, which the compiler, ending a line at each of the comment's four breaks, numbered 15.
        JsonElement turn = Assert.Single(profiled.GetProperty("hotSpots").EnumerateArray(), h => h.GetProperty("method").GetString()!.Contains("Spin.Turn", StringComparison.Ordinal));
        Assert.True(turn.TryGetProperty("source", out JsonElement source) && source.GetString() == "Program.cs:11", $"Spin.Turn's hot spot is not named at its line: {turn}");
        using Process program = Process.GetProcessById(spinning);
        Assert.True(DotnetProcesses.Runs(program), "The program an agent attached the profiler to ended with its snapshot");
        await Call(client, McpTools.Stop);
    }

    /// <summary>An agent's profile asking for a memory snapshot hears why it has no heap: without seconds, before anything is
    /// built or run, as the heap is read from the program as it runs; with seconds, of a program that ended before they
    /// passed, in the answer's heap note.</summary>
    private async Task An_agents_memory_profile_says_why_it_has_no_heap()
    {
        await TakeFolderAsync();
        Write("Program.cs", $"File.WriteAllText(Path.Combine(@\"{Folder}\", \"ran.txt\"), \"ran\");\nConsole.WriteLine(\"done\");\n");
        string ran = Path.Combine(Folder, "ran.txt");
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);

        CallToolResult refused = await Result(client, McpTools.Profile, new() { ["memory"] = true });
        Assert.True(refused.IsError == true && Text(refused).Contains(PassengerTools.MemoryWithoutSeconds, StringComparison.Ordinal),
            $"A memory profile without seconds answered: {Text(refused)}");
        Assert.False(File.Exists(ran), "A memory profile refused for want of seconds ran the program all the same");

        JsonElement ended = await Call(client, McpTools.Profile, new() { ["memory"] = true, ["seconds"] = 60 });
        Assert.True(File.Exists(ran), $"The memory profile with seconds never ran the program: {ended.GetRawText()}");
        Assert.True(!ended.TryGetProperty("heap", out JsonElement heap) || heap.ValueKind == JsonValueKind.Null, $"The program that ended at once has a heap: {heap}");
        Assert.True(ended.TryGetProperty("heapNote", out JsonElement note) && note.GetString()?.StartsWith("The program ended before its memory snapshot", StringComparison.Ordinal) == true,
            $"The memory profile of a program that ended before its seconds passed says nothing of why it has no heap: {ended.GetRawText()}");
    }

    /// <summary>An agent's profile of a program the debugger holds answers at once rather than waiting on it: refused while
    /// the program is stopped at a breakpoint; and, the program stopping at another as the profile's time runs out, its run
    /// let go unread, saying so; neither leaves a later profile refused as one already under way.</summary>
    private async Task An_agents_profile_of_a_program_the_debugger_holds_answers_at_once_and_leaves_the_next_free()
    {
        await TakeFolderAsync();
        Write("Program.cs", "using System.Diagnostics;\n"
            + "Console.WriteLine(\"begun\");\n"
            + "Stopwatch clock = Stopwatch.StartNew();\n"
            + "long turns = 0;\n"
            + "while (clock.Elapsed < TimeSpan.FromSeconds(3)) { turns = Spin.Turn(turns); }\n"
            + "Console.WriteLine($\"spun {turns}\");\n"
            + "while (true) { turns = Spin.Turn(turns); }\n"
            + "static class Spin\n{\n    public static long Turn(long turns) => turns + 1;\n}\n");
        const string Held = "is paused in the debugger: continue it to profile";
        await using McpClient client = await Start(["--mcp", Folder], languageServer: false);
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 2 });
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "Program.cs", ["line"] = 6 });
        await Call(client, McpTools.DebugStart, new() { ["waitSeconds"] = 120 });

        CallToolResult refused = await Result(client, McpTools.Profile, new() { ["attach"] = true, ["seconds"] = 1 });
        Assert.True(refused.IsError == true && Text(refused).Contains(Held, StringComparison.Ordinal),
            $"An agent's profile of the program stopped at its breakpoint answered: {Text(refused)}");

        // On to the second breakpoint, three seconds on, which it reaches as the profile's six seconds run.
        await Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 0 });
        CallToolResult letGo = await Result(client, McpTools.Profile, new() { ["attach"] = true, ["seconds"] = 6 });
        Assert.True(letGo.IsError == true && Text(letGo).Contains($"{Held} (its run so far was let go unread)", StringComparison.Ordinal),
            $"An agent's profile of the program that stopped at a breakpoint as it ran answered: {Text(letGo)}");

        await Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 0 });
        JsonElement profiled = await Call(client, McpTools.Profile, new() { ["attach"] = true, ["seconds"] = 1 });
        Assert.True(profiled.GetProperty("samples").GetInt32() > 0, $"Attach Profiler sampled nothing once the program went on: {profiled.GetRawText()}");
        await Call(client, McpTools.Stop);
    }
}
