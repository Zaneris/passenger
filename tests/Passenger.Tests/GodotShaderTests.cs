using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Godot;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A Godot project's shaders checked by Passenger's port of Godot's front end, with the shading language as the
/// Godot found takes it, the project the test writes left as it was: a misspelt name is marked with Godot's words at its
/// line in a .gdshader, a scene's own Shader and an include; where Godot's own compiler errs over shaders of every type
/// under each rendering method, Passenger marks the same error; checking shaders starts no Godot but its version's probe;
/// VERTEX completes in vertex() and not in light(), only a Godot taking it offering a built-in, and a uniform's card gives
/// its type and hint as Godot reads it, in a scene's own Shader too; Go to Declaration reaches an include's function; Find
/// Usages of a uniform lists the scene setting it; Context Help browses a built-in's place in the shading reference; a
/// Compatibility project's Forward+ hint is marked. Without a Godot of 4.5 or newer (GODOT, the PATH, Applications) each
/// test skips saying why. Run alone: each starts a Godot of its own.</summary>
[Collection(Alone.Name)]
public sealed class GodotShaderTests
{
    private const string Water = """
        shader_type spatial;
        render_mode blend_mix, cull_back;

        #include "res://lib/waves.gdshaderinc"

        uniform float speed : hint_range(0.0, 10.0) = 1.0;
        uniform vec4 tint : source_color = vec4(1.0);

        void vertex() {
        	VERTEX.y += wave(VERTEX.x, TIME * speed);
        }

        void fragment() {
        	ALBEDO = tint.rgb * speedd;
        }

        void light() {
        	DIFFUSE_LIGHT += ATTENUATION * LIGHT_COLOR;
        }

        """;

    private const string Waves = "// Waves the water shaders share.\nfloat wave(float x, float t) {\n\treturn sin(x * 2.0 + t) * 0.1;\n}\n";

    /// <summary>A scene whose own Shader includes the waves, escapes and all, and a material of the water shader setting speed.</summary>
    private const string Level = """
        [gd_scene load_steps=5 format=3]

        [ext_resource type="Shader" path="res://fx/water.gdshader" id="1_water"]

        [sub_resource type="Shader" id="Shader_glow"]
        code = "shader_type canvas_item;
        #include \"res://lib/waves.gdshaderinc\"

        void fragment() {
        	COLOR = vec4(glw, 0.0, 0.0, 1.0);
        }
        "

        [sub_resource type="ShaderMaterial" id="ShaderMaterial_glow"]
        shader = SubResource("Shader_glow")

        [sub_resource type="ShaderMaterial" id="ShaderMaterial_water"]
        shader = ExtResource("1_water")
        shader_parameter/speed = 2.0

        [node name="Level" type="Node3D"]

        [node name="Glow" type="Sprite2D" parent="."]
        material = SubResource("ShaderMaterial_glow")

        [node name="Sea" type="MeshInstance3D" parent="."]
        material_override = SubResource("ShaderMaterial_water")

        """;

    /// <summary>A scene whose own Shader declares a uniform and moves its vertices, a quote escaped above them, so the file's
    /// places and the code's differ.</summary>
    private const string Ripple = """
        [gd_scene load_steps=3 format=3]

        [sub_resource type="Shader" id="Shader_ripple"]
        code = "shader_type canvas_item;
        // A \"ripple\" over the sprite.
        uniform float strength : hint_range(0.0, 4.0) = 0.5;

        void vertex() {
        	VERTEX.x += sin(VERTEX.y) * strength;
        }
        "

        [sub_resource type="ShaderMaterial" id="ShaderMaterial_ripple"]
        shader = SubResource("Shader_ripple")

        [node name="Ripple" type="Sprite2D"]
        material = SubResource("ShaderMaterial_ripple")

        """;

