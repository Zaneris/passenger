using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A GDScript's Parameter Info from Passenger's own reading, Godot's server held at its start where a Godot is
/// found (never started where none is): after a call's bracket the signature of what it calls, a script's function as
/// written, a signal's emit, the engine's method as its API gives it and a built-in type's constructor, with the
/// parameter the caret is at, moving on at each comma and gone at the closing bracket; and after a function put in by
/// completion, its parentheses holding the caret. A Godot editor of its own each where one is found, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptParameterInfoTests
{
    private const string Script = "extends Node\n\nsignal scored(points: int, by: String)\n\n\n"
        + "## Grows the count.\nfunc grow(by: int = 1, times := 2) -> int:\n\treturn by * times\n\n\n"
        + "func _ready() -> void:\n\tpass\n";

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GDScriptParameterInfoTests()
    {
        folder = TestFolders.Create("gdscript-parameters-");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Parameters\"\n");
        TestFolders.Write(folder, "main.gd", Script);
    }

    [AvaloniaFact]
    public void A_scripts_function_and_signal_show_their_signature_and_the_parameter_the_caret_is_at()
    {
        (MainWindow window, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "\tpass");
            editor.Focus();
            Ui.Press(window, "ENTER", settle: false);
            window.KeyTextInput("grow");
            Ui.Press(window, "ESCAPE", settle: false);
            window.KeyTextInput("(");
            Signed(window, "func grow(by: int = 1, times = 2) -> int", "by: int = 1");
            Assert.Contains("Grows the count.", window.Assist.SignatureText, StringComparison.Ordinal);
            window.KeyTextInput("3");
            window.KeyTextInput(",");
            Signed(window, "func grow(", "times = 2");
            window.KeyTextInput("4");
            window.KeyTextInput(")");
            bool gone = Ui.Pumped(() => window.Assist.SignatureText is null, TimeSpan.FromSeconds(10));
            Assert.True(gone, $"The signature to go at the closing bracket: {window.Assist.SignatureText}");

            Ui.Press(window, "ENTER", settle: false);
            window.KeyTextInput("scored.emit");
            Ui.Press(window, "ESCAPE", settle: false);
            window.KeyTextInput("(");
            Signed(window, "signal scored(points: int, by: String)", "points: int");

            Ui.Press(window, "END", settle: false);
            Ui.Press(window, "ENTER", settle: false);
            window.KeyTextInput("g");
            window.KeyTextInput("row");
            bool listed = Ui.Pumped(() => window.Assist.CompletionLabels.FirstOrDefault() == "grow", TimeSpan.FromSeconds(30));
            Assert.True(listed, $"grow first in the list: {string.Join(", ", window.Assist.CompletionLabels.Take(8))}");
            Ui.Press(window, "ENTER", settle: false);
            Signed(window, "func grow(", "by: int = 1");
            Assert.True(window.Session.Web?.For(window.EditorArea.Active!) is not { Serving: true }, "Godot's server answered: the test's hold of its start did not hold");
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>The engine's method shows its signature as its API gives it, defaults and enum types among it, and a
    /// built-in type's constructor the one taking the arguments given.</summary>
    [AvaloniaFact]
    public void The_engines_method_and_a_built_in_types_constructor_show_their_signature()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): the engine's signatures come from its API");
        (MainWindow window, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "\tpass");
            editor.Focus();
            Ui.Press(window, "ENTER", settle: false);
            window.KeyTextInput("add_child");
            Ui.Press(window, "ESCAPE", settle: false);
            window.KeyTextInput("(");
            Signed(window, "func Node.add_child(node: Node, force_readable_name: bool = false, internal: Node.InternalMode = 0) -> void", "node: Node");
            window.KeyTextInput("self");
            window.KeyTextInput(",");
            Signed(window, "func Node.add_child(", "force_readable_name: bool = false");
            Ui.Press(window, "END", settle: false);
            Ui.Press(window, "ENTER", settle: false);
            window.KeyTextInput("var v := Vector2");
            Ui.Press(window, "ESCAPE", settle: false);
            window.KeyTextInput("(");
            window.KeyTextInput("1");
            window.KeyTextInput(",");
            Signed(window, "Vector2(x: float, y: float)", "y: float");
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>Parameter Info showing a signature, and the parameter the caret is at, waited for with the window's loop run.</summary>
    private static void Signed(MainWindow window, string signature, string parameter)
    {
        bool signed = Ui.Pumped(() => window.Assist.SignatureText is { } shown && shown.Contains(signature, StringComparison.Ordinal)
            && shown.Split('\n').Skip(1).Any(l => l == parameter), TimeSpan.FromSeconds(60));
        Assert.True(signed, $"Parameter Info to show {signature} at {parameter}: {window.Assist.SignatureText}");
    }

    private (MainWindow Window, TaskCompletionSource Release) Open()
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
        Assert.Null(window.OpenFile(Path.Combine(folder, "main.gd")));
        if (held is not null)
        {
            Ui.AwaitPumped(held, "Godot's GDScript server held at its start", TimeSpan.FromMinutes(1));
        }
        return (window, release);
    }
}
