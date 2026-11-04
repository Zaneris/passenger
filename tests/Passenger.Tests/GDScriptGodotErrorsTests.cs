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
/// types and calls), opened in a window whose
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

    /// <summary>The scripts written, opened one by one once Godot's server serves the project, and each one's errors from
    /// Godot's server and from Passenger's reading the same.</summary>
    private void Compared(params (string File, string Script)[] corpus)
    {
        foreach ((string file, string script) in corpus)
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
