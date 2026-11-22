using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A Godot project's shaders checked by Passenger's port of Godot's front end, with or without a Godot: a shader
/// with several errors is marked with each of them in Godot's words at its line; one that compiles is warned of what it
/// declares and never uses, but where its project turns a warning off; an include is read alone as Godot's editor reads
/// one; a uniform's card says what the check read of it; and a shader opened before its project keeps its marks once the
/// project is open.</summary>
public sealed class GodotShaderFrontEndTests
{
    private const string Several = """
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

        """;

    private const string Warned = """
        shader_type canvas_item;

        uniform float never;
        uniform float speed : hint_range(0.0, 4.0, 0.5) = 1.0;

        void vertex() {
        	float unused_local = 1.0;
        	VERTEX.x += speed;
        }

        """;

    private readonly string folder = TestFolders.Create("godot-shader-front-end-");

    /// <summary>A shader is marked with each error and warned as its project asks, its include read alone, its marks
    /// kept until its project opens.</summary>
    [AvaloniaFact]
    public void A_shader_is_marked_and_warned_as_godots_editor_does()
    {
        Flow.StepApart<GodotShaderFrontEndTests>(nameof(A_shader_with_several_errors_is_marked_with_each_in_Godots_words_at_its_line),
            test => test.A_shader_with_several_errors_is_marked_with_each_in_Godots_words_at_its_line());
        OpenWindows.CloseAll();
        Flow.StepApart<GodotShaderFrontEndTests>(nameof(A_shader_that_compiles_is_warned_as_its_project_asks_its_include_read_alone_and_a_uniforms_card_says_what_the_check_read),
            test => test.A_shader_that_compiles_is_warned_as_its_project_asks_its_include_read_alone_and_a_uniforms_card_says_what_the_check_read());
        OpenWindows.CloseAll();
        Flow.StepApart<GodotShaderFrontEndTests>(nameof(A_shader_opened_before_its_project_keeps_its_marks_as_typing_pauses_once_the_project_is_open),
            test => test.A_shader_opened_before_its_project_keeps_its_marks_as_typing_pauses_once_the_project_is_open());
    }

    private void A_shader_with_several_errors_is_marked_with_each_in_Godots_words_at_its_line()
    {
        TestFolders.Write(folder, GodotProject.FileName, "config_version=5\n\n[application]\n\nconfig/name=\"Several\"\n");
        TestFolders.Write(folder, "several.gdshader", Several);
        MainWindow window = Open("several.gdshader");
        (string Words, string At)[] errors =
        [
            ("Expected valid type hint after ':'.", "hint_nope"),
            ("Unknown identifier in expression: 'speedd'.", "TIME * speedd"),
            ("Invalid assignment of 'vec3' to 'float'.", "float a = vec3"),
            ("Expected a ';'.", "METALLIC = 0.5"),
            ("Unknown identifier in expression: 'nope'.", "ALPHA > nope"),
            ("Invalid arguments to operator '+': 'vec3, vec2'.", "vec3(1.0) + vec2"),
        ];
        Ui.WaitFor(() => errors.All(e => Marked(window, e.Words, e.At)), () => $"each error marked at its line: {Said(window)}");
        Assert.True(window.EditorArea.Active!.Markers.Count == errors.Length, $"The shader's {errors.Length} errors alone were to be marked: {Said(window)}");
        window.Close();
    }

    private void A_shader_that_compiles_is_warned_as_its_project_asks_its_include_read_alone_and_a_uniforms_card_says_what_the_check_read()
    {
        TestFolders.Write(folder, GodotProject.FileName, "config_version=5\n\n[application]\n\nconfig/name=\"Warned\"\n\n[debug]\n\nshader_language/warnings/unused_uniform=false\n");
        TestFolders.Write(folder, "warned.gdshader", Warned);
        TestFolders.Write(folder, Path.Combine("lib", "helpers.gdshaderinc"), "float lift(float v) {\n\treturn v * scale;\n}\n");
        MainWindow window = Open("warned.gdshader");
        Ui.WaitFor(() => Marked(window, "The local variable 'unused_local' is declared but never used.", "float unused_local", MarkerKind.Warning),
            () => $"the unused local warned of: {Said(window)}");
        Assert.True(window.EditorArea.Active!.Markers.Count == 1, $"The project turns unused uniforms off, so the unused local alone was to be warned of: {Said(window)}");

        CodeEditor editor = window.EditorArea.CodeEditor;
        window.MouseMove(new Point(0, 0), RawInputModifiers.None);
        int at = editor.Document.Snapshot.ToString().IndexOf("speed;", StringComparison.Ordinal) + 1;
        window.MouseMove(editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value, RawInputModifiers.None);
        Ui.WaitFor(() => window.Assist.HoverText is not null, "the card on speed");
        Assert.Contains("Godot reads as float, in 0.0 to 4.0, step 0.5", window.Assist.HoverText!, StringComparison.Ordinal);

        Assert.Null(window.OpenFile(Path.Combine(folder, "lib", "helpers.gdshaderinc")));
        Ui.WaitFor(() => Marked(window, "Unknown identifier in expression: 'scale'.", "v * scale"), () => $"the include read alone, its unknown name marked: {Said(window)}");
        window.Close();
    }

    private void A_shader_opened_before_its_project_keeps_its_marks_as_typing_pauses_once_the_project_is_open()
    {
        TestFolders.Write(folder, GodotProject.FileName, "config_version=5\n\n[application]\n\nconfig/name=\"Several\"\n");
        TestFolders.Write(folder, "several.gdshader", Several);
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        Assert.Null(window.OpenFile(Path.Combine(folder, "several.gdshader")));
        Ui.Settle();
        window.OpenFolder(folder);
        Ui.Settle();
        Ui.WaitFor(() => Marked(window, "Unknown identifier in expression: 'speedd'.", "TIME * speedd"), () => $"the shader marked once its project opened: {Said(window)}");
        OpenDocument shader = window.EditorArea.Active!;
        List<int> marked = [];
        shader.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OpenDocument.Markers))
            {
                marked.Add(shader.Markers.Count);
            }
        };
        CodeEditor editor = window.EditorArea.CodeEditor;
        editor.Focus();
        editor.Selections = SelectionSet.Single(editor.Document.Snapshot.Length);
        Ui.Press(window, "enter");
        Ui.Settle();
        Assert.True(!marked.Contains(0) && Marked(window, "Unknown identifier in expression: 'speedd'.", "TIME * speedd"),
            $"The shader's marks went as its typing paused ({string.Join(", ", marked)} marks in turn), as the check of the window with no folder still read it: {Said(window)}");
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

    /// <summary>Whether the open file has a mark of these words, of a kind, on the line holding some text.</summary>
    private static bool Marked(MainWindow window, string words, string at, MarkerKind kind = MarkerKind.Error)
    {
        TextSnapshot text = window.EditorArea.CodeEditor.Document.Snapshot;
        int line = text.GetLineFromOffset(text.ToString().IndexOf(at, StringComparison.Ordinal));
        return window.EditorArea.Active!.Markers.Any(m => m.Message == words && m.Kind == kind && text.GetLineFromOffset(m.Range.Start) == line);
    }

    private static string Said(MainWindow window) =>
        $"{window.EditorArea.Active?.Name} has {string.Join(" | ", window.EditorArea.Active?.Markers.Select(m => $"{m.Kind} {m.Message}") ?? [])} ({window.Status})";
}
