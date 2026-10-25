using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;
using Debuggees = Passenger.Tests.Debugging.Helpers.Debuggees;

namespace Passenger.Tests;

/// <summary>The Avalonia app McpAvaloniaAppTests' agents run and drive: tests/Debuggees/AgentApp (a counter's button, a name box that
/// greets on Enter, a binding and a misspelt one, a drop-down and a list to scroll), written once for the class with a project file on
/// the tests' own Avalonia and restored. Headless, so nothing of it shows on the screen. Its window's tag holds a lone '\r',
/// which XamlIl counts as a line's end and the editor does not, so every place below it the app reports is a line on from
/// the editor's, and an agent is told the editor's.</summary>
public sealed class AgentAppFolder : IDisposable
{
    public AgentAppFolder()
    {
        Folder = PathNames.Canonical(TestFolders.Create("agentapp-"));
        TestFolders.Prepare(() =>
        {
            AvaloniaApp.Debuggee(Folder, "AgentApp");
            string xaml = Path.Combine(Folder, "MainWindow.axaml");
            File.WriteAllText(xaml, File.ReadAllText(xaml).Replace("Title=\"Agent App\" Width", "Title=\"Agent App\"\r        Width", StringComparison.Ordinal));
            Restore.Run(Folder);
        }, Folder);
    }

    public string Folder { get; }

    public void Dispose() => TestFolders.Delete(Folder);
}

/// <summary>Agents testing the Avalonia app Passenger runs as a person would: they look at it through the pictures it draws
/// and its visual tree, click, type, press keys and scroll through its window's own input, read its failed bindings, and
/// find Passenger's own controls by name; and the app of the Debug tab they name, of two debugged at once.</summary>
[Collection(Alone.Name)]
public sealed class McpAvaloniaAppTests : McpRig, IClassFixture<AgentAppFolder>
{
    private readonly string appFolder;

    public McpAvaloniaAppTests(AgentAppFolder app) => appFolder = app.Folder;

    /// <summary>Run, driven and looked at, then profiled until it ends by itself once its window has been drawn
    /// ("--quit-once-drawn"), not for a time that a loaded machine's app may not have drawn in; and an app that draws nothing
    /// says so, its frames' count 0 and why.</summary>
    [Fact]
    public async Task An_agent_runs_an_avalonia_app_sees_it_clicks_its_button_types_into_it_scrolls_it_reads_its_failed_bindings_and_profiles_its_frames()
    {
        await using McpClient client = await Start(["--mcp", appFolder], languageServer: false);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The app did not build: {built}");
        Assert.False(built.TryGetProperty("outputTail", out _), $"A build that succeeded was answered with MSBuild's output: {built}");
        // The app never ends by itself, so run must come back once its UI is up, well within waitSeconds.
        long began = Stopwatch.GetTimestamp();
        JsonElement ran = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 300 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean(), $"The app ended as it started: {ran}");
        Assert.True(ran.GetProperty("status").GetString()!.Contains("its UI up", StringComparison.Ordinal) && Stopwatch.GetElapsedTime(began) < TimeSpan.FromSeconds(25).Scaled(),
            $"run waited {Stopwatch.GetElapsedTime(began).TotalSeconds:0} s for an app that never ends, and said: {ran}");

        JsonElement windows = await Call(client, McpTools.AppWindows);
        JsonElement main = Assert.Single(windows.GetProperty("windows").EnumerateArray());
        Assert.Equal(("Agent App", 480d, 360d), (main.GetProperty("title").GetString(), main.GetProperty("width").GetDouble(), main.GetProperty("height").GetDouble()));

        CallToolResult picture = await Result(client, McpTools.AppScreenshot);
        ImageContentBlock image = Assert.Single(picture.Content.OfType<ImageContentBlock>());
        double scaling = main.GetProperty("scaling").GetDouble();
        Assert.Equal(((int)(480 * scaling), (int)(360 * scaling)), PngSize(image.DecodedData.ToArray()));
        // A window drawn blank (nothing rendered) is a few hundred bytes of PNG; this one has text and controls.
        Assert.True(image.DecodedData.Length > 4000, $"The app's picture is {image.DecodedData.Length} bytes, as a blank one would be");
        Assert.Contains("pixels a unit", Assert.Single(picture.Content.OfType<TextContentBlock>()).Text, StringComparison.Ordinal);

