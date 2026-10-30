using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Discord;
using Passenger.App.Settings;

namespace Passenger.Tests;

/// <summary>Discord shows Playing Passenger: at first Passenger's own words alone, the solution and the file only as
/// the person chooses, Passenger's icon and button and the time since the solution opened; nothing once the setting
/// goes off, the solution closes or Passenger quits. An older Passenger's settings, never turned on, come up naming
/// only Passenger. With no Discord one line says so, and Discord is found when it starts. A stand-in listens in each
/// test's own place, and the run points <c>PASSENGER_DISCORD_IPC</c> where nothing listens, so no window reaches the
/// person's Discord.</summary>
public sealed class DiscordTests : DiscordFolders
{
    [AvaloniaFact]
    public void A_fresh_Passenger_shows_only_itself_in_Discord_and_turning_it_off_clears_it()
    {
        discord.Start();
        DateTimeOffset before = DateTimeOffset.UtcNow;
        MainWindow window = Started();
        DateTimeOffset after = DateTimeOffset.UtcNow;

        JsonObject activity = WaitForActivity(a => a is not null, "the activity a fresh Passenger shows");
        Frame handshake = discord.Frames[0];
        Assert.True(handshake.Op == 0 && handshake.Json.ToJsonString() == """{"v":1,"client_id":"1559714715847098468"}""",
            $"The first frame was not the handshake naming Passenger's Discord application: op {handshake.Op}, {handshake.Json.ToJsonString()}");
        Frame command = discord.Frames.Last(f => f.Op == 1);
        Assert.Equal("SET_ACTIVITY", (string?)command.Json["cmd"]);
        Assert.True((int?)command.Json["args"]?["pid"] == Environment.ProcessId, $"The activity names process {command.Json["args"]?["pid"]}, not Passenger's own, {Environment.ProcessId}");
        Assert.True(((string?)activity["details"], (string?)activity["state"]) == ("An IDE for C# and .NET", "Open source · Windows, macOS and Linux"),
            $"A fresh Passenger's activity is not Passenger's own words alone: {activity.ToJsonString()}");
        Assert.DoesNotContain("Shop", activity.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("passenger-1024", (string?)activity["assets"]?["large_image"]);
        Assert.Equal("Passenger: an IDE for C# and .NET, open to your coding agent", (string?)activity["assets"]?["large_text"]);
        Assert.True(activity["assets"]!.AsObject().Count == 2, $"The activity carries more art than Passenger's icon: {activity["assets"]!.ToJsonString()}");
        Assert.Equal("""[{"label":"Get Passenger","url":"https://passenger-ide.net"}]""", activity["buttons"]?.ToJsonString());
        long start = (long)activity["timestamps"]!["start"]!;
        Assert.True(start >= before.ToUnixTimeMilliseconds() && start <= after.ToUnixTimeMilliseconds(),
            $"The activity's time starts at {DateTimeOffset.FromUnixTimeMilliseconds(start):O}, not when the solution opened, between {before:O} and {after:O}");
        string[] keys = [.. activity.Select(p => p.Key).Order(StringComparer.Ordinal)];
        Assert.True(keys.SequenceEqual(["assets", "buttons", "details", "state", "timestamps"]), $"The activity carries more than it was to: {string.Join(", ", keys)}");
        Assert.True(window.Settings is { ShowInDiscord: true, DiscordShows: DiscordShown.PassengerOnly },
            $"A fresh Passenger's Discord settings are {window.Settings.ShowInDiscord}, {window.Settings.DiscordShows}");

        SetShown(window, false);
        Ui.WaitFor(() => discord.Ended == 1, "Discord to be let go of once the setting went off");
        Frame last = discord.Frames.Last();
        Assert.True(last.Op == 1 && (string?)last.Json["cmd"] == "SET_ACTIVITY" && last.Json["args"]?["activity"] is null && (int?)last.Json["args"]?["pid"] == Environment.ProcessId,
            $"The setting went off without the activity cleared first: the last frame was {last.Json.ToJsonString()}");
        SetShown(window, true);
        JsonObject again = WaitForActivity(a => a is not null, "the activity again once the setting came back on", connection: 2);
        Assert.True((long)again["timestamps"]!["start"]! == start, "Turned off and on, the activity's time began again rather than counting from when the solution opened");
        window.Close();
    }

    [AvaloniaFact]
    public void Switching_files_names_the_file_only_while_the_solution_and_the_file_are_chosen()
    {
        discord.Start();
        MainWindow window = Started();
        WaitForActivity(a => a is not null, "Passenger's own activity");
        window.OpenFile(Path.Combine(solution, "One.cs"));
        Wait(TimeSpan.FromMilliseconds(500));
        Assert.True(Activities().All(a => a is null || !a.ToJsonString().Contains("One.cs", StringComparison.Ordinal)),
            "Discord was told the file open before the person chose to name it");

        Choose(window, "The solution and the file");
        WaitForActivity(a => (string?)a?["state"] == "Editing One.cs", "the activity to name One.cs, the file open");
        window.OpenFile(Path.Combine(solution, "Two.cs"));
        WaitForActivity(a => (string?)a?["state"] == "Editing Two.cs", "the activity to follow the switch to Two.cs");
        window.OpenFile(Path.Combine(solution, "One.cs"));
        JsonObject one = WaitForActivity(a => (string?)a?["state"] == "Editing One.cs", "the activity to follow the switch back to One.cs");
        Assert.Equal("Shop", (string?)one["details"]);

        Choose(window, "The solution");
        WaitForActivity(a => a?["details"] is not null && a["state"] is null, "the activity to name the solution alone");
        int sent = Activities().Count;
        window.OpenFile(Path.Combine(solution, "Two.cs"));
        Ui.Settle();
        window.OpenFile(Path.Combine(solution, "One.cs"));
        Wait(TimeSpan.FromMilliseconds(500));
        Assert.True(Activities().Skip(sent).All(a => a?["state"] is null), "With the solution alone chosen, switching files still named one in Discord");

        Choose(window, "Passenger only");
        JsonObject only = WaitForActivity(a => (string?)a?["details"] == "An IDE for C# and .NET", "the activity to name nothing but Passenger");
        Assert.True((string?)only["state"] == "Open source · Windows, macOS and Linux" && only["timestamps"] is not null
            && (string?)only["assets"]?["large_image"] == "passenger-1024", $"Passenger only showed {only.ToJsonString()}");
        window.Close();
    }

    [AvaloniaFact]
    public void Of_two_solution_windows_the_one_in_front_alone_shows_its_activity_and_the_other_takes_over_as_it_closes()
    {
        discord.Start();
        string bank = Directory.CreateDirectory(Path.Combine(folder, "Bank")).FullName;
        File.WriteAllText(Path.Combine(bank, "Three.cs"), "class Three { }\n");
        MainWindow window = Started();
        Choose(window, "The solution");
        WaitForActivity(a => (string?)a?["details"] == "Shop", "the first window's solution");

        window.ApplySettings(window.Settings with { OpenProjectIn = OpenProjectIn.NewWindow });
        Assert.Null(window.Open(bank));
        Ui.Settle();
        MainWindow second = Assert.Single(PassengerApp.Windows, w => w != window);
        WaitForActivity(a => (string?)a?["details"] == "Bank", "the solution of the window in front");
        PassengerApp.BringForward(window);
        WaitForActivity(a => (string?)a?["details"] == "Shop", "the first window's solution once it is in front again");
        Assert.True(discord.Connections == 1, $"Two windows reached Discord {discord.Connections} times, not once between them");

        window.Close();
        WaitForActivity(a => (string?)a?["details"] == "Bank", "the remaining window's solution once the one in front closed");
        Assert.Equal(0, discord.Ended);
        second.Close();
        Ui.WaitFor(() => discord.Ended == 1, "Discord to be let go of as the last window closed", TimeSpan.FromSeconds(3));
        Assert.True(Activities().Last() is null, "Passenger quit without clearing its activity");
    }

    [AvaloniaFact]
    public void Closing_the_solution_or_Passenger_clears_the_activity()
    {
        discord.Start();
        MainWindow window = Started();
        Choose(window, "The solution");
        long first = (long)WaitForActivity(a => (string?)a?["details"] == "Shop", "the activity")["timestamps"]!["start"]!;
        Assert.Null(window.CloseSolution());
        Ui.WaitFor(() => discord.Ended == 1, "Discord to be let go of once the solution closed");
        Assert.True(Activities().Last() is null, "The solution closed, yet the activity was never cleared");

        Wait(TimeSpan.FromMilliseconds(50));
        window.OpenFolder(solution);
        JsonObject again = WaitForActivity(a => (string?)a?["details"] == "Shop", "the activity once the solution opened again", connection: 2);
        Assert.True((long)again["timestamps"]!["start"]! > first, "The solution opened again, yet its time counts from when it first opened");
        window.Close();
        Ui.WaitFor(() => discord.Ended == 2, "Discord to be let go of as Passenger quit", TimeSpan.FromSeconds(3));
        Assert.True(Activities().Last() is null, "Passenger quit without clearing its activity");
    }

    [AvaloniaFact]
    public void Without_Discord_one_line_is_logged_and_Passenger_finds_Discord_when_it_starts_and_again_when_it_comes_back()
    {
        MainWindow window = Started();
        Choose(window, "The solution");
        Ui.PumpUntil(() => Discords().Length > 0, "a line saying Discord is not running", TimeSpan.FromSeconds(10));
        // Ten looks or so: the test's window looks for Discord every 200 ms.
        Wait(TimeSpan.FromSeconds(2));
        Assert.True(Discords().Length == 1, $"With no Discord the log has more than one line about it:\n{string.Join('\n', Discords())}");
        Assert.Contains($"Discord: not running (nothing at {discord.Base}-0)", Discords()[0], StringComparison.Ordinal);

        discord.Start();
        WaitForActivity(a => (string?)a?["details"] == "Shop", "the activity once Discord started");

        int said = Discords().Length;
        discord.Stop();
        Wait(TimeSpan.FromSeconds(2));
        string[] since = [.. Discords().Skip(said)];
        Assert.Contains(since, line => line.Contains($"Discord: {discord.Base}-0 closed the connection; looking for it again", StringComparison.Ordinal));
        Assert.True(since.Length == 1, $"Discord went away and the log says more than one line about it:\n{string.Join('\n', since)}");
        int connected = discord.Connections;
        discord.Start();
        WaitForActivity(a => (string?)a?["details"] == "Shop", "the activity once Discord came back", connection: connected + 1);
        window.Close();
    }

    [AvaloniaFact]
    public void Only_the_persons_window_reaches_Discord_and_a_tests_never_the_persons_own()
    {
        discord.Start();
        // A window no one called the person's (a screenshot's, an agent's), the setting on: Discord hears nothing.
        MainWindow other = new() { Width = 1200, Height = 800, StateFolder = settings, DiscordIpc = discord.Ipc };
        other.Show();
        other.ApplySettings(other.Settings with { ShowInDiscord = true });
        other.OpenFolder(solution);
        Wait(TimeSpan.FromSeconds(1));
        Assert.True(discord.Connections == 0, "A window that is not the person's reached Discord");
        other.Close();

        Assert.Equal(TestRun.DiscordNowhere, Environment.GetEnvironmentVariable(DiscordIpc.Variable));
        int before = Ui.IdeLog().Split('\n').Length;
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        Assert.True(window.DiscordIpc.Places.SequenceEqual([TestRun.DiscordNowhere]),
            $"A test's window looks for Discord at {string.Join(", ", window.DiscordIpc.Places)}, not only the run's own place");
        window.Show();
        window.ApplySettings(window.Settings with { ShowInDiscord = true });
        window.ShowActivityInDiscord();
        window.OpenFolder(solution);
        string[] said = [];
        Ui.PumpUntil(() => (said = [.. Ui.IdeLog().Split('\n').Skip(before - 1).Where(l => l.Contains("Discord: ", StringComparison.Ordinal))]).Length > 0,
            "a line saying where the window looked for Discord", TimeSpan.FromSeconds(10));
        Assert.True(said.Length == 1 && said[0].Contains($"nothing at {TestRun.DiscordNowhere}-0", StringComparison.Ordinal),
            $"The test's window looked for Discord other than in the run's own place:\n{string.Join('\n', said)}");
        window.Close();
    }

    [AvaloniaFact]
    public void The_Discord_page_has_its_options_at_their_defaults_says_who_sees_the_names_greyed_while_off_and_kept()
    {
        MainWindow window = Started();
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Query = "discord";
        Ui.Settle();
        Assert.Equal([SettingsDialog.DiscordPage], dialog.Pages);
        Assert.Equal([Show, "Show", Idle], dialog.Shown);
        CheckBox on = Editor<CheckBox>(dialog, Show);
        ComboBox shows = Editor<ComboBox>(dialog, "Show");
        CheckBox idle = Editor<CheckBox>(dialog, Idle);
        NumericUpDown minutes = Editor<NumericUpDown>(dialog, Idle);
        Assert.True(on.IsChecked == true, "Show activity in Discord is off in a fresh Passenger");
        Assert.Equal(["Passenger only", "The solution", "The solution and the file"], shows.Items.Cast<string>());
        Assert.Equal("Passenger only", shows.SelectedItem);
        Assert.True(idle.IsChecked == true && minutes.Value == 5, $"The idle time is not the plugins' 5 minutes, on: {idle.IsChecked}, {minutes.Value}");
        Assert.True(shows.IsEnabled && idle.IsEnabled && minutes.IsEnabled, "Discord's options are greyed while its activity is on");
        string[] words = [.. dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];
        Assert.True(words.Any(w => w.Contains("Passenger only: that you are using Passenger, and nothing of your work", StringComparison.Ordinal)
            && w.Contains("Whoever can see your Discord activity sees these names", StringComparison.Ordinal)),
            $"The Discord page does not say what each choice shows and who sees it:\n{string.Join('\n', words.Where(w => w.Length > 40))}");

        Ui.Click(on);
        Assert.False(shows.IsEnabled || idle.IsEnabled || minutes.IsEnabled, "Discord's options can be changed while its activity is off");
        Ui.Click(on);
        Assert.True(shows.IsEnabled && idle.IsEnabled && minutes.IsEnabled, "Turning the activity on left its options greyed");
        shows.SelectedIndex = 1;
        minutes.Value = 12;
        dialog.Ok();
        Ui.Settle();
        window.Close();

        MainWindow again = Started();
        Assert.True(again.Settings is { ShowInDiscord: true, DiscordShows: DiscordShown.Solution, DiscordIdleMinutes: 12 },
            $"The Discord options chosen were not kept: {again.Settings.ShowInDiscord}, {again.Settings.DiscordShows}, {again.Settings.DiscordIdleMinutes}");
        again.Close();
    }

    /// <summary>A settings.json an older Passenger wrote holds its defaults, the activity off and the solution and the file
    /// to be named: one never turned on stays off, Discord told nothing, naming only Passenger should the person turn it on;
    /// one the person had turned on keeps their choice.</summary>
    [AvaloniaFact]
    public void An_older_settings_file_never_turned_on_stays_off_and_one_turned_on_keeps_its_choice()
    {
        discord.Start();
        File.WriteAllText(Path.Combine(settings, "settings.json"),
            "{\n  \"version\": 1,\n  \"editorFontSize\": 15,\n  \"showInDiscord\": false,\n  \"discordShows\": \"SolutionAndFile\"\n}\n");
        MainWindow window = Started();
        Assert.True(window.Settings is { ShowInDiscord: false, DiscordShows: DiscordShown.PassengerOnly, EditorFontSize: 15 },
            $"An older Passenger's settings, Discord never turned on, came up as {window.Settings.ShowInDiscord}, {window.Settings.DiscordShows}, font {window.Settings.EditorFontSize}");
        Wait(TimeSpan.FromSeconds(1));
        Assert.True(discord.Connections == 0, "Upgraded from settings that never turned Discord on, Passenger reached Discord");
        window.Close();

        string chosen = TestFolders.Create("dc-chosen-");
        try
        {
            File.WriteAllText(Path.Combine(chosen, "settings.json"), "{\n  \"version\": 1,\n  \"showInDiscord\": true,\n  \"discordShows\": \"SolutionAndFile\"\n}\n");
            MainWindow kept = Started(state: chosen);
            Assert.True(kept.Settings is { ShowInDiscord: true, DiscordShows: DiscordShown.SolutionAndFile },
                $"An older Passenger's settings that had turned Discord on, naming the solution and the file, came up as {kept.Settings.ShowInDiscord}, {kept.Settings.DiscordShows}");
            kept.Close();
        }
        finally
        {
            TestFolders.Delete(chosen);
        }
    }
}

/// <summary>A Discord that never answers never holds the window or its close, and the person idle for the idle time shows
/// only Passenger until they are back. Held to the idle time, so the class runs alone.</summary>
[Collection(Alone.Name)]
public sealed class DiscordResponsivenessTests : DiscordFolders
{
    [AvaloniaFact]
    public void A_Discord_that_never_answers_never_slows_the_window()
    {
        discord.Start(answers: false);
        MainWindow window = Started();
        Ui.PumpUntil(() => discord.Connections > 0, "the window to reach the Discord that never answers", TimeSpan.FromSeconds(10));
        // Files switched, each settling, the handshake unanswered: a window that waited for Discord would never settle.
        for (int i = 0; i < 10; i++)
        {
            window.OpenFile(Path.Combine(solution, i % 2 == 0 ? "One.cs" : "Two.cs"));
            Ui.Settle();
        }
        Ui.PumpUntil(() => discord.Connections > 1, "the window to try again after the handshake went unanswered", TimeSpan.FromSeconds(10));
        Assert.True(Discords().Length == 1 && Discords()[0].Contains("did not answer the handshake", StringComparison.Ordinal),
            $"A Discord that never answers was said other than once:\n{string.Join('\n', Discords())}");
        window.Close();
        Ui.WaitFor(() => discord.Ended == discord.Connections, "Passenger to let go of the Discord that never answers as it quit", TimeSpan.FromSeconds(3));
    }

