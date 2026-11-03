using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.UnitTests.Godot;

/// <summary>GDScript read by Passenger's own parser as Godot's parser reads it: the tree, Godot's errors in its own words
/// (the scripts many cases come from are Godot's own tests, modules/gdscript/tests/scripts/parser/errors), recovery after
/// an error, and an edit's parse, which takes the members it did not reach from the tree before.</summary>
public class GDScriptParserTests
{
    [Fact]
    public void A_scripts_class_and_members_are_read_with_their_places_as_the_editor_counts_them()
    {
        string text = "@tool\nclass_name Player extends \"res://actor.gd\"\n\nsignal hit(amount: int)\n@export var speed := 2.0:\n\tset(value):\n\t\tspeed = value\n"
            + "const LIMIT = 3\nenum State { IDLE, RUN = 2 }\nenum { NORTH, SOUTH }\nstatic func make() -> Node:\n\treturn null\n\nclass Inner extends Node:\n\tvar x\n"
            + "\tfunc f():\n\t\tpass\n\nfunc _ready() -> void:\n\tvar callback := func(a): return a * 2\n\tprint(callback.call(1))\n";
        GDScriptTree tree = GDScriptTree.Parse(text);
        Assert.Empty(tree.Errors);
        Assert.True(tree.Class.IsTool, "The script's @tool is lost");
        Assert.Equal(("Player", "res://actor.gd"), (tree.Class.Identifier?.Name, tree.Class.ExtendsPath));
        Assert.Equal(["GDScriptSignal hit", "GDScriptVariable speed", "GDScriptConstant LIMIT", "GDScriptEnum State", "GDScriptEnum ", "GDScriptFunction make",
            "GDScriptClass Inner", "GDScriptFunction _ready"], tree.Members.Select(m => $"{m.Node.GetType().Name} {NameOf(m.Node)}"));
        TextSnapshot snapshot = TextSnapshot.From(text);
        GDScriptMember ready = tree.Members[^1];
        GDScriptFunction function = (GDScriptFunction)ready.Node;
        Assert.Equal(new TextPosition(18, 5), snapshot.GetPosition(ready.Offset + function.Identifier!.Start));
        Assert.Equal(new TextPosition(18, 0), snapshot.GetPosition(ready.Start));
        Assert.IsType<GDScriptLambda>(((GDScriptVariable)function.Body!.Statements[0]).Initializer);
        GDScriptVariable speed = (GDScriptVariable)tree.Members[1].Node;
        Assert.True(speed.Infers && speed.Property == GDScriptPropertyStyle.Inline && speed.Setter is not null, "speed's setter is not read as Godot reads it");
        Assert.Equal(["@export"], speed.Annotations.Select(a => a.Name));
        Assert.Equal(["x", "f"], ((GDScriptClass)tree.Members[6].Node).Members.Select(NameOf));
        Assert.Equal(["NORTH", "SOUTH"], ((GDScriptEnum)tree.Members[4].Node).Values.Select(v => v.Name.Name));
    }

