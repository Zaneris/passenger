using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Passenger.App;
using Passenger.App.Mcp;
using Passenger.App.Settings;
using Passenger.App.State;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>McpAttachTests' agents where what is tested is held to a time: an attach given up during initialization, one
/// past a full socket backlog, an attached diagnostic on a suspended server given up or shut out, and a call nobody answers
/// coming back at the window's limit, also while its word to a slow agent is still written. So the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class McpAttachTimingTests : McpRig, IClassFixture<RestoredFolders>
{
    /// <summary>The fixture each step of a flow takes for an instance of its own.</summary>
    private readonly RestoredFolders restored;

    public McpAttachTimingTests(RestoredFolders folders)
        : base(folders) => restored = folders;

    /// <summary>An attach under time as one flow, each step one test's check in an instance of its own: an attach
    /// cancelled as it initializes closing its stream without a queued call losing its join, a diagnostic waiting on a
    /// suspended server stopping when cancelled or shut out, a call nobody answers coming back at the window's limit
    /// saying what it waits on, and a call ending while its word to a slow agent is written coming back after
    /// it.</summary>
    [AvaloniaFact]
    public void An_attached_agents_calls_come_back_at_their_limit_or_when_cancelled_never_losing_a_join_or_a_word()
    {
        Flow.StepApart("cancelling an attach during initialization closes its stream and a queued call does not lose the join", () => new McpAttachTimingTests(restored), t => t.Cancelling_an_attach_during_initialization_closes_its_stream_and_a_queued_call_does_not_lose_the_join());
        Flow.StepApart("an attached diagnostic stops waiting on a suspended server when cancelled or shut out (false)", () => new McpAttachTimingTests(restored), t => t.An_attached_diagnostic_stops_waiting_on_a_suspended_server_when_cancelled_or_shut_out(false));
        Flow.StepApart("an attached diagnostic stops waiting on a suspended server when cancelled or shut out (true)", () => new McpAttachTimingTests(restored), t => t.An_attached_diagnostic_stops_waiting_on_a_suspended_server_when_cancelled_or_shut_out(true));
        Flow.StepApart("an agents call nobody answers comes back at the windows limit and hears what it waits on meanwhile", () => new McpAttachTimingTests(restored), t => t.An_agents_call_nobody_answers_comes_back_at_the_windows_limit_and_hears_what_it_waits_on_meanwhile());
        Flow.StepApart("a call that ends while its word to a slow agent is still written comes back after that word", () => new McpAttachTimingTests(restored), t => t.A_call_that_ends_while_its_word_to_a_slow_agent_is_still_written_comes_back_after_that_word());
    }

    private void Cancelling_an_attach_during_initialization_closes_its_stream_and_a_queued_call_does_not_lose_the_join()
    {
        string notes = Plain("mcp-initialize-");
        MainWindow window = AgentsWindow(notes, languageServer: false);
        McpClient? client = null;
        using CancellationTokenSource firstCancel = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        using CancellationTokenSource queuedCancel = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        try
        {
            using WithheldWindow fake = new(window);
            client = Await(Waiting.Apart(() => Start(["--mcp-attach", notes])), "the agent's session starting");
            using CancellationTokenSource acceptLimit = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            acceptLimit.CancelAfter(TimeSpan.FromSeconds(10).Scaled());
            Task<Socket> accepting = fake.Listener.AcceptAsync(acceptLimit.Token).AsTask();
            Task<CallToolResult> first = Waiting.Apart(() => CallGivingUp(client, McpTools.Workspace, new(), firstCancel.Token));
            using Socket accepted = Await(accepting, "the proxy connecting to the withheld window");
            using NetworkStream stream = new(accepted, ownsSocket: false);
            using StreamReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
            string? initialize = Await(reader.ReadLineAsync(Cancel).AsTask().WaitAsync(TimeSpan.FromSeconds(10).Scaled(), Cancel), "the proxy's initialize request");
            Assert.NotNull(initialize);
            using JsonDocument request = JsonDocument.Parse(initialize);
            Assert.Contains(request.RootElement.GetProperty("method").GetString(), new[] { "server/discover", "initialize" });

            TaskCompletionSource queuedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<CallToolResult> queued = Waiting.Apart(async () =>
            {
                Task<CallToolResult> call = CallGivingUp(client, McpTools.Workspace, new(), queuedCancel.Token);
                queuedStarted.SetResult();
                return await call;
            });
            Ui.PumpUntil(() => queuedStarted.Task.IsCompleted, "the second call starting while initialization holds the join", TimeSpan.FromSeconds(3));
            queuedCancel.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Await(queued.WaitAsync(TimeSpan.FromSeconds(3).Scaled(), Cancel), "the queued call giving up"));
            Assert.False(first.IsCompleted, "Cancelling a queued call ended the join another call still owns");

            Task<JsonElement> retry = Waiting.Apart(() => Call(client, McpTools.Workspace));
            Assert.False(Await(Waiting.Apart(() => Waiting.Until(() => fake.Listener.Poll(0, SelectMode.SelectRead), TimeSpan.FromSeconds(1))),
                "checking whether the cancelled waiter let another join pass"), "Cancelling a queued call released another call's join lock");
            Assert.False(retry.IsCompleted, "A third call passed the join while initialization was still held");
            fake.Dispose();
            firstCancel.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => Await(first.WaitAsync(TimeSpan.FromSeconds(3).Scaled(), Cancel), "the initializing call giving up"));
            Await(reader.ReadToEndAsync(Cancel).WaitAsync(TimeSpan.FromSeconds(10).Scaled(), Cancel),
                "the cancelled initialization's stream to reach EOF");
            Assert.Equal(window.Folder, Await(retry.WaitAsync(TimeSpan.FromSeconds(10).Scaled(), Cancel), "the next call joining the real window").GetProperty("folder").GetString());
        }
        finally
        {
            firstCancel.Cancel();
            queuedCancel.Cancel();
            try
            {
                if (client is not null)
                {
                    Gone(client);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaFact]
    public void An_attach_passes_a_full_socket_backlog_and_joins_the_next_window()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "This test fills a Linux Unix-domain socket backlog");
        string notes = Plain("mcp-backlog-");
        MainWindow window = AgentsWindow(notes, languageServer: false);
        McpClient? client = null;
        List<Socket> fillers = [];
        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        try
        {
            using WithheldWindow fake = new(window);
            bool full = false;
            for (int i = 0; i < 16; i++)
            {
                Socket filler = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
                fillers.Add(filler);
                try
                {
                    filler.Connect(new UnixDomainSocketEndPoint(fake.Path));
                }
                catch (SocketException blocked) when (blocked.SocketErrorCode == SocketError.WouldBlock)
                {
                    full = true;
                    break;
                }
            }
            Assert.True(full, "Sixteen nonblocking connections did not fill the withheld window's backlog");
            client = Await(Waiting.Apart(() => Start(["--mcp-attach", notes])), "the agent's session starting");
            Task<CallToolResult> workspace = Waiting.Apart(() => CallGivingUp(client, McpTools.Workspace, new(), giveUp.Token));
            CallToolResult result = Await(workspace.WaitAsync(TimeSpan.FromSeconds(15).Scaled(), Cancel), "the proxy passing the full backlog and joining the real window");
            Assert.False(result.IsError == true, Text(result));
            using JsonDocument seen = JsonDocument.Parse(Text(result));
            Assert.Equal(window.Folder, seen.RootElement.GetProperty("folder").GetString());
        }
        finally
        {
            giveUp.Cancel();
            foreach (Socket filler in fillers)
            {
                filler.Dispose();
            }
            try
            {
                if (client is not null)
                {
                    Gone(client);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    private void An_attached_diagnostic_stops_waiting_on_a_suspended_server_when_cancelled_or_shut_out(bool revokeConnection)
    {
        MainWindow window = AgentsWindow(Folder);
        McpClient? client = null;
        OwnProcess suspended = default;
        Task<CallToolResult>? diagnostic = null;
        using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        TaskCompletionSource refreshesHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.OpenFile(Path.Combine(window.Folder!, "Helper.cs"));
            Ui.Settle();
            // Taken here, on the UI thread, where the folder is restored.
            string taken = Folder;
            client = Await(Waiting.Apart(() => Start(["--mcp-attach", taken])), "the agent attaching");
            Await(Waiting.Apart(() => Call(client, McpTools.Diagnostics, new() { ["path"] = "Helper.cs" })), "the language server loading the sample");
            // The window's own pull of the file it shows, at the server's word that its diagnostics moved, would wait on it too.
            window.CSharpService!.HoldRefreshes(refreshesHeld.Task);
            Ui.Settle();
            Assert.DoesNotContain(InFlight.Now(), AsksTheServer);
            Passenger.Lsp.LanguageServer server = window.CSharpService!.Session!.Server;
            (server.SlowRequest, server.ResponseLimit) = (TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
            suspended = OwnProcess.Of(server.ProcessId);
            suspended.Suspend("The test's language server");

            // A file the server has not been asked of, so the call waits on it (Helper.cs's last pull would answer it).
            diagnostic = Waiting.Apart(() => revokeConnection
                ? Result(client, McpTools.Diagnostics, new() { ["path"] = "Program.cs" })
                : CallGivingUp(client, McpTools.Diagnostics, new() { ["path"] = "Program.cs" }, giveUp.Token));
            Ui.PumpUntil(() => InFlight.Now().Any(AsksTheServer), "the attached agent's diagnostic to wait on the suspended server", TimeSpan.FromSeconds(10));
            if (revokeConnection)
            {
                window.ApplySettings(window.Settings with { McpServer = false });
                CallToolResult refused = Ui.AwaitPumped(diagnostic, "the shut-out agent's diagnostic to return", TimeSpan.FromSeconds(10));
                Assert.True(refused.IsError == true, Text(refused));
                Assert.Contains("connection to the Passenger window ended", Text(refused), StringComparison.Ordinal);
                Ui.PumpUntil(() => window.Agents == 0, "the still-open window to let the agent go", TimeSpan.FromSeconds(10));
                Assert.True(window.IsVisible, "Shutting out an agent closed the person's window");
            }
            else
            {
                giveUp.Cancel();
                Ui.PumpUntil(() => diagnostic.IsCompleted, "the agent's diagnostic giving up", TimeSpan.FromSeconds(10));
                Assert.ThrowsAny<OperationCanceledException>(() => Ui.AwaitPumped(diagnostic, "the agent's diagnostic giving up"));
            }
            Assert.True(Ui.Pumped(() => !InFlight.Now().Any(AsksTheServer), TimeSpan.FromSeconds(10)),
                $"The diagnostic's request never cleared while its server stayed suspended; in flight:\n{InFlight.Describe(InFlight.Now(), System.Diagnostics.Stopwatch.GetTimestamp())}");
            Assert.True(Processes.IsRunning(suspended.Id), "The request cleared only because its language server ended");
        }
        finally
        {
            if (diagnostic is not null)
            {
                // Left unawaited by a failure above, its end as the agent goes is no later test's fault.
                UnawaitedWork.Dismiss(diagnostic);
            }
            giveUp.Cancel();
            suspended.Kill();
            refreshesHeld.TrySetResult();
            try
            {
                if (client is not null)
                {
                    Gone(client);
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    /// <summary>A request an agent's C# question waits on the language server for: the take-in of what was written on disk,
    /// which comes first, or the file's diagnostics.</summary>
    private static bool AsksTheServer((string What, long Since) piece) =>
        piece.What.Contains(Passenger.Lsp.LspMethods.DispatchExtensionMessage, StringComparison.Ordinal)
        || piece.What.Contains(Passenger.Lsp.LspMethods.DocumentDiagnostic, StringComparison.Ordinal);

    /// <summary>A live socket for this process and folder, ranked before the real window, which never answers initialize.</summary>
    private sealed class WithheldWindow : IDisposable
    {
        // Named as a window's socket is, short: macOS takes a socket path of at most 104 bytes, and its temporary folder is long.
        public string Path { get; } = System.IO.Path.Combine(McpHost.SocketFolder, $"{ProcessId}-held{Guid.NewGuid().ToString("N")[..6]}.sock");
        public Socket Listener { get; } = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        public WithheldWindow(MainWindow window)
        {
            try
            {
                Listener.Bind(new UnixDomainSocketEndPoint(Path));
                Listener.Listen(1);
                File.WriteAllText(System.IO.Path.ChangeExtension(Path, ".folder"), window.Folder);
                File.SetLastWriteTimeUtc(Path, File.GetLastWriteTimeUtc(window.AgentSocket!).AddMinutes(1));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Listener.Dispose();
            File.Delete(Path);
            File.Delete(System.IO.Path.ChangeExtension(Path, ".folder"));
        }
    }

    /// <summary>Nobody at the window: an agent's call that waits for the person to let it in, and one that waits for their
    /// leave to build, each come back once the window's limit is up, saying nobody answered, the question gone; meanwhile
    /// the status bar says who waits on what and the agent hears, through --mcp-attach, what it waits on; a later call asks
    /// again. With protocol traces on, mcp.log has each call's waits and ends with their times.</summary>
    private void An_agents_call_nobody_answers_comes_back_at_the_windows_limit_and_hears_what_it_waits_on_meanwhile()
    {
        TimeSpan limit = TimeSpan.FromSeconds(5).Scaled();
        string unattended = Plain("mcp-unanswered-");
        MainWindow window = new() { Width = 1200, Height = 800, AgentAnswerLimit = limit, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(unattended);
        Ui.Settle();
        window.ApplySettings(window.Settings with { TraceProtocols = true });
        string where = $"the Passenger window on {window.Folder}";
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            Heard letIn = new();
            Task<CallToolResult> unanswered = Waiting.Apart(() => Result(client, McpTools.Workspace, heard: letIn));
            string? said = null;
            WaitFor(() => window.OwnedWindows.OfType<AgentAdmissionDialog>().Any(d => d.IsVisible) && (said = window.AgentStatus) is not null,
                "the window to ask whether the agent may work there");
            Assert.Equal("Test Agent waits for you: let it in?", said);
            CallToolResult notLetIn = Await(unanswered, "the call nobody let in");
            Assert.True(notLetIn.IsError == true && Text(notLetIn).Contains($"Nobody at {where} answered within {Durations.Said(limit)} whether Test Agent may work there. A later call asks again.", StringComparison.Ordinal),
                $"The call nobody answered was answered: {Text(notLetIn)}");
            Assert.DoesNotContain(window.OwnedWindows.OfType<AgentAdmissionDialog>(), d => d.IsVisible);
            Assert.Null(window.AgentStatus);
            Assert.True(letIn.Told.Any(t => t.Message?.StartsWith($"Waiting for the person at {where} to let Test Agent in", StringComparison.Ordinal) == true),
                $"The agent never heard what its call waited on: {letIn}");

            window.ApplySettings(window.Settings with { McpServer = true });
            Heard leave = new();
            Task<CallToolResult> unallowed = Waiting.Apart(() => Result(client, McpTools.Build, heard: leave));
            said = null;
            WaitFor(() => window.OwnedWindows.OfType<AgentToolDialog>().Any(d => d.IsVisible) && (said = window.AgentStatus) is not null,
                "the window to ask whether the agent may build");
            Assert.Equal("Test Agent waits for you: allow build?", said);
            Assert.Contains(Texts(window.OwnedWindows.OfType<AgentToolDialog>().Single(d => d.IsVisible)),
                t => t.StartsWith("Test Agent, connected through Passenger's MCP server, wants to build", StringComparison.Ordinal));
            CallToolResult notAllowed = Await(unallowed, "the build nobody allowed");
            Assert.True(notAllowed.IsError == true && Text(notAllowed).Contains($"Nobody at {where} answered within {Durations.Said(limit)} whether Test Agent may use build. A later call asks again.", StringComparison.Ordinal),
                $"The build nobody allowed was answered: {Text(notAllowed)}");
            WaitFor(() => !window.OwnedWindows.OfType<AgentToolDialog>().Any(d => d.IsVisible) && window.AgentStatus == "Agent connected",
                "the question nobody answered to close and the status bar to stop saying the agent waits");
            Assert.False(window.Builder.IsRunning, "A build nobody allowed ran");
            IReadOnlyList<ModelContextProtocol.ProgressNotificationValue> told = leave.Told;
            Assert.True(told.Any(t => t.Message?.StartsWith($"Waiting for the person at {where} to allow Test Agent to use build", StringComparison.Ordinal) == true),
                $"The agent never heard its build waited for the person: {leave}");
            // The SDK's client hands the progress over as it comes, not always in order.
            Assert.True(told.Select(t => t.Progress).Distinct().Count() == told.Count, $"The progress told through --mcp-attach repeated a count: {leave}");
            // The window's word passed on as it said it, never the proxy's own beat over it.
            Assert.True(!told.Any(t => System.Text.RegularExpressions.Regex.IsMatch(t.Message ?? string.Empty, $@"\({SaidWhile.Pattern}\) \({SaidWhile.Pattern}\)$")),
                $"--mcp-attach told its own beat over the window's words: {leave}");

            Task<CallToolResult> again = Waiting.Apart(() => Result(client, McpTools.Build));
            Press(Question(window, "the question asked again"), "Deny");
            Assert.Contains("did not allow build", Text(Await(again, "the build the person denied")), StringComparison.Ordinal);

            IdeLog.Flush();
            string trace = Ui.Shared(IdeLog.PathOf(IdeLog.Mcp));
            foreach (string line in (string[])[
                "workspace] began, from Test Agent, told progress",
                $"workspace] waiting: Waiting for the person at {where} to let Test Agent in",
                "workspace] answered an error after ",
                $"build] waiting: Waiting for the person at {where} to allow Test Agent to use build",
                "build] answered an error after "])
            {
                Assert.True(trace.Contains(line, StringComparison.Ordinal), $"mcp.log does not have \"{line}\":\n{string.Join('\n', trace.Split('\n').TakeLast(30))}");
            }
            Assert.Matches($@"\[window call \d+ build\] waited {SaidWhile.Pattern} \(Waiting for the person .* to allow Test Agent to use build .*\): Unanswered", trace);
        }
        finally
        {
            window.ApplySettings(window.Settings with { TraceProtocols = false });
            Gone(client);
            window.Close();
        }
    }

    /// <summary>A slow agent: the window's word of what a call waits on is still being written to it when the call ends
    /// at the window's limit. The word goes whole first and the answer after it, on a line of its own, and --mcp-attach
    /// passes both on in that order, so the agent hears both; a word cut short as the call ended would have taken the
    /// answer with it, and the call would never come back.</summary>
    private void A_call_that_ends_while_its_word_to_a_slow_agent_is_still_written_comes_back_after_that_word()
    {
        TimeSpan limit = TimeSpan.FromSeconds(5).Scaled();
        string unattended = Plain("mcp-stalled-");
        MainWindow window = new() { Width = 1200, Height = 800, AgentAnswerLimit = limit, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(unattended);
        Ui.Settle();
        string where = $"the Passenger window on {window.Folder}";
        McpClient client = Await(Waiting.Apart(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        Heard told = new();
        IAsyncDisposable hearing = Heard.All(client, told);
        try
        {
            Heard letIn = new();
            Task<CallToolResult> unanswered = Waiting.Apart(() => Result(client, McpTools.Workspace, heard: letIn));
            WaitFor(() => window.OwnedWindows.OfType<AgentAdmissionDialog>().Any(d => d.IsVisible), "the window to ask whether the agent may work there");
            // The call's next beat stalls until well past the window's limit, which ends the call while the beat is written.
            window.StallNextAgentLine(limit + TimeSpan.FromSeconds(5).Scaled());
            CallToolResult notLetIn = Await(unanswered, "the call nobody let in, ended while its word to the agent was still written");
            Assert.True(notLetIn.IsError == true && Text(notLetIn).Contains($"Nobody at {where} answered within {Durations.Said(limit)} whether Test Agent may work there.", StringComparison.Ordinal),
                $"The call nobody let in was answered: {Text(notLetIn)}");
            // The agent's client hands each message over on a thread of its own: the word at times after the answer.
            Ui.WaitFor(() => told.Told.Any(t => t.Message?.StartsWith($"Waiting for the person at {where} to let Test Agent in", StringComparison.Ordinal) == true),
                () => $"the agent to hear what its call waited on (it heard {told})");
        }
        finally
        {
            try
            {
                Ui.Await(hearing.DisposeAsync().AsTask(), "the agent's session to stop keeping its progress");
            }
            finally
            {
                Gone(client);
                window.Close();
            }
        }
    }
}