        string buttons = Text(await Call(client, McpTools.AppTree, new() { ["filter"] = "Button" }));
        Assert.Contains("Button#Count \"Count\"", buttons, StringComparison.Ordinal);
        Assert.Contains("\"Not clicked yet\"", Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#Counter" })), StringComparison.Ordinal);
        string clicked = Text(await Call(client, McpTools.AppClick, new() { ["control"] = "#Count" }));
        Assert.StartsWith("Clicked Button#Count @", clicked, StringComparison.Ordinal);
        Assert.Contains("\"Clicked once\"", Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#Counter" })), StringComparison.Ordinal);
        // By what it says: the button is clicked, not the text inside it.
        await Call(client, McpTools.AppClick, new() { ["control"] = "\"Count\"" });
        Assert.Contains("\"Clicked 2 times\"", Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#Counter" })), StringComparison.Ordinal);

        Assert.Contains("it shows \"Ada\"", Text(await Call(client, McpTools.AppType, new() { ["control"] = "#NameBox", ["text"] = "Ada" })), StringComparison.Ordinal);
        await Call(client, McpTools.AppKey, new() { ["keys"] = "Enter" });
        Assert.Contains("\"Hello, Ada\"", Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#Greeting" })), StringComparison.Ordinal);
        Assert.Contains("it shows \"Grace\"", Text(await Call(client, McpTools.AppType, new() { ["control"] = "#NameBox", ["text"] = "Grace", ["replace"] = true })), StringComparison.Ordinal);

        await Call(client, McpTools.AppClick, new() { ["control"] = "#Colour" });
        JsonElement popup = Assert.Single((await Call(client, McpTools.AppWindows)).GetProperty("windows").EnumerateArray(), w => w.TryGetProperty("owner", out _));
        Assert.Equal(("PopupRoot", main.GetProperty("id").GetString()), (popup.GetProperty("type").GetString(), popup.GetProperty("owner").GetString()));
        string picked = Text(await Call(client, McpTools.AppClick, new() { ["control"] = "ComboBoxItem \"Blue\"" }));
        string colour = Text(await Call(client, McpTools.AppTree, new() { ["filter"] = "#Colour" }));
        Assert.True(colour.Contains("ComboBox#Colour \"Blue\"", StringComparison.Ordinal), $"Blue, clicked in the drop-down ({picked}), is not chosen: {colour}");

        CallToolResult hidden = await Result(client, McpTools.AppClick, new() { ["control"] = "\"Item 30\"" });
        Assert.True(hidden.IsError, "A click on an item out of view was sent to whatever is there");
        Assert.Contains("scrolled out of view inside ScrollViewer#List", Text(hidden), StringComparison.Ordinal);
        Assert.Contains("ScrollViewer#List at 0,", Text(await Call(client, McpTools.AppScroll, new() { ["control"] = "#List", ["dy"] = 3 })), StringComparison.Ordinal);
        string list = Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#List", ["properties"] = new[] { "Offset" } }));
        Assert.DoesNotContain("Offset=0, 0", list, StringComparison.Ordinal);
        Assert.EndsWith("[out of view]", list.Split('\n').Single(l => l.Contains("\"Item 1\"", StringComparison.Ordinal)), StringComparison.Ordinal);

        JsonElement failed = Assert.Single((await Call(client, McpTools.AppBindingErrors)).EnumerateArray());
        Assert.Equal(("TextBlock#Caption", "Nmae", "Name"), (failed.GetProperty("control").GetString(), failed.GetProperty("path").GetString(),
            failed.GetProperty("nearest").EnumerateArray().Single().GetString()));
        Assert.Equal(AppXamlPlace("Nmae"), failed.GetProperty("place").GetString());

        Assert.Contains("TextBlock#ProjectName", Text(await Call(client, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#ProjectName" })), StringComparison.Ordinal);

        // The tools say so once the app has ended, a moment after Stop.
        await Call(client, McpTools.Stop);
        CallToolResult gone = null!;
        await Waiting.UntilAsync(async () => (gone = await Result(client, McpTools.AppTree)).IsError == true, TimeSpan.FromSeconds(10), Cancel, TimeSpan.FromMilliseconds(200));
        Assert.True(gone.IsError, "The tree of an app that has ended was answered");
        Assert.Contains("AgentApp has ended", Text(gone), StringComparison.Ordinal);

        JsonElement undrawn = (await Call(client, McpTools.Profile, new() { ["arguments"] = "--quit-at-once" })).GetProperty("frames");
        Assert.True(undrawn.GetProperty("count").GetInt32() == 0 && undrawn.GetProperty("why").GetString() is { Length: > 0 },
            $"The profile of an Avalonia app that drew nothing did not say why it has no frames: {undrawn}");
        JsonElement profiled = await Call(client, McpTools.Profile, new() { ["arguments"] = "--quit-once-drawn" });
        JsonElement frames = profiled.GetProperty("frames");
        Assert.True(frames.GetProperty("count").GetInt32() > 0, $"The profile of an Avalonia app has no frames: {frames}");
        string[] places = [.. frames.GetProperty("heaviest").EnumerateArray().Select(c => c.TryGetProperty("xaml", out JsonElement at) ? at.GetString() : null).OfType<string>()];
        Assert.NotEmpty(places);
        Assert.All(places, place => Assert.True(place.StartsWith("MainWindow.axaml:", StringComparison.Ordinal),
            $"A control's XAML place is {place}, not the folder's MainWindow.axaml and a line, as every other path a tool gives"));
        // At the editor's lines, though XamlIl's run one on below the window's lone '\r'.
        string[] lines = File.ReadAllText(Path.Combine(appFolder, "MainWindow.axaml")).Split('\n');
        (string Control, string Place)[] named = [.. frames.GetProperty("heaviest").EnumerateArray()
            .Where(c => c.GetProperty("control").GetString()!.Contains('#', StringComparison.Ordinal) && c.TryGetProperty("xaml", out _))
            .Select(c => (c.GetProperty("control").GetString()!, c.GetProperty("xaml").GetString()!))];
        Assert.NotEmpty(named);
        Assert.All(named, c => Assert.True(lines[int.Parse(c.Place.Split(':')[1], CultureInfo.InvariantCulture) - 1].Contains($"x:Name=\"{c.Control.Split('#')[1]}\"", StringComparison.Ordinal),
            $"{c.Control}'s XAML place is {c.Place}, not its element's line"));
        JsonElement[] types = [.. frames.GetProperty("types").EnumerateArray()];
        Assert.Contains(types, t => t.GetProperty("type").GetString() == "TextBlock" && t.GetProperty("controlCount").GetInt32() >= 4
            && t.GetProperty("controls").EnumerateArray().Any(c => c.GetProperty("control").GetString()!.StartsWith("TextBlock#", StringComparison.Ordinal)));
        Assert.All(types, t => Assert.True(t.GetProperty("measures").GetInt32() + t.GetProperty("arranges").GetInt32() > 0, $"A type with no layout is listed: {t}"));
        JsonElement longest = frames.GetProperty("longest");
        Assert.True(longest.GetProperty("durationMs").GetDouble() == frames.GetProperty("longestMs").GetDouble(),
            $"The longest frame is not the one the frames say: {frames}");
    }

    /// <summary>What the Live Visual Tree and the editor give a person debugging the app, given an agent: debug_start back
    /// once the app's UI is up; a click into a breakpoint told at once where it stopped; a button's properties with where each
    /// value came from and its styles with why each applies or not; Break When Value Changes on the counter's text, which a
    /// click stops where the code-behind sets it; the binding values the window's XAML shows at that stop; and, debugged again,
    /// a failed binding stopping it with XAML Binding Errors on, and running on with it off.</summary>
    [Fact]
    public async Task An_agent_debugging_an_avalonia_app_clicks_into_a_breakpoint_reads_a_controls_sources_and_styles_breaks_where_a_property_changes_and_on_a_failed_binding()
    {
        await using McpClient client = await Start(["--mcp", appFolder], languageServer: false);
        // The app never stops by itself, so debug_start must come back once its UI is up, well within waitSeconds.
        Stopwatch launching = Stopwatch.StartNew();
        JsonElement started = await Call(client, McpTools.DebugStart, new() { ["waitSeconds"] = 300 });
        Assert.True(started.GetProperty("status").GetString()!.Contains("its UI up", StringComparison.Ordinal) && launching.Elapsed < TimeSpan.FromSeconds(50).Scaled(),
            $"debug_start waited {launching.Elapsed.TotalSeconds:0} s for an app that never stops, and said: {started}");
        Assert.Single((await Call(client, McpTools.AppWindows)).GetProperty("windows").EnumerateArray());

        // Set while the app runs; the click must say where it stopped, as the paused app cannot say what the click did.
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "MainWindow.axaml.cs", ["line"] = 23 });
        Stopwatch asked = Stopwatch.StartNew();
        string clicked = Text(await Call(client, McpTools.AppClick, new() { ["control"] = "#Count" }));
        Assert.True(asked.Elapsed < TimeSpan.FromSeconds(8).Scaled(), $"The click that stopped at a breakpoint took {asked.Elapsed} to say so: {clicked}");
        Assert.Contains("and that paused the program (breakpoint) at MainWindow.axaml.cs:23:", clicked, StringComparison.Ordinal);
        JsonElement stopped = await Call(client, McpTools.DebugState);
        Assert.Equal("paused", stopped.GetProperty("state").GetString());
        string[] frames = [.. stopped.GetProperty("frames").EnumerateArray().Select(f => f.GetString()!)];
        Assert.True(frames[0].Contains("MainWindow.axaml.cs:23", StringComparison.Ordinal) && frames.Length < 8
            && frames.Any(f => f.Contains(" frames outside the folder's code (", StringComparison.Ordinal) && f.Contains("Avalonia.", StringComparison.Ordinal)),
            $"The stop's frames, for an agent:\n{string.Join('\n', frames)}");
        await Call(client, McpTools.BreakpointSet, new() { ["path"] = "MainWindow.axaml.cs", ["line"] = 23, ["remove"] = true });
        await Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 1 });
        Assert.Contains("\"Clicked once\"", Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#Counter" })), StringComparison.Ordinal);

        JsonElement button = await Call(client, McpTools.AppDetails, new() { ["control"] = "#Count" });
        Assert.Equal("Button", button.GetProperty("type").GetString());
        Assert.Equal(AppXamlPlace("Button x:Name=\"Count\""), button.GetProperty("xaml").GetString());
        JsonElement content = Assert.Single(button.GetProperty("properties").EnumerateArray(), p => p.GetProperty("name").GetString() == "Content");
        Assert.Equal(("\"Count\"", "Local"), (content.GetProperty("value").GetString(), content.GetProperty("source").GetString()));
        JsonElement[] styles = [.. button.GetProperty("styles").EnumerateArray()];
        Assert.True(styles.Any(s => s.GetProperty("applies").GetBoolean()) && styles.Any(s => !s.GetProperty("applies").GetBoolean()
            && s.GetProperty("whyNot").GetString() == "`:pressed` is not active on it"), $"The button's styles do not say which apply and why not: {button.GetProperty("styles")}");
        Assert.DoesNotContain(button.GetProperty("properties").EnumerateArray(), p => p.GetProperty("source").GetString() == "Default");

        JsonElement watched = Assert.Single((await Call(client, McpTools.BreakpointSet, new() { ["control"] = "#Counter", ["property"] = "Text" })).EnumerateArray());
        Assert.Equal("Text on TextBlock#Counter", watched.GetProperty("property").GetString());
        Assert.True((await Call(client, McpTools.AppDetails, new() { ["control"] = "#Counter", ["properties"] = "Text" })).GetProperty("properties").EnumerateArray()
            .Any(p => p.GetProperty("name").GetString() == "Text" && p.TryGetProperty("breakpoint", out JsonElement marked) && marked.GetBoolean()),
            "The counter's details do not mark its watched text");
        clicked = Text(await Call(client, McpTools.AppClick, new() { ["control"] = "#Count" }));
        Assert.True(clicked.Contains("and that paused the program", StringComparison.Ordinal) && clicked.Contains("at MainWindow.axaml.cs:24:", StringComparison.Ordinal),
            $"The click did not stop where the code-behind sets the watched text: {clicked}");

        await Call(client, McpTools.Open, new() { ["path"] = "MainWindow.axaml" });
        JsonElement stop = await Call(client, McpTools.DebugState);
        string[] values = stop.TryGetProperty("bindingValues", out JsonElement shown) ? [.. shown.EnumerateArray().Select(v => v.GetString()!)] : [];
        Assert.True(values.Contains("MainWindow.axaml:12: Text: \"Ada\"") && values.Any(v => v.StartsWith("MainWindow.axaml:11: Text: binding failed at Nmae: ", StringComparison.Ordinal)),
            $"The binding values at the stop are not those the editor shows: {stop}");

        // By the breakpoint's own id for its element, since the paused app answers no query.
        await Call(client, McpTools.BreakpointSet, new() { ["control"] = watched.GetProperty("control").GetString(), ["property"] = "Text", ["remove"] = true });
        Assert.DoesNotContain((await Call(client, McpTools.DebugState, new() { ["of"] = "breakpoints" })).EnumerateArray(), b => b.TryGetProperty("property", out _));
        await Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 1 });
        Assert.Contains("\"Clicked 2 times\"", Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#Counter" })), StringComparison.Ordinal);
        await Call(client, McpTools.Stop);

        JsonElement on = Assert.Single((await Call(client, McpTools.BreakpointSet, new() { ["bindingErrors"] = true })).EnumerateArray(), b => b.TryGetProperty("bindingErrors", out _));
        Assert.True(on.GetProperty("enabled").GetBoolean(), $"XAML Binding Errors is not on: {on}");
        JsonElement failed = await Call(client, McpTools.DebugStart);
        Assert.True(failed.GetProperty("state").GetString() == "paused"
            && failed.GetProperty("locals").EnumerateArray().Any(l => l.GetProperty("name").GetString() == "$bindingError" && l.GetProperty("value").GetString()!.Contains("Nmae", StringComparison.Ordinal)),
            $"The misspelt binding did not stop the app with XAML Binding Errors on: {failed}");
        // At the editor's line, which XamlIl's runs one past below the window's lone '\r'.
        int reference = failed.GetProperty("locals").EnumerateArray().First(l => l.GetProperty("name").GetString() == "$bindingError").GetProperty("reference").GetInt32();
        JsonElement xaml = Assert.Single((await Call(client, McpTools.DebugState, new() { ["reference"] = reference })).EnumerateArray(), m => m.GetProperty("name").GetString() == "Xaml");
        Assert.Equal($"\"{AppXamlPlace("TextBlock x:Name=\"Caption\"")}\"", xaml.GetProperty("value").GetString());
        JsonElement off = Assert.Single((await Call(client, McpTools.BreakpointSet, new() { ["bindingErrors"] = true, ["remove"] = true })).EnumerateArray(), b => b.TryGetProperty("bindingErrors", out _));
        Assert.False(off.GetProperty("enabled").GetBoolean(), $"XAML Binding Errors is still on: {off}");
        Assert.Equal("running", (await Call(client, McpTools.DebugContinue, new() { ["waitSeconds"] = 2 })).GetProperty("state").GetString());
        await Call(client, McpTools.Stop);
    }

