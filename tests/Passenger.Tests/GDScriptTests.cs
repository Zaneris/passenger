using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>GDScript through Godot's own language server, in the headless Godot editor Passenger starts for a project the
/// test writes (its user folders the run's): a script is coloured, completes a Godot class's member, hovers its docs and
/// goes to a declaration, keeping the project's editor layout as it was; an error typed shows in Problems as typed, a
/// warning with its name; and a project deleted under that editor is let go of, said once. Without a Godot (GODOT, the PATH, Applications) each test skips saying why. A Godot editor of
/// its own each, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptTests
{
    private const string Script = "extends Node2D\n\n## How fast it spins.\n@export var speed: float = 2.5\nvar counter := 0\n\n\n"
        + "func _process(delta: float) -> void:\n\tcounter += 1\n\tvar doubled := twice(counter)\n\tif doubled > 100:\n\t\tget_tree()\n\n\n"
        + "func twice(value: int) -> int:\n\treturn value * 2\n";

    /// <summary>The person's own editor layout, which the headless editor would write its own over.</summary>
    private const string Layout = "[docks]\n\nmine=1\n";

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GDScriptTests()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): install Godot 4 to run these tests");
        folder = TestFolders.Create("gdscript-");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Turns\"\n"
            + "run/main_scene=\"res://main.tscn\"\n");
        TestFolders.Write(folder, "main.tscn", "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://main.gd\" id=\"1\"]\n\n"
            + "[node name=\"Main\" type=\"Node2D\"]\nscript = ExtResource(\"1\")\n");
        TestFolders.Write(folder, "main.gd", Script);
        TestFolders.Write(folder, Path.Combine(".godot", "editor", "editor_layout.cfg"), Layout);
    }

    [AvaloniaFact]
    public void A_script_is_coloured_completes_a_Godot_classs_member_hovers_its_docs_and_goes_to_a_declaration()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        Assert.Equal(TokenKind.Keyword, KindOf(editor, "func"));
        string layout = Path.Combine(folder, ".godot", "editor", "editor_layout.cfg");
        Ui.WaitFor(() => File.ReadAllText(layout) == Layout, () => $"the project's own editor layout put back over the headless editor's:\n{File.ReadAllText(layout)}");
        string card = Hover(window, editor, "get_tree");
        Assert.Contains("func Node.get_tree() -> SceneTree", card, StringComparison.Ordinal);
        Assert.Contains("Returns the SceneTree that contains this node", card.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "\t\tget_tree()");
        editor.Focus();
        window.KeyTextInput(".");
        Ui.WaitFor(() => window.Assist.CompletionLabels.Any(l => l.StartsWith("change_scene_to_file", StringComparison.Ordinal)),
            () => $"SceneTree's members after get_tree()'s dot; the list has {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
        window.KeyTextInput("change_scene_to_f");
        Ui.WaitFor(() => window.Assist.CompletionLabels.FirstOrDefault()?.StartsWith("change_scene_to_file", StringComparison.Ordinal) == true,
            () => $"the list narrowed to change_scene_to_file; it has {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
        Ui.Press(window, "ENTER");
        Ui.WaitFor(() => editor.Document.Snapshot.ToString().Contains("\t\tget_tree().change_scene_to_file(", StringComparison.Ordinal),
            () => $"the member put in; the line is {editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line)}");
        Ui.Press(window, "ESCAPE");


        Ui.PlaceAfter(editor, "var doubled := tw");
        Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration));
        int declared = Script.Split('\n').ToList().FindIndex(l => l.StartsWith("func twice", StringComparison.Ordinal));
        Ui.WaitFor(() => editor.CaretPosition.Line == declared, () => $"the caret at twice's declaration, line {declared + 1}; it is at {editor.CaretPosition.Line + 1}");
        Ui.WaitFor(() => File.ReadAllText(layout) == Layout, () => $"the project's own editor layout kept while the headless editor runs:\n{File.ReadAllText(layout)}");
        window.Close();
    }

    [AvaloniaFact]
    public void An_error_typed_shows_in_Problems_as_typed_and_a_warning_with_its_name()
    {
        MainWindow window = Open();
        CodeEditor editor = window.EditorArea.CodeEditor;
        ProblemsModel problems = window.BottomPanel.Problems;
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Warning),
            () => $"the warning of _process's unused parameter; Problems has {string.Join("; ", problems.FileProblems.Select(p => p.Message))}");
        Problem unused = problems.FileProblems.First(p => p.Severity == ProblemSeverity.Warning);
        Assert.StartsWith("UNUSED_PARAMETER: The parameter \"delta\" is never used", unused.Message, StringComparison.Ordinal);

        Ui.PlaceAfter(editor, "\treturn value * 2");
        editor.Focus();
        window.KeyTextInput(" +");
        Ui.WaitFor(() => problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error),
            () => $"the error of the expression left unfinished; Problems has {string.Join("; ", problems.FileProblems.Select(p => p.Message))}");
        Problem error = problems.FileProblems.First(p => p.Severity == ProblemSeverity.Error);
        Assert.Contains("Expected expression after \"+\" operator", error.Message, StringComparison.Ordinal);
        Ui.Press(window, "BACK_SPACE");
        Ui.Press(window, "BACK_SPACE");
        Ui.WaitFor(() => !problems.FileProblems.Any(p => p.Severity == ProblemSeverity.Error),
            () => $"the error gone with what made it; Problems has {string.Join("; ", problems.FileProblems.Select(p => p.Message))}");
        window.Close();
    }

    /// <summary>A project deleted under the headless editor Passenger started for it, in that editor's first seconds, ends the
    /// putting back of what the editor writes there, said once rather than at every look.</summary>
    [AvaloniaFact]
    public void A_project_deleted_under_its_headless_editor_is_let_go_of_said_once()
    {
        MainWindow window = Open();
        string project = PathNames.Canonical(folder);
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // Windows keeps a folder the editor works in, and what it holds open: the project.godot goes all the same.
            File.Delete(Path.Combine(folder, "project.godot"));
        }
        // The project as the log names it, by the path its script was opened by or its real one.
        string[] Said() => [.. Ui.Shared(IdeLog.PathOf(IdeLog.Ide)).Split('\n')
            .Where(l => l.Contains("GDScript: ", StringComparison.Ordinal) && (l.Contains(project, StringComparison.Ordinal) || l.Contains(folder, StringComparison.Ordinal)))];
        static bool Gone(string line) => line.Contains(" is gone, so what Godot's headless editor wrote there is no longer put back", StringComparison.Ordinal);
        Ui.WaitFor(() => Said().Any(Gone), () => $"the IDE's log to say {project} is gone ({string.Join(" | ", Said())})");
        Ui.Hold(TimeSpan.FromSeconds(1), "what the headless editor writes looked at again, as it is every 200 ms in its first seconds");
        // A file the deletion held a moment (Windows) may be said before; nothing is tried once the going is said.
        string[] said = Said();
        Assert.True(said.Count(Gone) == 1 && Gone(said[^1]),
            $"The project's going was to be said once, and nothing tried there after; the log says:\n{string.Join('\n', said)}");
        window.Close();
    }

    /// <summary>The project's folder open on its script, once Godot's server answers for it.</summary>
    private MainWindow Open()
    {
        MainWindow window = new()
        {
            Width = 1200,
            Height = 800,
            StartsLanguageServer = false,
            GodotEnvironment = GodotTests.Game.Home(folder + "-home"),
        };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        Assert.Null(window.OpenFile(Path.Combine(folder, "main.gd")));
        Ui.Settle();
        OpenDocument document = window.EditorArea.Active!;
        Ui.WaitFor(() => window.Session.Web?.For(document)?.Lsp is not null && window.Session.Web.StateOf(document) is null,
            () => $"Godot's server for {document.Name}: {window.Session.Web?.StateOf(document)} {document.Notice}", TimeSpan.FromSeconds(90));
        return window;
    }

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

    private static TokenKind? KindOf(CodeEditor editor, string stretch)
    {
        TextSnapshot text = editor.Document.Snapshot;
        int start = text.ToString().IndexOf(stretch, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{stretch}' is not in the text");
        return editor.Highlighter!.GetSpans(text, start, stretch.Length).Where(s => s.Start <= start && s.End > start).Select(s => (TokenKind?)s.Kind).FirstOrDefault();
    }
}