    [AvaloniaFact]
    public void After_the_idle_time_only_Passenger_shows_until_the_person_is_back()
    {
        discord.Start();
        // The person chose the solution and the file before, so no dialog's time counts towards the short idle time.
        File.WriteAllText(Path.Combine(settings, "settings.json"),
            $"{{\n  \"version\": {AppSettings.Upgrades.Count},\n  \"discordShows\": \"SolutionAndFile\"\n}}\n");
        MainWindow window = Started(idle: TimeSpan.FromSeconds(2));
        window.OpenFile(Path.Combine(solution, "One.cs"));
        // At work in the editor, however long the window took to start.
        Ui.Press(window, "right");
        WaitForActivity(a => (string?)a?["state"] == "Editing One.cs", "the activity naming the file");
        JsonObject idle = WaitForActivity(a => (string?)a?["details"] == "An IDE for C# and .NET" && a["timestamps"] is null, "only Passenger once the person was idle");
        string[] keys = [.. idle.Select(p => p.Key).Order(StringComparer.Ordinal)];
        Assert.True(keys.SequenceEqual(["assets", "buttons", "details", "state"]) && (string?)idle["state"] == "Open source · Windows, macOS and Linux",
            $"Idle, the activity names more than Passenger: {idle.ToJsonString()}");
        Ui.Press(window, "right");
        WaitForActivity(a => (string?)a?["state"] == "Editing One.cs", "the solution and the file once a key was pressed");
        window.Close();
    }
}

/// <summary>A Discord stand-in listening in each test's own place, the test's state and solution folders, and the helpers
/// that start the person's window there and read what Discord was sent.</summary>
public abstract class DiscordFolders : IDisposable
{
    protected const string Show = "Show activity in Discord";
    protected const string Idle = "Only Passenger when idle for";

