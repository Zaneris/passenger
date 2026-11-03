using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A GDScript read by Passenger's own parser and analyzer while Godot's server for it has not started (held at its
/// start where a Godot is found, never started where none is): the types inferred where none is written are shown after
/// their names, a member completes after <c>self.</c> and a local by its first letters, and Go to Declaration reaches a
/// function of the script; Godot's warnings and errors are marked as the project's settings ask and each is fixed as one
/// command of Undo. A Godot editor of its own each where one is found, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptTypesTests
{
    private const string Script = "extends Node\n\nvar counter := 0\nvar label := \"hi\"\n\n\nfunc grow(by = 1.5, times := 2):\n"
        + "\tvar total := counter * times\n\treturn total\n\n\nfunc _ready() -> void:\n\tgrow()\n";

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GDScriptTypesTests()
    {
        folder = TestFolders.Create("gdscript-types-");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Types\"\n");
        TestFolders.Write(folder, "main.gd", Script);
    }

    [AvaloniaFact]
    public void Types_inferred_where_none_is_written_are_shown_after_their_names()
    {
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            string text = document.Text.Snapshot.ToString();
            Dictionary<int, string> expected = new()
            {
                [text.IndexOf("counter", StringComparison.Ordinal) + "counter".Length] = ": int",
                [text.IndexOf("label", StringComparison.Ordinal) + "label".Length] = ": String",
                [text.IndexOf("by =", StringComparison.Ordinal) + "by".Length] = ": float",
                [text.IndexOf("times :=", StringComparison.Ordinal) + "times".Length] = ": int",
                [text.IndexOf("total :=", StringComparison.Ordinal) + "total".Length] = ": int",
            };
            Ui.PumpUntil(() => expected.All(e => document.InlayHints.Any(h => h.Offset == e.Key && h.Label == e.Value && h.Kind == InlayHintKind.Type)),
                "the inferred types after their names (counter: int, label: String, by: float, times: int, total: int)", TimeSpan.FromSeconds(60));
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    [AvaloniaFact]
    public void Completion_and_go_to_declaration_answer_before_godots_server_has_started()
    {
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "\tgrow()");
            editor.Focus();
            Ui.Press(window, "ENTER", settle: false);
            window.KeyTextInput("self.");
            Ui.PumpUntil(() => window.Assist.CompletionLabels.Contains("counter") && window.Assist.CompletionLabels.Contains("grow"),
                "the script's members after self's dot, counter and grow among them", TimeSpan.FromSeconds(60));
            Assert.True(window.Session.Web?.For(document) is not { Serving: true }, "Godot's server answered: the test's hold of its start did not hold");
            window.KeyTextInput("lab");
            Ui.PumpUntil(() => window.Assist.CompletionLabels.FirstOrDefault() == "label",
                "the list narrowed to label", TimeSpan.FromSeconds(30));
            Ui.Press(window, "ENTER", settle: false);
            Ui.PumpUntil(() => document.Text.Snapshot.ToString().Contains("\tself.label\n", StringComparison.Ordinal),
                "self.label put in", TimeSpan.FromSeconds(10));

            Ui.PlaceAfter(editor, "\tgro");
            Ui.Press(window, Ui.Shortcut(AppActions.GoToDeclaration), settle: false);
            int declared = Script.Split('\n').ToList().FindIndex(l => l.StartsWith("func grow", StringComparison.Ordinal));
            Ui.PumpUntil(() => editor.CaretPosition.Line == declared, $"the caret at grow's declaration, line {declared + 1}", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    private const string Warned = "extends Node\n\n\nfunc coroutine() -> int:\n\tawait get_tree().process_frame\n\tprint(\"waited\")\n\n\n"
        + "func _ready() -> void:\n\tvar unused := 1\n\tcoroutine()\n";

    [AvaloniaFact]
    public void Godots_warnings_and_errors_are_marked_before_its_server_starts_and_each_fix_is_one_command()
    {
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Types\"\n\n[debug]\n\n"
            + "gdscript/warnings/inferred_declaration=1\ngdscript/warnings/missing_await=1\n");
        TestFolders.Write(folder, "warned.gd", Warned);
        (MainWindow window, OpenDocument document, TaskCompletionSource release) = Open("warned.gd");
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            string[] marked =
            [
                "Not all code paths return a value.",
                "INFERRED_DECLARATION: Variable \"unused\" has an implicitly inferred static type.",
                "UNUSED_VARIABLE: The local variable \"unused\" is declared but never used in the block. If this is intended, prefix it with an underscore: \"_unused\".",
                "MISSING_AWAIT: \"await\" keyword might be desired because the expression is a coroutine.",
            ];
            Ui.PumpUntil(() => marked.All(m => document.Markers.Any(k => k.Message == m)),
                "Godot's error and three warnings marked from Passenger's reading, the project's settings turning two on", TimeSpan.FromSeconds(60));
            Assert.Equal(MarkerKind.Error, document.Markers.First(m => m.Message == marked[0]).Kind);
            Assert.True(window.Session.Web?.For(document) is not { Serving: true }, "Godot's server answered: the test's hold of its start did not hold");

            Fix(window, editor, "var unu", "Prefix 'unused' with an underscore");
            Ui.PumpUntil(() => document.Text.Snapshot.ToString().Contains("\tvar _unused := 1\n", StringComparison.Ordinal) && !document.Markers.Any(m => m.Message == marked[2]),
                "the variable prefixed and its warning gone", TimeSpan.FromSeconds(30));
            Ui.Run(window, EditorActions.Undo, settle: false);
            Ui.PumpUntil(() => document.Text.Snapshot.ToString() == Warned && document.Markers.Any(m => m.Message == marked[2]),
                "Undo to take the fix back whole, and the warning marked again", TimeSpan.FromSeconds(30));

            Fix(window, editor, "var unu", "Add type annotation ': int'");
            Ui.PumpUntil(() => !document.Markers.Any(m => m.Message == marked[1]), "the inferred type's warning gone once the type is written", TimeSpan.FromSeconds(30));
            Fix(window, editor, "\tcorou", "Add 'await'");
            Ui.PumpUntil(() => !document.Markers.Any(m => m.Message == marked[3]), "the missing await's warning gone once it is there", TimeSpan.FromSeconds(30));
            Fix(window, editor, "func coroutine() -> i", "Add 'return 0'");
            Ui.PumpUntil(() => !document.Markers.Any(m => m.Message == marked[0]), "the error gone once the function returns", TimeSpan.FromSeconds(30));
            Assert.Equal("extends Node\n\n\nfunc coroutine() -> int:\n\tawait get_tree().process_frame\n\tprint(\"waited\")\n\treturn 0\n\n\n"
                + "func _ready() -> void:\n\tvar unused: int = 1\n\tawait coroutine()\n", document.Text.Snapshot.ToString());
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>A fix chosen from Show Context Actions at a place, once Passenger's reading of the text as it stands offers it.</summary>
    private static void Fix(MainWindow window, CodeEditor editor, string after, string title)
    {
        Ui.PlaceAfter(editor, after);
        Ui.PumpUntil(() =>
        {
            if (window.Assist.Offered.Contains(title))
            {
                return true;
            }
            if (window.Assist.Offered.Count > 0)
            {
                Ui.Press(window, "ESCAPE", settle: false);
            }
            Ui.Run(window, AppActions.QuickFix, settle: false);
            return window.Assist.Offered.Contains(title);
        }, $"'{title}' offered at '{after}'", TimeSpan.FromSeconds(30));
        for (int i = window.Assist.Offered.ToList().IndexOf(title); i > 0; i--)
        {
            Ui.Press(window, "DOWN", settle: false);
        }
        Ui.Press(window, "ENTER", settle: false);
    }

    /// <summary>A key typed in the middle of a GDScript of 3,000 lines allocates on the window's thread within a few
    /// kilobytes of one in a script of 30 lines (the editor carrying the script's thousand type hints through it), the
    /// script's tree and types kept as for any open script: the key notes its edit and the pause restarts, the parse and
    /// the types made off the window's thread once typing pauses, a list of hints set only where a type changed. No
    /// project.godot, so no Godot server starts.</summary>
    [AvaloniaFact]
    public void A_key_in_a_gdscript_of_3000_lines_allocates_on_the_window_thread_what_one_in_30_lines_does()
    {
        string scripts = TestFolders.Create("gdscript-keys-");
        TestFolders.Write(scripts, "small.gd", Generated(10));
        TestFolders.Write(scripts, "large.gd", Generated(1000));
        MainWindow window = new() { Width = 700, Height = 300, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(scripts);
        Ui.Settle();
        try
        {
            long small = KeyBytes(window, Path.Combine(scripts, "small.gd"));
            long large = KeyBytes(window, Path.Combine(scripts, "large.gd"));
            TestContext.Current.TestOutputHelper?.WriteLine($"Four keys allocated {small:N0} bytes on the window's thread in a GDScript of 30 lines and {large:N0} in one of 3,000");
            Assert.True(large <= small + 65536,
                $"Four keys in a GDScript of 3,000 lines allocated {large:N0} bytes on the window's thread, against {small:N0} in one of 30: the script is read again whole as it is typed");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A script of three-line functions, each with a typed and an inferred local.</summary>
    private static string Generated(int functions) =>
        "extends Node\n\nvar total := 0\n\n" + string.Concat(Enumerable.Range(0, functions).Select(i => $"func step_{i}(by: int) -> int:\n\tvar next := total + by * {i}\n\treturn next\n\n"));

    /// <summary>What four keys in the middle function's body (at its return, which changes no inferred type; the first leaves
    /// <c>next</c> unused, its warning marked and its name's colour gone, the last puts both back) allocate on the window's
    /// thread with the pause after each, the least of three rounds.</summary>
    private static long KeyBytes(MainWindow window, string path)
    {
        Assert.Null(window.OpenFile(path));
        Ui.Settle();
        CodeEditor editor = window.EditorArea.CodeEditor;
        string text = editor.Document.Snapshot.ToString();
        int functions = text.Split("func step_").Length - 1;
        Ui.PlaceAfter(editor, $"func step_{functions / 2}(by: int) -> int:\n\tvar next := total + by * {functions / 2}\n\treturn next");
        editor.Focus();
        Action[] keys = [() => window.KeyTextInput("x"), () => window.KeyTextInput("y"), () => Ui.Press(window, "BACK_SPACE", settle: false), () => Ui.Press(window, "BACK_SPACE", settle: false)];
        long[] least = [.. keys.Select(_ => long.MaxValue)];
        for (int round = 0; round < 3; round++)
        {
            for (int k = 0; k < keys.Length; k++)
            {
                GC.Collect(1, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                long before = GC.GetAllocatedBytesForCurrentThread();
                keys[k]();
                Ui.Settle();
                least[k] = Math.Min(least[k], GC.GetAllocatedBytesForCurrentThread() - before);
                Ui.Hold(TimeSpan.FromMilliseconds(400), "the typing's pause, its parse and types made");
                Ui.Settle();
            }
        }
        Assert.Equal(text, editor.Document.Snapshot.ToString());
        return least.Sum();
    }

    /// <summary>The window on the folder with a script open (main.gd's by default), Godot's server for it held at its start
    /// where a Godot is found.</summary>
    private (MainWindow Window, OpenDocument Document, TaskCompletionSource Release) Open(string script = "main.gd")
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
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? held = godot is { Exists: true } ? window.Session.Web?.HoldNextConfigure(release.Task) : null;
        Assert.Null(window.OpenFile(Path.Combine(folder, script)));
        if (held is not null)
        {
            Ui.AwaitPumped(held, "Godot's GDScript server held at its start", TimeSpan.FromMinutes(1));
        }
        return (window, window.EditorArea.Active!, release);
    }
}