    [Theory]
    [InlineData("@export\nfunc test():\n    pass\n", "Annotation \"@export\" cannot be applied to a function.")]
    [InlineData("func test() {\n\tprint(\"Hello world!\");\n}\n", "Expected end of statement after bodyless function declaration, found \"{\" instead.")]
    [InlineData("func test():\n\tvar TEST = 50\n\tconst TEST = 25\n", "There is already a variable named \"TEST\" declared in this scope.")]
    [InlineData("func hello(arg1):\n\tprint(arg1)\n\nfunc test():\n\thello(arg1 = 25)\n", "Assignment is not allowed inside an expression.")]
    [InlineData("func test():\n\t$=$\n", "Expected node path as string or identifier after \"$\".")]
    [InlineData("func test():\n\tvar dictionary = { hello = \"world\",, }\n", "Expected expression as dictionary key.")]
    [InlineData("func test():\n\tvar escape = \"invalid escape \\h <- here\"\n", "Invalid escape in string.")]
    [InlineData("func test():\n\tvar 23test = \"is not a valid identifier\"\n", "Expected variable name after \"var\".")]
    [InlineData("func test():\n\tfunc standalone():\n\t\tprint(\"can't be accessed\")\n", "Standalone lambdas cannot be accessed. Consider assigning it to a variable.")]
    [InlineData("func test():\n\tmatch 1:\n\t\t[[[var a]]], 2:\n\t\t\tpass\n", "Cannot use a variable bind with multiple patterns.")]
    [InlineData("func test():\n\tif true # Missing colon here.\n\t\tprint(\"true\")\n", "Expected \":\" after \"if\" condition, found \"Newline\" instead.")]
    [InlineData("func test():\n\tvar _num = 1__23\n", "Multiple underscores cannot be adjacent in a numeric literal.")]
    [InlineData("func _static_init():\n\tprint(\"static init\")\n", "Static constructor must be declared static.")]
    [InlineData("func test():\n\tvar array = [1, 2, 3]\n\tarray[] = 4\n", "Expected expression after \"[\".")]
    [InlineData("func test():\n\tvar TEST = 1\n\tfor TEST in 2:\n\t\tpass\n", "There is already a variable named \"TEST\" declared in this scope.")]
    [InlineData("func f(...args, extra_arg):\n\tpass\n", "Cannot have parameters after the rest parameter.")]
    [InlineData("signal event\n\nfunc test():\n\tyield(\"event\")\n", "\"yield\" was removed in Godot 4. Use \"await\" instead.")]
    [InlineData("export var test = 3\n", "The \"export\" keyword was removed in Godot 4. Use an export annotation (\"@export\", \"@export_range\", etc.) instead.")]
    [InlineData("func test():\n\tvar while = \"it's been a while\"\n", "Expected variable name after \"var\".")]
    [InlineData("func test():\n\tvar a = 0\n\tprint(a++)\n", "Expected expression after \"+\" operator.")]
    [InlineData("func test():\n    print(\"Using spaces\")\n\tprint(\"Using tabs\")\n", "Used tab character for indentation instead of space as used before in the file.")]
    [InlineData("<<<<<<< HEAD\nfunc test():\n\tpass\n", "Unexpected \"VCS conflict marker\" in class body.")]
    [InlineData("var x = 1\nvar x = 2\n", "Variable \"x\" has the same name as a previously declared variable.")]
    [InlineData("enum { A }\nconst A = 1\n", "Constant \"A\" has the same name as a previously declared enum value.")]
    [InlineData("@deprecated\nfunc f():\n\tpass\n", "\"@deprecated\" annotation does not exist. Use \"## @deprecated: Reason here.\" instead.")]
    [InlineData("func f():\n\tvar s = r\"unterminated\n", "Unterminated string.")]
    [InlineData("func f():\n\tvar x = 0x\n", "Expected hexadecimal digit after \"0x\".")]
    [InlineData("func f():\n\tbreak\n", "Cannot use \"break\" outside of a loop.")]
    [InlineData("@warning_ignore_start(\"unreachable_code\")\n@warning_ignore_start(\"unreachable_code\")\n\nfunc test():\n\tpass\n",
        "Warning \"UNREACHABLE_CODE\" is already being ignored by \"@warning_ignore_start\" at line 1.")]
    [InlineData("func test():\n\t@warning_ignore_restore(\"unreachable_code\")\n\tpass\n", "Warning \"UNREACHABLE_CODE\" is not being ignored by \"@warning_ignore_start\".")]
    [InlineData("@warning_ignore_start(\"no_such_warning\")\nvar x\n", "Invalid warning name: \"no_such_warning\".")]
    public void An_error_is_said_in_Godots_own_words(string script, string expected)
    {
        IReadOnlyList<GDScriptError> errors = GDScriptTree.Parse(script).Errors;
        Assert.True(errors.Any(e => e.Message == expected), $"Godot says \"{expected}\", the parser said: {string.Join(" | ", errors.Select(e => e.Message))}");
    }

    [Fact]
    public void A_parse_recovers_after_an_error_so_the_members_after_it_are_still_read()
    {
        string text = "func broken(:\n\tvar = \n\nvar after := 1\n\nfunc fine(a, b = 2):\n\treturn a + b\n";
        GDScriptTree tree = GDScriptTree.Parse(text);
        Assert.NotEmpty(tree.Errors);
        Assert.Contains("after", tree.Members.Select(m => NameOf(m.Node)));
        Assert.Contains("fine", tree.Members.Select(m => NameOf(m.Node)));
        GDScriptError first = tree.Errors[0];
        Assert.True(first.Start >= 0 && first.End <= text.Length && first.Start <= first.End, $"An error lies outside the text: {first}");
    }

