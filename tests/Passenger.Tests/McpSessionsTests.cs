using System.Text.Json;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent on a window debugging two programs at once: debug_state lists both, a debugging tool that names
/// neither is refused while it cannot tell which is meant, one that names a program acts on it alone, stop with a
/// program's name ends that one, and stop with detach lets go of the one it names.</summary>
[Collection(Alone.Name)]
public sealed class McpSessionsTests : McpRig
{
    /// <summary>Both launched, then one launched and one attached to, in one window over the pair: the agent steps and stops
    /// the one it names, and lets go of the one the debugger attached to, which runs on to its end while the other stays
    /// paused; one the debugger started is refused, and with both paused one named by neither cannot be told.</summary>
    [AvaloniaFact]
    public void An_agent_names_which_of_two_programs_debugged_at_once_it_acts_on_or_detaches_and_is_refused_when_it_names_none_it_can_tell()
    {
        string pair = TestFolders.Create("pair-");
        DeleteAtEnd(pair);
        DebugSessionsTests.Pair.WriteTo(pair);
        Restore.Run(pair);
        string counter = Path.Combine(PathNames.Canonical(pair), "Shared", "Counter.cs");
        MainWindow window = AgentsWindow(pair, languageServer: false);
        window.Debugger.Breakpoints.Toggle(counter, DebugSessionsTests.Pair.Next);
        DebugSession first = DebugSessionsTests.Debug(window, "First");
        DebugSession second = DebugSessionsTests.Debug(window, "Second");
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        System.Diagnostics.Process? program = null;
        try
        {
            JsonElement state = Unasked(window, client, McpTools.DebugState, new() { ["session"] = "First" });
            Assert.Equal(["First: paused", "Second: paused"], state.GetProperty("sessions").EnumerateArray().Select(s => s.GetString()));
            Assert.Equal("First", state.GetProperty("session").GetString());

            CallToolResult refused = Await(Task.Run(() => Result(client, McpTools.DebugStep)), "the agent's unnamed step");
            string why = Text(refused);
            Assert.True(refused.IsError == true && why.Contains("First: paused", StringComparison.Ordinal) && why.Contains("Second: paused", StringComparison.Ordinal)
                && why.Contains("session", StringComparison.Ordinal), $"An unnamed step with two programs paused was answered: {why}");

            JsonElement stepped = Unasked(window, client, McpTools.DebugStep, new() { ["session"] = "First" });
            Assert.True(stepped.GetProperty("session").GetString() == "First" && stepped.GetProperty("location").GetString()!.Contains($"Counter.cs:{DebugSessionsTests.Pair.Next + 2}:", StringComparison.Ordinal),
                $"The agent's step of First was answered: {stepped}");
            Assert.Same(first, window.Debugger.Session);
            Assert.Equal(DebugState.Paused, second.State);
            JsonElement other = Unasked(window, client, McpTools.DebugState, new() { ["session"] = "Second" });
            Assert.True(other.GetProperty("location").GetString()!.Contains($"Counter.cs:{DebugSessionsTests.Pair.Next + 1}:", StringComparison.Ordinal),
                $"Stepping First moved Second, whose state is: {other}");

            Assert.Equal("Stopped Second", Unasked(window, client, McpTools.Stop, new() { ["session"] = "Second" }).GetString());
            WaitFor(() => second.State == DebugState.Ended, "the second program to end");
            Assert.Equal(DebugState.Paused, first.State);
            JsonElement went = Unasked(window, client, McpTools.DebugContinue);
            Assert.True(went.GetProperty("session").GetString() == "First" && went.GetProperty("location").GetString()!.Contains($"Counter.cs:{DebugSessionsTests.Pair.Next + 1}:", StringComparison.Ordinal),
                $"The agent's unnamed continue, with only First debugged, was answered: {went}");
            Assert.Equal("Stopped", Unasked(window, client, McpTools.Stop).GetString());
            WaitFor(() => !window.Debugger.IsDebugging, "every program to end");

            // First, built by its first launch, started held for the debugger and attached to; each stops on Main's line 3.
            window.Debugger.Breakpoints.Toggle(counter, DebugSessionsTests.Pair.Next);
            window.Debugger.Breakpoints.Toggle(Path.Combine(PathNames.Canonical(pair), "First", "Program.cs"), 3);
            window.Debugger.Breakpoints.Toggle(Path.Combine(PathNames.Canonical(pair), "Second", "Program.cs"), 3);
            second = DebugSessionsTests.Debug(window, "Second");
            string built = Path.Combine(pair, "First", "bin", "Debug", TestProjects.Framework, OperatingSystem.IsWindows() ? "First.exe" : "First");
            System.Diagnostics.ProcessStartInfo start = ChildProcesses.Command(built, []);
            start.Environment["DOTNET_DefaultDiagnosticPortSuspend"] = "1";
            start.RedirectStandardOutput = true;
            program = ChildProcesses.Start(start);
            Task<string> written = program.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Assert.Null(Await(window.Debugger.AttachAsync(program.Id, "First"), "the debugger to attach to First"));
            first = window.Debugger.SessionNamed("First")!;
            WaitFor(() => first.State == DebugState.Paused && second.State == DebugState.Paused, "both programs to stop at their breakpoints");

            CallToolResult unnamed = Await(Task.Run(() => Result(client, McpTools.Stop, new() { ["detach"] = true })), "the agent's unnamed detach");
            why = Text(unnamed);
            Assert.True(unnamed.IsError == true && why.Contains("First: paused", StringComparison.Ordinal) && why.Contains("Second: paused", StringComparison.Ordinal),
                $"An unnamed detach with two programs paused was answered: {why}");

            CallToolResult launched = Await(Task.Run(() => Result(client, McpTools.Stop, new() { ["detach"] = true, ["session"] = "Second" })), "the agent's detach of Second");
            string notDetached = Text(launched);
            Assert.True(launched.IsError == true && notDetached.Contains("The debugger started this program", StringComparison.Ordinal),
                $"The detach of a program the debugger started was answered: {notDetached}");

            string detached = Unasked(window, client, McpTools.Stop, new() { ["detach"] = true, ["session"] = "First" }).GetString()!;
            Assert.True(detached.StartsWith("Detached from First, which runs on", StringComparison.Ordinal), $"The detach of First was answered: {detached}");
            Assert.True(program.WaitForExit(TimeSpan.FromSeconds(30).Scaled()), "First, let go, did not run on to its end");
            Assert.Equal("first 3", written.Result.Trim());
            Assert.Equal(DebugState.Paused, second.State);
            Assert.Equal("Stopped", Unasked(window, client, McpTools.Stop).GetString());
            WaitFor(() => !window.Debugger.IsDebugging, "every program to end");
        }
        finally
        {
            Gone(client);
            window.Close();
            if (program is { HasExited: false })
            {
                Processes.Kill(program.Id);
            }
            program?.Dispose();
        }
    }
}