    protected readonly string folder = TestFolders.Create("dc-");
    protected readonly string settings = TestFolders.Create("dc-settings-");
    protected readonly string solution;
    protected readonly DiscordStandIn discord;

    protected DiscordFolders()
    {
        // A short folder: a Unix socket's path is at most 103 bytes.
        discord = new DiscordStandIn(folder);
        solution = Directory.CreateDirectory(Path.Combine(folder, "Shop")).FullName;
        File.WriteAllText(Path.Combine(solution, "One.cs"), "class One { }\n");
        File.WriteAllText(Path.Combine(solution, "Two.cs"), "class Two { }\n");
    }

    public void Dispose()
    {
        discord.Dispose();
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    /// <summary>The person's window as the app makes it, its settings kept in the test's state folder (or another), looking
    /// for Discord at the stand-in's place, with the solution open.</summary>
    protected MainWindow Started(TimeSpan? idle = null, string? state = null)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state ?? settings, DiscordIpc = discord.Ipc, DiscordIdleAfter = idle };
        window.Show();
        window.KeepSettings();
        window.ShowActivityInDiscord();
        window.OpenFolder(solution);
        Ui.Settle();
        return window;
    }

    /// <summary>Turns Show activity in Discord on or off as a person does, in Settings.</summary>
    protected static void SetShown(MainWindow window, bool on)
    {
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Page = SettingsDialog.DiscordPage;
        Ui.Settle();
        CheckBox box = Editor<CheckBox>(dialog, Show);
        if (box.IsChecked != on)
        {
            Ui.Click(box);
        }
        dialog.Ok();
        Ui.Settle();
    }

