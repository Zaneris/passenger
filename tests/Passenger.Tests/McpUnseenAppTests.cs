using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>tests/Debuggees/AgentApp made a desktop app as a person's is, written once for the class and restored: on the
/// desktop's own platforms (Avalonia.Desktop), its BuildAvaloniaApp() choosing them (UsePlatformDetect). Its Main starts no
/// UI: it says it ran and ends, so a Passenger that let it run would fail the test with no window opened.</summary>
public class DesktopAgentAppFolder : IAsyncLifetime
{
    /// <summary>What the app's Main writes, never written where its BuildAvaloniaApp() is started in its place.</summary>
    public const string MainRan = "AgentApp's Main ran";

    public DesktopAgentAppFolder()
    {
        Folder = PathNames.Canonical(TestFolders.Create("desktopapp-"));
    }

    /// <summary>The app's project file.</summary>
    protected virtual string Project => AvaloniaApp.DesktopProject();

    /// <summary>The app's Properties/launchSettings.json, where it has one.</summary>
    protected virtual string? LaunchSettings => null;

    /// <summary>The app's Program.cs.</summary>
    protected virtual string Program => $$"""
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
        """;

    public ValueTask InitializeAsync() => new(TestFolders.PrepareAsync(async () =>
    {
        AvaloniaApp.Debuggee(Folder, "AgentApp", Project);
        File.WriteAllText(Path.Combine(Folder, "Program.cs"), Program);
        if (LaunchSettings is { } profiles)
        {
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(Folder, "Properties")).FullName, "launchSettings.json"), profiles);
        }
        await Restore.RunAsync(Folder);
    }, Folder));

    public string Folder { get; }

    public ValueTask DisposeAsync()
    {
        TestFolders.Delete(Folder);
        return ValueTask.CompletedTask;
    }
}

/// <summary>The desktop app carrying Avalonia's headless platform too, as an app does for its tests, none of its own code
/// using it.</summary>
public sealed class HeadlessCarryingAppFolder : DesktopAgentAppFolder
{
    protected override string Project => AvaloniaApp.DesktopProject("Avalonia.Headless");
}

/// <summary>The desktop app run by a launch profile that names its program, the app's assembly under dotnet, rather than its
/// project's.</summary>
public sealed class NamedProgramAppFolder : DesktopAgentAppFolder
{
    /// <summary>The configuration the profile makes.</summary>
    public const string Configuration = "AgentApp: Named";

    protected override string LaunchSettings => """
        { "profiles": { "Named": { "commandName": "Executable", "executablePath": "dotnet", "commandLineArgs": "$(TargetPath)" } } }
        """;
}

/// <summary>The desktop app whose own Main chooses its platform: asked for it the headless one, after UsePlatformDetect has
/// loaded the desktop's, else X11, set up with no window, which no Mac or Windows has to start and nothing opens on Linux.</summary>
public sealed class PlatformChoosingAppFolder : DesktopAgentAppFolder
{
    /// <summary>The argument that has its Main choose the headless platform.</summary>
    public const string Unseen = "--unseen";

    /// <summary>What the app writes once a platform of a screen has started.</summary>
    public const string PlatformStarted = "AgentApp's platform started";

    protected override string Project => AvaloniaApp.DesktopProject("Avalonia.Headless");

    protected override string Program => $$"""
        using Avalonia;
        using Avalonia.Headless;

        namespace AgentApp;

        public static class Program
        {
            public static int Main(string[] args)
            {
                if (args is ["{{Unseen}}"])
                {
                    return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont()
                        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).StartWithClassicDesktopLifetime(args);
                }
                AppBuilder.Configure<App>().UseX11().UseSkia().UseHarfBuzz().WithInterFont().SetupWithoutStarting();
                System.Console.WriteLine("{{PlatformStarted}}");
                return 3;
            }
        }
        """;
}

