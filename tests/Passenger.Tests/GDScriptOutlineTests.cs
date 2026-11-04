using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Navigation;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A GDScript's outline from Passenger's own parse of it, Godot's server held at its start where a Godot is found
/// (never started where none is): Structure lists its signals, an enum with its values under it, constants, variables and
/// functions with their parameters and the types written, an inner class's members under it; it follows a function typed,
/// and a row chosen goes to its declaration. A Godot editor of its own each where one is found, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptOutlineTests
{
    private const string Script = "class_name Player\nextends Node\n\nsignal hurt(amount: int)\nenum State { IDLE, RUN }\nconst LIMIT: int = 3\n"
        + "@export var speed: float = 2.5\n\n\nfunc hit(target, amount := 1) -> void:\n\tpass\n\n\nclass Inner extends RefCounted:\n\tvar depth := 0\n\n"
        + "\tfunc dig() -> int:\n\t\treturn depth\n";

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GDScriptOutlineTests()
    {
        folder = TestFolders.Create("gdscript-outline-");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Outline\"\n");
        TestFolders.Write(folder, "player.gd", Script);
    }

    [AvaloniaFact]
    public void Structure_lists_a_scripts_members_as_written_follows_one_typed_and_goes_to_one_chosen()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, GodotEnvironment = GodotTests.Game.Home(folder + "-home") };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? held = godot is { Exists: true } ? window.Session.Web?.HoldNextConfigure(release.Task) : null;
        try
        {
            Assert.Null(window.OpenFile(Path.Combine(folder, "player.gd")));
            if (held is not null)
            {
                Ui.AwaitPumped(held, "Godot's GDScript server held at its start", TimeSpan.FromMinutes(1));
            }
            Ui.Run(window, AppActions.ShowStructure, settle: false);
            StructureView structure = window.StructureView;
            (string, string, int)[] expected =
            [
                ("hurt(amount: int)", string.Empty, 0), ("State", string.Empty, 0), ("IDLE", string.Empty, 1), ("RUN", string.Empty, 1), ("LIMIT", ": int", 0),
                ("speed", ": float", 0), ("hit(target, amount = 1)", ": void", 0), ("Inner", ": RefCounted", 0), ("depth", string.Empty, 1), ("dig()", ": int", 1),
            ];
            bool listed = Ui.Pumped(() => structure.Rows.Select(r => (r.Name, r.Detail ?? string.Empty, r.Depth)).SequenceEqual(expected), TimeSpan.FromSeconds(30));
            Assert.True(listed, $"The script's members in Structure: {Listed(structure)} ({structure.Said})");

            CodeEditor editor = window.EditorArea.CodeEditor;
            Ui.PlaceAfter(editor, "\tpass\n");
            editor.Focus();
            Ui.Press(window, "ENTER", settle: false);
            window.KeyTextInput("func grow(by: int) -> int:");
            Ui.Press(window, "ENTER", settle: false);
            Passenger.Text.TextSnapshot typed = editor.Document.Snapshot;
            int line = editor.CaretPosition.Line;
            editor.Select(new Passenger.Text.TextRange(typed.GetLineStart(line), typed.GetLineText(line).Length));
            window.KeyTextInput("\treturn by");
            bool followed = Ui.Pumped(() => structure.Rows.Any(r => r.Name == "grow(by: int)" && r.Detail == ": int" && r.Depth == 0), TimeSpan.FromSeconds(30));
            Assert.True(followed, $"Structure to follow the function typed: {Listed(structure)}");

            ListBox list = structure.GetVisualDescendants().OfType<ListBox>().First();
            // Found with the window's loop run, not settled: Godot's server's held start keeps it from settling.
            SymbolRow dig = structure.Rows.First(r => r.Name == "dig()");
            list.ScrollIntoView(dig);
            Control? row = null;
            Ui.PumpUntil(() => (row = list.ContainerFromItem(dig)) is not null, "dig's row shown", TimeSpan.FromSeconds(10));
            Ui.Click(row!, settle: false);
            bool went = Ui.Pumped(() => editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line).TrimStart().StartsWith("func dig", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            Assert.True(went, $"dig's row to go to its declaration: the caret is on {editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line)}");
            Assert.True(window.Session.Web?.For(window.EditorArea.Active!) is not { Serving: true }, "Godot's server answered: the test's hold of its start did not hold");
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    private static string Listed(StructureView structure) => string.Join(", ", structure.Rows.Select(r => $"{new string(' ', r.Depth)}{r.Name} {r.Detail}"));
}