    /// <summary>On the person's window an agent asks before its first click or key in their app or in Passenger itself, and
    /// once allowed both still never answers the questions Passenger asks the person for it, nor reaches Settings. Passenger's
    /// own windows are inspected once a process, so this is the run's one test of them in the test process itself.</summary>
    [AvaloniaFact]
    public void An_agent_on_the_persons_window_is_asked_before_it_first_clicks_or_presses_keys_in_their_avalonia_app_or_in_passenger()
    {
        MainWindow window = AgentsWindow(appFolder, languageServer: false);
        McpClient? client = null;
        SettingsDialog? shown = null;
        try
        {
            Ui.Launched(window, window.RunAsync(), "the app to build and start", TimeSpan.FromMinutes(4));
            client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
            McpClient agent = client;

            Assert.Contains("Button#Count", Text(Await(Task.Run(() => Call(agent, McpTools.AppTree, new() { ["filter"] = "#Count" })), "the app's tree")), StringComparison.Ordinal);
            Assert.Empty(window.OwnedWindows.OfType<ConfirmDialog>());

            Task<JsonElement> clicking = Task.Run(() => Call(agent, McpTools.AppClick, new() { ["control"] = "#Count" }));
            ConfirmDialog asked = Question(window, "the question for the agent's click");
            Assert.Contains("Allow app_click?", Texts(asked));
            Press(asked, "Allow");
            Assert.StartsWith("Clicked Button#Count", Text(Await(clicking, "the click")), StringComparison.Ordinal);
            Assert.Contains("\"Clicked once\"", Text(Await(Task.Run(() => Call(agent, McpTools.AppTree, new() { ["control"] = "#Counter" })), "the counter")), StringComparison.Ordinal);

            TextBlock project = window.FindControl<TextBlock>("ProjectName")!;
            // The tree names an element by its type, #name, any .classes, then its text.
            Assert.Matches($"TextBlock#ProjectName(\\.[\\w-]+)* \"{System.Text.RegularExpressions.Regex.Escape(project.Text!)}\"",
                Text(Await(Task.Run(() => Call(agent, McpTools.AppTree, new() { ["app"] = "ide", ["filter"] = "#ProjectName" })), "Passenger's tree")));
            Await(Task.Run(() => Call(agent, McpTools.AppClick, new() { ["app"] = "ide", ["control"] = "#RunConfigButton" })), "the click on Passenger's run configurations");
            Dictionary<string, object?> menu = new() { ["app"] = "ide", ["filter"] = "MenuItem \"AgentApp\"" };
            Assert.Contains("MenuItem \"AgentApp\"", Text(Await(Task.Run(() => Call(agent, McpTools.AppTree, menu)), "the run configurations' menu")), StringComparison.Ordinal);
            Task<JsonElement> escaping = Task.Run(() => Call(agent, McpTools.AppKey, new() { ["app"] = "ide", ["keys"] = "Escape" }));
            Press(Question(window, "the question for the agent's keys"), "Allow");
            string escaped = Text(Await(escaping, "the Escape"));
            string after = Text(Await(Task.Run(() => Call(agent, McpTools.AppTree, menu)), "the menu closed"));
            Assert.True(after.StartsWith("Nothing in Passenger matches", StringComparison.Ordinal), $"Escape ({escaped}) left the menu open: {after}");

            Task<CallToolResult> building = Task.Run(() => Result(agent, McpTools.Build));
            ConfirmDialog question = Question(window, "the question for the agent's build");
            CallToolResult clicked = Await(Task.Run(() => Result(agent, McpTools.AppClick, new() { ["app"] = "ide", ["window"] = "ConfirmDialog", ["control"] = "Button \"Allow\"" })),
                "the agent's click on the question's Allow");
            Assert.True(clicked.IsError == true && Text(clicked).Contains("only they answer it", StringComparison.Ordinal),
                $"The agent's click on the question its own build waits on was answered: {Text(clicked)}");
            CallToolResult entered = Await(Task.Run(() => Result(agent, McpTools.AppKey, new() { ["app"] = "ide", ["window"] = "ConfirmDialog", ["keys"] = "Enter" })),
                "the agent's Enter in the question");
            Assert.True(entered.IsError == true && Text(entered).Contains("only they answer it", StringComparison.Ordinal),
                $"The agent's Enter in the question its own build waits on was answered: {Text(entered)}");
            Assert.True(question.IsVisible && !building.IsCompleted, "The question the agent's build waits on was answered by the agent's own input");
            Press(question, "Deny");
            Assert.Contains("did not allow build", Text(Await(building, "the build the person denied")), StringComparison.Ordinal);

            shown = window.ShowSettings();
            Ui.Settle();
            CallToolResult set = Await(Task.Run(() => Result(agent, McpTools.AppClick, new() { ["app"] = "ide", ["window"] = "SettingsDialog", ["x"] = 20, ["y"] = 20 })),
                "the agent's click in Settings");
            Assert.True(set.IsError == true && Text(set).Contains("Settings is the person's alone", StringComparison.Ordinal),
                $"The agent's click in Settings was answered: {Text(set)}");
        }
        finally
        {
            shown?.Close();
            if (client is { } going)
            {
                Gone(going);
            }
            window.Runner.Stop();
            WaitFor(() => !window.Runner.IsRunning, "the app stopped");
            window.Close();
        }
    }