    [Fact]
    public void An_errors_place_is_where_Godot_puts_it_whatever_the_line_breaks()
    {
        GDScriptTree tree = GDScriptTree.Parse("func f():\r\n\tvar ok = 1\r\n\tvar bad = \"\\q\"\r\n");
        GDScriptError error = Assert.Single(tree.Errors);
        Assert.Equal(("Invalid escape in string.", 36, 38), (error.Message, error.Start, error.End));
    }

    [Fact]
    public void Literals_are_read_with_the_values_Godot_gives_them()
    {
        GDScriptTree tree = GDScriptTree.Parse("const A = 0xFF\nconst B = 0b1010\nconst C = 1_000\nconst D = 1.5e3\nconst E = .5\nconst F = -3\n"
            + "const G = \"a\\tb\\u00e9\"\nconst H = r\"raw\\n\"\nconst I = &\"name\"\nconst J = ^\"Path/To\"\nconst K = \"\"\"two\nlines\"\"\"\nconst L = null\nconst M = PI\n");
        Assert.Empty(tree.Errors);
        object?[] values = [.. tree.Members.Select(m => ((GDScriptConstant)m.Node).Initializer switch
        {
            GDScriptLiteral literal => literal.Value,
            GDScriptIdentifier identifier => identifier.Name,
            _ => "?",
        })];
        Assert.Equal<object?>([255L, 10L, 1000L, 1500.0, 0.5, -3L, "a\tb\u00e9", "raw\\n", "name", "Path/To", "two\nlines", null, "PI"], values);
        Assert.Equal(GDScriptLiteralKind.NodePath, ((GDScriptLiteral)((GDScriptConstant)tree.Members[9].Node).Initializer!).Kind);
    }

    [Fact]
    public void Lambdas_inside_brackets_and_calls_parse_on_their_own_indented_lines()
    {
        string text = "func f():\n\tvar list = [func():\n\t\tprint(1)\n\t\treturn 2,\n\t\tfunc(): return 3]\n\tbutton.pressed.connect(func():\n\t\tprint(\"pressed\")\n\t)\n"
            + "\tvar g = func(x: int) -> int: return x + 1\n\tawait get_tree().create_timer(1.0).timeout\n";
        GDScriptTree tree = GDScriptTree.Parse(text);
        Assert.True(tree.Errors.Count == 0, string.Join(" | ", tree.Errors.Select(e => e.Message)));
        Assert.Equal(4, ((GDScriptFunction)tree.Members[0].Node).Body!.Statements.Count);
    }

    [Fact]
    public void Match_patterns_and_properties_parse_as_Godot_reads_them()
    {
        string text = "var health := 10:\n\tget:\n\t\treturn health\n\tset(value):\n\t\thealth = clampi(value, 0, 100)\nvar named: int = 0: set = set_named, get = get_named\n\n"
            + "func f(x):\n\tmatch x:\n\t\t1, 2:\n\t\t\tpass\n\t\t[var a, ..]:\n\t\t\tprint(a)\n\t\t{\"k\": var v, ..}:\n\t\t\tprint(v)\n\t\tvar n when n > 3:\n\t\t\tprint(n)\n\t\t_:\n\t\t\tpass\n";
        GDScriptTree tree = GDScriptTree.Parse(text);
        Assert.True(tree.Errors.Count == 0, string.Join(" | ", tree.Errors.Select(e => e.Message)));
        GDScriptMatch match = (GDScriptMatch)((GDScriptFunction)tree.Members[2].Node).Body!.Statements[0];
        Assert.Equal([GDScriptPatternKind.Literal, GDScriptPatternKind.Array, GDScriptPatternKind.Dictionary, GDScriptPatternKind.Bind, GDScriptPatternKind.Wildcard],
            match.Branches.Select(b => b.Patterns[0].Kind));
        Assert.NotNull(match.Branches[3].Guard);
        Assert.Equal(("set_named", "get_named"), (((GDScriptVariable)tree.Members[1].Node).SetterName?.Name, ((GDScriptVariable)tree.Members[1].Node).GetterName?.Name));
    }

