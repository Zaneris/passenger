using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent fixing a Godot game's checks through code_actions and apply_code_action as a person does through Show
/// Context Actions: the open scene's connection to a method its script lacks lists the method's making, which makes it in the
/// script, saved, so the scene's mark goes. Without a Godot 4 .NET build, it skips saying why.</summary>
public sealed class McpGodotFixTests : McpRig
{
    [Fact]
    public async Task An_agent_makes_the_method_a_scenes_connection_calls_through_code_actions()
    {
        GodotFound? godot = GodotExecutables.Find(null);
        Assert.SkipWhen(godot is not { WhyNot: null, Version: not null, Packages: not null },
            godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
                : $"The Godot found carries no packages a C# project restores from: {godot.WhyNot}");
        string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-fix-"));
        try
        {
            GodotTests.Game.Make(folder, godot!.Version!, godot.Packages!, GodotFixesTests.Script, GodotFixesTests.Scene);
            await Restore.RunAsync(folder);
            await using ModelContextProtocol.Client.McpClient client = await Start(["--mcp", folder]);
            await Until(async () => Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "Main.cs" })).Contains("which Main has no method of", StringComparison.Ordinal),
                "Main.cs to be read against its scene");
            int line = GodotSceneTests.LineOf(GodotFixesTests.Scene, "method=\"OnQuitPressed\"");
            int column = GodotFixesTests.Scene.Split('\n')[line - 1].IndexOf("OnQuitPressed", StringComparison.Ordinal) + 2;
            Dictionary<string, object?> at = new() { ["path"] = "main.tscn", ["line"] = line, ["column"] = column };
            string fix = "Create method OnQuitPressed() in Main";
            string[] offered = [.. (await Call(client, McpTools.CodeActions, at)).EnumerateArray().Select(a => a.GetString()!)];
            Assert.Contains($"{fix} [quickfix]", offered);

            string made = Text(await Call(client, McpTools.ApplyCodeAction, new(at) { ["title"] = fix }));
            Assert.Contains("Main.cs", made, StringComparison.Ordinal);
            Assert.Contains("private void OnQuitPressed()", File.ReadAllText(Path.Combine(folder, "Main.cs")), StringComparison.Ordinal);
            string marks = string.Empty;
            Exception? failed = await Record.ExceptionAsync(() => Until(async () => !(marks = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "main.tscn" })))
                .Contains("has no method OnQuitPressed", StringComparison.Ordinal), "the scene's mark of the missing method to go"));
            Assert.True(failed is null, $"{failed?.Message}\nThe scene's marks: {marks}");
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }
}
