using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A GDScript's completion from Passenger's own reading of it, Godot's server held at its start where a Godot is
/// found (never started where none is): keywords where a statement or a class's body takes them and annotations by place,
/// the engine's globals and utility functions with a call's parentheses put in, node paths from the scenes running the
/// script, the strings Godot's calls take by kind, the engine's virtual functions after <c>func</c> put in whole, and type
/// names after a colon, an arrow and <c>extends</c>; and the same lists once Godot's server serves the script. A Godot
/// editor of its own each where one is found, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptCompletionTests
{
    private const string Script = "extends Node\n\nsignal scored(points: int)\n\n@export var speed := 2.0\nvar counter := 0\n\n\n"
        + "func _ready() -> void:\n\tcounter += 1\n";

    private const string Scene = "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://main.gd\" id=\"1\"]\n\n"
        + "[node name=\"Main\" type=\"Node\"]\nscript = ExtResource(\"1\")\n\n[node name=\"Title\" type=\"Label\" parent=\".\"]\n\n"
        + "[node name=\"Player\" type=\"Node2D\" parent=\".\"]\n\n[node name=\"Sprite\" type=\"Sprite2D\" parent=\"Player\"]\n\n"
        + "[node name=\"Health\" type=\"ProgressBar\" parent=\"Player\"]\nunique_name_in_owner = true\n";

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GDScriptCompletionTests()
    {
        folder = TestFolders.Create("gdscript-completion-");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Completes\"\n\n"
            + "[input]\n\njump={\n\"deadzone\": 0.5,\n\"events\": []\n}\n");
        TestFolders.Write(folder, "main.gd", Script);
        TestFolders.Write(folder, "main.tscn", Scene);
        TestFolders.Write(folder, "enemy.gd", "class_name Enemy\nextends Node2D\n");
    }

    [AvaloniaFact]
    public void Keywords_complete_where_a_statement_or_a_class_body_takes_them_and_annotations_by_place()
    {
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            TypeName(window, "retu");
            Listed(window, "return", "the return keyword at a statement's start");
            Ui.Press(window, "ESCAPE", settle: false);
            Erase(window, "retu".Length);
            TypeName(window, "@");
            Listed(window, "@warning_ignore", "a statement's annotation after its @");
            Assert.DoesNotContain("@export", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE", settle: false);

            NewLineAfter(window, editor, "var counter := 0");
            ClearLine(window, editor);
            TypeName(window, "@export_r");
            First(window, "@export_range", "a member's annotation at the class's level");
            Ui.Press(window, "ENTER", settle: false);
            Ui.PumpUntil(() => document.Text.Snapshot.ToString().Contains("\n@export_range\n", StringComparison.Ordinal), "@export_range put in, its @ written once",
                TimeSpan.FromSeconds(10));
            NewLineAfter(window, editor, "\n@export_range");
            ClearLine(window, editor);
            TypeName(window, "si");
            Listed(window, "signal", "a declaration's keyword at the class's level");
            Assert.DoesNotContain("return", window.Assist.CompletionLabels);
            Assert.True(window.Session.Web?.For(document) is not { Serving: true }, "Godot's server answered: the test's hold of its start did not hold");
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>The engine's utility functions, built-in types, classes, singletons and global constants complete beside the
    /// script's own names, and a function goes in with its call's parentheses, the caret inside them where it takes arguments.</summary>
    [AvaloniaFact]
    public void The_engines_globals_complete_beside_the_scripts_names_and_a_function_goes_in_with_its_parentheses()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): the engine's globals come from its API");
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            foreach ((string typed, string expected) in new[] { ("Vecto", "Vector2"), ("Inpu", "Input"), ("KEY_ESC", "KEY_ESCAPE"), ("cou", "counter") })
            {
                TypeName(window, typed);
                Listed(window, expected, $"{expected} after {typed}");
                Ui.Press(window, "ESCAPE", settle: false);
                Erase(window, typed.Length);
            }
            TypeName(window, "prin");
            First(window, "print", "the engine's print");
            Ui.Press(window, "ENTER", settle: false);
            Ui.PumpUntil(() => document.Text.Snapshot.ToString().Contains("\tprint()\n", StringComparison.Ordinal), "print() put in", TimeSpan.FromSeconds(10));
            int line = editor.CaretPosition.Line;
            Assert.Equal("\tprint(", editor.Document.Snapshot.GetLineText(line)[..editor.CaretPosition.Column]);
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>After <c>$</c> the paths the scene running the script gives from its node, after <c>%</c> its unique names,
    /// each with the node's class and scene, a chosen one put in whole.</summary>
    [AvaloniaFact]
    public void A_node_path_completes_from_the_scenes_running_the_script()
    {
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            TypeName(window, "$");
            Listed(window, "$Title", "the scene's nodes after $");
            Assert.Contains("$Player/Sprite", window.Assist.CompletionLabels);
            Assert.Contains(window.Assist.CompletionDetails, d => d.Label == "$Title" && d.Detail.Contains("Label", StringComparison.Ordinal) && d.Detail.Contains("main.tscn", StringComparison.Ordinal));
            window.KeyTextInput("Player/Sp");
            First(window, "$Player/Sprite", "the list narrowed through the path's slash");
            Ui.Press(window, "ENTER", settle: false);
            Ui.PumpUntil(() => document.Text.Snapshot.ToString().Contains("\t$Player/Sprite\n", StringComparison.Ordinal), "$Player/Sprite put in", TimeSpan.FromSeconds(10));
            Ui.Press(window, "ESCAPE", settle: false);
            NewLineAfter(window, editor, "\t$Player/Sprite");
            TypeName(window, "%");
            Listed(window, "%Health", "the scene's unique names after %");
            Assert.DoesNotContain("$Title", window.Assist.CompletionLabels);
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>A string Godot's call takes completes what it names: an input action of project.godot's, a node's path from
    /// the script's node, a file's res:// path, a signal of the script's own; the item chosen takes the string's text.</summary>
    [AvaloniaFact]
    public void A_string_godots_call_takes_completes_the_names_of_its_kind()
    {
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            foreach ((string call, string expected) in new[]
            {
                ("Input.is_action_pressed(", "jump"), ("get_node(", "Player/Sprite"), ("load(", "res://main.tscn"), ("emit_signal(", "scored"),
            })
            {
                TypeOverLine(window, editor, call + "\"\")");
                Ui.PlaceAfter(editor, "\t" + call + "\"");
                TypeName(window, expected[..2]);
                Listed(window, expected, $"{expected} in {call}'s string");
                Ui.Press(window, "ESCAPE", settle: false);
            }
            Ui.PlaceAfter(editor, "\temit_signal(\"sc");
            Ui.Press(window, Ui.Shortcut(AppActions.Complete), settle: false);
            First(window, "scored", "the script's signal in emit_signal's string again");
            Ui.Press(window, "ENTER", settle: false);
            Ui.PumpUntil(() => document.Text.Snapshot.ToString().Contains("\temit_signal(\"scored\")", StringComparison.Ordinal), "the signal's name put in the string", TimeSpan.FromSeconds(10));
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>After <c>func</c> at a class's level the engine's virtual functions the class may override, one chosen put in
    /// whole with its parameters, what it returns and its colon; one the script defines already is not offered.</summary>
    [AvaloniaFact]
    public void After_func_the_engines_virtual_functions_complete_whole()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): the virtual functions come from its API");
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            Ui.Press(window, "ENTER", settle: false);
            ClearLine(window, editor);
            window.KeyTextInput("func ");
            TypeName(window, "_pro");
            First(window, "_process", "Node's _process after func");
            Assert.DoesNotContain("_ready", window.Assist.CompletionLabels);
            Ui.Press(window, "ENTER", settle: false);
            Ui.PumpUntil(() => document.Text.Snapshot.ToString().Contains("func _process(delta: float) -> void:", StringComparison.Ordinal),
                "_process put in whole", TimeSpan.FromSeconds(10));
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>A type's name completes after a variable's colon (the project's class_name among them), void after a
    /// function's arrow, and a class after extends; a name being declared after var opens no list.</summary>
    [AvaloniaFact]
    public void Type_names_complete_after_a_colon_an_arrow_and_extends()
    {
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            window.KeyTextInput("var foe: ");
            TypeName(window, "Ene");
            Listed(window, "Enemy", "the project's class_name after a variable's colon");
            Ui.Press(window, "ESCAPE", settle: false);
            TypeOverLine(window, editor, "var ");
            TypeName(window, "f");
            Assert.False(Ui.Pumped(() => window.Assist.IsCompletionOpen, TimeSpan.FromSeconds(2)), $"A name being declared opened a list: {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
            Ui.PlaceAfter(editor, "func _ready() -> ");
            Ui.Press(window, "shift END", settle: false);
            TypeName(window, "vo");
            Listed(window, "void", "void after a function's arrow");
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>Once Godot's server serves the script, the lists are still Passenger's own: the scene's nodes after <c>$</c>
    /// with their classes and scenes, and the input action in a string, as before it started.</summary>
    [AvaloniaFact]
    public void Passengers_lists_answer_while_godots_server_serves_the_script()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): Godot's server runs in its editor");
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            release.SetResult();
            Ui.PumpUntil(() => window.Session.Web?.For(document) is { Serving: true }, "Godot's server serving the script", TimeSpan.FromSeconds(90));
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            TypeName(window, "$");
            Listed(window, "$Player/Sprite", "the scene's nodes after $ beside Godot's server");
            Assert.Contains(window.Assist.CompletionDetails, d => d.Label == "$Title" && d.Detail.Contains("main.tscn", StringComparison.Ordinal));
            Ui.Press(window, "ESCAPE", settle: false);
            TypeOverLine(window, editor, "Input.is_action_pressed(\"\")");
            Ui.PlaceAfter(editor, "is_action_pressed(\"");
            TypeName(window, "ju");
            Listed(window, "jump", "project.godot's action beside Godot's server");
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>A new line after a line of the script's, the caret at its end.</summary>
    private static void NewLineAfter(MainWindow window, CodeEditor editor, string line)
    {
        Ui.PlaceAfter(editor, line);
        editor.Focus();
        Ui.Press(window, "ENTER", settle: false);
    }

    /// <summary>The list's best item, the one Enter puts in, waited for with the window's loop run.</summary>
    private static void First(MainWindow window, string label, string what)
    {
        bool first = Ui.Pumped(() => window.Assist.CompletionLabels.FirstOrDefault() == label, TimeSpan.FromSeconds(60));
        Assert.True(first, $"No {what} first in the list: it held {string.Join(", ", window.Assist.CompletionLabels.Take(20))}");
    }

    /// <summary>A name typed as a person types it: its first character alone, which opens the list, then the rest, which
    /// narrows it.</summary>
    private static void TypeName(MainWindow window, string name)
    {
        window.KeyTextInput(name[..1]);
        if (name.Length > 1)
        {
            window.KeyTextInput(name[1..]);
        }
    }

    /// <summary>The caret's line's text after its indentation typed over as one input, which opens no list.</summary>
    private static void TypeOverLine(MainWindow window, CodeEditor editor, string text)
    {
        TextSnapshot snapshot = editor.Document.Snapshot;
        int line = editor.CaretPosition.Line;
        string written = snapshot.GetLineText(line);
        int indent = written.Length - written.TrimStart('\t', ' ').Length;
        editor.Select(new TextRange(snapshot.GetLineStart(line) + indent, written.Length - indent));
        window.KeyTextInput(text);
    }

    /// <summary>The characters before the caret taken back, a key each.</summary>
    private static void Erase(MainWindow window, int count)
    {
        for (int i = 0; i < count; i++)
        {
            Ui.Press(window, "BACK_SPACE", settle: false);
        }
    }

    /// <summary>The caret's line emptied.</summary>
    private static void ClearLine(MainWindow window, CodeEditor editor)
    {
        TextSnapshot snapshot = editor.Document.Snapshot;
        int line = editor.CaretPosition.Line;
        if (snapshot.GetLineText(line).Length > 0)
        {
            editor.Select(new TextRange(snapshot.GetLineStart(line), snapshot.GetLineText(line).Length));
            Ui.Press(window, "BACK_SPACE", settle: false);
        }
    }

    /// <summary>The list holding an item, waited for with the window's loop run (Godot's server's held start keeps it from settling).</summary>
    private static void Listed(MainWindow window, string label, string what)
    {
        bool listed = Ui.Pumped(() => window.Assist.CompletionLabels.Contains(label), TimeSpan.FromSeconds(60));
        Assert.True(listed, $"No {what}: the list held {string.Join(", ", window.Assist.CompletionLabels.Take(20))}");
    }

    private (MainWindow Window, OpenDocument Document, TaskCompletionSource Release) Open()
    {
        MainWindow window = new()
        {
            Width = 1200,
            Height = 800,
            StartsLanguageServer = false,
            GodotEnvironment = GodotTests.Game.Home(folder + "-home"),
            GodotHeadlessAtOpen = true,
        };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? held = godot is { Exists: true } ? window.Session.Web?.HoldNextConfigure(release.Task) : null;
        Assert.Null(window.OpenFile(Path.Combine(folder, "main.gd")));
        if (held is not null)
        {
            Ui.AwaitPumped(held, "Godot's GDScript server held at its start", TimeSpan.FromMinutes(1));
        }
        return (window, window.EditorArea.Active!, release);
    }
}
