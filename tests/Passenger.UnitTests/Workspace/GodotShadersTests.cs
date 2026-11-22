using Passenger.Workspace;
using Passenger.Workspace.GodotShader;

namespace Passenger.UnitTests.Workspace;

/// <summary>Godot's shading language as Passenger reads it: the carried table of shader types, processor functions and their
/// built-ins, modes and hints, and what a Godot's refusals of its probes leave of it; a shader's own declarations and what
/// is written at a place; the name at a place; a scene's string mapped to the file escapes and all; and a check's messages
/// marked on the shader, an include and the line leading to it.</summary>
public class GodotShadersTests
{
    private const string Water = """
        shader_type spatial;
        render_mode blend_mix, cull_back;

        #include "res://lib/waves.gdshaderinc"

        // A comment naming VERTEX.
        uniform float speed : hint_range(0.0, 10.0) = 1.0;
        global uniform vec4 sky_tint;
        varying vec3 world;
        const float LIFT = 0.5, DROP = 0.2;
        struct Ripple { float height; };

        void vertex() {
        	float local_height = VERTEX.y;
        	world = VERTEX;
        }

        void light() {
        	DIFFUSE_LIGHT += ATTENUATION;
        }
        """;

    /// <summary>A shader's built-ins, declarations, places and names are read as Godot reads them.</summary>
    [Fact]
    public void A_shaders_names_and_places_are_read_as_godot_reads_them() => Cases.All(
        (nameof(The_carried_table_offers_VERTEX_in_a_spatial_vertex_function_and_not_in_its_light_with_every_function_s_scopes),
            The_carried_table_offers_VERTEX_in_a_spatial_vertex_function_and_not_in_its_light_with_every_function_s_scopes),
        (nameof(A_shader_s_declarations_are_read_with_their_types_hints_and_where_parameters_and_locals_are_seen),
            A_shader_s_declarations_are_read_with_their_types_hints_and_where_parameters_and_locals_are_seen),
        (nameof(The_name_at_a_place_is_the_processor_function_s_built_in_a_declaration_or_an_include_s_function),
            The_name_at_a_place_is_the_processor_function_s_built_in_a_declaration_or_an_include_s_function));

    /// <summary>An include no file answers is marked once, on its path or the include leading to it, where a branch
    /// reaches it.</summary>
    [Fact]
    public void An_include_no_file_answers_is_marked_once_where_godot_would_say_it() => Cases.All(
        (nameof(An_include_no_file_answers_is_marked_on_its_path_with_Godot_s_preprocessor_s_words_and_a_relative_one_resolved_from_its_folder),
            An_include_no_file_answers_is_marked_on_its_path_with_Godot_s_preprocessor_s_words_and_a_relative_one_resolved_from_its_folder),
        (nameof(Every_include_the_branches_reach_that_no_file_answers_is_marked_on_its_path_and_no_other),
            Every_include_the_branches_reach_that_no_file_answers_is_marked_on_its_path_and_no_other));

    private void The_carried_table_offers_VERTEX_in_a_spatial_vertex_function_and_not_in_its_light_with_every_function_s_scopes()
    {
        GodotShaderTable table = GodotShaderTable.Carried;
        Assert.Equal(["spatial", "canvas_item", "particles", "sky", "fog", "texture_blit"], table.Types.Select(t => t.Name));
        IReadOnlyList<(GodotShaderBuiltIn BuiltIn, string Scope)> vertex = table.BuiltInsIn("spatial", "vertex");
        Assert.Contains(vertex, b => b.BuiltIn is { Name: "VERTEX", Type: "vec3", Constant: false } && b.Scope == "vertex");
        Assert.Contains(vertex, b => b.BuiltIn.Name == "TIME" && b.Scope == GodotShaderTable.Global);
        Assert.Contains(vertex, b => b.BuiltIn.Name == "PI" && b.Scope == GodotShaderTable.Constants);
        Assert.DoesNotContain(table.BuiltInsIn("spatial", "light"), b => b.BuiltIn.Name == "VERTEX");
        Assert.Equal(["PI", "TAU", "E"], table.BuiltInsIn("canvas_item", null).Select(b => b.BuiltIn.Name));
        Assert.Contains("blend_mix", table.TypeOf("spatial")!.Modes.SelectMany(m => m.Written));
        Assert.Contains("unshaded", table.TypeOf("spatial")!.Modes.SelectMany(m => m.Written));
        Assert.Equal("float texture_sdf(vec2 sdf_pos)", table.StageFunctionsIn("canvas_item", "fragment").Single(f => f.Name == "texture_sdf").Signature);
        Assert.Contains(table.Functions, f => f.Signature == "float radians(float degrees)");
        Assert.DoesNotContain(table.Functions, f => f.Name == "vec3");
        Assert.Contains("hint_range", table.HintsOf("spatial"));
    }

