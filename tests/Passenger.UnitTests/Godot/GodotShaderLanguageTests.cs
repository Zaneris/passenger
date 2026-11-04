using Passenger.Workspace;
using Passenger.Workspace.GodotShader;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's shading language compiler as Passenger ports it, checked as Godot's shader editor checks a shader:
/// shaders of every type compile clean, an error is said in Godot's words at its line (each case's words and line as
/// Godot 4.7.2's own compiler gave them), an include's error names the include, and the warnings come as the editor
/// gives them.</summary>
public class GodotShaderLanguageTests
{
    [Theory]
    [InlineData("""
        shader_type spatial;
        render_mode blend_mix, depth_draw_opaque, cull_back, diffuse_burley, specular_schlick_ggx;

        uniform vec4 albedo : source_color = vec4(1.0);
        uniform sampler2D texture_albedo : source_color, filter_linear_mipmap, repeat_enable;
        uniform float roughness : hint_range(0.0, 1.0) = 0.5;
        uniform int mode : hint_enum("One", "Two", "Three") = 1;
        varying vec3 world_pos;
        const float SCALE = 2.0 * PI;

        struct Light {
        	vec3 color;
        	float energy;
        };

        float brightness(vec3 c) {
        	return dot(c, vec3(0.299, 0.587, 0.114));
        }

        void vertex() {
        	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
        	VERTEX.y += sin(TIME * SCALE) * 0.1;
        }

        void fragment() {
        	Light l = Light(vec3(1.0), 2.0);
        	ALBEDO = albedo.rgb * texture(texture_albedo, UV).rgb * l.color;
        	ROUGHNESS = roughness * brightness(world_pos);
        	int arr[3] = int[](1, 2, 3);
        	for (int i = 0; i < arr.length(); i++) {
        		if (arr[i] == mode) {
        			EMISSION += vec3(float(i));
        		}
        	}
        	switch (mode) {
        		case 0:
        			ALPHA = 1.0;
        			break;
        		default:
        			break;
        	}
        }

        void light() {
        	DIFFUSE_LIGHT += clamp(dot(NORMAL, LIGHT), 0.0, 1.0) * ATTENUATION * LIGHT_COLOR / PI;
        }
        """)]
    [InlineData("""
        shader_type canvas_item;
        render_mode blend_add;

        uniform float speed = 1.0;
        uniform sampler2D screen_texture : hint_screen_texture, filter_linear_mipmap;

        void vertex() {
        	VERTEX += vec2(sin(TIME * speed), 0.0);
        }

        void fragment() {
        	COLOR = texture(TEXTURE, UV) + vec4(textureLod(screen_texture, SCREEN_UV, 0.0).rgb, 0.0) * 0.1;
        }
        """)]
    [InlineData("""
        shader_type particles;

        uniform float spread = 1.0;

        void start() {
        	TRANSFORM[3].xyz = vec3(spread, 0.0, 0.0);
        	VELOCITY = vec3(0.0, 1.0, 0.0);
        }

        void process() {
        	VELOCITY.y -= 9.8 * DELTA;
        }
        """)]
    [InlineData("""
        shader_type sky;

        uniform vec3 sky_color : source_color = vec3(0.2, 0.4, 0.8);

        void sky() {
        	COLOR = AT_CUBEMAP_PASS ? sky_color : mix(sky_color, vec3(1.0), clamp(EYEDIR.y, 0.0, 1.0));
        }
        """)]
    [InlineData("""
        shader_type fog;

        uniform float density : hint_range(0, 1, 0.0001) = 1.0;

        void fog() {
        	DENSITY = density * clamp(1.0 - length(UVW - vec3(0.5)), 0.0, 1.0);
        }
        """)]
    public void A_shader_of_each_type_that_Godot_compiles_is_marked_with_nothing(string code)
    {
        GodotShaderChecked result = Check(code);
        Assert.True(result.Messages.Count == 0, $"A shader Godot compiles clean was marked: {string.Join("; ", result.Messages)}");
    }

