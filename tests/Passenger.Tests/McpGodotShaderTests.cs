using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Godot;
using Passenger.App.Mcp;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An agent's tools on a Godot shader answer as its editor does: diagnostics gives Godot's front end's error at its
/// line, as the agent's edit left the shader and once it mended it; definition goes to an include's function; symbol_info
/// says what a built-in is; usages lists a uniform's uses; outline its declarations; a shader read only in part says so. It
/// needs no Godot; one found is probed for its shading language once. Run alone: that probe starts a Godot of its own.</summary>
[Collection(Alone.Name)]
public sealed class McpGodotShaderTests : McpRig
{
    /// <summary>An agent's reading of Godot's files with no Godot started as one flow, each step one test's check in an
    /// instance of its own: a shader's tools from Godot's front end and one nested too deep read in part, a GDScript's
    /// warnings named as Godot names them and one nested too deep read in part, a script reformatted by Godot's style
    /// guide, a scene's diagnostics answering for an edit, its outline naming an imported model, a node renamed again and
    /// again and the binary animations a rename leaves, and godot_editor saying the person's editor connects to them.</summary>
    [Fact]
    public async Task An_agent_reads_reformats_and_renames_Godots_files_with_no_Godot_started()
    {
        await Flow.StepApartAsync("an agents tools on a shader answer from Godots front end and its language", () => new McpGodotShaderTests(), t => t.An_agents_tools_on_a_shader_answer_from_Godots_front_end_and_its_language());
        await Flow.StepApartAsync("an agents diagnostics of a shader nested past what passenger reads say it was read in part", () => new McpGodotShaderTests(), t => t.An_agents_diagnostics_of_a_shader_nested_past_what_passenger_reads_say_it_was_read_in_part());
        await Flow.StepApartAsync("an agents diagnostics of a gdscript give a warnings name as its code and godots words as its message", () => new McpGDScriptMarksTests(), t => t.An_agents_diagnostics_of_a_gdscript_give_a_warnings_name_as_its_code_and_godots_words_as_its_message());
        await Flow.StepApartAsync("an agents diagnostics of a script nested past what passenger reads say it was read in part", () => new McpGDScriptMarksTests(), t => t.An_agents_diagnostics_of_a_script_nested_past_what_passenger_reads_say_it_was_read_in_part());
        await Flow.StepApartAsync("an agent reformats a gdscript by godots style guide and it is saved", () => new McpGDScriptFormatTests(), t => t.An_agent_reformats_a_gdscript_by_godots_style_guide_and_it_is_saved());
        await Flow.StepApartAsync("an agents diagnostics of a scene it has just edited answer for the edit", () => new McpGodotSceneMarksTests(), t => t.An_agents_diagnostics_of_a_scene_it_has_just_edited_answer_for_the_edit());
        await Flow.StepApartAsync("an agents outline of a scene names an imported model a node instances", () => new McpGodotFixTests(), t => t.An_agents_outline_of_a_scene_names_an_imported_model_a_node_instances());
        await Flow.StepApartAsync("an agents renames of a node one after another each find the closed scene as it stands", () => new McpGodotRenameTests(), t => t.An_agents_renames_of_a_node_one_after_another_each_find_the_closed_scene_as_it_stands());
        await Flow.StepApartAsync("an agents rename says the binary animations it leaves", () => new McpGodotRenameTests(), t => t.An_agents_rename_says_the_binary_animations_it_leaves());
        await Flow.StepApartAsync("godot editor on an agents own passenger says the persons editor connects to their window", () => new McpGodotEditorTests(), t => t.Godot_editor_on_an_agents_own_passenger_says_the_persons_editor_connects_to_their_window());
    }

    private const string Shader = """
        shader_type spatial;
        #include "res://lib/waves.gdshaderinc"

        uniform float speed : hint_range(0.0, 10.0) = 1.0;

        void vertex() {
        	VERTEX.y += wave(VERTEX.x, TIME * speed);
        }

        """;

    internal async Task An_agents_tools_on_a_shader_answer_from_Godots_front_end_and_its_language()
    {
        string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-shaders-"));
        try
        {
            TestFolders.Write(folder, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Shaded\"\n");
            TestFolders.Write(folder, "water.gdshader", Shader);
            TestFolders.Write(folder, Path.Combine("lib", "waves.gdshaderinc"), "float wave(float x, float t) {\n\treturn sin(x + t);\n}\n");
            Dictionary<string, string?> homes = new() { [GodotUserFolders.HomeVariable] = folder + "-home" };
            await using McpClient client = await Start(["--mcp", folder], environment: homes, languageServer: false);
            // The probe of a Godot found, on its first start on a machine, may outlast diagnostics' wait: asked again, as an agent would.
            string first = string.Empty;
            Assert.True(await Waiting.UntilAsync(async () => !(first = Text(await Call(client, McpTools.Diagnostics, new() { ["path"] = "water.gdshader" })))
                .Contains("Not yet checked", StringComparison.Ordinal), TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken), $"The shader was never checked: {first}");
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

    /// <summary>A shader past what Passenger reads marks no error, so the note is all that tells an agent it went unchecked.</summary>
    internal async Task An_agents_diagnostics_of_a_shader_nested_past_what_passenger_reads_say_it_was_read_in_part()
    {
        string folder = PathNames.Canonical(TestFolders.Create("godot-mcp-shader-part-"));
        try
        {
            TestFolders.Write(folder, "project.godot", "config_version=5\n\n[application]\n\nconfig/name=\"Part\"\n");
            TestFolders.Write(folder, "deep.gdshader", "shader_type spatial;\n\nvoid fragment() {\n\tfloat a = " + new string('(', 20_000) + "1.0" + new string(')', 20_000) + ";\n}\n");
            Dictionary<string, string?> homes = new() { [GodotUserFolders.HomeVariable] = folder + "-home" };
            await using McpClient client = await Start(["--mcp", folder], environment: homes, languageServer: false);
            JsonElement note = default;
            Assert.True(await Waiting.UntilAsync(async () => (await Call(client, McpTools.Diagnostics, new() { ["path"] = "deep.gdshader" })).EnumerateArray()
                .FirstOrDefault(f => f.GetProperty("message").GetString()!.StartsWith("Passenger read no deeper", StringComparison.Ordinal))
                is { ValueKind: JsonValueKind.Object } found
                && (note = found).ValueKind == JsonValueKind.Object, TimeSpan.FromSeconds(120), TestContext.Current.CancellationToken), "The agent was never told the shader was read only in part");
            Assert.Equal(("info", 4), (note.GetProperty("severity").GetString(), note.GetProperty("line").GetInt32()));
            Assert.Contains("the rest of the shader is unread", note.GetProperty("message").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestFolders.Delete(folder);
            TestFolders.Delete(folder + "-home");
        }
    }
}