    private void A_shader_s_declarations_are_read_with_their_types_hints_and_where_parameters_and_locals_are_seen()
    {
        GodotShaderCode code = GodotShaderCode.Read(Water);
        Assert.Equal("spatial", code.Type);
        Assert.Equal(["blend_mix", "cull_back"], code.RenderModes);
        Assert.Equal("res://lib/waves.gdshaderinc", Assert.Single(code.Includes).Path);
        GodotShaderDeclaration speed = code.Declarations.Single(d => d.Name == "speed");
        Assert.Equal((GodotShaderDeclared.Uniform, "float", "hint_range(0.0, 10.0)"), (speed.Kind, speed.Type, speed.Hints));
        Assert.Equal("uniform float speed : hint_range(0.0, 10.0) = 1.0;", speed.Written);
        Assert.Equal("global", code.Declarations.Single(d => d.Name == "sky_tint").Scope);
        Assert.Equal(GodotShaderDeclared.Varying, code.Declarations.Single(d => d.Name == "world").Kind);
        Assert.Equal(["LIFT", "DROP"], code.Declarations.Where(d => d.Kind == GodotShaderDeclared.Constant).Select(d => d.Name));
        Assert.Equal(GodotShaderDeclared.Struct, code.Declarations.Single(d => d.Name == "Ripple").Kind);
        Assert.Equal(["vertex", "light"], code.Functions.Select(f => f.Name));
        GodotShaderDeclaration local = code.Declarations.Single(d => d.Name == "local_height");
        Assert.Equal((GodotShaderDeclared.Local, "vertex"), (local.Kind, local.Function));
        Assert.DoesNotContain(code.SeenAt(Water.IndexOf("DIFFUSE_LIGHT", StringComparison.Ordinal)), d => d.Name == "local_height");
        Assert.Contains(code.SeenAt(Water.IndexOf("world = VERTEX", StringComparison.Ordinal)), d => d.Name == "local_height");
        Assert.Equal(2, code.Uses("VERTEX").Count());
    }

    private void The_name_at_a_place_is_the_processor_function_s_built_in_a_declaration_or_an_include_s_function()
    {
        GodotShaderTable table = GodotShaderTable.Carried;
        string text = Water + "\nvoid fragment() {\n\tALBEDO = vec3(mix(wave(TIME), 1.0, 0.5));\n}\n";
        GodotShaderCode code = GodotShaderCode.Read(text);
        int At(string what) => text.IndexOf(what, StringComparison.Ordinal) + 1;
        Dictionary<string, string> includes = new() { ["res://lib/waves.gdshaderinc"] = "float wave(float t) {\n\treturn sin(t);\n}\n" };
        GodotShaderName? vertex = GodotShaderNames.At(table, code, "spatial", At("VERTEX.y"), includes);
        Assert.Equal((GodotShaderNameKind.BuiltIn, "vertex"), (vertex?.Kind, vertex?.Scope));
        GodotShaderName? time = GodotShaderNames.At(table, code, "spatial", At("TIME"), includes);
        Assert.Equal((GodotShaderNameKind.BuiltIn, GodotShaderTable.Global), (time?.Kind, time?.Scope));
        Assert.Null(GodotShaderNames.At(table, code, "spatial", At("ATTENUATION") - 20, includes));
        Assert.Equal(GodotShaderNameKind.Declared, GodotShaderNames.At(table, code, "spatial", At("world = "), includes)?.Kind);
        GodotShaderName? wave = GodotShaderNames.At(table, code, "spatial", At("wave(TIME"), includes);
        Assert.Equal(("res://lib/waves.gdshaderinc", GodotShaderDeclared.Function), (wave?.Include, wave?.Declaration?.Kind));
        Assert.Equal(GodotShaderNameKind.Function, GodotShaderNames.At(table, code, "spatial", At("mix(wave"), includes)?.Kind);
        Assert.Null(GodotShaderNames.At(table, code, "spatial", At("VERTEX.y") + 7, includes));
        Assert.Equal(GodotShaderNameKind.RenderMode, GodotShaderNames.At(table, code, "spatial", At("cull_back"), includes)?.Kind);
        Assert.Equal(GodotShaderNameKind.Hint, GodotShaderNames.At(table, code, "spatial", At("hint_range"), includes)?.Kind);
    }

    private void An_include_no_file_answers_is_marked_on_its_path_with_Godot_s_preprocessor_s_words_and_a_relative_one_resolved_from_its_folder()
    {
        string shader = "shader_type spatial;\n#include \"../lib/gone.gdshaderinc\"\nvoid fragment() {}\n";
        GodotShaderUnit unit = GodotShaderUnit.Of("res://fx/water.gdshader", shader, _ => null);
        Assert.Equal(["res://lib/gone.gdshaderinc"], unit.Missing);
        IReadOnlyList<GodotShaderMessage> messages = [new(true, "", 2, "Shader include file does not exist: res://lib/gone.gdshaderinc")];
        GodotShaderMark mark = Assert.Single(unit.MarksIn(null, messages));
        Assert.Equal(("../lib/gone.gdshaderinc", "Shader include file does not exist: res://lib/gone.gdshaderinc"), (shader[mark.Start..mark.End], mark.Message));
        Assert.Equal("res://shaders/a.gdshaderinc", GodotResPaths.Resolved("shaders/./a.gdshaderinc", "res://level.tscn::Shader_x"));
    }

    private void Every_include_the_branches_reach_that_no_file_answers_is_marked_on_its_path_and_no_other()
    {
        string shader = "shader_type spatial;\n#include \"a.gdshaderinc\"\n#if 0\n#include \"off.gdshaderinc\"\n#else\n#include \"../b.gdshaderinc\"\n#endif\nvoid fragment() {}\n";
        Assert.Equal(["a.gdshaderinc", "../b.gdshaderinc"], MarkedOf(shader, _ => null, "res://fx/water.gdshader").Select(m => shader[m.Start..m.End]));
    }

    /// <summary>The marks a check of a shader puts on its own text, its includes read through a file's text by res:// path.</summary>
    private static IReadOnlyList<GodotShaderMark> MarkedOf(string shader, Func<string, string?> textOf, string res = "res://water.gdshader")
    {
        GodotShaderUnit unit = GodotShaderUnit.Of(res, shader, textOf);
        return unit.MarksIn(null, GodotShaderCheck.Check(unit, GodotShaderTable.Carried, new GodotShaderCheckOptions { Warnings = null }).Messages);
    }

}
