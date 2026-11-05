using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Godot;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Game window and a game on macOS's embedded display, against a stand-in that connects to Passenger as the
/// game's remote debugger, shares a layer and keeps what it is told: the display told the window's scale and then the size
/// of the game's place at it, as Godot's editor tells them, a pointer given in what its Godot takes, and nothing said wrong
/// of a person's pointer before the game connects. The Live Tree comes beside the Game window as a game connects, and a
/// generated configuration chooses the Game window. Nothing here draws a game: the picture is looked at in a real window.</summary>
public sealed class GamePaneTests
{
    private static readonly (int Width, int Height) FourByThree = (720, 540);

    [AvaloniaFact]
    public void The_embedded_display_is_told_the_windows_scale_and_then_the_size_of_the_games_place_at_that_scale()
    {
        using Rig rig = Rig.Open();
        rig.Window.SetRenderScaling(2);
        Ui.Settle();
        Game game = rig.Hold(FourByThree);
        game.Shares(4242);

        (string first, string second) = game.TwoTold();
        Assert.Equal(GodotWords.EmbedState, first);
        Assert.Equal(GodotWords.EmbedWindowSize, second);
        Assert.Equal(2f, game.ToldScale);
        GamePlace place = rig.Place(2);
        Assert.Equal((place.Width, place.Height), game.ToldSize);
        Assert.True(Math.Abs(((double)place.Width / place.Height) - (720.0 / 540)) < 0.01, $"The game's place of {place} is not its own shape, 720 by 540");
        Assert.True(place.X + place.Width <= rig.Pane.Bounds.Width * 2 && place.Y + place.Height <= rig.Pane.Bounds.Height * 2, $"The game's place of {place} falls outside the pane's {rig.Pane.Bounds} at 2x");
    }

    [AvaloniaFact]
    public void A_window_come_to_a_screen_of_another_scale_tells_the_display_its_scale_before_the_new_size()
    {
        using Rig rig = Rig.Open();
        rig.Window.SetRenderScaling(2);
        Ui.Settle();
        Game game = rig.Hold(FourByThree);
        game.Shares(4242);
        game.TwoTold();

        rig.Window.SetRenderScaling(1);
        Ui.Settle();
        (string first, string second) = game.TwoTold();
        Assert.Equal((GodotWords.EmbedState, GodotWords.EmbedWindowSize), (first, second));
        Assert.Equal(1f, game.ToldScale);
        Assert.Equal((rig.Place(1).Width, rig.Place(1).Height), game.ToldSize);
    }

    [AvaloniaFact]
    public void The_game_follows_the_pane_as_it_changes_size()
    {
        using Rig rig = Rig.Open();
        // Tall enough that the pane's height decides its size, whatever the width of the window's right side.
        Game game = rig.Hold((200, 800));
        game.Shares(4242);
        game.TwoTold();
        (int, int) before = game.ToldSize;

        rig.Window.Height -= 200;
        Ui.Settle();

        Ui.WaitFor(() => game.Read() && game.ToldSize != before, () => $"the game to be told the pane's new size (it was told {game.ToldSize}, the pane is {rig.Pane.Bounds})");
        Assert.Equal((rig.Place(1).Width, rig.Place(1).Height), game.ToldSize);
    }

    [AvaloniaTheory]
    [InlineData("4.7.2", 1)]
    [InlineData("4.6.2", 2)]
    public void A_click_reaches_the_game_from_its_own_top_left_in_what_its_godot_takes(string version, double unit)
    {
        using Rig rig = Rig.Open(version);
        rig.Window.SetRenderScaling(2);
        Ui.Settle();
        Game game = rig.Hold(FourByThree);
        game.Shares(4242);
        game.TwoTold();
        GamePlace place = rig.Place(2);

        // A logical unit in from the game's top left, wherever it stands in the pane.
        Point inPane = new((place.X / 2.0) + 10, (place.Y / 2.0) + 20);
        Point inWindow = rig.Pane.TranslatePoint(inPane, rig.Window)!.Value;
        rig.Window.MouseDown(inWindow, MouseButton.Left);
        rig.Window.MouseUp(inWindow, MouseButton.Left);
        Ui.Settle();

        Ui.WaitFor(() => game.Read() && game.ToldEvents > 0, () => "the game to hear the click");
        (float x, float y) = game.LastPointer;
        Assert.True(Math.Abs(x - (10 * unit)) < 0.5 && Math.Abs(y - (20 * unit)) < 0.5,
            $"The click 10,20 in from the game's top left reached Godot {version} at {x},{y}, where it takes {unit} to a logical unit");
    }

    [AvaloniaFact]
    public void The_live_tree_comes_below_the_game_window_as_a_game_connects_and_the_game_window_stays_shown()
    {
        using Rig rig = Rig.Open();
        DockHost dock = rig.Window.DockHost;
        Assert.Equal(dock[ToolWindowIds.Game].Anchor, dock[ToolWindowIds.LiveTree].Anchor);

        Game game = rig.Hold(FourByThree, taken: true);
        Ui.WaitFor(() => dock[ToolWindowIds.LiveTree].IsShown, () => $"the Live Tree to show as the game connects ({game.Listened.State})");

        Assert.True(dock[ToolWindowIds.Game].IsShown, "The Live Tree took the Game window's place as the game connected, so the person who chose the Game window doesn't see the game");
        Assert.Equal(ToolAnchor.RightTop, dock[ToolWindowIds.Game].Anchor);
        Assert.Equal(ToolAnchor.RightBottom, dock[ToolWindowIds.LiveTree].Anchor);
    }

