using Passenger.Workspace;

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

    [Fact]
    public void The_carried_table_offers_VERTEX_in_a_spatial_vertex_function_and_not_in_its_light_with_every_function_s_scopes()
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

    [Fact]
    public void A_probe_compiles_only_its_candidate_and_what_a_Godot_refused_is_left_out_of_the_table()
    {
        GodotShaderTable table = GodotShaderTable.Carried;
        IReadOnlyList<GodotShaderProbe> probes = table.Probes();
        Assert.Contains(probes, p => p.Code == "shader_type spatial;\nvoid vertex() {\n\tVERTEX;\n}\n");
        Assert.Contains(probes, p => p.Code == "shader_type canvas_item;\nvoid fragment() {\n\ttexture_sdf(vec2(0.0));\n}\n");
        Assert.Contains(probes, p => p.Code == "shader_type spatial;\nrender_mode blend_mix;\n");
        Assert.Contains(probes, p => p.Code == "shader_type spatial;\nuniform float probed : hint_range(0.0, 1.0);\n");
        Assert.Equal(probes.Count, probes.Select(p => p.Key).Distinct().Count());

        // What Godot 4.7.2 refuses of the 4.8 table.
        string Key(string code) => probes.Single(p => p.Code == code).Key;
        HashSet<string> refused =
        [
            Key("shader_type spatial;\nvoid fragment() {\n\tSTREAMING_LOD;\n}\n"),
            Key("shader_type texture_blit;\nrender_mode blend_premul_alpha;\n"),
            Key("shader_type spatial;\nuniform float probed : no_editor;\n"),
            Key("shader_type sky;\nrender_mode disable_fog;\n"),
        ];
        GodotShaderTable kept = table.Without(refused);
        Assert.DoesNotContain(kept.BuiltInsIn("spatial", "fragment"), b => b.BuiltIn.Name == "STREAMING_LOD");
        Assert.Contains(kept.BuiltInsIn("spatial", "fragment"), b => b.BuiltIn.Name == "ALBEDO");
        Assert.Equal(["blend_mix", "blend_add", "blend_sub", "blend_mul", "blend_disabled"], kept.TypeOf("texture_blit")!.Modes.SelectMany(m => m.Written));
        Assert.DoesNotContain("disable_fog", kept.TypeOf("sky")!.Modes.SelectMany(m => m.Written));
        Assert.DoesNotContain("no_editor", kept.HintsOf("spatial"));
        Assert.Contains("no_editor", kept.HintsOf("canvas_item"));
        Assert.Equal(table.Fingerprint, kept.Fingerprint);
    }

    [Fact]
    public void A_shader_s_declarations_are_read_with_their_types_hints_and_where_parameters_and_locals_are_seen()
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

    [Fact]
    public void What_is_written_at_a_place_is_a_mode_a_hint_an_include_a_body_or_nothing_in_a_comment()
    {
        GodotShaderCode code = GodotShaderCode.Read(Water);
        int At(string text, int into = 0) => Water.IndexOf(text, StringComparison.Ordinal) + into;
        Assert.Equal(GodotShaderSpot.RenderModes, code.SpotAt(At("cull_back", 2)));
        Assert.Equal(GodotShaderSpot.Hints, code.SpotAt(At("hint_range", 4)));
        Assert.Equal(GodotShaderSpot.Include, code.SpotAt(At("lib/waves", 3)));
        Assert.Equal(GodotShaderSpot.None, code.SpotAt(At("naming VERTEX", 8)));
        Assert.Equal(GodotShaderSpot.Body, code.SpotAt(At("world = VERTEX", 9)));
        Assert.Equal(GodotShaderSpot.Member, code.SpotAt(At("VERTEX.y", 8)));
        Assert.Equal(GodotShaderSpot.Top, code.SpotAt(At("varying", 3)));
        Assert.Equal(GodotShaderSpot.ShaderType, code.SpotAt(At("spatial", 3)));
    }

    [Fact]
    public void The_name_at_a_place_is_the_processor_function_s_built_in_a_declaration_or_an_include_s_function()
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

    [Fact]
    public void A_scene_s_shader_code_maps_to_the_file_through_its_escapes()
    {
        string scene = "[gd_scene format=3]\n\n[sub_resource type=\"Shader\" id=\"Shader_glow\"]\ncode = \"shader_type canvas_item;\n#include \\\"res://lib/waves.gdshaderinc\\\"\n"
            + "// a \\\\ and a \\u00e9\nvoid fragment() {\n\tCOLOR = vec4(glw);\n}\n\"\n";
        GodotFile file = GodotFile.Parse(scene);
        GodotValue value = file.Sections.Single(s => s.Tag == "sub_resource").Property("code")!.Value;
        string code = value.Text!;
        Assert.False(value.Plain);
        int[] places = GodotStringPlaces.Of(scene, value.TextStart, value.TextEnd);
        Assert.Equal(code.Length + 1, places.Length);
        for (int i = 0; i < code.Length; i++)
        {
            Assert.True(scene[places[i]] == code[i] || scene[places[i]] == '\\', $"the code's character {i} ('{code[i]}') was placed on '{scene[places[i]]}'");
        }
        // Godot's line 5 of the code, its misspelt name, lands on the file's own.
        (int start, int end) = GodotShaderUnit.OnLine(code, 5, "Unknown identifier in expression: 'glw'.");
        (int from, int to) = GodotStringPlaces.RangeOf(places, start, end);
        Assert.Equal("glw", scene[from..to]);
        (int quote, int quoted) = GodotStringPlaces.RangeOf(places, code.IndexOf("res://", StringComparison.Ordinal) - 1, code.IndexOf("res://", StringComparison.Ordinal));
        Assert.Equal("\\\"", scene[quote..quoted]);
    }

    [Fact]
    public void A_check_s_messages_are_marked_on_the_shader_on_an_include_and_on_the_include_leading_to_it()
    {
        IReadOnlyList<GodotShaderMessage> messages =
        [
            new(true, "", 15, "Unknown identifier in expression: 'speedd'."),
            new(true, "res://lib/waves.gdshaderinc", 2, "Unknown identifier in expression: 'nope'."),
            new(false, "res://lib/waves.gdshaderinc", 1, "A warning of its own."),
        ];

        string shader = Water.Replace("world = VERTEX;", "world = VERTEX * speedd;", StringComparison.Ordinal);
        string waves = "float wave(float t) {\n\treturn nope;\n}\n";
        GodotShaderUnit unit = GodotShaderUnit.Of("res://fx/water.gdshader", shader, path => path == "res://lib/waves.gdshaderinc" ? waves : null);
        Assert.Equal(["res://lib/waves.gdshaderinc"], unit.Includes.Keys);
        IReadOnlyList<GodotShaderMark> own = unit.MarksIn(null, messages);
        Assert.Equal("speedd", shader[own[0].Start..own[0].End]);
        Assert.Equal("res://lib/waves.gdshaderinc", shader[own[1].Start..own[1].End]);
        Assert.Equal("res://lib/waves.gdshaderinc:2: Unknown identifier in expression: 'nope'.", own[1].Message);
        IReadOnlyList<GodotShaderMark> included = unit.MarksIn("res://lib/waves.gdshaderinc", messages);
        Assert.Equal(["nope", "float wave(float t) {"], included.Select(m => waves[m.Start..m.End]));
        Assert.Equal([true, false], included.Select(m => m.Error));
    }

    [Fact]
    public void An_include_no_file_answers_is_marked_on_its_path_with_Godot_s_preprocessor_s_words_and_a_relative_one_resolved_from_its_folder()
    {
        string shader = "shader_type spatial;\n#include \"../lib/gone.gdshaderinc\"\nvoid fragment() {}\n";
        GodotShaderUnit unit = GodotShaderUnit.Of("res://fx/water.gdshader", shader, _ => null);
        Assert.Equal(["res://lib/gone.gdshaderinc"], unit.Missing);
        IReadOnlyList<GodotShaderMessage> messages = [new(true, "", 2, "Shader include file does not exist: res://lib/gone.gdshaderinc")];
        GodotShaderMark mark = Assert.Single(unit.MarksIn(null, messages));
        Assert.Equal(("../lib/gone.gdshaderinc", "Shader include file does not exist: res://lib/gone.gdshaderinc"), (shader[mark.Start..mark.End], mark.Message));
        Assert.Equal("res://shaders/a.gdshaderinc", GodotShaderUnit.Resolved("res://level.tscn::Shader_x", "shaders/./a.gdshaderinc"));
    }

    [Theory]
    [InlineData("void fragment(){float answer = 1.0; ALPHA = answer;}")]
    [InlineData("void fragment() {float answer = 1.0; ALPHA = answer;}")]
    [InlineData("void fragment() { float answer = 1.0; ALPHA = answer; }")]
    [InlineData("void fragment(){/* first */float answer = 1.0; ALPHA = answer;}")]
    [InlineData("void fragment(){\n\tfloat answer = 1.0;\n\tALPHA = answer;\n}")]
    public void A_local_is_read_however_close_it_stands_to_its_function_s_brace(string function)
    {
        GodotShaderDeclaration local = Assert.Single(GodotShaderCode.Read($"shader_type spatial;\n{function}\n").Declarations, d => d.Kind == GodotShaderDeclared.Local);
        Assert.Equal(("answer", "float", "fragment"), (local.Name, local.Type, local.Function));
    }
}