    [Theory]
    [InlineData("shader_type spatial;\n\nuniform float speed = 1.0;\n\nvoid fragment() {\n\tALBEDO = vec3(speedd);\n}\n", "Unknown identifier in expression: 'speedd'.", 6)]
    [InlineData("shader_type spatial;\n\nvoid fragment() {\n\tfloat a = vec3(1.0);\n\tALBEDO = vec3(a);\n}\n", "Invalid assignment of 'vec3' to 'float'.", 4)]
    [InlineData("shader_type spatial;\n\nvoid fragment() {\n\tALBEDO = vec3(1.0)\n}\n", "Expected a ';'.", 5)]
    [InlineData("shader_type spatial;\nrender_mode blend_nope;\n\nvoid fragment() {\n}\n", "Invalid render mode: 'blend_nope'.", 2)]
    [InlineData("shader_type particles;\n\nvoid process() {\n\tdiscard;\n}\n", "Use of 'discard' is not supported for the 'particles' shader type.", 4)]
    [InlineData("shader_type spatial;\n\nvarying int count;\n\nvoid vertex() {\n\tcount = 1;\n}\n\nvoid fragment() {\n\tALBEDO = vec3(float(count));\n}\n",
        "Varying with integer data type must be declared with `flat` interpolation qualifier.", 3)]
    [InlineData("shader_type particles;\n\nvoid start() {\n\tVELOCITY = vec3(f());\n}\n\nfloat f() {\n\treturn 1.0;\n}\n", "No matching function found for: 'f'.", 4)]
    public void An_error_is_said_in_Godot_s_words_at_the_line_Godot_gives(string code, string error, int line)
    {
        GodotShaderChecked result = Check(code);
        GodotShaderMessage first = Assert.Single(result.Messages, m => m.Error);
        Assert.Equal((error, line, string.Empty), (first.Text, first.Line, first.File));
    }

    [Fact]
    public void An_error_in_an_include_names_the_include_and_its_line()
    {
        GodotShaderChecked result = Check("shader_type spatial;\n\n#include \"inner.gdshaderinc\"\n\nvoid fragment() {\n\tALBEDO = vec3(bad());\n}\n",
            new Dictionary<string, string> { ["res://inner.gdshaderinc"] = "float bad() {\n\treturn xx;\n}\n" });
        Assert.Equal([new GodotShaderMessage(true, "res://inner.gdshaderinc", 2, "Unknown identifier in expression: 'xx'.")], result.Messages);
    }

    [Fact]
    public void A_hint_only_Forward_plus_has_is_refused_under_another_rendering_method()
    {
        const string code = "shader_type spatial;\n\nuniform sampler2D nrt : hint_normal_roughness_texture;\n\nvoid fragment() {\n\tALBEDO = texture(nrt, SCREEN_UV).rgb;\n}\n";
        Assert.Empty(Check(code).Messages);
        GodotShaderMessage refused = Assert.Single(Check(code, method: "mobile").Messages);
        Assert.Equal("'hint_normal_roughness_texture' is only available when using the Forward+ renderer.", refused.Text);
    }

    [Fact]
    public void A_shader_that_compiles_is_warned_of_what_it_declares_and_never_uses_by_line()
    {
        GodotShaderChecked result = Check("""
            shader_type spatial;

            uniform float never;
            const float ALSO_NEVER = 1.0;
            varying float v_never;

            float not_called() {
            	return 1.0;
            }

            void fragment() {
            	float local_never = 1.0;
            	if (ALBEDO.r == 0.5) {
            		ALBEDO = vec3(1.0);
            	}
            	;
            }
            """);
        Assert.Equal([
            (3, "The uniform 'never' is declared but never used."),
            (4, "The const 'ALSO_NEVER' is declared but never used."),
            (5, "The varying 'v_never' is declared but never used."),
            (7, "The function 'not_called' is declared but never used."),
            (12, "The local variable 'local_never' is declared but never used."),
            (13, "Direct floating-point comparison (this may not evaluate to `true` as you expect). Instead, use `abs(a - b) < 0.0001` for an approximate but predictable comparison."),
            (16, "Empty statement. Remove ';' to fix this warning."),
        ], result.Messages.Select(m => (m.Line, m.Text)));
        Assert.DoesNotContain(result.Messages, m => m.Error);
    }

    [Fact]
    public void A_preprocessor_s_error_is_the_only_one_said_as_Godot_s_editor_says_it()
    {
        GodotShaderChecked result = Check("shader_type spatial;\n#ifdef FOO\nvoid fragment() {\n}\n");
        Assert.Equal([new GodotShaderMessage(true, string.Empty, 3, "Can't find matching branch directive.")], result.Messages);
    }

    private static GodotShaderChecked Check(string code, IReadOnlyDictionary<string, string>? includes = null, string method = "forward_plus") =>
        GodotShaderCheck.Check(new GodotShaderUnit("res://shader.gdshader", code, includes ?? new Dictionary<string, string>(), []), GodotShaderTable.Carried,
            new GodotShaderCheckOptions { RenderingMethod = method, Godot = new Version(4, 7, 2) });
}