    /// <summary>Chooses what the activity shows, in Settings.</summary>
    protected static void Choose(MainWindow window, string shown)
    {
        SettingsDialog dialog = window.ShowSettings();
        Ui.Settle();
        dialog.Page = SettingsDialog.DiscordPage;
        Ui.Settle();
        ComboBox shows = Editor<ComboBox>(dialog, "Show");
        shows.SelectedIndex = Array.IndexOf(SettingsDialog.DiscordChoices, shown);
        dialog.Ok();
        Ui.Settle();
    }

    /// <summary>The activity each SET_ACTIVITY gave, in order, null for a clear.</summary>
    protected IReadOnlyList<JsonObject?> Activities() =>
        [.. discord.Frames.Where(f => f.Op == 1 && (string?)f.Json["cmd"] == "SET_ACTIVITY").Select(f => f.Json["args"]?["activity"] as JsonObject)];

    /// <summary>Waits for the newest activity on a connection to be as wanted, and gives it.</summary>
    protected JsonObject WaitForActivity(Func<JsonObject?, bool> wanted, string what, int connection = 1)
    {
        JsonObject? newest = null;
        Ui.WaitFor(() =>
        {
            Frame? last = discord.Frames.LastOrDefault(f => f.Connection >= connection && f.Op == 1 && (string?)f.Json["cmd"] == "SET_ACTIVITY");
            newest = last?.Json["args"]?["activity"] as JsonObject;
            return last is not null && wanted(newest);
        }, $"{what}; Discord was last sent {Activities().LastOrDefault()?.ToJsonString() ?? "nothing"}", TimeSpan.FromSeconds(15));
        return newest!;
    }

