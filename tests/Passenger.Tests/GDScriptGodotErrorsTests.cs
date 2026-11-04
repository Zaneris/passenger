using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Passenger's own reading of a GDScript gives the errors Godot's own server gives on the same scripts, in the same
/// words on the same lines: a corpus of scripts, a family of Godot's analyzer's errors each (names not found, mismatched
/// types and calls, operators, casts and type tests, static misuse and collections, declarations, files lacked), opened in a window whose
/// Godot's server serves them, each script's errors from Godot's diagnostics and from Passenger's marks set beside each
/// other (an error Godot pushes twice at one place counted once). Without a Godot each test skips saying why; a Godot editor
/// of its own, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptGodotErrorsTests
{
    /// <summary>Names and members Godot does not find: undeclared names (with a Godot 3 name's rename hint), built-in types
    /// and global enums standing on their own, members known bases lack, engine enums used as values.</summary>
    internal const string Names = "extends Node\n\nenum Mode { A, B }\nvar speed := 1.0\n\n\nfunc _ready() -> void:\n"
        + "\tprint(undefined_name)\n\tprint(int)\n\tprint(Side)\n\tprint(Vector3.Axis)\n\tprint(Vector3.NOPE)\n\tprint(Mode.C)\n"
        + "\tvar v := Vector3()\n\tprint(v.w)\n\tprint(KinematicBody2D)\n\tprint(ProcessMode.PROCESS_MODE_INHERIT, Vector3.Axis.AXIS_X, Side.SIDE_LEFT, v.x, Vector3.ZERO)\n";

    /// <summary>Calls of functions Godot does not find: on self, on a member that is a value, a built-in value, an engine
    /// class, an enum, a local Callable, and super calls of a virtual function and of none.</summary>
    internal const string Calls = "extends Node\n\nenum Mode { A, B }\nvar speed := 1.0\nvar react := func(): pass\n\n\nfunc _ready() -> void:\n"
        + "\tmissing()\n\tspeed()\n\treact()\n\tvar v := Vector3()\n\tv.nope()\n\tNode.nope()\n\tMode.clear()\n\tMode.size()\n\tMode.nope()\n"
        + "\tvar local := func(): pass\n\tlocal()\n\tlocal.call()\n\tsuper()\n\tsuper.nope()\n\tprint(v.length(), int(2.5))\n\n\nfunc _process(_delta: float) -> void:\n\tsuper(_delta)\n";

    /// <summary>Types Godot does not find: in the scope, under a built-in type or an engine class, a member or local that
    /// holds no type, a local constant declared further on, and collection element types where they cannot go.</summary>
    internal const string Types = "extends Node\n\nvar speed := 1.0\nvar a: Missing\nvar b: speed\nvar c: Vector3.Nope\nvar d: Node.Nope\nvar e: ProcessMode\n"
        + "var f: int[int]\nvar g: Array[int, int]\n\n\nfunc _ready() -> void:\n\tvar h: Later\n\tconst Later = Mode\n\tvar i := 1\n\tvar j: i\n\n\nenum Mode { A }\n";

    /// <summary>Values of types their targets take none of, constants assigned, and calls given too few or too many
    /// arguments or a value as a function's that returns nothing.</summary>
    internal const string Mismatches = "extends Node\n\nenum MyEnum { A, B }\nenum Other { X, Y }\nconst LIMIT = 3\nvar count: int = \"three\"\nvar e: MyEnum = Other.X\n"
        + "var typed_float: float = 1\nvar n := null\n\n\nfunc takes(a: int, b: String = \"\") -> int:\n\treturn a + b.length()\n\n\nfunc nothing() -> void:\n\treturn 1\n\n\n"
        + "func gives() -> int:\n\treturn \"a\"\n\n\nfunc _ready() -> void:\n\tcount = \"four\"\n\tLIMIT = 4\n\tMyEnum.A = 1\n\tvar s: String = 5\n\ttakes()\n"
        + "\ttakes(1, \"b\", 3)\n\ttakes(\"x\")\n\tvar v = nothing()\n\tprint(len(\"x\"), s, v, typed_float, n)\n\te = Other.Y\n\tvar w: Vector2 = Vector2.ZERO\n\tw = Color.RED\n";

    /// <summary>Operators given types they take no pair of (constants folded by Godot said in its evaluation's words, an
    /// integer divided by zero, a negative shift), compound assignments, casts to a type nothing converts to, and type tests
    /// a value can never pass.</summary>
    internal const string Operators = "extends Node\n\nenum Mode { A, B }\nconst LIMIT = 3\nconst NAMES = [\"a\"]\nvar count: int = 0\n"
        + "var label: String = \"\"\nvar flag: bool = false\nvar items: Array[int] = []\nvar node: Node\n\n\n"
        + "func _ready() -> void:\n\tprint(2.2 << 4)\n\tprint(true + true)\n"
        + "\tprint({\"hello\": \"world\"} + {\"godot\": \"engine\"})\n\tprint(\"hello\" + [\"world\"])\n\tprint(count + label)\n"
        + "\tprint(flag * 2)\n\tprint(LIMIT + \"x\")\n\tprint(count == label)\n\tprint(1 == \"1\")\n\tprint(-label)\n"
        + "\tprint(~1.5)\n\tprint(1 / 0)\n\tprint(5 % 0)\n\tprint(-1 << 2)\n\tprint(1 in 2)\n\tprint(NAMES + 1)\n"
        + "\tprint(Vector2(1, 2) + 1)\n\tprint(node + 1)\n\tprint(node == 1)\n\tprint(Mode + 1)\n\tprint(\"a\" and true)\n"
        + "\tprint(count == null, label % count, -Mode.A, count in items, items + [1], node in items, node != null, not node)\n"
        + "\tflag += \"bar\"\n\tcount -= label\n\tcount += 1\n\tprint(count as Array)\n\tprint(node as int)\n\tprint(1 as Array)\n"
        + "\tprint(Mode.A as String)\n\tprint(node as Resource)\n"
        + "\tprint(label as int, count as Mode, Mode.A as int, node as Node2D, 1.5 as int)\n\tprint(\"a\" as int)\n"
        + "\tprint(count is String)\n\tprint(node is Resource)\n\tprint(LIMIT is float)\n\tprint(count is Missing)\n"
        + "\tprint(Mode.A is String)\n\tprint(items is Array[String])\n\tprint(node is Node2D, Mode.A is int, items is Array)\n"
        + "\tvar local := 1\n\tprint(local + \"x\")\n\tconst LOCAL := 2\n\tprint(LOCAL is String)\n";

    /// <summary>Static places reaching the instance (a static function, a static variable's value and setter, a lambda in
    /// them), functions no instance is needed for called on classes, indices, loops and typed collections written out that
    /// Godot refuses.</summary>
    internal const string Statics = "extends Node\n\nsignal fired\nenum Mode { A, B }\nvar count: int = 0\nvar items: Array[int] = []\n"
        + "var table: Dictionary[String, int] = {}\nvar keyed: Dictionary[int, int] = {}\nvar any_keyed: Dictionary[Variant, int] = {}\n"
        + "var label: String = \"\"\n"
        + "var flag: bool = false\nvar node: Node\nvar colour := Color.RED\nstatic var shared := 1\n"
        + "static var from_func = non_static_func()\nstatic var callable_value = non_static_func\nstatic var with_setter:\n"
        + "\tset(value):\n\t\tprint(count)\n\t\tnon_static_func()\n\n\nfunc non_static_func() -> int:\n\treturn 1\n\n\n"
        + "static func static_func() -> void:\n\tnon_static_func()\n\tprint(count)\n\tprint(fired)\n\tprint(non_static_func)\n"
        + "\tprint(shared)\n\tprint(name)\n\tget_child_count()\n\tprint($Child)\n\tvar f := func():\n\t\tnon_static_func()\n"
        + "\tf.call()\n\tMode.size()\n\n\nfunc _ready() -> void:\n\tVector2.length()\n\tNode.get_name()\n\tInner.method()\n"
        + "\tInner.static_method()\n\tNode.new()\n\tprint(items[label])\n\tprint(items[1.5])\n\tprint(items[Mode.A])\n"
        + "\tprint(label[flag])\n\tprint(table[1])\n\tprint(table[label])\n\tprint(keyed[1.5])\n\tprint(keyed[label])\n\tprint(any_keyed[label])\n"
        + "\tprint(node[0])\n\tprint(node[\"name\"])\n\tprint(Mode[\"A\"])\n\tprint(count[0])\n\tprint(flag[0])\n"
        + "\tprint(colour[label])\n\tprint(colour[1.5])\n\tfor x in true:\n\t\tpass\n\tfor x in node:\n\t\tpass\n"
        + "\tfor x in colour:\n\t\tpass\n\tfor x in label:\n\t\tpass\n\tfor x in Vector2i(1, 2):\n\t\tpass\n\tfor x in range():\n"
        + "\t\tpass\n\tfor x in range(1, 2, 3, 4):\n\t\tpass\n\tfor x: String in items:\n\t\tpass\n\tfor x: float in items:\n"
        + "\t\tpass\n\tfor x: String in [1, 2]:\n\t\tpass\n\tfor x: int in {\"a\": 1}:\n\t\tpass\n\tfor x: Node in range(3):\n"
        + "\t\tpass\n\tvar typed: Array[int] = [1, \"x\", 2.5]\n\tvar typed_dict: Dictionary[String, int] = {\"a\": \"b\", 1: 2}\n"
        + "\titems = [\"x\"]\n\ttakes([1, \"x\"])\n\n\nfunc takes(_list: Array[String]) -> Array[int]:\n\treturn [\"a\"]\n\n\n"
        + "class Inner:\n\tfunc method() -> void:\n\t\tpass\n\n\tstatic func static_method() -> void:\n\t\tpass\n";

    /// <summary>A script class with members and functions of its own, which <see cref="Declarations"/> extends, and in which
    /// Godot finds no error.</summary>
    internal const string Base = "class_name BaseThing\nextends Node\n\nsignal changed\nvar health := 10\nconst LIMIT = 3\n\n\n"
        + "func take(amount: int, source: Node = null) -> int:\n\treturn amount\n\n\nfunc untyped(value):\n\treturn value\n\n\n"
        + "func gives() -> Node:\n\treturn null\n\n\nstatic func make() -> void:\n\tpass\n";

    /// <summary>Members whose names a parent script, the engine class or a built-in type has, inner classes hiding a
    /// built-in type, an engine class or a global script class, overrides whose signatures don't match the parent's (a
    /// script's and the engine's virtuals), and exports Godot refuses.</summary>
    internal const string Declarations = "extends BaseThing\n\nvar health_extra := 1\nvar changed := 2\nvar LIMIT2 := 3\nvar name := \"x\"\nvar script: int\n"
        + "var process_mode := 0\nvar NOTIFICATION_READY := 1\nvar PROCESS_MODE_INHERIT := 2\nvar Sprite2D := 1\nvar Vector2 := 2\n"
        + "var health := 5\nconst Node3D = 0\nsignal ready\nenum { CHANGED_A, health_b }\nenum Timer { A }\n"
        + "@export var exported: int\n@export var exported_node: Node\n@export var exported_object: Object\n"
        + "@export var exported_ref: RefCounted\n@export var exported_array: Array[Node]\n@export var nothing\n"
        + "@export static var static_exported: int\n@export @export_range(0, 10) var twice: int\n@export var inferred = 5\n\n\n"
        + "func take(amount: int) -> int:\n\treturn amount\n\n\nfunc untyped(value: int):\n\treturn value\n\n\n"
        + "func gives() -> Object:\n\treturn null\n\n\nfunc make() -> void:\n\tpass\n\n\nfunc _ready() -> int:\n\treturn 0\n\n\n"
        + "func _process(delta: int) -> void:\n\tpass\n\n\nfunc _input(event: InputEventKey) -> void:\n\tpass\n\n\n"
        + "func _physics_process(_delta: float) -> void:\n\tpass\n\n\nclass Vector3:\n\tpass\n\n\nclass Node2D:\n\tpass\n\n\n"
        + "class BaseThing:\n\tpass\n\n\nclass Holder extends Resource:\n\t@export var node: Node\n"
        + "\t@export var nodes: Array[Node]\n\t@export var res: Resource\n";

    /// <summary>A project of singleton autoloads (Game, Hub) and one that is none (Quiet), for <see cref="Files"/>.</summary>
    internal const string FilesProject = "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Files\"\n\n[autoload]\n\n"
        + "Game=\"*res://game.gd\"\nHub=\"*res://hub.gd\"\nQuiet=\"res://quiet.gd\"\n";

    /// <summary>Bases and preloads of files the project lacks (by res:// path and relative to the script), its one error
    /// where a class's base is lacked, and classes named as singleton autoloads, beside the scripts they name.</summary>
    internal static readonly (string File, string Script)[] Files =
    [
        ("missing_base.gd", "extends \"res://nowhere.gd\"\n\n\nfunc _ready():\n\tprint(undefined)\n"),
        ("inner_missing.gd", "extends Node\n\n\nclass Broken:\n\textends \"res://gone.gd\"\n\n\nclass Vector2:\n\tpass\n\n\nfunc _ready():\n"
            + "\tprint(undefined)\n"),
        ("preloads.gd", "extends Node\n\nconst Missing = preload(\"res://missing.tscn\")\nconst Present = preload(\"res://game.gd\")\n"
            + "const Relative = preload(\"nothing.png\")\nconst Near = preload(\"sub/thing.gd\")\n"
            + "var icon = preload(\"res://icon.png\")\n\n\nfunc _ready() -> void:\n\tvar scene = preload(\"res://sub/none.tres\")\n"
            + "\tprint(Missing, Present, Relative, Near, icon, scene)\n"),
        ("hub_named.gd", "class_name Hub\nextends Node\n"),
        ("inner_names.gd", "extends Node\n\n\nclass Game:\n\tpass\n\n\nclass Quiet:\n\tpass\n"),
        ("sub/relative_base.gd", "extends \"missing_here.gd\"\n"),
    ];

    /// <summary>The scripts <see cref="Files"/>' autoloads and preloads name, in which Godot finds no error.</summary>
    internal static readonly (string File, string Script)[] FilesBeside =
    [
        ("game.gd", "extends Node\n"),
        ("hub.gd", "extends Node\n"),
        ("quiet.gd", "extends Node\n"),
        ("sub/thing.gd", "extends Resource\n"),
    ];

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GDScriptGodotErrorsTests()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): the errors are compared with its own server's");
        folder = TestFolders.Create("gdscript-godot-errors-");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Errors\"\n");
    }

    [AvaloniaFact]
    public void Names_godot_does_not_find_are_its_errors_in_its_words() => Compared(("names.gd", Names), ("calls.gd", Calls), ("types.gd", Types));

    [AvaloniaFact]
    public void Values_its_targets_take_none_of_are_its_errors_in_its_words() => Compared(("mismatches.gd", Mismatches));

    [AvaloniaFact]
    public void Operators_casts_and_type_tests_godot_refuses_are_its_errors_in_its_words() => Compared(("operators.gd", Operators));

    [AvaloniaFact]
    public void Static_misuse_indices_loops_and_typed_collections_godot_refuses_are_its_errors_in_its_words() => Compared(("statics.gd", Statics));

    [AvaloniaFact]
    public void Declarations_godot_refuses_are_its_errors_in_its_words() => Compared([("base.gd", Base)], ("decls.gd", Declarations));

    [AvaloniaFact]
    public void Files_the_project_lacks_and_names_its_singletons_have_are_its_errors_in_its_words()
    {
        TestFolders.Write(folder, "project.godot", FilesProject);
        Compared(FilesBeside, Files);
    }

    /// <summary>A corpus compared with no script beside it.</summary>
    private void Compared(params (string File, string Script)[] corpus) => Compared([], corpus);

    /// <summary>The scripts written (those <paramref name="beside"/> the corpus, in which Godot finds no error, too), the
    /// corpus's opened one by one once Godot's server serves the project, and each one's errors from Godot's server and
    /// from Passenger's reading the same.</summary>
    private void Compared(IReadOnlyList<(string File, string Script)> beside, params (string File, string Script)[] corpus)
    {
        foreach ((string file, string script) in beside.Concat(corpus))
        {
            TestFolders.Write(folder, file, script);
        }
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            List<string> differ = [];
            foreach ((string file, _) in corpus)
            {
                Assert.Null(window.OpenFile(Path.Combine(folder, file)));
                OpenDocument document = window.EditorArea.Active!;
                Ui.WaitFor(() => window.Session.Web?.For(document) is { Serving: true } served && served.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)
                    && served.Passengers.Any(m => m.Kind == MarkerKind.Error), () => $"Godot's and Passenger's errors in {file}", TimeSpan.FromMinutes(2));
                WebDocument web = window.Session.Web!.For(document)!;
                TextSnapshot text = document.Text.Snapshot;
                SortedSet<string> godots = [.. web.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Range.Start.Line + 1}: {d.Message}")];
                SortedSet<string> passengers = [.. web.Passengers.Where(m => m.Kind == MarkerKind.Error).Select(m => $"{text.GetLineFromOffset(m.Range.Start) + 1}: {m.Message}")];
                differ.AddRange(godots.Except(passengers).Select(e => $"{file}:{e} (Godot's alone)"));
                differ.AddRange(passengers.Except(godots).Select(e => $"{file}:{e} (Passenger's alone)"));
            }
            Assert.True(differ.Count == 0, $"Passenger's errors are not Godot's server's:\n{string.Join('\n', differ)}");
        }
        finally
        {
            window.Close();
        }
    }
}
