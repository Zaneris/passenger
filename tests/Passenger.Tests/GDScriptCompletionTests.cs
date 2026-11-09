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

    /// <summary>After self's dot, and among the names in scope, the script's own members come before those its engine class
    /// gives it, however their names sort; a function returning nothing says void.</summary>
    [AvaloniaFact]
    public void A_scripts_own_members_are_listed_before_those_its_engine_class_gives_it()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): the engine's members come from its API");
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            window.KeyTextInput("self.");
            bool listed = Ui.Pumped(() => window.Assist.CompletionLabels.Count > 3, TimeSpan.FromSeconds(60));
            Assert.True(listed, "No members after self's dot");
            Assert.True(window.Assist.CompletionLabels.Take(3).OrderBy(l => l, StringComparer.Ordinal).SequenceEqual(["counter", "scored", "speed"]),
                $"The script's own members are not first after self's dot: {string.Join(", ", window.Assist.CompletionLabels.Take(12))}");
            Assert.Contains(("_ready", "void"), window.Assist.CompletionDetails);
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>After a class's name and its dot the list holds what the class has itself, its static members and constants,
    /// and nothing Godot rejects there, an instance's variables, functions and signals; a value of the class has them all.</summary>
    [AvaloniaFact]
    public void After_a_class_name_the_list_holds_what_the_class_has_and_nothing_only_an_instance_has()
    {
        TestFolders.Write(folder, "worker.gd", "class_name Worker\nextends Node\n\nsignal finished\n\nconst LIMIT := 3\n\nvar instance_field := 1\n\n\n"
            + "func instance_fn() -> int:\n\treturn 1\n\n\nstatic func static_fn() -> int:\n\treturn 2\n");
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            window.KeyTextInput("Worker");
            window.KeyTextInput(".");
            Listed(window, "static_fn", "static function after the class's dot");
            Assert.Contains("LIMIT", window.Assist.CompletionLabels);
            Assert.Empty(window.Assist.CompletionLabels.Intersect(["instance_field", "instance_fn", "finished"]));
            Ui.Press(window, "ESCAPE", settle: false);

            TypeOverLine(window, editor, "Worker.new()");
            window.KeyTextInput(".");
            Listed(window, "instance_field", "instance's variable after a value of the class");
            Assert.Contains("instance_fn", window.Assist.CompletionLabels);
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>After a class's name <c>new</c> goes in with its parentheses, the caret inside them where its constructor takes
    /// arguments and after them where it takes none; a class Godot refuses to make lists none.</summary>
    [AvaloniaFact]
    public void After_a_class_name_new_goes_in_with_its_parentheses_and_an_abstract_class_lists_none()
    {
        TestFolders.Write(folder, "worker.gd", "class_name Worker\nextends Node\n");
        TestFolders.Write(folder, "boss.gd", "class_name Boss\nextends Node\n\n\nfunc _init(rank: int) -> void:\n\tpass\n");
        TestFolders.Write(folder, "shape.gd", "@abstract\nclass_name Shape\nextends RefCounted\n\nconst SIDES := 3\n");
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            window.KeyTextInput("Shape");
            window.KeyTextInput(".");
            Listed(window, "SIDES", "constant after an abstract class's dot");
            Assert.DoesNotContain("new", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE", settle: false);

            foreach ((string name, string written, string beforeCaret) in new[] { ("Worker", "\tWorker.new()\n", "\tWorker.new()"), ("Boss", "\tBoss.new()\n", "\tBoss.new(") })
            {
                TypeOverLine(window, editor, name);
                window.KeyTextInput(".");
                Listed(window, "new", $"new after {name}'s dot");
                Assert.Equal("new", window.Assist.CompletionLabels[0]);
                window.KeyTextInput("ne");
                First(window, "new", $"new as {name}'s dot is typed past");
                Ui.Press(window, "ENTER", settle: false);
                Ui.PumpUntil(() => document.Text.Snapshot.ToString().Contains(written, StringComparison.Ordinal), $"{written.Trim()} put in", TimeSpan.FromSeconds(10));
                Assert.Equal(beforeCaret, editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line)[..editor.CaretPosition.Column]);
            }
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>In a static function the list holds what needs no instance, the class's static members, and none of what Godot
    /// rejects there, an instance's variables, functions and signals and <c>self</c>; a function beside it has them all.</summary>
    [AvaloniaFact]
    public void A_static_function_completes_only_what_needs_no_instance()
    {
        TestFolders.Write(folder, "main.gd", "extends Node\n\nsignal spent\n\nstatic var shared_count := 1\nvar stock := 1\n\n\n"
            + "func spend() -> void:\n\tprint(1)\n\n\nstatic func spawn() -> void:\n\tprint(2)\n");
        (MainWindow window, _, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "print(2)");
            TypeName(window, "s");
            Listed(window, "shared_count", "static variable in a static function");
            Assert.Contains("spawn", window.Assist.CompletionLabels);
            Assert.Empty(window.Assist.CompletionLabels.Intersect(["stock", "spend", "spent", "self"]));
            Ui.Press(window, "ESCAPE", settle: false);
            Erase(window, 1);

            NewLineAfter(window, editor, "print(1)");
            TypeName(window, "s");
            Listed(window, "stock", "instance's variable in a function");
            Assert.Superset(new HashSet<string> { "spend", "spent", "self", "shared_count", "spawn" }, window.Assist.CompletionLabels.ToHashSet());
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>An engine class's enum values complete in a script built on the class, and a built-in type's enum and its values
    /// after the type's name.</summary>
    [AvaloniaFact]
    public void An_engine_enums_values_complete_in_a_script_on_its_class_and_after_a_built_in_types_name()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): the engine's enums come from its API");
        (MainWindow window, _, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            TypeName(window, "PROCESS_MODE_INH");
            Listed(window, "PROCESS_MODE_INHERIT", "value of an enum of the class the script is built on");
            Ui.Press(window, "ESCAPE", settle: false);
            Erase(window, "PROCESS_MODE_INH".Length);

            window.KeyTextInput("Vector2");
            window.KeyTextInput(".");
            Listed(window, "AXIS_X", "value of a built-in type's enum after its name");
            Assert.Contains("Axis", window.Assist.CompletionLabels);
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>After a script's enum by its name the list holds its values and the Dictionary functions Godot lets it call, a
    /// function that takes arguments going in with the caret between its parentheses.</summary>
    [AvaloniaFact]
    public void After_a_scripts_enum_the_list_holds_its_values_and_a_constant_dictionarys_functions()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): Dictionary's functions come from the engine's API");
        TestFolders.Write(folder, "main.gd", "extends Node\n\nenum Mood { CALM }\n\n\nfunc _ready() -> void:\n\tprint(1)\n");
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "print(1)");
            window.KeyTextInput("Mood");
            window.KeyTextInput(".");
            Listed(window, "CALM", "value of the enum");
            Assert.Superset(new HashSet<string> { "keys", "size", "has" }, window.Assist.CompletionLabels.ToHashSet());
            Assert.DoesNotContain("clear", window.Assist.CompletionLabels);
            window.KeyTextInput("has");
            First(window, "has", "Dictionary's has after the enum's dot");
            Ui.Press(window, "ENTER", settle: false);
            Ui.PumpUntil(() => document.Text.Snapshot.ToString().Contains("\tMood.has()\n", StringComparison.Ordinal), "Mood.has() put in", TimeSpan.FromSeconds(10));
            Assert.Equal("\tMood.has(", editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line)[..editor.CaretPosition.Column]);
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>A constant's value and an enum's list a constant and none of what Godot refuses there, a variable, a function, a
    /// signal, <c>self</c>; a variable's value beside them lists them all.</summary>
    [AvaloniaFact]
    public void A_constants_value_and_an_enums_complete_only_what_godot_folds()
    {
        TestFolders.Write(folder, "main.gd", "extends Node\n\nsignal spent\n\nconst steps := 1\nvar stock := 1\n\n\nfunc spend() -> void:\n\tprint(1)\n");
        (MainWindow window, _, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "var stock := 1");
            foreach (string written in new[] { "const C = ", "enum { X = " })
            {
                TypeOverLine(window, editor, written);
                TypeName(window, "s");
                Listed(window, "steps", $"constant after {written}");
                Assert.Empty(window.Assist.CompletionLabels.Intersect(["stock", "spend", "spent", "self", "super"]));
                Ui.Press(window, "ESCAPE", settle: false);
            }

            TypeOverLine(window, editor, "var other = ");
            TypeName(window, "s");
            Listed(window, "stock", "variable in a variable's value");
            Assert.Superset(new HashSet<string> { "steps", "spend", "spent", "self" }, window.Assist.CompletionLabels.ToHashSet());
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>A function beside a static one lists the scene's nodes after <c>$</c>; the static one, where Godot refuses it, none.</summary>
    [AvaloniaFact]
    public void A_static_function_offers_no_node_paths()
    {
        TestFolders.Write(folder, "main.gd", "extends Node\n\n\nfunc _ready() -> void:\n\tprint(1)\n\n\nstatic func spawn() -> void:\n\tprint(2)\n");
        (MainWindow window, _, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "print(1)");
            window.KeyTextInput("$");
            Listed(window, "$Player", "node of the scene after $");
            Ui.Press(window, "ESCAPE", settle: false);
            Erase(window, 1);

            NewLineAfter(window, editor, "print(2)");
            window.KeyTextInput("$");
            bool listed = Ui.Pumped(() => window.Assist.CompletionLabels.Count > 0, TimeSpan.FromSeconds(3));
            Assert.False(listed, $"Node paths offered in a static function, where Godot refuses $: {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>An enum's value lists the values before it, a named enum's too, and neither itself nor what comes after; inside a
    /// call the integer can be made from any constant, so <c>PI</c> is listed there and only there, and <c>as</c> beside a value.</summary>
    [AvaloniaFact]
    public void An_enums_value_lists_the_values_before_it_and_a_call_in_it_takes_any_constant()
    {
        TestFolders.Write(folder, "main.gd", "extends Node\n\nconst PINE := 1\nvar stock := 1\n");
        (MainWindow window, _, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "var stock := 1");
            TypeOverLine(window, editor, "enum Named { Alpha = 1, Beta = ");
            TypeName(window, "a");
            Listed(window, "Alpha", "the value before");
            Assert.DoesNotContain("Beta", window.Assist.CompletionLabels);
            Assert.DoesNotContain("Named", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE", settle: false);

            TypeOverLine(window, editor, "enum { X = int(");
            TypeName(window, "P");
            Listed(window, "PI", "a float in a call");
            Ui.Press(window, "ESCAPE", settle: false);

            TypeOverLine(window, editor, "enum { X = ");
            TypeName(window, "P");
            Listed(window, "PINE", "an integer constant");
            Assert.DoesNotContain("PI", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE", settle: false);

            TypeOverLine(window, editor, "enum { X = 1 ");
            TypeName(window, "a");
            Listed(window, "as", "a cast after a value");
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>After a closing bracket the list holds what the bracket closes, a call's result rather than its last argument's
    /// type, and a group's parentheses list what they hold.</summary>
    [AvaloniaFact]
    public void After_a_closing_bracket_the_list_holds_what_the_bracket_closes()
    {
        TestFolders.Write(folder, "worker.gd", "class_name Worker\nextends Node\n\nvar instance_field := 1\n\n\nfunc twin(other: Vector2) -> Worker:\n\treturn self\n");
        (MainWindow window, _, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            window.KeyTextInput("Worker.new().twin(Vector2.ZERO)");
            window.KeyTextInput(".");
            Listed(window, "instance_field", "variable of what the call gives after its closing parenthesis");
            Ui.Press(window, "ESCAPE", settle: false);

            TypeOverLine(window, editor, "(Worker.new())");
            window.KeyTextInput(".");
            Listed(window, "instance_field", "variable of what a group holds after its closing parenthesis");
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>After a member read off a class or a value the list holds that member's own, however many dots came before.</summary>
    [AvaloniaFact]
    public void After_a_member_read_off_another_the_list_holds_what_that_member_has()
    {
        TestFolders.Write(folder, "worker.gd", "class_name Worker\nextends Node\n\nvar buddy: Worker\nvar instance_field := 1\n\n\n"
            + "class Inner:\n\tconst DEEP := 3\n\n\tstatic func make() -> int:\n\t\treturn 1\n");
        (MainWindow window, _, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            window.KeyTextInput("Worker.Inner");
            window.KeyTextInput(".");
            Listed(window, "make", "inner class's static function after two dots");
            Assert.Contains("DEEP", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE", settle: false);

            TypeOverLine(window, editor, "Worker.new().buddy");
            window.KeyTextInput(".");
            Listed(window, "instance_field", "variable of the class a value's variable holds");
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>A local declared in a block is offered in that block and not after it, where Godot rejects it; a local around
    /// the block is offered at both.</summary>
    [AvaloniaFact]
    public void A_blocks_local_completes_in_its_block_and_not_after_it()
    {
        TestFolders.Write(folder, "main.gd", "extends Node\n\n\nfunc _ready() -> void:\n\tvar here_value := 2\n\tif here_value > 0:\n\t\tvar hidden_value := 1\n"
            + "\t\tprint(hidden_value)\n\tprint(1)\n");
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "print(hidden_value)");
            TypeName(window, "h");
            Listed(window, "hidden_value", "local of the block the caret is in");
            Assert.Contains("here_value", window.Assist.CompletionLabels);
            Ui.Press(window, "ESCAPE", settle: false);
            Erase(window, 1);

            NewLineAfter(window, editor, "print(1)");
            TypeName(window, "h");
            Listed(window, "here_value", "local around the block after it");
            Assert.DoesNotContain("hidden_value", window.Assist.CompletionLabels);
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>A name typed after a raw string whose backslash comes before another completes, the string closed at its
    /// quote as Godot closes it.</summary>
    [AvaloniaFact]
    public void A_name_typed_after_a_raw_string_ending_in_a_backslash_completes()
    {
        TestFolders.Write(folder, "main.gd", "extends Node\n\n\nfunc _ready() -> void:\n\tvar named_path := \"x\"\n\tvar p = r\"C:\\\\\" + \n");
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "r\"C:\\\\\" + ");
            editor.Focus();
            TypeName(window, "na");
            Listed(window, "named_path", "local after the raw string");
        }
        finally
        {
            window.Close();
            release.TrySetResult();
        }
    }

    /// <summary>A list opened on a name typed before it, by its key or as typing outruns the list, shows its first item's card
    /// as it opens, the item chosen before the list shows.</summary>
    [AvaloniaFact]
    public void A_list_opened_on_a_name_already_typed_shows_its_first_items_card_as_it_opens()
    {
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            NewLineAfter(window, editor, "\tcounter += 1");
            TypeOverLine(window, editor, "counte");
            Ui.Press(window, "control SPACE", settle: false);
            First(window, "counter", "the script's counter for what was typed");
            bool described = Ui.Pumped(() => window.Assist.ItemDocumentation?.Contains("var counter", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(30));
            Assert.True(described, $"The list opened on counte shows no card for its first item, counter, until another is chosen: {window.Assist.ItemDocumentation}");
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
