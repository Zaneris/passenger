using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent's usages of a GDScript's function, through the same Find Usages a person's is: it lists the C# string
/// calling it on a node the scene runs the script on, and the scene's connection to it, beside what Godot's server finds.
/// Its project restores from the packages the Godot found carries and the script starts GDScript's server in a headless
/// Godot editor, so without a Godot 4 .NET build it skips saying why; a Godot editor of its own, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class McpGodotScriptTests : McpRig
{
    [Fact]
    public async Task An_agents_usages_of_a_gdscripts_function_list_the_csharp_string_and_the_scenes_connection_naming_it()
    {
        GodotFound? godot = GodotExecutables.Find(null);
        Assert.SkipWhen(godot is not { WhyNot: null, Version: not null, Packages: not null },
            godot is null ? "No Godot was found (GODOT, the PATH, Applications): install Godot 4's .NET build, whose packages its project restores from"
                : $"The Godot found carries no packages a C# project restores from: {godot.WhyNot}");
        string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-script-"));
        try
        {
            GodotTests.Game.Make(folder, godot!.Version!, godot.Packages!, GodotScriptCallsTests.Script, GodotScriptCallsTests.Scene);
            File.WriteAllText(Path.Combine(folder, "enemy.gd"), GodotScriptCallsTests.Enemy);
            await Restore.RunAsync(folder);
            Dictionary<string, string?> homes = new() { [Passenger.App.Godot.GodotUserFolders.HomeVariable] = folder + "-home" };
            await using ModelContextProtocol.Client.McpClient client = await Start(["--mcp", folder], environment: homes);
            int line = GodotSceneTests.LineOf(GodotScriptCallsTests.Enemy, "func take_damage");
            int called = GodotSceneTests.LineOf(GodotScriptCallsTests.Script, "enemy.Call(\"take_damage\"");
            string found = string.Empty;
            await Until(async () => (found = Text(await Call(client, McpTools.Usages, new() { ["path"] = "enemy.gd", ["line"] = line, ["column"] = 8 })))
                .Contains($"{called}:", StringComparison.Ordinal) && found.Contains("Main.cs", StringComparison.Ordinal), "usages of take_damage to list the C# call");
            Assert.Contains("main.tscn", found, StringComparison.Ordinal);
        }
        finally
        {
            TestFolders.Delete(folder);
            TestFolders.Delete(folder + "-home");
        }
    }
}
