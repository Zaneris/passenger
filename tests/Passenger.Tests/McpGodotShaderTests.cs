using ModelContextProtocol.Client;
using Passenger.App.Godot;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent's tools on a Godot shader answer as its editor does: diagnostics gives Godot's compiler's error at its
/// line, as the agent's edit left the shader and once it mended it; definition goes to an include's function; symbol_info
/// says what a built-in is; usages lists a uniform's uses; outline its declarations. Without a Godot of 4.5 or newer (GODOT,
/// the PATH, Applications) it skips saying why. Run alone: it starts a Godot of its own.</summary>
[Collection(Alone.Name)]
public sealed class McpGodotShaderTests : McpRig
{
    private const string Shader = """
        shader_type spatial;
        #include "res://lib/waves.gdshaderinc"

        uniform float speed : hint_range(0.0, 10.0) = 1.0;

        void vertex() {
        	VERTEX.y += wave(VERTEX.x, TIME * speed);
        }

        """;

    [Fact]
    public async Task An_agents_tools_on_a_shader_answer_from_Godots_compiler_and_its_language()
    {
        GodotFound? godot = GodotExecutables.Find(null);
        Assert.SkipWhen(godot is not { Exists: true } || !System.Version.TryParse(string.Join('.', (godot.Version ?? "0.0").Split('.', '-').Take(2)), out Version? number)
            || number < new Version(4, 5), "No Godot 4.5 or newer was found (GODOT, the PATH, Applications), whose compiler checks shaders");
        string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-shaders-"));
        try
        {
            TestFolders.Write(folder, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Shaded\"\n");
            TestFolders.Write(folder, "water.gdshader", Shader);
            TestFolders.Write(folder, Path.Combine("lib", "waves.gdshaderinc"), "float wave(float x, float t) {\n\treturn sin(x + t);\n}\n");
            Dictionary<string, string?> homes = new() { [GodotUserFolders.HomeVariable] = folder + "-home" };
            await using McpClient client = await Start(["--mcp", folder], environment: homes, languageServer: false);
            // Godot's first start on a machine may outlast diagnostics' wait, which then says so: asked again, as an agent would.
            string first = string.Empty;
            Assert.True(await Waiting.UntilAsync(async () => !(first = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "water.gdshader" })))
                .Contains("Not yet checked", StringComparison.Ordinal), TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken), $"Godot's compiler never checked the shader: {first}");
            Assert.Equal("[]", first);

            await Call(client, McpTools.Edit, new() { ["path"] = "water.gdshader", ["oldText"] = "TIME * speed", ["newText"] = "TIME * sped" });
            string marked = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "water.gdshader" }));
            Assert.True(marked.Contains("Unknown identifier in expression: 'sped'.", StringComparison.Ordinal) && marked.Contains("\"line\":7", StringComparison.Ordinal),
                $"The agent was not told Godot's error on line 7 of the shader it had just broken: {marked}");
            await Call(client, McpTools.Edit, new() { ["path"] = "water.gdshader", ["oldText"] = "TIME * sped", ["newText"] = "TIME * speed" });
            Assert.Equal("[]", Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "water.gdshader" })));

            string declared = Text(await Call(client, McpTools.Definition, new() { ["path"] = "water.gdshader", ["line"] = 7, ["column"] = 15 }));
            Assert.True(declared.Contains("waves.gdshaderinc\"", StringComparison.Ordinal) && declared.Contains("\"line\":1", StringComparison.Ordinal),
                $"definition of wave was to reach its declaration in the include: {declared}");
            string time = Text(await Call(client, McpTools.SymbolInfo, new() { ["path"] = "water.gdshader", ["line"] = 7, ["column"] = 30 }));
            Assert.Contains("const float TIME", time, StringComparison.Ordinal);
            string uses = Text(await Call(client, McpTools.Usages, new() { ["path"] = "water.gdshader", ["line"] = 4, ["column"] = 16 }));
            Assert.True(uses.Contains("\"count\":2", StringComparison.Ordinal), $"usages of speed were to list its declaration and its use: {uses}");
            string outline = Text(await Call(client, McpTools.Outline, new() { ["path"] = "water.gdshader" }));
            Assert.Contains("uniform float speed : hint_range(0.0, 10.0) = 1.0; (uniform, line 4)", outline, StringComparison.Ordinal);
        }
        finally
        {
            TestFolders.Delete(folder);
            TestFolders.Delete(folder + "-home");
        }
    }
}