    /// <summary>A key typed in one function of a script parses that function's run alone and takes the others from the
    /// tree before as they were, so a key costs what it changed.</summary>
    [Fact]
    public void A_key_typed_in_one_function_parses_its_run_alone()
    {
        string text = string.Concat(Enumerable.Range(0, 40).Select(i => $"func f{i}(a):\n\tvar b{i} = a * {i}\n\treturn b{i}\n\n"));
        GDScriptTree tree = GDScriptTree.Parse(text);
        int at = text.IndexOf("var b20", StringComparison.Ordinal) + 5;
        string typed = text.Insert(at, "x");
        GDScriptTree next = tree.Reparse(typed, new TextEdit(at, 0, "x"));
        Assert.Equal(1, next.RunsParsed);
        Assert.Same(tree.Members[30].Node, next.Members[30].Node);
        Assert.Equal(tree.Members[30].Start + 1, next.Members[30].Start);
        Assert.Equal("bx20", ((GDScriptVariable)((GDScriptFunction)next.Members[20].Node).Body!.Statements[0]).Identifier!.Name);
        Assert.Equal(Shape(GDScriptTree.Parse(typed)), Shape(next));
    }

    /// <summary>Edits of every kind at seeded places of a script that holds most of GDScript, each parsed as an edit of
    /// the tree before, read the same as a whole parse of the edited text: members, places and errors.</summary>
    [Fact]
    public void An_edits_parse_reads_the_same_as_a_whole_parse_of_the_edited_text()
    {
        string text = "@tool\nextends Node\n\nsignal done(ok: bool)\nconst A = [1, 2]\nenum E { X, Y = 3 }\n@export_group(\"G\")\n@export var v: int = 1\n"
            + "var p := 0:\n\tget:\n\t\treturn p\n\nfunc a(x: int, y := 2) -> int:\n\tif x > y:\n\t\treturn x\n\telif x == y:\n\t\treturn 0\n\telse:\n\t\treturn y\n\n"
            + "static func b():\n\tfor i in range(3):\n\t\tprint(i)\n\twhile false:\n\t\tbreak\n\nclass C:\n\tvar w = \"\"\"long\n\tstring\"\"\"\n\tfunc c():\n\t\tmatch 1:\n"
            + "\t\t\t_:\n\t\t\t\tpass\n\nfunc d():\n\tvar f = func(q): return q\n\tvar dict = {\"a\": 1, b = 2}\n\t$Node/Child.queue_free()\n\tawait done\n";
        string[] inserts = ["a", "\n", "\t", "(", ")", "\"", ":", "func ", "var z\n", "#", "\n\n", " "];
        Random random = new(1196);
        for (int round = 0; round < 300; round++)
        {
            GDScriptTree tree = GDScriptTree.Parse(text);
            int at = random.Next(text.Length + 1);
            TextEdit edit = random.Next(3) == 0 && at < text.Length ? new TextEdit(at, Math.Min(random.Next(1, 6), text.Length - at), string.Empty)
                : new TextEdit(at, 0, inserts[random.Next(inserts.Length)]);
            string edited = string.Concat(text.AsSpan(0, edit.Start), edit.Text, text.AsSpan(edit.End));
            Assert.True(Shape(GDScriptTree.Parse(edited)) == Shape(tree.Reparse(edited, edit)), $"An edit {edit} reads otherwise than a whole parse of its text");
        }
    }

    private static string Shape(GDScriptTree tree) =>
        string.Join("\n", tree.Members.Select(m => $"{m.Node.GetType().Name} {NameOf(m.Node)} {m.Start}-{m.End}").Concat(tree.Errors.Select(e => $"! {e.Start}-{e.End} {e.Message}")));

    private static string? NameOf(GDScriptNode node) => node switch
    {
        GDScriptFunction f => f.Identifier?.Name,
        GDScriptVariable v => v.Identifier?.Name,
        GDScriptConstant c => c.Identifier?.Name,
        GDScriptSignal s => s.Identifier?.Name,
        GDScriptEnum e => e.Identifier?.Name,
        GDScriptClass c => c.Identifier?.Name,
        GDScriptAnnotation a => a.Name,
        _ => null,
    };
}
