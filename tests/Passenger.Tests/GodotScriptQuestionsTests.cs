using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Dap;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A GDScript session's questions to a game that leaves them unanswered, against a stand-in game that connects to
/// Passenger as its remote debugger and answers only what the test lets it: a stop whose stack never comes is shown paused
/// once the stack's question is given up on, saying what the game left unanswered; an evaluation the game ends under is
/// refused, its session ending with the game; and neither leaves a fault for the IDE's errors, then or once the game has
/// ended and its tasks are collected.</summary>
public sealed class GodotScriptQuestionsTests
{
    [AvaloniaFact]
    public void A_question_given_up_on_and_one_the_game_ends_under_leave_no_fault_among_the_IDEs_errors()
    {
        string folder = TestFolders.Create("godot-questions-");
        (LaunchPlan plan, GodotGame? listened) = GodotGame.Listen(new LaunchPlan { Name = "Unanswering", Program = "godot", WorkingDirectory = folder, Godot = folder },
            new GodotScriptStart([], StopAtErrors: false));
        GodotGame game = listened!;
        string address = plan.Arguments[plan.Arguments.ToList().IndexOf(GodotGame.RemoteDebug) + 1];
        using TcpClient standIn = new();
        standIn.Connect(IPAddress.Loopback, new Uri(address).Port);
        StandIn played = new(standIn.GetStream());
        Task heard = Waiting.Apart(played.HearAsync);
        try
        {
            Ui.WaitFor(() => game.State == LiveAppState.Connected, "the stand-in game to connect");
            InProcessEnd end = new("GDScript adapter of Unanswering");
            GodotScriptAdapter adapter = new(game, end, () => Task.CompletedTask);
            adapter.Start();
            List<string> said = [];
            DebugSession session = Ui.Await(DebugSession.AttachInProcessAsync(end, DebugEngine.GDScript, "Unanswering", new BreakpointStore(), new BuiltLines(_ => null),
                _ => { }, output => said.Add(output.Output)), "the GDScript session's attach", TimeSpan.FromMinutes(1));

            // A breakpoint's stop, whose stack the game never says.
            played.Send(GodotWords.DebugEnter, [true, "Breakpoint", true, GodotMessage.MainThread]);
            Ui.WaitFor(() => session.State == DebugState.Paused, () => $"the stop to be shown paused once its stack's question was given up on ({session.State}; said {string.Concat(said)})",
                TimeSpan.FromMinutes(1));
            Assert.Contains($"Unanswering did not answer with its stack in {LiveApp.AnswerLimit.TotalSeconds:0} s", string.Concat(said), StringComparison.Ordinal);

            // Asked again, the stack comes; an evaluation in its frame then has the game end under it.
            played.Answering = true;
            IReadOnlyList<DapStackFrame> frames = Ui.Await(session.StackTraceAsync((int)GodotMessage.MainThread), "the stop's stack asked again", TimeSpan.FromMinutes(1));
            Task<EvaluateResponse> evaluated = session.EvaluateAsync("counter", frames[0].Id, EvaluateContexts.Watch);
            Ui.WaitFor(() => evaluated.IsCompleted, "the evaluation the game ended under to be refused", TimeSpan.FromMinutes(1));
            Assert.True(evaluated.IsFaulted && evaluated.Exception?.InnerException is DapException,
                $"The evaluation the game ended under came to {(evaluated.IsFaulted ? evaluated.Exception?.InnerException?.ToString() : "an answer")}, not a refusal");
            Ui.WaitFor(() => game.State == LiveAppState.Ended && session.State == DebugState.Ended, () => $"the game and its session to end with its connection ({game.State}, {session.State})");

            // A question's task left to fault unobserved reaches the IDE's errors only once it is collected.
            for (int pass = 0; pass < 3; pass++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Ui.Settle();
            Assert.True(IdeErrors.Errors.Count == 0, $"A question given up on left the IDE's errors {string.Join("; ", IdeErrors.Errors.Select(e => e.Message))}");
        }
        finally
        {
            game.Dispose();
            standIn.Dispose();
            Ui.WaitFor(() => heard.IsCompleted, "the stand-in's reading to end with its connection");
        }
    }

    /// <summary>A stand-in game over its connection: it says what the test sends, and reads what Passenger asks, answering
    /// only once <see cref="Answering"/>: the stack with one frame, the frame's values with <c>self</c>, and an evaluation
    /// by ending, its connection closed.</summary>
    private sealed class StandIn(NetworkStream wire)
    {
        public volatile bool Answering;

        public void Send(string name, IReadOnlyList<object?> data)
        {
            byte[] frame = GodotMessage.Frame(name, GodotMessage.MainThread, data);
            wire.Write(frame);
        }

        public async Task HearAsync()
        {
            byte[] head = new byte[4];
            try
            {
                while (true)
                {
                    await wire.ReadExactlyAsync(head);
                    byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(head)];
                    await wire.ReadExactlyAsync(body);
                    GodotMessage asked = GodotMessage.Read(body);
                    if (!Answering)
                    {
                        continue;
                    }
                    switch (asked.Name)
                    {
                        case GodotWords.GetStackDump:
                            Send(GodotWords.StackDump, [3L, "res://main.gd", 4L, "_process"]);
                            break;
                        case GodotWords.GetStackFrameVars:
                            Send(GodotWords.StackFrameVars, [1L]);
                            Send(GodotWords.StackFrameVar, ["self", (long)GodotScope.Member, (long)GodotType.Object, 1L, "Node"]);
                            break;
                        case GodotWords.Evaluate:
                            wire.Close();
                            return;
                    }
                }
            }
            catch (Exception gone) when (gone is IOException or ObjectDisposedException or EndOfStreamException)
            {
                // Passenger let go of the connection.
            }
        }
    }
}
