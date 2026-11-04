using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>tests/Debuggees/AgentApp made a desktop app as a person's is, written once for the class and restored: on the
/// desktop's own platforms (Avalonia.Desktop), its BuildAvaloniaApp() choosing them (UsePlatformDetect). Its Main starts no
/// UI: it says it ran and ends, so a Passenger that let it run would fail the test with no window opened.</summary>
public sealed class DesktopAgentAppFolder : IAsyncLifetime
{
    /// <summary>What the app's Main writes, never written where its BuildAvaloniaApp() is started in its place.</summary>
    public const string MainRan = "AgentApp's Main ran";

    public DesktopAgentAppFolder()
    {
        Folder = PathNames.Canonical(TestFolders.Create("desktopapp-"));
    }

    public ValueTask InitializeAsync() => new(TestFolders.PrepareAsync(async () =>
    {
        AvaloniaApp.Debuggee(Folder, "AgentApp", AvaloniaApp.DesktopProject());
        File.WriteAllText(Path.Combine(Folder, "Program.cs"), $$"""
            using Avalonia;

            namespace AgentApp;

            public static class Program
            {
                public static int Main(string[] args)
                {
                    System.Console.WriteLine("{{MainRan}}");
                    return 3;
                }

                public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();
            }
            """);
        await Restore.RunAsync(Folder);
    }, Folder));

    public string Folder { get; }

    public ValueTask DisposeAsync()
    {
        TestFolders.Delete(Folder);
        return ValueTask.CompletedTask;
    }
}

/// <summary>An agent's own Passenger running a desktop app whose Main would open its window on the screen: the app's
/// inspector starts it through its BuildAvaloniaApp() on Avalonia's headless platform in the Main's place, before the Main,
/// so no window reaches a screen and the app tools see and use it as they do any app's.</summary>
[Collection(Alone.Name)]
public sealed class McpUnseenAppTests : McpRig, IClassFixture<DesktopAgentAppFolder>
{
    private readonly string appFolder;

    public McpUnseenAppTests(DesktopAgentAppFolder app) => appFolder = app.Folder;

    /// <summary>Run with no window on any screen: its window and picture are the app's own, a click and a XAML edit Hot
    /// Reload applies reach it, its Main never runs; then debugged and profiled the same way, its arguments its Main's.</summary>
    [Fact]
    public async Task An_agents_own_passenger_runs_debugs_and_profiles_a_desktop_avalonia_app_on_the_headless_platform_and_its_tools_use_it()
    {
        await using McpClient client = await Start(["--mcp", appFolder], languageServer: false);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The app did not build: {built}");
        // The app never ends by itself, so run must come back once its UI is up: an hour's wait would outlast the call's limit.
        JsonElement ran = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 3600, ["hotReload"] = false });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean() && ran.GetProperty("status").GetString()!.Contains("its BuildAvaloniaApp() started in place of its Main", StringComparison.Ordinal),
            $"The desktop app did not come up on the headless platform: {ran}");
        Assert.DoesNotContain(DesktopAgentAppFolder.MainRan, Text(await Call(client, McpTools.Output)), StringComparison.Ordinal);

        JsonElement main = Assert.Single((await Call(client, McpTools.AppWindows)).GetProperty("windows").EnumerateArray());
        Assert.Equal(("Agent App", 480d, 360d), (main.GetProperty("title").GetString(), main.GetProperty("width").GetDouble(), main.GetProperty("height").GetDouble()));
        CallToolResult picture = await Result(client, McpTools.AppScreenshot);
        ImageContentBlock image = Assert.Single(picture.Content.OfType<ImageContentBlock>());
        double scaling = main.GetProperty("scaling").GetDouble();
        Assert.Equal(((int)(480 * scaling), (int)(360 * scaling)), PngSize(image.DecodedData.ToArray()));
        // A window drawn blank (nothing rendered) is a few hundred bytes of PNG; this one has text and controls.
        Assert.True(image.DecodedData.Length > 4000, $"The app's picture is {image.DecodedData.Length} bytes, as a blank one would be");

        Assert.StartsWith("Clicked Button#Count @", Text(await Call(client, McpTools.AppClick, new() { ["control"] = "#Count" })), StringComparison.Ordinal);
        Assert.Contains("\"Clicked once\"", Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#Counter" })), StringComparison.Ordinal);

        await Call(client, McpTools.Edit, new() { ["path"] = "MainWindow.axaml", ["oldText"] = "Text=\"Nobody greeted\"", ["newText"] = "Text=\"Greeted unseen\"" });
        JsonElement reloaded = await Call(client, McpTools.HotReload);
        Assert.True(reloaded.GetProperty("result").GetString() == "applied", $"The window's XAML edit was not applied to the app: {reloaded}");
        Assert.Contains("\"Greeted unseen\"", Text(await Call(client, McpTools.AppTree, new() { ["control"] = "#Greeting" })), StringComparison.Ordinal);
        await Call(client, McpTools.Stop);

        JsonElement started = await Call(client, McpTools.DebugStart, new() { ["waitSeconds"] = 3600 });
        Assert.True(started.GetProperty("status").GetString()!.Contains("its BuildAvaloniaApp() started in place of its Main", StringComparison.Ordinal),
            $"The debugged desktop app did not come up on the headless platform: {started}");
        Assert.Single((await Call(client, McpTools.AppWindows)).GetProperty("windows").EnumerateArray());
        await Call(client, McpTools.Stop);

        // Its Main's arguments reach the app it ends by itself once drawn, as its Main's would.
        JsonElement frames = (await Call(client, McpTools.Profile, new() { ["arguments"] = "--quit-once-drawn" })).GetProperty("frames");
        Assert.True(frames.GetProperty("count").GetInt32() > 0, $"The profile of the desktop app on the headless platform has no frames: {frames}");
        Assert.DoesNotContain(DesktopAgentAppFolder.MainRan, Text(await Call(client, McpTools.Output)), StringComparison.Ordinal);
    }
}
