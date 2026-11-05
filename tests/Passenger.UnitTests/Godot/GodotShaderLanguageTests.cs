using Passenger.Workspace;
using Passenger.Workspace.GodotShader;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's shading language compiler as Passenger ports it, checked as Godot's shader editor checks a shader:
/// shaders of every type compile clean, an error is said in Godot's words at its line (each case's words and line as
/// Godot 4.7.2's and 4.8 beta 2's own compilers gave them), every error at once, an include's error naming the include,
/// and the warnings as the editor gives them.</summary>
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
    public void A_function_only_the_high_end_has_is_refused_under_Compatibility_as_Godot_s_editor_on_it_refuses_it()
    {
        const string code = "shader_type canvas_item;\n\nvoid fragment() {\n\tCOLOR.r = fma(UV.x, 2.0, 1.0);\n}\n";
        Assert.Empty(Check(code).Messages);
        GodotShaderMessage refused = Assert.Single(Check(code, method: "gl_compatibility").Messages);
        Assert.Equal((4, "Built-in function \"fma(float, float, float)\" is only supported on high-end platforms."), (refused.Line, refused.Text));
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

    [Fact]
    public void Every_error_is_said_at_once_the_first_as_Godot_gives_it_and_none_that_only_follows_from_one_before()
    {
        GodotShaderChecked result = Check("""
            shader_type spatial;

            uniform sampler2D tex : hint_nope;

            void vertex() {
            	VERTEX.y += sin(TIME * speedd);
            	float a = vec3(1.0);
            	VERTEX.x += a;
            }

            void fragment() {
            	ALBEDO = texture(tex, UV).rgb;
            	ROUGHNESS = 1.0
            	METALLIC = 0.5;
            	if (ALPHA > nope) {
            		ALBEDO = vec3(0.0);
            	} else {
            		ALBEDO = vec3(1.0) + vec2(1.0);
            	}
            }

            float no_return() {
            	float r = 1.0;
            }
            """);
        Assert.Equal([
            (3, "Expected valid type hint after ':'."),
            (6, "Unknown identifier in expression: 'speedd'."),
            (7, "Invalid assignment of 'vec3' to 'float'."),
            (14, "Expected a ';'."),
            (15, "Unknown identifier in expression: 'nope'."),
            (18, "Invalid arguments to operator '+': 'vec3, vec2'."),
            (24, "Expected at least one 'return' statement in a non-void function."),
        ], result.Messages.Select(m => (m.Line, m.Text)));
        Assert.All(result.Messages, m => Assert.True(m.Error, $"A shader with errors was warned: {m.Text}"));
    }

    [Theory]
    [InlineData("shader_type canvas_item;\n\nvoid fragment() {\n\tCOLOR = vec4(true, 0.5, 0.5, 1.0);\n}\n",
        "Invalid arguments for the built-in function: \"vec4(bool,float,float,float)\".", 4, null, 0)]
    [InlineData("shader_type canvas_item;\n\nconst int A = 2;\nconst int B = A;\n\nvoid fragment() {\n\tfloat arr[B];\n\tCOLOR.r = arr[0];\n}\n",
        "Expected a positive integer constant.", 7, null, 0)]
    [InlineData("shader_type spatial;\n\nuniform float x : no_storage, no_storage;\n\nvoid fragment() {\n\tALBEDO = vec3(x);\n}\n",
        "Expected valid type hint after ':'.", 3, "Duplicated property usage flag 'no_storage'.", 3)]
    [InlineData("shader_type canvas_item;\n\nfloat thing() {\n\treturn 1.0;\n}\n\nstruct thing {\n\tfloat x;\n};\n\nvoid fragment() {\n\tCOLOR.r = thing();\n}\n",
        "Expected expression, found: 'PARENTHESIS_CLOSE'.", 12, "Redefinition of 'thing'.", 7)]
    public void A_shader_is_read_as_the_Godot_found_reads_it_4_7_and_4_8_each_its_own_way(string code, string error47, int line47, string? error48, int line48)
    {
        GodotShaderMessage said47 = Assert.Single(Check(code).Messages);
        Assert.Equal((error47, line47), (said47.Text, said47.Line));
        IReadOnlyList<GodotShaderMessage> said48 = Check(code, godot: new Version(4, 8)).Messages;
        Assert.Equal(error48 is null ? [] : [(error48, line48)], said48.Select(m => (m.Text, m.Line)));
    }

    [Fact]
    public void A_project_s_warning_settings_are_taken_as_Godot_s_editor_takes_them()
    {
        const string code = "shader_type spatial;\n\nuniform float unused;\n\nvoid fragment() {\n\tfloat x = 1.0;\n}\n";
        GodotShaderCheckOptions options = GodotShaderCheckOptions.Of(
            "config_version=5\n\n[debug]\n\nshader_language/warnings/unused_uniform=false\nshader_language/warnings/treat_warnings_as_errors=true\n", new Version(4, 7, 2));
        GodotShaderMessage said = Assert.Single(Check(code, options).Messages);
        Assert.Equal(new GodotShaderMessage(true, string.Empty, 6, "The local variable 'x' is declared but never used. Warnings should be fixed to prevent errors."), said);
        Assert.Empty(Check(code, GodotShaderCheckOptions.Of("config_version=5\n\n[debug]\n\nshader_language/warnings/enable=false\n", null)).Messages);
        Assert.Equal(2, Check(code, GodotShaderCheckOptions.Of("config_version=5\n", null)).Messages.Count);
    }

    [Fact]
    public void An_include_is_warned_of_what_Godot_s_editor_warns_an_include_of()
    {
        GodotShaderChecked result = GodotShaderCheck.Check(new GodotShaderUnit("res://helpers.gdshaderinc", """
            float unused_helper() {
            	return 1.0;
            }

            uniform float unused_in_include;
            const float UNUSED_C = 1.0;

            float used(float v) {
            	if (v == 0.5) {
            		return 1.0;
            	}
            	float local_never = 1.0;
            	return v;
            }
            """, new Dictionary<string, string>(), []), GodotShaderTable.Carried, new GodotShaderCheckOptions());
        Assert.Equal([(5, "The uniform 'unused_in_include' is declared but never used."), (12, "The local variable 'local_never' is declared but never used.")],
            result.Messages.Select(m => (m.Line, m.Text)));
    }

    [Fact]
    public void A_global_uniform_is_checked_against_the_project_s_shader_globals()
    {
        GodotShaderCheckOptions options = GodotShaderCheckOptions.Of(
            "config_version=5\n\n[shader_globals]\n\ntint={\n\"type\": \"color\",\n\"value\": Color(1, 1, 1, 1)\n}\nlevel={\n\"type\": \"float\",\n\"value\": 0.0\n}\n", null);
        GodotShaderChecked result = Check("shader_type spatial;\n\nglobal uniform vec4 tint;\nglobal uniform vec3 level;\nglobal uniform float nothere;\n\nvoid fragment() {\n\tALBEDO = tint.rgb;\n}\n", options);
        Assert.Equal([(4, "Global uniform 'level' must be of type 'float'."), (5, "Global uniform 'nothere' does not exist. Create it in Project Settings.")],
            result.Messages.Select(m => (m.Line, m.Text)));
    }

    /// <summary>A shader nested far past the stack its parse has left is no error of the shader's: reading stops there, said
    /// as a note, where a stack that ran out would end the IDE with it.</summary>
    [Theory]
    [InlineData("float a = ", "(", "1.0", ")")]
    [InlineData("float a = ", "sin(", "1.0", ")")]
    [InlineData("float a = ", "true ? 1.0 : ", "1.0", "")]
    [InlineData("", "{\n", "", "}")]
    [InlineData("", "if (true) {\n", "", "}")]
    public void A_shader_nested_too_deeply_has_a_note_and_no_error(string start, string open, string inside, string close)
    {
        string nested = string.Concat(Enumerable.Repeat(open, 20_000)) + inside + string.Concat(Enumerable.Repeat(close, 20_000));
        GodotShaderMessage note = Assert.Single(Check($"shader_type spatial;\n\nvoid fragment() {{\n\t{start}{nested};\n}}\n").Messages);
        Assert.Equal((GodotShaderLanguage.TooDeep, false, true), (note.Text, note.Error, note.Note));
    }

    [Fact]
    public void Where_reading_stopped_is_marked_as_a_note()
    {
        string code = "shader_type spatial;\n\nvoid fragment() {\n\tfloat a = " + new string('(', 20_000) + "1.0" + new string(')', 20_000) + ";\n}\n";
        GodotShaderMark mark = Assert.Single(new GodotShaderUnit("res://shader.gdshader", code, new Dictionary<string, string>(), []).MarksIn(null, Check(code).Messages));
        Assert.Equal((false, true), (mark.Error, mark.Note));
    }

    /// <summary>An error before reading stopped is the shader's own and stays; a mistake past it is unread and says nothing.</summary>
    [Fact]
    public void An_error_before_reading_stopped_stands_and_nothing_past_it_is_said()
    {
        string nested = new string('(', 20_000) + "1.0" + new string(')', 20_000);
        GodotShaderChecked result = Check($"shader_type spatial;\n\nvoid vertex() {{\n\tfloat a = before;\n}}\n\nvoid fragment() {{\n\tfloat b = {nested};\n\tfloat c = after;\n}}\n");
        Assert.Equal([(true, false), (false, true)], result.Messages.Select(m => (m.Error, m.Note)));
        Assert.DoesNotContain(result.Messages, m => m.Text.Contains("'after'", StringComparison.Ordinal));
    }

    /// <summary>A chain of signs, read as a list and only then a tree, and a preprocessor's condition nested past the stack,
    /// by brackets or by an operator chain's links, are no errors of the shader's and come to an end.</summary>
    [Theory]
    [InlineData("void fragment() {\n\tfloat a = ", "- ", "1.0;\n}", "", "", 20_000)]
    [InlineData("#if ", "(", "1", ")", "\n#endif", 20_000)]
    [InlineData("#if ", "- ", "1", "", "\n#endif", 10_000)]
    [InlineData("#if ", "1 + ", "1", "", "\n#endif", 10_000)]
    public void Chains_too_long_for_the_stack_are_no_errors(string start, string open, string inside, string close, string end, int times)
    {
        string nested = string.Concat(Enumerable.Repeat(open, times)) + inside + string.Concat(Enumerable.Repeat(close, times));
        Assert.DoesNotContain(Check($"shader_type spatial;\n\n{start}{nested}{end}\n").Messages, m => m.Error);
    }

    private static GodotShaderChecked Check(string code, GodotShaderCheckOptions options) =>
        GodotShaderCheck.Check(new GodotShaderUnit("res://shader.gdshader", code, new Dictionary<string, string>(), []), GodotShaderTable.Carried, options);

    private static GodotShaderChecked Check(string code, IReadOnlyDictionary<string, string>? includes = null, string method = "forward_plus", Version? godot = null) =>
        GodotShaderCheck.Check(new GodotShaderUnit("res://shader.gdshader", code, includes ?? new Dictionary<string, string>(), []), GodotShaderTable.Carried,
            new GodotShaderCheckOptions { RenderingMethod = method, Godot = godot ?? new Version(4, 7, 2) });
}
