using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Passenger.App.Mcp;
using Passenger.Core;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>An agent sees a running Godot game's frames with no window on the person's screen: its own Passenger runs the
/// game through Godot's embedded display on macOS (a background process with no window or Dock icon), so app_screenshot
/// gives what the game draws. On a system where Godot draws nothing without a window (Windows, Linux) or a Godot before 4.5
/// it skips saying why. Run alone: it holds a game to its frames.</summary>
[Collection(Alone.Name)]
public sealed class McpGodotFramesTests : McpRig
{
    /// <summary>The player, an orange square, steps right each press of the right arrow.</summary>
    private const string Main = "extends Node2D\n\n\nfunc _unhandled_input(event: InputEvent) -> void:\n\tif event.is_action_pressed(\"ui_right\"):\n"
        + "\t\t$Player.position.x += 200\n\t\tprint(\"stepped to \", $Player.position.x)\n";

    private readonly string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-frames-"));

    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public McpGodotFramesTests()
    {
        Assert.SkipWhen(godot is not { WhyNotScripts: null }, godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests"
            : $"The Godot found cannot run a Godot 4 game: {godot.WhyNotScripts}");
        Assert.SkipUnless(GodotExecutables.DrawsWithoutWindow(godot!.Version), OperatingSystem.IsMacOS()
            ? $"Godot {godot.Version} has no embedded display: Godot 4.5 and newer draw a game on macOS with no window"
            : "Godot draws a game here only in a window, which would show on the person's screen: an agent's game runs headless, with no frames");
        File.WriteAllText(Path.Combine(folder, "project.godot"), "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Stepper\"\n"
            + "run/main_scene=\"res://main.tscn\"\nconfig/features=PackedStringArray(\"4.7\")\n\n[display]\n\nwindow/size/viewport_width=640\nwindow/size/viewport_height=360\n");
        File.WriteAllText(Path.Combine(folder, "main.tscn"), "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://main.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n\n[node name=\"Player\" type=\"ColorRect\" parent=\".\"]\n"
            + "offset_left = 40.0\noffset_top = 140.0\noffset_right = 120.0\noffset_bottom = 220.0\ncolor = Color(1, 0.4, 0.1, 1)\n");
        File.WriteAllText(Path.Combine(folder, "main.gd"), Main);
        DeleteAtEnd(folder);
        DeleteAtEnd(folder + "-home");
    }

    [Fact]
    public async Task An_agent_moves_a_games_player_by_input_and_reads_back_a_frame_showing_it_moved()
    {
        await using McpClient client = await Start(["--mcp", folder], languageServer: false,
            environment: new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" });
        await Call(client, McpTools.Run, new() { ["configuration"] = "Stepper", ["waitSeconds"] = 60 });

        (int width, int height, double before) = await FrameAsync(client);
        Assert.Equal((640, 360), (width, height));
        Assert.InRange(before, 60, 100);
        string pressed = Text(await Call(client, McpTools.AppKey, new() { ["keys"] = "Right" }));
        await Until(async () => Text(await Call(client, McpTools.Output)).Contains("stepped to 240", StringComparison.Ordinal), $"the game to step its player ({pressed})");
        double after = (await FrameAsync(client)).Player;
        Assert.True(after is > 260 and < 300, $"The frame after the step was to show the player moved right from x {before} to about 280: it is at {after}");
        await Call(client, McpTools.Stop);
    }

    /// <summary>The game's frame as app_screenshot gives it: its size, and the middle of the player's orange across it.</summary>
    private async Task<(int Width, int Height, double Player)> FrameAsync(McpClient client)
    {
        CallToolResult picture = await Result(client, McpTools.AppScreenshot);
        Assert.True(picture.IsError != true, $"The game's picture was refused: {Text(picture)}");
        ImageContentBlock image = Assert.Single(picture.Content.OfType<ImageContentBlock>());
        using SKBitmap frame = SKBitmap.Decode(image.DecodedData.ToArray());
        List<int> orange = [];
        for (int x = 0; x < frame.Width; x++)
        {
            for (int y = 0; y < frame.Height; y += 4)
            {
                SKColor pixel = frame.GetPixel(x, y);
                if (pixel.Red > 200 && pixel.Green is > 70 and < 140 && pixel.Blue < 60)
                {
                    orange.Add(x);
                    break;
                }
            }
        }
        Assert.True(orange.Count > 0, $"The game's {frame.Width}x{frame.Height} frame shows no orange player");
        return (frame.Width, frame.Height, orange.Average());
    }
}
