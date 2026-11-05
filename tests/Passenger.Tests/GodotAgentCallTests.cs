using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Run;

namespace Passenger.Tests;

/// <summary>An agent's call on a Godot game held to its limit whatever stalls, against a stand-in game that connects to
/// Passenger as its remote debugger and then reads and answers nothing: a question unanswered, a message waiting behind
/// another its connection has not taken, and a message its connection will not take each fail within the call's limit,
/// saying what it waited on, the last letting go of the connection.</summary>
public sealed class GodotAgentCallTests
{
    /// <summary>The agent's call's limit here: shorter than every question's own.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    [AvaloniaFact]
    public void A_call_on_a_game_that_takes_nothing_fails_within_its_limit_saying_what_it_waited_on()
    {
        (GodotGame game, TcpClient standIn) = Connected("Unanswering");
        try
        {
            Assert.EndsWith("waiting for it to answer with its scene tree", Refusal(() => game.SceneTreeAsync()), StringComparison.Ordinal);

            // Messages no reader takes, until one waits on the connection (Windows takes a first large one whole).
            List<Task> filling = [];
            while (filling.All(t => t.IsCompleted) && filling.Count < 8)
            {
                filling.Add(game.SetPropertyAsync(1, "text", new string('x', 32 * 1024 * 1024)));
                Ui.Hold(TimeSpan.FromMilliseconds(500), "the message's write to be taken whole or wait");
            }
            Assert.True(filling.Any(t => !t.IsCompleted), $"The stand-in's connection took {filling.Count} messages of 32 MB unread");
            Assert.EndsWith("waiting to tell it scene:request_scene_tree behind another message that its connection had not taken",
                Refusal(() => game.SceneTreeAsync()), StringComparison.Ordinal);
            game.Dispose();
            Ui.WaitFor(() => filling.All(t => t.IsCompleted), "the waiting writes to end with the game");
            Assert.All(filling.Where(t => !t.IsCompletedSuccessfully), t => Assert.IsType<LiveAppException>(t.Exception?.InnerException));
        }
        finally
        {
            game.Dispose();
            standIn.Dispose();
        }

        (GodotGame second, TcpClient other) = Connected("Unread");
        try
        {
            string refused = Refusal(async () =>
            {
                // As many as it takes for one to wait on a connection no one reads.
                for (int i = 0; i < 8; i++)
                {
                    await second.SetPropertyAsync(1, "text", new string('x', 32 * 1024 * 1024));
                }
            });
            Assert.EndsWith("waiting for its connection to take scene:set_object_property, which it would not: Passenger let go of the connection", refused, StringComparison.Ordinal);
            Ui.WaitFor(() => second.State == LiveAppState.Ended, "the connection let go of to end the game's debugging");
        }
        finally
        {
            second.Dispose();
            other.Dispose();
        }
    }

    /// <summary>A game listened for as a launch's, and a stand-in for it connected, which reads and answers nothing.</summary>
    private static (GodotGame Game, TcpClient StandIn) Connected(string name)
    {
        string folder = TestFolders.Create("godot-call-");
        (LaunchPlan plan, GodotGame? game) = GodotGame.Listen(new LaunchPlan { Name = name, Program = "godot", WorkingDirectory = folder, Godot = folder });
        Assert.NotNull(game);
        string address = plan.Arguments[plan.Arguments.ToList().IndexOf(GodotGame.RemoteDebug) + 1];
        TcpClient standIn = new();
        standIn.Connect(IPAddress.Loopback, new Uri(address).Port);
        Ui.WaitFor(() => game.State == LiveAppState.Connected, () => "the stand-in game to connect: " + (game.ConnectionTaken
            ? "Passenger took its connection, but the word of it posted to the window's thread never ran there"
            : "its connect was made, but Passenger never took the connection"));
        return (game, standIn);
    }

    /// <summary>What a call's question or message to the game came to within the call's limit: its refusal's words.</summary>
    private static string Refusal(Func<Task> asked)
    {
        return Ui.Await(SaidAsync(), "the call's refusal", TimeSpan.FromSeconds(30));

        async Task<string> SaidAsync()
        {
            using IDisposable call = GodotGame.Within(Limit);
            try
            {
                await asked();
                return "answered";
            }
            catch (LiveAppException refused)
            {
                return refused.Message;
            }
        }
    }
}