    /// <summary>A spatial shader reading the normal and roughness buffer, which only Forward+ draws.</summary>
    private const string Rough = """
        shader_type spatial;

        uniform sampler2D roughness_buffer : hint_normal_roughness_texture;

        void fragment() {
        	ALBEDO = texture(roughness_buffer, SCREEN_UV).rgb;
        }

        """;

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GodotShaderTests()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): install Godot 4.5 or newer to run these tests");
        Assert.SkipWhen(!System.Version.TryParse(string.Join('.', (godot.Version ?? "0.0").Split('.').Take(2)), out Version? number) || number < new Version(4, 5),
            $"{godot.Executable} is Godot {godot.Version}, whose scripts hear no logger: these tests need Godot 4.5 or newer");
        folder = TestFolders.Create("godot-shaders-");
        TestFolders.Write(folder, GodotProject.FileName, "config_version=5\n\n[application]\n\nconfig/name=\"Shaded\"\n");
        TestFolders.Write(folder, Path.Combine("fx", "water.gdshader"), Water);
        TestFolders.Write(folder, Path.Combine("lib", "waves.gdshaderinc"), Waves);
        TestFolders.Write(folder, "level.tscn", Level);
    }

    [AvaloniaFact]
    public void A_misspelt_name_is_marked_with_Godots_words_at_its_line_in_a_shader_a_scenes_own_shader_and_through_an_include()
    {
        SortedDictionary<string, string> before = Files();
        MainWindow window = Open(Path.Combine("fx", "water.gdshader"));
        Marker speedd = Marked(window, "Unknown identifier in expression: 'speedd'.");
        TextSnapshot text = window.EditorArea.CodeEditor.Document.Snapshot;
        Assert.Equal(("speedd", LineOf(Water, "tint.rgb * speedd")), (text.GetText(speedd.Range.Start, speedd.Range.Length), text.GetLineFromOffset(speedd.Range.Start)));

        // An error typed in the include is its own, and the shader's on the line including it.
        Assert.Null(window.OpenFile(Path.Combine(folder, "lib", "waves.gdshaderinc")));
        CodeEditor include = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(include, "return sin(x");
        include.Focus();
        window.KeyTextInput("x");
        Marker own = Marked(window, "Unknown identifier in expression: 'xx'.");
        Assert.Equal(LineOf(Waves, "return sin"), include.Document.Snapshot.GetLineFromOffset(own.Range.Start));
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => window.EditorArea.Active!.Markers.Count == 0, () => $"the include's mark to go once mended: {Said(window)}");

        Assert.Null(window.OpenFile(Path.Combine(folder, "level.tscn")));
        Marker glw = Marked(window, "Unknown identifier in expression: 'glw'.");
        TextSnapshot scene = window.EditorArea.CodeEditor.Document.Snapshot;
        Assert.Equal(("glw", LineOf(Level, "COLOR = vec4(glw")), (scene.GetText(glw.Range.Start, glw.Range.Length), scene.GetLineFromOffset(glw.Range.Start)));
        window.Close();
        Ui.Settle();
        SortedDictionary<string, string> after = Files();
        Assert.True(before.SequenceEqual(after), $"The project was to be left as it was, but holds {string.Join(", ", after.Keys)} (it held {string.Join(", ", before.Keys)})");
    }

    [AvaloniaFact]
    public void VERTEX_completes_in_vertex_and_not_in_light_and_a_uniforms_card_gives_its_type_and_hint()
    {
        MainWindow window = Open(Path.Combine("fx", "water.gdshader"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Marked(window, "Unknown identifier in expression: 'speedd'.");
        Ui.PlaceAfter(editor, "tint.rgb * speed");
        editor.Focus();
        Ui.Press(window, "DELETE");
        Ui.WaitFor(() => window.EditorArea.Active!.Markers.Count == 0, () => $"the shader to compile with no error once mended: {Said(window)}");

        Ui.PlaceAfter(editor, "TIME * speed);");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("V");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("VERTEX"), () => $"VERTEX among vertex()'s completions: {string.Join(", ", window.Assist.CompletionLabels.Take(12))}");
        Assert.Contains("VIEW_MATRIX", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");

        Ui.PlaceAfter(editor, "ATTENUATION * LIGHT_COLOR;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("V");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("VIEW_MATRIX"), () => $"light()'s completions: {string.Join(", ", window.Assist.CompletionLabels.Take(12))}");
        Assert.DoesNotContain("VERTEX", window.Assist.CompletionLabels);
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");

        // The Godot found decides: fragment()'s STREAMING_LOD came with Godot 4.8, so an older Godot's list leaves it out.
        Ui.PlaceAfter(editor, "tint.rgb * speed;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("S");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("SCREEN_UV"), () => $"fragment()'s completions: {string.Join(", ", window.Assist.CompletionLabels.Take(12))}");
        bool streams = System.Version.Parse(string.Join('.', godot!.Version!.Split('.', '-').Take(2))) >= new Version(4, 8);
        Assert.True(window.Assist.CompletionLabels.Contains("STREAMING_LOD") == streams,
            $"Godot {godot.Version} {(streams ? "takes" : "refuses")} STREAMING_LOD, but fragment()'s list {(streams ? "lacks" : "offers")} it");
        Ui.Press(window, "ESCAPE");
        Ui.Press(window, "BACK_SPACE");

        string card = Hover(window, editor, "speed);");
        Assert.Contains("uniform float speed : hint_range(0.0, 10.0) = 1.0;", card, StringComparison.Ordinal);
        Assert.Contains("Godot reads as float, in 0.0 to 10.0, step 0.001", card, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Go_to_Declaration_reaches_an_includes_function_and_Find_Usages_of_a_uniform_lists_the_scene_setting_it()
    {
        MainWindow window = Open(Path.Combine("fx", "water.gdshader"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "+= wav");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => window.EditorArea.Active?.Name == "waves.gdshaderinc" && window.EditorArea.CodeEditor.CaretPosition.Line == LineOf(Waves, "float wave"),
            () => $"the caret at wave()'s declaration in waves.gdshaderinc ({window.Status}): {window.EditorArea.Active?.Name}:{window.EditorArea.CodeEditor.CaretPosition.Line + 1}");

        Assert.Null(window.OpenFile(Path.Combine(folder, "fx", "water.gdshader")));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "TIME * spe");
        Ui.Run(window, AppActions.FindUsages);
        (string File, int Line)[] uses = [("water.gdshader", LineOf(Water, "uniform float speed")), ("water.gdshader", LineOf(Water, "TIME * speed")),
            ("level.tscn", LineOf(Level, "shader_parameter/speed"))];
        Ui.WaitFor(() => uses.All(u => window.Find.Rows.Any(r => r.Usage is { } found && Path.GetFileName(found.Path) == u.File && found.Line == u.Line)),
            () => $"Find Usages of speed to list {string.Join(", ", uses)} ({window.Status}): {string.Join("; ", window.Find.Rows.Select(r => r.Usage).OfType<Usage>().Select(u => $"{Path.GetFileName(u.Path)}:{u.Line}"))}");
        window.Close();
    }

    /// <summary>Context Help on TIME browses the global built-ins of the spatial shader's page of Godot's shading reference,
    /// for the version of the Godot found; on the shader's own uniform it says there is none, browsing nothing.</summary>
    [AvaloniaFact]
    public void Context_help_on_TIME_browses_its_place_in_Godots_shading_reference()
    {
        MainWindow window = Open(Path.Combine("fx", "water.gdshader"));
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.PlaceAfter(editor, "TIM");
        Ui.Press(window, Ui.Shortcut(AppActions.ContextHelp));
        Uri page = new($"https://docs.godotengine.org/en/{GodotDocs.VersionOf(godot!.Version)}/tutorials/shaders/shader_reference/spatial_shader.html#global-built-ins");
        Ui.WaitFor(() => window.Browsed.Contains(page), () => $"TIME's place in the shading reference to be browsed ({window.Status}): {string.Join(", ", window.Browsed)}");

        Ui.PlaceAfter(editor, "uniform vec4 ti");
        Ui.Press(window, Ui.Shortcut(AppActions.ContextHelp));
        Ui.WaitFor(() => window.Status == Refusals.NoShaderDocumentation, () => $"Context Help on the shader's own uniform to say it has no page ({window.Status})");
        Assert.Equal([page], window.Browsed);
        window.Close();
    }

    /// <summary>Inside a scene's own Shader a uniform's card gives its type and hint as Godot's compile of the scene read
    /// them, Go to Declaration on its use reaches its line in the scene, Context Help on VERTEX browses vertex()'s built-ins
    /// of canvas_item shaders, and VERTEX completes in vertex().</summary>
    [AvaloniaFact]
    public void VERTEX_completes_inside_a_scenes_own_shader_and_its_uniforms_card_gives_its_type_and_hint()
    {
        TestFolders.Write(folder, "ripple.tscn", Ripple);
        MainWindow window = Open("ripple.tscn");
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => !window.EditorArea.Active!.IsChecking, () => $"Godot's compile of the scene's shader: {Said(window)}", TimeSpan.FromSeconds(60));
        Assert.True(window.EditorArea.Active!.Markers.Count == 0, $"The scene's shader compiles with no error, but is marked: {Said(window)}");

        string card = Hover(window, editor, "strength;");
        Assert.Contains("uniform float strength : hint_range(0.0, 4.0) = 0.5;", card, StringComparison.Ordinal);
        Assert.Contains("Godot reads as float, in 0.0 to 4.0, step 0.001", card, StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "* streng");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        Ui.WaitFor(() => editor.CaretPosition.Line == LineOf(Ripple, "uniform float strength"),
            () => $"the caret at strength's declaration in the scene ({window.Status}): line {editor.CaretPosition.Line + 1}");

        Ui.PlaceAfter(editor, "VERTE");
        Ui.Press(window, Ui.Shortcut(AppActions.ContextHelp));
        Uri page = new($"https://docs.godotengine.org/en/{GodotDocs.VersionOf(godot!.Version)}/tutorials/shaders/shader_reference/canvas_item_shader.html#vertex-built-ins");
        Ui.WaitFor(() => window.Browsed.Contains(page), () => $"VERTEX's place in the shading reference to be browsed ({window.Status}): {string.Join(", ", window.Browsed)}");

        Ui.PlaceAfter(editor, "* strength;");
        editor.Focus();
        Ui.Press(window, "ENTER");
        window.KeyTextInput("V");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Contains("VERTEX"), () => $"VERTEX among the scene's vertex() completions: {string.Join(", ", window.Assist.CompletionLabels.Take(12))}");
        Ui.Press(window, "ESCAPE");
        window.Close();
    }

    /// <summary>A Compatibility project's shader is read under that rendering method, so a hint only Forward+ gives is marked
    /// with Godot's words at its line.</summary>
    [AvaloniaFact]
    public void A_compatibility_projects_shader_is_marked_where_it_asks_for_what_only_Forward_plus_draws()
    {
        TestFolders.Write(folder, GodotProject.FileName, "config_version=5\n\n[application]\n\nconfig/name=\"Shaded\"\n\n[rendering]\n\nrenderer/rendering_method=\"gl_compatibility\"\n");
        TestFolders.Write(folder, Path.Combine("fx", "rough.gdshader"), Rough);
        MainWindow window = Open(Path.Combine("fx", "rough.gdshader"));
        Marker hint = Marked(window, "'hint_normal_roughness_texture' is only available when using the Forward+ renderer.");
        Assert.Equal(LineOf(Rough, "hint_normal_roughness_texture"), window.EditorArea.CodeEditor.Document.Snapshot.GetLineFromOffset(hint.Range.Start));
        window.Close();
    }

    /// <summary>A session that opens a shader, an include and a scene's own Shader, edits them and checks them starts no
    /// Godot for them: the shading language's table is probed once for the Godot found's version (kept in the state's cache,
    /// so at most once here), and every check after it is Passenger's own.</summary>
    [AvaloniaFact]
    public void Opening_editing_and_checking_shaders_starts_no_Godot_but_the_probe_of_its_version()
    {
        List<string> godots = [];
        EventHandler<ChildStart> started = (_, child) =>
        {
            if (child.Start is { } start && start.FileName == godot!.Executable)
            {
                lock (godots)
                {
                    godots.Add(ChildProcesses.Shown(start));
                }
            }
        };
        ChildProcesses.Started += started;
        try
        {
            MainWindow window = Open(Path.Combine("fx", "water.gdshader"));
            Marked(window, "Unknown identifier in expression: 'speedd'.");
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "tint.rgb * speed");
            editor.Focus();
            Ui.Press(window, "DELETE");
            Ui.WaitFor(() => window.EditorArea.Active!.Markers.Count == 0, () => $"the shader to check with no mark once mended: {Said(window)}");
            Ui.PlaceAfter(editor, "TIME * speed");
            window.KeyTextInput("x");
            Marked(window, "Unknown identifier in expression: 'speedx'.");
            Assert.Null(window.OpenFile(Path.Combine(folder, "lib", "waves.gdshaderinc")));
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "return sin(x");
            window.EditorArea.CodeEditor.Focus();
            window.KeyTextInput("x");
            Marked(window, "Unknown identifier in expression: 'xx'.");
            Assert.Null(window.OpenFile(Path.Combine(folder, "level.tscn")));
            Marked(window, "Unknown identifier in expression: 'glw'.");
            Ui.Settle();
            lock (godots)
            {
                Assert.True(godots.Count <= 1 && godots.All(g => g.Contains("PassengerShaderProbes.gd", StringComparison.Ordinal)),
                    $"Only the probe of the Godot found's shading language was to start a Godot, but these did: {string.Join("; ", godots)}");
            }
            window.Close();
        }
        finally
        {
            ChildProcesses.Started -= started;
        }
    }

    /// <summary>Shaders of every type, with and without errors, an include's error among them and what one rendering method
    /// refuses: under each method, wherever Godot's own compiler (the Godot found, headless with user folders of the
    /// test's) errs, Passenger marks the shader with the same words at the same line, or on its <c>#include</c> with the
    /// include's place; where Godot compiles a shader, Passenger marks no error on it.</summary>
    [AvaloniaFact]
    public void Each_shader_is_marked_where_Godots_own_compiler_errs_in_its_words_at_its_line_under_each_rendering_method()
    {
        foreach (string method in new[] { "forward_plus", "mobile", "gl_compatibility" })
        {
            string project = TestFolders.Create($"godot-shader-parity-{method}-");
            TestFolders.Write(project, GodotProject.FileName, $"config_version=5\n\n[application]\n\nconfig/name=\"Parity\"\n\n[rendering]\n\nrenderer/rendering_method=\"{method}\"\n");
            foreach ((string name, string code) in Compared)
            {
                TestFolders.Write(project, name, code);
            }
            Dictionary<string, List<(string File, int Line, string Text)>> godotSays = GodotErrors(project);
            MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
            window.Show();
            window.OpenFolder(project);
            Ui.Settle();
            foreach ((string name, _) in Compared.Where(c => c.Name.EndsWith(".gdshader", StringComparison.Ordinal)))
            {
                Assert.Null(window.OpenFile(Path.Combine(project, name)));
                Ui.WaitFor(() => !window.EditorArea.Active!.IsChecking, () => $"{name} to be checked under {method}: {Said(window)}", TimeSpan.FromSeconds(60));
                IReadOnlyList<Marker> marks = window.EditorArea.Active!.Markers;
                TextSnapshot text = window.EditorArea.CodeEditor.Document.Snapshot;
                List<(string File, int Line, string Text)> errors = godotSays.GetValueOrDefault("res://" + name) ?? [];
                if (errors.Count == 0)
                {
                    Assert.True(!marks.Any(m => m.Kind == MarkerKind.Error), $"Godot compiles {name} under {method}, but Passenger marks it: {Said(window)}");
                    continue;
                }
                (string file, int line, string words) = errors[0];
                bool marked = file.Length == 0
                    ? marks.Any(m => m.Kind == MarkerKind.Error && m.Message == words && text.GetLineFromOffset(m.Range.Start) == line - 1)
                    : marks.Any(m => m.Kind == MarkerKind.Error && m.Message == $"{file}:{line}: {words}");
                Assert.True(marked, $"Godot's compiler says of {name} under {method} \"{words}\" at {(file.Length == 0 ? string.Empty : file + ":")}{line}, but Passenger marks: {Said(window)}");
            }
            window.Close();
            Ui.Settle();
        }
    }

    /// <summary>The shaders compared with Godot's own compiler, by their paths in the project: one of each type with an
    /// error, others that compile, one including an include that errs, and one a rendering method refuses. A high end's
    /// function is not among them: headless, Godot draws through its dummy renderer, which is no low end.</summary>
    private static readonly (string Name, string Code)[] Compared =
    [
        ("spatial_name.gdshader", "shader_type spatial;\n\nuniform float speed = 1.0;\n\nvoid fragment() {\n\tALBEDO = vec3(speedd);\n}\n"),
        ("spatial_types.gdshader", "shader_type spatial;\n\nvoid fragment() {\n\tfloat a = vec3(1.0);\n\tALBEDO = vec3(a);\n}\n"),
        ("spatial_ok.gdshader", "shader_type spatial;\nrender_mode unshaded;\n\nuniform float never;\nuniform vec4 tint : source_color = vec4(1.0);\n\nvoid fragment() {\n\tALBEDO = tint.rgb;\n}\n"),
        ("canvas_semicolon.gdshader", "shader_type canvas_item;\n\nvoid fragment() {\n\tCOLOR = vec4(1.0)\n}\n"),
        ("canvas_ok.gdshader", "shader_type canvas_item;\n\nuniform float speed = 1.0;\n\nvoid vertex() {\n\tVERTEX += vec2(sin(TIME * speed), 0.0);\n}\n"),
        ("particles_discard.gdshader", "shader_type particles;\n\nvoid process() {\n\tdiscard;\n}\n"),
        ("particles_ok.gdshader", "shader_type particles;\n\nvoid start() {\n\tVELOCITY = vec3(0.0, 1.0, 0.0);\n}\n\nvoid process() {\n\tVELOCITY.y -= 9.8 * DELTA;\n}\n"),
        ("sky_call.gdshader", "shader_type sky;\n\nvoid sky() {\n\tCOLOR = vec3(no_such(EYEDIR.y));\n}\n"),
        ("sky_ok.gdshader", "shader_type sky;\n\nvoid sky() {\n\tCOLOR = mix(vec3(0.2), vec3(1.0), clamp(EYEDIR.y, 0.0, 1.0));\n}\n"),
        ("fog_builtin.gdshader", "shader_type fog;\n\nvoid fog() {\n\tDENSITY = VERTEX.x;\n}\n"),
        ("fog_ok.gdshader", "shader_type fog;\n\nvoid fog() {\n\tDENSITY = clamp(1.0 - length(UVW - vec3(0.5)), 0.0, 1.0);\n}\n"),
        ("lib/bad.gdshaderinc", "float bad() {\n\treturn xx;\n}\n"),
        ("lib/good.gdshaderinc", "float lift(float v) {\n\treturn v * 0.5 + 0.5;\n}\n"),
        ("include_bad.gdshader", "shader_type spatial;\n\n#include \"lib/bad.gdshaderinc\"\n\nvoid fragment() {\n\tALBEDO = vec3(bad());\n}\n"),
        ("include_good.gdshader", "shader_type canvas_item;\n\n#include \"res://lib/good.gdshaderinc\"\n\nvoid fragment() {\n\tCOLOR.rgb = vec3(lift(UV.x));\n}\n"),
        ("forward_hint.gdshader", "shader_type spatial;\n\nuniform sampler2D nrt : hint_normal_roughness_texture;\n\nvoid fragment() {\n\tALBEDO = texture(nrt, SCREEN_UV).rgb;\n}\n"),
    ];

    /// <summary>Godot's own compiler's errors for each shader of a project, by its res:// path: the Godot found, headless on
    /// the project with user folders beside it, compiling each as a Shader resource and writing what its logger heard.</summary>
    private Dictionary<string, List<(string File, int Line, string Text)>> GodotErrors(string project)
    {
        TestFolders.Write(project, "compare.gd", CompareScript);
        string written = Path.Combine(project, "heard.txt");
        System.Diagnostics.ProcessStartInfo start = ChildProcesses.Command(godot!.Executable, [GodotExecutables.Headless, "--path", project, "--script", "res://compare.gd", "--", written], project);
        foreach ((string name, string value) in Passenger.App.Godot.GodotUserFolders.Under(project + "-home"))
        {
            start.Environment[name] = value;
        }
        ChildResult result = Ui.Await(ChildProcesses.RunAsync(start, limit: TimeSpan.FromSeconds(60) * TestRun.TimeScale), "Godot's own compile of the project's shaders");
        Assert.True(File.Exists(written), $"Godot ({godot.Executable}) wrote nothing of its compile: {result.ExitCode} {result.Output} {result.Error}");
        Dictionary<string, List<(string File, int Line, string Text)>> errors = new(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(written))
        {
            string[] parts = line.Split('\t');
            if (parts.Length == 5 && parts[1] == "3")
            {
                if (!errors.TryGetValue(parts[0], out List<(string File, int Line, string Text)>? said))
                {
                    errors[parts[0]] = said = [];
                }
                said.Add((parts[2], int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture), parts[4]));
            }
        }
        File.Delete(written);
        File.Delete(Path.Combine(project, "compare.gd"));
        return errors;
    }

    /// <summary>The script Godot compiles a project's shaders with: each .gdshader as a Shader resource at its path, a line
    /// for each error or warning its logger heard (the shader, its type, file, line and words) into the file it is given.</summary>
    private const string CompareScript = """
        extends SceneTree

        class Heard extends Logger:
        	var mutex := Mutex.new()
        	var messages: Array = []

        	func _log_error(_function: String, file: String, line: int, code: String, rationale: String, _editor_notify: bool,
        			error_type: int, _script_backtraces: Array[ScriptBacktrace]) -> void:
        		mutex.lock()
        		messages.append([error_type, file, line, code if code != "" else rationale])
        		mutex.unlock()

        	func _log_message(_message: String, _error: bool) -> void:
        		pass

        	func taken() -> Array:
        		mutex.lock()
        		var all := messages
        		messages = []
        		mutex.unlock()
        		return all

        func _initialize() -> void:
        	var heard := Heard.new()
        	OS.add_logger(heard)
        	var lines := PackedStringArray()
        	var files := []
        	collect("res://", files)
        	for path in files:
        		heard.taken()
        		var shader := Shader.new()
        		shader.take_over_path(path)
        		shader.code = FileAccess.get_file_as_string(path)
        		shader.get_rid()
        		for m in heard.taken():
        			lines.append("%s\t%d\t%s\t%d\t%s" % [path, m[0], m[1], m[2], m[3]])
        	var out := FileAccess.open(OS.get_cmdline_user_args()[0], FileAccess.WRITE)
        	out.store_string("\n".join(lines))
        	out.close()
        	quit()

        func collect(dir: String, files: Array) -> void:
        	for f in DirAccess.get_files_at(dir):
        		if f.ends_with(".gdshader"):
        			files.append(dir.path_join(f))
        	for d in DirAccess.get_directories_at(dir):
        		if not d.begins_with("."):
        			collect(dir.path_join(d), files)

        """;

    private MainWindow Open(string file)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.Null(window.OpenFile(Path.Combine(folder, file)));
        Ui.Settle();
        return window;
    }

    /// <summary>The open file's mark of these words, once its check has given it.</summary>
    private static Marker Marked(MainWindow window, string words)
    {
        Ui.WaitFor(() => window.EditorArea.Active!.Markers.Any(m => m.Message == words), () => $"the mark '{words}': {Said(window)}", TimeSpan.FromSeconds(60));
        return window.EditorArea.Active!.Markers.First(m => m.Message == words);
    }

    private static string Said(MainWindow window) =>
        $"{window.EditorArea.Active?.Name} has {string.Join(" | ", window.EditorArea.Active?.Markers.Select(m => m.Message) ?? [])} ({window.EditorArea.Active?.Notice ?? window.Status})";

    /// <summary>The line (from zero) of a text's first line holding some words.</summary>
    private static int LineOf(string text, string words) => text[..text.IndexOf(words, StringComparison.Ordinal)].Count(c => c == '\n');

    /// <summary>Every file and folder of the project with what a file holds, so anything Godot made or wrote there shows.</summary>
    private SortedDictionary<string, string> Files() => new(Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(folder, f), f => File.Exists(f) ? File.ReadAllText(f) : "(a folder)"), StringComparer.Ordinal);

    /// <summary>The card shown with the pointer resting on some text of the editor's.</summary>
    private static string Hover(MainWindow window, CodeEditor editor, string text)
    {
        window.MouseMove(new Point(0, 0), RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is null, "the last card to go");
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
        Point point = editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value;
        window.MouseMove(point, RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, $"the card on {text}");
        return window.Assist.HoverText!;
    }
}