    /// <summary>The log's lines about this test's Discord.</summary>
    protected string[] Discords() =>
        [.. Ui.IdeLog().Split('\n').Where(l => l.Contains("Discord: ", StringComparison.Ordinal) && l.Contains(discord.Base, StringComparison.Ordinal))];

    /// <summary>Lets the window run a while, its timers and Passenger's looks for Discord with it.</summary>
    protected static void Wait(TimeSpan time) => Ui.Hold(time, "the window running a while, its timers and Passenger's looks for Discord with it");

    /// <summary>What edits an option, beside its label in the page shown.</summary>
    protected static T Editor<T>(SettingsDialog dialog, string label) where T : Control =>
        ((Visual)dialog.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == label).Parent!).GetVisualDescendants().OfType<T>().First();

    /// <summary>A frame Discord was sent, on the connection numbered from 1.</summary>
    protected sealed record Frame(int Connection, int Op, JsonObject Json);

    /// <summary>The Discord app as Passenger finds it, in a test's own place: a Unix socket at <c>discord-ipc-0</c> in the
    /// test's folder, or a pipe of the test's own name on Windows. It answers the handshake with READY and a command with
    /// its answer, as Discord does (or, told not to, never answers), and keeps every frame it is sent.</summary>
    protected sealed class DiscordStandIn(string folder) : IDisposable
    {
        private readonly List<Frame> frames = [];
        private readonly List<Stream> clients = [];
        private CancellationTokenSource? listening;
        private Socket? listener;
        private bool answers;
        private int connections;
        private int ended;

        public string Base { get; } = OperatingSystem.IsWindows()
            ? $"passenger-test-{Guid.NewGuid().ToString("N")[..12]}-discord-ipc"
            : Path.Combine(folder, "discord-ipc");

        /// <summary>Where the test's window looks for it, trying again every 200 ms.</summary>
        public DiscordIpc Ipc => new([Base])
        {
            Retry = TimeSpan.FromMilliseconds(200),
            Pause = TimeSpan.FromMilliseconds(100),
            Timeout = TimeSpan.FromSeconds(1),
        };

        public int Connections => Volatile.Read(ref connections);

        /// <summary>Connections that have ended, whichever end closed them.</summary>
        public int Ended => Volatile.Read(ref ended);

        public IReadOnlyList<Frame> Frames
        {
            get
            {
                lock (frames)
                {
                    return [.. frames];
                }
            }
        }

        /// <summary>Discord starts.</summary>
        public void Start(bool answers = true)
        {
            this.answers = answers;
            CancellationTokenSource cancel = new();
            listening = cancel;
            if (OperatingSystem.IsWindows())
            {
                _ = Waiting.Apart(() => AcceptPipesAsync(cancel.Token));
                return;
            }
            string path = Base + "-0";
            File.Delete(path);
            Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(path));
            socket.Listen(4);
            listener = socket;
            _ = Waiting.Apart(() => AcceptSocketsAsync(socket, cancel.Token));
        }

        /// <summary>Discord quits: its socket goes and every connection ends.</summary>
        public void Stop()
        {
            listening?.Cancel();
            listening = null;
            listener?.Dispose();
            listener = null;
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(Base + "-0");
            }
            Stream[] open;
            lock (frames)
            {
                open = [.. clients];
                clients.Clear();
            }
            foreach (Stream client in open)
            {
                client.Dispose();
            }
        }

        public void Dispose() => Stop();

        private async Task AcceptSocketsAsync(Socket socket, CancellationToken cancel)
        {
            try
            {
                while (true)
                {
                    Socket client = await socket.AcceptAsync(cancel);
                    _ = Waiting.Apart(() => ServeAsync(new NetworkStream(client, ownsSocket: true), cancel));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // Stopped.
            }
        }

        /// <summary>Discord's pipe on Windows: a server instance waiting for each next connection.</summary>
        private async Task AcceptPipesAsync(CancellationToken cancel)
        {
            try
            {
                while (true)
                {
                    NamedPipeServerStream pipe = new(Base + "-0", PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    try
                    {
                        await pipe.WaitForConnectionAsync(cancel);
                    }
                    catch
                    {
                        await pipe.DisposeAsync();
                        throw;
                    }
                    _ = Waiting.Apart(() => ServeAsync(pipe, cancel));
                }
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Stopped.
            }
        }

        private async Task ServeAsync(Stream client, CancellationToken cancel)
        {
            int number = Interlocked.Increment(ref connections);
            lock (frames)
            {
                clients.Add(client);
            }
            byte[] head = new byte[8];
            try
            {
                while (true)
                {
                    await client.ReadExactlyAsync(head, cancel);
                    int op = BinaryPrimitives.ReadInt32LittleEndian(head);
                    byte[] body = new byte[BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(4))];
                    await client.ReadExactlyAsync(body, cancel);
                    JsonObject json = JsonNode.Parse(body)!.AsObject();
                    lock (frames)
                    {
                        frames.Add(new Frame(number, op, json));
                    }
                    if (!answers)
                    {
                        continue;
                    }
                    if (op == 0)
                    {
                        await SendAsync(client, new JsonObject
                        {
                            ["cmd"] = "DISPATCH",
                            ["evt"] = "READY",
                            ["data"] = new JsonObject { ["v"] = 1, ["user"] = new JsonObject { ["id"] = "1", ["username"] = "stand-in" } },
                            ["nonce"] = null,
                        }, cancel);
                    }
                    else if (op == 1)
                    {
                        await SendAsync(client, new JsonObject { ["cmd"] = (string?)json["cmd"], ["evt"] = null, ["data"] = null, ["nonce"] = (string?)json["nonce"] }, cancel);
                    }
                }
            }
            catch (Exception error) when (error is EndOfStreamException or IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The connection ended, from either end.
            }
            finally
            {
                Interlocked.Increment(ref ended);
                lock (frames)
                {
                    clients.Remove(client);
                }
                await client.DisposeAsync();
            }
        }

        private static async Task SendAsync(Stream client, JsonObject answer, CancellationToken cancel)
        {
            byte[] json = Encoding.UTF8.GetBytes(answer.ToJsonString());
            byte[] frame = new byte[8 + json.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, 1);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
            json.CopyTo(frame, 8);
            await client.WriteAsync(frame, cancel);
            await client.FlushAsync(cancel);
        }
    }
}