    /// <summary>Where some text begins in the app's window XAML as a tool names a place: "MainWindow.axaml:line:column", from
    /// one, as the editor counts lines (at each '\n' alone).</summary>
    private string AppXamlPlace(string find)
    {
        string text = File.ReadAllText(Path.Combine(appFolder, "MainWindow.axaml"));
        int at = text.IndexOf(find, StringComparison.Ordinal);
        return $"MainWindow.axaml:{text.AsSpan(0, at).Count('\n') + 1}:{at - text.LastIndexOf('\n', at)}";
    }

    /// <summary>An agent on a window debugging two Avalonia apps at once (<see cref="DebuggedAppsTests"/>' pair): its app tools act
    /// on the app of the Debug tab it names, by the name debug_state lists, else on the chosen tab's; the failed bindings it
    /// reads name the program each failed in.</summary>
    [AvaloniaFact]
    public void An_agents_app_tools_read_the_app_of_the_debug_tab_it_names_and_the_chosen_tabs_by_default()
    {
        string apps = TestFolders.Create("apps-");
        DeleteAtEnd(apps);
        Restore.Run(DebuggedAppsTests.WriteTo(apps));
        MainWindow window = AgentsWindow(apps, languageServer: false);
        DebuggedAppsTests.Debug(window, "BindingErrorsApp", "Caption");
        DebuggedAppsTests.Debug(window, "LiveTreeApp", "Delete");
        McpClient client = Await(Task.Run(() => Start(["--mcp-attach", ProcessId])), "the agent attaching");
        try
        {
            string chosen = Text(Unasked(window, client, McpTools.AppTree));
            Assert.True(chosen.Contains("Delete", StringComparison.Ordinal) && !chosen.Contains("Caption", StringComparison.Ordinal),
                $"The agent's app_tree naming no app was not the chosen tab's, LiveTreeApp's: {chosen}");
            string named = Text(Unasked(window, client, McpTools.AppTree, new() { ["app"] = "BindingErrorsApp" }));
            Assert.True(named.Contains("Caption", StringComparison.Ordinal) && !named.Contains("Delete", StringComparison.Ordinal),
                $"The agent's app_tree naming BindingErrorsApp was not that app's: {named}");
            JsonElement failures = Unasked(window, client, McpTools.AppBindingErrors);
            Assert.True(failures.EnumerateArray().Any(f => f.GetProperty("app").GetString() == "BindingErrorsApp" && f.GetProperty("path").GetString() == "Nmae"),
                $"The first app's failed binding was not listed under its name: {failures}");
            Assert.Equal("Stopped", Unasked(window, client, McpTools.Stop).GetString());
            WaitFor(() => !window.Debugger.IsDebugging, "both apps to end");
        }
        finally
        {
            Gone(client);
            window.Close();
        }
    }
}
