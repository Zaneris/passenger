using Passenger.Workspace.GodotShader;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's shader preprocessor as Passenger ports it: macros expanded, branches kept or dropped with their
/// regions, includes inlined between their markers, the renderer's defines, and each error in Godot's words at the places
/// it names.</summary>
public class GodotShaderPreprocessorTests
{
    private const string Shader = "res://shaders/s.gdshader";

    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["res://inc/a.gdshaderinc"] = "float helper() {\n    return 1.0;\n}\n",
        ["res://inc/b.gdshaderinc"] = "#bogus\n",
        ["res://shaders/other.gdshader"] = "shader_type spatial;\n",
    };

    [Fact]
    public void Macros_are_expanded_with_their_arguments_and_joins_and_comments_leave_their_line_breaks()
    {
        GodotShaderPreprocessed read = Read("shader_type spatial;\n#define SCALE 2.0\n#define MUL(a, b) ((a) * (b))\n#define CAT(x) x ## _end\n"
            + "float f = MUL(SCALE, 3.0); // comment\nint CAT(name) = 1; /* block\ncomment */ int z;\n");
        Assert.Null(read.Error);
        string[] lines = read.Code.Split('\n');
        Assert.Equal("float f = ((2.0)*(3.0));", Compact(lines[4]));
        Assert.Equal("int name_end = 1;", Compact(lines[5]));
        Assert.Equal("int z;", lines[6].Trim());
        Assert.Equal(8, lines.Length);
    }

    [Fact]
    public void Branches_keep_one_side_under_the_renderer_s_defines_and_say_their_regions()
    {
        GodotShaderPreprocessed read = Read("shader_type spatial;\n#ifdef MISSING\nint a;\n#else\nint b;\n#endif\n"
            + "#if defined(SCALE) || CURRENT_RENDERER == RENDERER_MOBILE\nint c;\n#elif 1\nint d;\n#endif\n", "mobile");
        Assert.Null(read.Error);
        Assert.DoesNotContain("int a;", read.Code, StringComparison.Ordinal);
        Assert.Contains("int b;", read.Code, StringComparison.Ordinal);
        Assert.Contains("int c;", read.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("int d;", read.Code, StringComparison.Ordinal);
        Assert.Equal(["2-2 off", "4-4 on", "7-7 on", "9-9 off"], read.Regions.Select(r => $"{r.FromLine}-{r.ToLine} {(r.Enabled ? "on" : "off")}"));
        // Every line kept but the last directive's own line break, which Godot's preprocessor drops at the file's end too.
        Assert.Equal(11, read.Code.Split('\n').Length);
    }

    [Theory]
    [InlineData("shader_type spatial;\n\n#bogus\n", "Unknown directive.", 3)]
    [InlineData("#define A 1\n#define A 2\n", "Macro redefinition.", 2)]
    [InlineData("#ifdef X\nint a;\n", "Can't find matching branch directive.", 2)]
    [InlineData("#if 1.5\n#endif\n", "Floating-point number operations are not supported by the shader preprocessor.", 1)]
    [InlineData("#if FOO\n#endif\n", "Condition evaluation error.", 1)]
    [InlineData("#if (1\n#endif\n", "Expected a ')'.", 1)]
    [InlineData("#if 1 = 1\n#endif\n", "Expected '='.", 1)]
    [InlineData("#if 1 / 0\n#endif\n", "Condition evaluation error.", 1)]
    [InlineData("int a; #define X\n", "Invalid symbols placed before directive.", 1)]
    [InlineData("int a;\n/* never closed\n", "Block comment mismatch", 2)]
    [InlineData("#undef CURRENT_RENDERER\n", "Cannot use 'undef' on built-in define.", 1)]
    [InlineData("#else\n", "Unmatched 'else' directive.", 1)]
    [InlineData("#error stop here\n", "stop here", 1)]
    [InlineData("shader_type spatial;\n#include \"missing.gdshaderinc\"\n", "Shader include file does not exist: res://shaders/missing.gdshaderinc", 2)]
    [InlineData("shader_type spatial;\n#include \"other.gdshader\"\n", "Shader include resource type is wrong.", 2)]
    public void An_error_is_said_in_godot_s_words_at_its_line(string code, string error, int line)
    {
        GodotShaderPreprocessed read = Read(code);
        Assert.Equal(error, read.Error);
        Assert.Equal([new GodotShaderFilePosition(Shader, line)], read.ErrorPositions);
    }

    [Fact]
    public void An_include_is_inlined_between_its_markers_its_relative_path_taken_from_the_shader_s_folder()
    {
        GodotShaderPreprocessed read = Read("shader_type spatial;\n#include \"../inc/a.gdshaderinc\"\nvoid fragment() {}\n");
        Assert.Null(read.Error);
        Assert.Equal(["res://inc/a.gdshaderinc"], read.Included);
        Assert.Contains("@@>res://inc/a.gdshaderinc\nfloat helper() {", read.Code, StringComparison.Ordinal);
        Assert.Contains("}\n\n@@<res://inc/a.gdshaderinc\n", read.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_in_an_include_names_the_shader_s_line_then_the_include_s()
    {
        GodotShaderPreprocessed read = Read("shader_type spatial;\n\n#include \"res://inc/b.gdshaderinc\"\n");
        Assert.Equal("Unknown directive.", read.Error);
        Assert.Equal([new GodotShaderFilePosition("res://inc/b.gdshaderinc", 3), new GodotShaderFilePosition("res://inc/b.gdshaderinc", 1)], read.ErrorPositions);
    }

    [Fact]
    public void A_line_continuation_joins_a_macro_s_body_and_keeps_the_line_count()
    {
        GodotShaderPreprocessed read = Read("#define LONG 1 + \\\n 2\nint x = LONG;\n");
        Assert.Null(read.Error);
        Assert.Equal(["", "", "int x = 1 + 2 ;", ""], read.Code.Split('\n').Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries))));
    }

    private static GodotShaderPreprocessed Read(string code, string method = "forward_plus") =>
        GodotShaderPreprocessor.Preprocess(code, Shader, method,
            path => Files.TryGetValue(path, out string? text) ? new GodotShaderIncluded(true, path.EndsWith(".gdshaderinc", StringComparison.Ordinal), text) : null);

    /// <summary>A line with its spaces taken out but one between words.</summary>
    private static string Compact(string line) => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        .Replace("( ", "(", StringComparison.Ordinal).Replace(" )", ")", StringComparison.Ordinal).Replace(" * ", "*", StringComparison.Ordinal)
        .Replace(" ;", ";", StringComparison.Ordinal);
}
