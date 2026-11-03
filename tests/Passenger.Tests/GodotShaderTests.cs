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

/// <summary>A Godot project's shaders checked by Godot's own compiler, in the headless helper Passenger runs on a project of
/// its own (its user folders the run's), the project the test writes left as it was: a misspelt name in a .gdshader and in
/// a scene's own Shader is marked with Godot's words at its line, an include's own error on it and on the line including
/// it; VERTEX completes in vertex() and not in light(), and only a Godot taking it offers a built-in; a uniform's card gives its type and hint as Godot reads it; Go to
/// Declaration reaches an include's function; Find Usages of a uniform lists the shader's uses and the
/// scene setting it; Context Help on TIME browses its place in Godot's shading reference. Without a Godot of 4.5 or newer
/// (GODOT, the PATH, Applications) each test skips saying why. Run alone: each starts a Godot of its own.</summary>
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

    /// <summary>The open file's mark of these words, once Godot's compiler has given it.</summary>
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
