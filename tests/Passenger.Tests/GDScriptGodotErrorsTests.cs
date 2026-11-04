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
/// types and calls, operators, casts and type tests, static misuse and collections, declarations, files lacked, members
/// reaching themselves, assignments and constructions, @onready, calls made as it reads, getters and setters, bases), opened
/// in a window whose Godot's server serves them, each script's errors from Godot's diagnostics and from Passenger's marks set
/// beside each other (an error Godot pushes twice at one place counted once). Without a Godot each test skips saying why;
/// a Godot editor of its own, so run alone.</summary>
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

    /// <summary>Members whose values reach themselves as Godot resolves them (enums', constants', variables', a parameter's
    /// default, a lambda's, an override's), what := then infers from, and an instance's members read from their classes
    /// held constant, none from a script a variable holds.</summary>
    internal const string Cycles = "class_name Reader\nextends Node\n\nenum NamedEnum0 { VALUE = NamedEnum0.VALUE }\nenum { ENUM_VALUE_0 = ENUM_VALUE_0 }\n"
        + "enum { ENUM_VALUE_1 = ENUM_VALUE_2 }\nenum { ENUM_VALUE_2 = ENUM_VALUE_1 }\nconst CONST_0 = CONST_0\nvar var_1 := var_2\nvar var_2 := var_1\n"
        + "const Other = preload(\"res://cycles_other.gd\")\nvar health := 1\nvar held := preload(\"res://cycles_other.gd\")\n"
        + "var lambda_param = func (_p = lambda_param_ref): return 0\nvar lambda_param_ref = lambda_param\n\n\n"
        + "static func func_0(p := func_0()) -> int:\n\treturn p\n\n\nfunc _ready() -> void:\n"
        + "\tprint(Reader.health, Inner.x, Inner.y, Other.value, Other.made, held.value, held.nope)\n\n\n"
        + "class Inner:\n\tvar x := 1\n\tstatic var y := 2\n\n\nclass InnerA:\n\tfunc f(p := InnerB.new().f()) -> int:\n\t\treturn p\n\n\n"
        + "class InnerB extends InnerA:\n\tfunc f(p := 1) -> int:\n\t\treturn super.f() + p\n";

    /// <summary>The script <see cref="Cycles"/> preloads, in which Godot finds no error.</summary>
    internal const string CyclesOther = "extends RefCounted\n\nvar value := 1\nstatic var made := 2\n";

    /// <summary>Assignments to what Godot holds constant (a function, a signal, an enum, a class or engine class, an
    /// engine's method, through self or a value too) or read-only (an engine's property with no setter, a value held in
    /// one), none to what it does not, and a new() it refuses: an engine singleton's, an abstract class's, and one built on
    /// an engine class that cannot be made.</summary>
    internal const string Assigns = "extends Node\n\nsignal fired\nconst LIMIT = 1\nenum Mode { A }\nvar tree := SceneTree.new()\n"
        + "var held_node: Node\nvar loose\n\n\nfunc f() -> void:\n\tpass\n\n\nstatic func s() -> void:\n\tpass\n\n\n"
        + "func _ready() -> void:\n\tf = null\n\tfired = Signal()\n\tMode = {}\n\tInner = null\n\tNode = null\n"
        + "\tqueue_free = null\n\tself.f = null\n\theld_node.queue_free = null\n\ttree.root = Window.new()\n"
        + "\ttree.current_scene = null\n\ttree.root.name = \"x\"\n"
        + "\tvar state := PhysicsDirectBodyState3DExtension.new()\n\tstate.center_of_mass.x += 1.0\n"
        + "\tstate.transform.origin.x = 1.0\n\tTime.new()\n\tInstancePlaceholder.new()\n\tAbstracted.new()\n"
        + "\tBuilt.new()\n\tCanvasItem.new()\n\tInput.new()\n\tScript.new()\n\tBuilt.Deeper.new()\n\tloose.root = null\n"
        + "\tname = \"n\"\n\tget_tree().root = null\n\ts = null\n\tprint(state, LIMIT)\n\tstate.free()\n\n\n"
        + "@abstract class Abstracted:\n\tpass\n\n\nclass Built extends CanvasItem:\n\tfunc _init():\n\t\tpass\n\n\n"
        + "\tclass Deeper extends Light2D:\n\t\tpass\n\n\nclass Inner:\n\tpass\n";

    /// <summary>An abstract script made through a constant, its class_name and a variable holding it.</summary>
    internal const string Makes = "extends Node\n\nconst Held = preload(\"res://abstract_thing.gd\")\n\n\nfunc _ready() -> void:\n\tHeld.new()\n"
        + "\tAbstractThing.new()\n\tvar s := preload(\"res://abstract_thing.gd\")\n\ts.new()\n";

    /// <summary>The abstract script <see cref="Makes"/> makes, in which Godot finds no error.</summary>
    internal const string AbstractThing = "@abstract class_name AbstractThing\nextends RefCounted\n";

    /// <summary>An <c>@onready</c> in a class built on no Node (the script's, inner ones), on a static variable and written
    /// twice, beside those Godot takes.</summary>
    internal const string Onready = "extends RefCounted\n\n@onready var nope := 0\n\n\nclass Inner extends Node:\n\t@onready var fine := 1\n"
        + "\t@onready @onready var twice := 2\n\t@onready static var still := 3\n\n\nclass Plain:\n"
        + "\t@onready var none = 0\n";

    /// <summary>A Node's <c>@onready</c>, which Godot takes, and its inner class's built on no Node.</summary>
    internal const string OnreadyInner = "extends Node\n\n@onready var fine := 1\n\n\nclass Inner extends RefCounted:\n\t@onready var nope = 0\n";

    /// <summary>Calls Godot makes as it reads the script, every argument a constant: math utility functions given types
    /// they take none of, len(), char() and ord() given what they refuse, Colors made from codes (4.8 refusing those that
    /// are no colour), beside the same calls of a value that is no constant.</summary>
    internal const string Evaluated = "extends Node\n\nconst CODE = \"nope\"\nconst SHADE = \"Alice blue\"\nvar held := Color()\n\n\n"
        + "func _ready() -> void:\n\tprint(floor(Color()), ceil(\"a\"), round(true), abs(Vector2()), sign(1.5))\n"
        + "\tprint(snapped(Color(), 1), snapped(1, \"x\"), snapped(Vector2(), 1.0), snapped(1.5, 2))\n"
        + "\tprint(lerp(\"a\", \"b\", 0.5), lerp(1, Vector2(), 0.5), lerp(1.0, 2.0, 0.5))\n"
        + "\tprint(len(Color()), len(1), len(null), len(\"abc\"), len([1]), len(held))\n"
        + "\tprint(char(-1), char(0), char(0xD800), char(0x110000), char(65))\n"
        + "\tprint(ord(\"ab\"), ord(\"\"), ord(\"a\"))\n"
        + "\tprint(Color(\"\"), Color(\"invalid\"), Color(CODE), Color(SHADE), Color(\"#fff\"), Color(\"red\"), Color(\"12345\"), Color(\"#abcd\"))\n"
        + "\tprint(floor(held), len(held), Color(str(1)))\n";

    /// <summary>Properties' getters and setters: inline ones typed by their property, named functions Godot refuses (not
    /// declared, an inherited one too, of parameters or types that do not fit), untyped getters (typed by their last
    /// return up to 4.7, Variant from 4.8), beside those Godot takes.</summary>
    internal const string Properties = "extends \"res://props_base.gd\"\n\nvar _prop: int\nvar _text := \"\"\n\nvar a: String:\n"
        + "\tset = set_int, get = get_int\nvar b: String:\n\tget = get_untyped\nvar c: String:\n\tget = get_mixed\n"
        + "var d: String:\n\tget = get_local\nvar e: int:\n\tget = inherited_get\nvar f: float:\n"
        + "\tset = set_int, get = get_int\nvar g: int:\n\tset = set_two, get = get_takes\nvar h:\n"
        + "\tset = set_int, get = get_string\nvar i: Variant:\n\tset = set_int, get = get_string\nvar j:\n"
        + "\tset = set_untyped, get = get_int\nvar k: String:\n\tset = set_default, get = nowhere\nvar m := 1:\n"
        + "\tset = set_string\nvar n: String:\n\tset(value):\n\t\t_prop = value\n\tget:\n\t\treturn _prop\nvar o := 0:\n"
        + "\tset(value):\n\t\tvar x: String = value\n\t\to = value\nvar p: String:\n\tget = get_empty\n\n\n"
        + "func set_int(value: int) -> void:\n\t_prop = value\n\n\nfunc set_string(value: String) -> void:\n"
        + "\t_text = value\n\n\nfunc set_untyped(value) -> void:\n\t_prop = value\n\n\n"
        + "func set_two(x: int, y: int) -> void:\n\t_prop = x + y\n\n\nfunc set_default(value := 1) -> void:\n"
        + "\t_prop = value\n\n\nfunc get_int() -> int:\n\treturn _prop\n\n\nfunc get_string() -> String:\n"
        + "\treturn _text\n\n\nfunc get_untyped():\n\treturn _prop\n\n\nfunc get_mixed():\n\tif _prop > 0:\n"
        + "\t\treturn _text\n\treturn _prop\n\n\nfunc get_local():\n\tvar _prop := \"x\"\n\treturn _prop\n\n\n"
        + "func get_takes(x: int) -> int:\n\treturn x\n\n\nfunc get_empty():\n\tpass\n";

    /// <summary>The base <see cref="Properties"/> extends, whose function it names, in which Godot finds no error.</summary>
    internal const string PropertiesBase = "extends Node\n\nfunc inherited_get() -> int:\n\treturn 1\n";

    /// <summary>Bases Godot refuses: constants of no class, a variable, an engine singleton, nested types of an engine
    /// class, not found or of no class, and a cycle.</summary>
    internal const string Inherits = "class SomeClass:\n\tconst CONSTANT = 1\n\nconst CONSTANT = 1\n\nvar variable = 1\n\n# GH-75870\n\n"
        + "class Class1 extends CONSTANT:\n\tpass\n\nclass Class2 extends variable:\n\tpass\n\n"
        + "# GH-82081. `Time` is an engine singleton.\nclass Class3 extends Time:\n\tpass\n\n"
        + "class Class4 extends RefCounted.Nested:\n\tpass\n\nclass Class5 extends SomeClass.UnknownClass:\n\tpass\n\n"
        + "class Class6 extends SomeClass.CONSTANT:\n\tpass\n\nclass ClassA extends ClassB:\n\tpass\n"
        + "class ClassB extends ClassA:\n\tpass\n\nfunc test():\n\tpass\n";

    /// <summary>Classes extending themselves or their own members, some through the script's class_name, which from Godot
    /// 4.8 are not looked for among a class's own members.</summary>
    internal const string InnerBase = "class_name InnerBase\n\nclass A extends InnerA:\n\tclass InnerA:\n\t\tpass\n\nclass B extends InnerB:\n"
        + "\tconst InnerB = A\n\nclass C extends InnerC.InnerInnerC:\n\tclass InnerC:\n\t\tclass InnerInnerC:\n"
        + "\t\t\tpass\n\nclass D extends InnerBase.D:\n\tpass\n\nclass E extends InnerBase.E.InnerE:\n\tclass InnerE:\n"
        + "\t\tpass\n\nclass F extends F:\n\tpass\n\nclass G extends G.InnerG:\n\tclass InnerG:\n\t\tpass\n\n"
        + "class H extends InnerBase:\n\tpass\n\nconst _I = I\nclass I extends _I:\n\tpass\n\nconst _J = J\n"
        + "class J extends _J.InnerJ:\n\tclass InnerJ:\n\t\tpass\n";

    /// <summary>Members of each kind written as a base (a function, a signal, an enum), and nested types: of an engine
    /// class, an instance's member, a static one, beside one Godot takes.</summary>
    internal const string BaseKinds = "extends Node\n\nsignal fired\nenum Mode { A }\nvar held := 1\n\n\nfunc some_func() -> void:\n\tpass\n\n\n"
        + "class K extends some_func:\n\tpass\n\n\nclass L extends fired:\n\tpass\n\n\nclass M extends Mode:\n\tpass\n\n"
        + "\nclass N extends Node.Nested:\n\tpass\n\n\nclass O extends Q.Deep:\n\tpass\n\n\nclass Q:\n\tclass Deep:\n"
        + "\t\tpass\n\tvar inst := 1\n\tstatic var shared := 2\n\n\nclass R extends Q.inst:\n\tpass\n\n\n"
        + "class S extends Q.shared:\n\tpass\n\n\nclass T extends Engine:\n\tpass\n";

    /// <summary>self.free() in a method, a lambda and an inner class, beside free() by its name alone and another node's
    /// free(), which Godot 4.8 refuses and 4.7 takes, and a name declared nowhere that both refuse.</summary>
    internal const string Frees = "extends Node\n\n\nfunc _ready() -> void:\n\tvar l := func lambda():\n\t\tself.free()\n\tself.free()\n"
        + "\tfree()\n\tget_child(0).free()\n\tl.call()\n\tprint(nowhere)\n\n\nstatic func later() -> void:\n\tpass\n\n\n"
        + "class Held extends Object:\n\tfunc drop() -> void:\n\t\tself.free()\n";

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
    public void Members_reaching_themselves_and_an_instances_members_read_from_its_class_are_its_errors_in_its_words() =>
        Compared([("cycles_other.gd", CyclesOther)], ("cycles.gd", Cycles));

    [AvaloniaFact]
    public void Assignments_to_constants_and_read_only_properties_and_a_new_it_refuses_are_its_errors_in_its_words() =>
        Compared([("abstract_thing.gd", AbstractThing)], ("assigns.gd", Assigns), ("makes.gd", Makes));

    [AvaloniaFact]
    public void An_onready_it_refuses_is_its_error_in_its_words() => Compared(("onready.gd", Onready), ("onready_inner.gd", OnreadyInner));

    [AvaloniaFact]
    public void Calls_it_makes_as_it_reads_given_what_they_refuse_are_its_errors_in_its_words() => Compared(("evaluated.gd", Evaluated));

    [AvaloniaFact]
    public void Getters_and_setters_it_refuses_are_its_errors_in_its_words() => Compared([("props_base.gd", PropertiesBase)], ("props.gd", Properties));

    [AvaloniaFact]
    public void Bases_it_refuses_are_its_errors_in_its_words() => Compared(("inherits.gd", Inherits), ("inner_base.gd", InnerBase), ("kinds.gd", BaseKinds));

    [AvaloniaFact]
    public void Self_free_is_its_error_in_its_words_where_it_is_one() => Compared(("frees.gd", Frees));

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
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home"), GodotHeadlessAtOpen = true };
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