/// <summary>An agent's own Passenger running a desktop app whose Main would open its window on the screen: the app's
/// inspector starts it through its BuildAvaloniaApp() on Avalonia's headless platform in the Main's place, before the Main,
/// so no window reaches a screen and the app tools see and use it as they do any app's.</summary>
[Collection(Alone.Name)]
public sealed class McpUnseenAppTests : McpRig, IClassFixture<DesktopAgentAppFolder>, IClassFixture<HeadlessCarryingAppFolder>, IClassFixture<PlatformChoosingAppFolder>,
    IClassFixture<NamedProgramAppFolder>
{
    private readonly string appFolder;
    private readonly string carryingFolder;
    private readonly string choosingFolder;
    private readonly string namedFolder;

    public McpUnseenAppTests(DesktopAgentAppFolder app, HeadlessCarryingAppFolder carrying, PlatformChoosingAppFolder choosing, NamedProgramAppFolder named)
    {
        appFolder = app.Folder;
        carryingFolder = carrying.Folder;
        choosingFolder = choosing.Folder;
        namedFolder = named.Folder;
    }

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

    /// <summary>Carrying the headless platform says nothing of the Main beside it: the app goes as it goes without the package,
    /// so its Main, which would draw on the screen, never runs.</summary>
    [Fact]
    public async Task A_desktop_app_that_only_carries_the_headless_platform_is_started_on_it_in_its_mains_place()
    {
        await using McpClient client = await Start(["--mcp", carryingFolder], languageServer: false);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The app did not build: {built}");
        JsonElement ran = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 3600, ["hotReload"] = false });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean() && ran.GetProperty("status").GetString()!.Contains("its BuildAvaloniaApp() started in place of its Main", StringComparison.Ordinal),
            $"The package alone was taken to mean the app draws headless, which would leave its Main to open its windows on the person's screen: {ran}");
        Assert.DoesNotContain(DesktopAgentAppFolder.MainRan, Text(await Call(client, McpTools.Output)), StringComparison.Ordinal);
        await Call(client, McpTools.Stop);
    }

    /// <summary>A launch profile naming the app's assembly under dotnet starts the same desktop app, so it goes the same way,
    /// though the project's build is not what the plan runs.</summary>
    [Fact]
    public async Task A_desktop_app_a_launch_profile_names_is_started_on_the_headless_platform_in_its_mains_place()
    {
        await using McpClient client = await Start(["--mcp", namedFolder], languageServer: false);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The app did not build: {built}");
        JsonElement ran = await Call(client, McpTools.Run, new() { ["configuration"] = NamedProgramAppFolder.Configuration, ["waitSeconds"] = 3600 });
        Assert.True(ran.GetProperty("stillRunning").GetBoolean() && ran.GetProperty("status").GetString()!.Contains("its BuildAvaloniaApp() started in place of its Main", StringComparison.Ordinal),
            $"The named desktop app was started as it is, where its windows would reach the person's screen: {ran}");
        Assert.DoesNotContain(DesktopAgentAppFolder.MainRan, Text(await Call(client, McpTools.Output)), StringComparison.Ordinal);
        await Call(client, McpTools.Stop);
    }

    /// <summary>A Main whose own code uses the headless platform chooses for itself: the platform loaded and left is no end,
    /// the headless one comes up with no Main left out, and one of a screen ends the app as it starts, before it has done
    /// anything there. Windows puts the app on a desktop of its own instead.</summary>
    [Fact]
    public async Task A_main_choosing_its_platform_runs_on_the_headless_one_and_ends_as_it_starts_one_of_a_screen()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows runs an app nobody is to see on a desktop of its own, where its Main needs no stopping");
        await using McpClient client = await Start(["--mcp", choosingFolder], languageServer: false);
        JsonElement built = await Call(client, McpTools.Build);
        Assert.True(built.GetProperty("succeeded").GetBoolean(), $"The app did not build: {built}");

        JsonElement headless = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 3600, ["hotReload"] = false, ["arguments"] = PlatformChoosingAppFolder.Unseen });
        string up = headless.GetProperty("status").GetString()!;
        Assert.True(headless.GetProperty("stillRunning").GetBoolean() && up.Contains("on Avalonia's headless platform", StringComparison.Ordinal) && !up.Contains("in place of its Main", StringComparison.Ordinal),
            $"A Main choosing the headless platform did not come up there itself: {headless}");
        await Call(client, McpTools.Stop);

        JsonElement ended = await Call(client, McpTools.Run, new() { ["waitSeconds"] = 3600, ["hotReload"] = false });
        Assert.True(!ended.GetProperty("stillRunning").GetBoolean() && ended.GetProperty("status").GetString()!.Contains("It was ended as it started", StringComparison.Ordinal),
            $"A Main starting a platform of a screen was not ended as it did, saying why: {ended}");
        Assert.DoesNotContain(PlatformChoosingAppFolder.PlatformStarted, Text(await Call(client, McpTools.Output)), StringComparison.Ordinal);
    }
}