    /// <summary>A headless window on a Godot project of GDScript alone, with what the tests hold of it.</summary>
    private sealed class Rig : IDisposable
    {
        private readonly List<Game> games = [];
        private readonly string? version;

        private Rig(MainWindow window, string folder, string? version)
        {
            Window = window;
            Folder = folder;
            this.version = version;
        }

        public MainWindow Window { get; }

        public string Folder { get; }

        public GamePane Pane => Window.GamePane;

        public static Rig Open(string? version = "4.7.2")
        {
            string folder = TestFolders.Create("game-pane-");
            TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Pane\"\nrun/main_scene=\"res://main.tscn\"\n");
            TestFolders.Write(folder, "main.tscn", "[gd_scene format=3]\n\n[node name=\"Main\" type=\"Node2D\"]\n");
            MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false };
            window.Show();
            window.OpenFolder(folder);
            Ui.Settle();
            return new Rig(window, folder, version);
        }

        /// <summary>The game's place in the pane at a scale, as it fits there.</summary>
        public GamePlace Place(double scale) => GameFit.Within(((int)Math.Round(Pane.Bounds.Width * scale), (int)Math.Round(Pane.Bounds.Height * scale)), Pane.OwnSize);

        /// <summary>A game of a size held in the Game window, shown, its stand-in connected unless it is to stay away; one
        /// taken is among the session's games as it connects, as a launch's is.</summary>
        public Game Hold((int Width, int Height) size, bool connects = true, bool taken = false)
        {
            (LaunchPlan plan, GodotGame? listened) = GodotGame.Listen(new LaunchPlan { Name = "Pane", Program = "godot", WorkingDirectory = Folder, Godot = Folder, GodotVersion = version });
            Assert.NotNull(listened);
            Window.DockHost.ShowBehind(ToolWindowIds.Game);
            Ui.Settle();
            Pane.OwnSize = size;
            Pane.HoldLayer(listened);
            if (taken)
            {
                Window.Session.TakeGame(listened);
            }
            Ui.Settle();
            Game game = new(listened, plan, connects);
            games.Add(game);
            return game;
        }

        public void Dispose()
        {
            foreach (Game game in games)
            {
                game.Dispose();
            }
            Window.Close();
        }
    }

    /// <summary>A stand-in for the game, connected to Passenger as its remote debugger: what it is told, read as it comes.</summary>
    private sealed class Game : IDisposable
    {
        /// <summary>The length of a mouse button's event in Godot's codec, its place at bytes 3 and 7.</summary>
        private const int ButtonEvent = 12;

        private readonly TcpClient? standIn;
        private readonly byte[] head = new byte[4];
        private readonly ConcurrentQueue<GodotMessage> told = new();

        public Game(GodotGame listened, LaunchPlan plan, bool connects)
        {
            Listened = listened;
            if (connects)
            {
                string address = plan.Arguments[plan.Arguments.ToList().IndexOf(GodotGame.RemoteDebug) + 1];
                standIn = new TcpClient();
                standIn.Connect(IPAddress.Loopback, new Uri(address).Port);
                Ui.WaitFor(() => listened.State == LiveAppState.Connected, () => "the stand-in game to connect");
            }
        }

        public GodotGame Listened { get; }

        public float ToldScale { get; private set; }

        public (int Width, int Height) ToldSize { get; private set; }

        public int ToldEvents { get; private set; }

        public (float X, float Y) LastPointer { get; private set; }

        /// <summary>The game shares its layer through a context of an id.</summary>
        public void Shares(long context)
        {
            standIn!.GetStream().Write(GodotMessage.Frame(GodotWords.EmbeddedContext, context));
            Ui.WaitFor(() => Listened.EmbeddedContext is not null, () => "Passenger to hear the layer's context");
        }

        /// <summary>The first two messages of what comes next for the display, of its state and its size, as told in turn.</summary>
        public (string First, string Second) TwoTold()
        {
            List<string> names = [];
            Ui.WaitFor(() =>
            {
                Read();
                while (names.Count < 2 && told.TryDequeue(out GodotMessage? message))
                {
                    if (message.Name is GodotWords.EmbedState or GodotWords.EmbedWindowSize)
                    {
                        names.Add(message.Name);
                        Note(message);
                    }
                }
                return names.Count == 2;
            }, () => $"the display to be told its state and size (it was told {string.Join(", ", names)})");
            return (names[0], names[1]);
        }

        /// <summary>Takes in what has come; true, for a wait's condition to go on from.</summary>
        public bool Read()
        {
            NetworkStream stream = standIn!.GetStream();
            while (stream.DataAvailable)
            {
                stream.ReadExactly(head);
                byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(head)];
                stream.ReadExactly(body);
                GodotMessage message = GodotMessage.Read(body);
                Note(message);
                told.Enqueue(message);
            }
            return true;
        }

        private void Note(GodotMessage message)
        {
            switch (message.Name)
            {
                case GodotWords.EmbedState when message.Data is [byte[] state]:
                    ToldScale = BinaryPrimitives.ReadSingleLittleEndian(state);
                    break;
                case GodotWords.EmbedWindowSize when message.Data is [GodotMath { Values: [var width, var height] }]:
                    ToldSize = ((int)width, (int)height);
                    break;
                case GodotWords.EmbedEvent when message.Data is [byte[] { Length: ButtonEvent } button]:
                    ToldEvents++;
                    LastPointer = (BinaryPrimitives.ReadSingleLittleEndian(button.AsSpan(3)), BinaryPrimitives.ReadSingleLittleEndian(button.AsSpan(7)));
                    break;
            }
        }

        public void Dispose()
        {
            Listened.Dispose();
            standIn?.Dispose();
        }
    }
}
