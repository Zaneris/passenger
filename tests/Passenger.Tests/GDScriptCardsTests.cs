using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A GDScript's cards from Passenger's own reading, the hover's and a list's item's alike, Godot's server held at
/// its start where a Godot is found (never started where none is): a script's member says its signature or type and its
/// <c>##</c> doc comment, an engine's member its signature and the words of Godot's class reference, which Passenger dumps
/// from the Godot found once per version. A Godot editor of its own each where one is found, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptCardsTests
{
    private const string Script = "extends Node\n\n## How fast it spins, in turns a second.\n@export var speed: float = 2.5\n\n\n"
        + "## Grows the count by [param by], [b]times[/b] over.\nfunc grow(by: int = 1, times := 2) -> int:\n\treturn by * times\n\n\n"
        + "func _ready() -> void:\n\tspeed = grow(3)\n\tget_tree()\n";

    private readonly string folder;
    private readonly GodotFound? godot = GodotExecutables.Find(null);

    public GDScriptCardsTests()
    {
        folder = TestFolders.Create("gdscript-cards-");
        TestFolders.Write(folder, "project.godot", "; Engine configuration file.\nconfig_version=5\n\n[application]\n\nconfig/name=\"Cards\"\n");
        TestFolders.Write(folder, "main.gd", Script);
    }

    [AvaloniaFact]
    public void A_scripts_members_card_says_its_signature_and_its_doc_comment()
    {
        (MainWindow window, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            string card = HoverUntil(window, editor, "grow(3)", c => c.Contains("Grows", StringComparison.Ordinal));
            Assert.Contains("func grow(by: int = 1, times = 2) -> int", card, StringComparison.Ordinal);
            Assert.Contains("Grows the count by by, times over.", card, StringComparison.Ordinal);
            card = HoverUntil(window, editor, "speed = ", c => c.Contains("How fast", StringComparison.Ordinal));
            Assert.Contains("var speed: float", card, StringComparison.Ordinal);
            Assert.Contains("How fast it spins, in turns a second.", card, StringComparison.Ordinal);

            Ui.PlaceAfter(editor, "\tget_tree()");
            editor.Focus();
            Ui.Press(window, "ENTER", settle: false);
            window.KeyTextInput("g");
            window.KeyTextInput("ro");
            bool described = Ui.Pumped(() => window.Assist.ItemDocumentation?.Contains("Grows the count", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(60));
            Assert.True(described, $"grow's item to say its doc comment: {window.Assist.ItemDocumentation} (the list held {string.Join(", ", window.Assist.CompletionLabels.Take(8))})");
            Assert.Contains("func grow(", window.Assist.ItemDocumentation, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>An engine's member hovered says its signature as Godot writes it and its class reference's words, BBCode made
    /// words; a utility function's item in the list says its own.</summary>
    [AvaloniaFact]
    public void An_engines_members_card_says_its_signature_and_the_class_references_words()
    {
        Assert.SkipWhen(godot is not { Exists: true }, "No Godot was found (GODOT, the PATH, Applications): the class reference is dumped from it");
        (MainWindow window, TaskCompletionSource release) = Open();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            // The first card may come before the class reference has been dumped and read.
            string card = HoverUntil(window, editor, "get_tree", c => c.Contains("Returns the", StringComparison.Ordinal), TimeSpan.FromSeconds(120));
            Assert.Contains("func Node.get_tree() -> SceneTree", card, StringComparison.Ordinal);
            Assert.Contains("Returns the SceneTree that contains this node", card.Replace("\n", " ", StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("[SceneTree]", card, StringComparison.Ordinal);

            Ui.PlaceAfter(editor, "\tget_tree()");
            editor.Focus();
            Ui.Press(window, "ENTER", settle: false);
            window.KeyTextInput("p");
            window.KeyTextInput("rint_rich");
            bool described = Ui.Pumped(() => window.Assist.ItemDocumentation?.Contains("func print_rich(", StringComparison.Ordinal) == true
                && window.Assist.ItemDocumentation.Contains("BBCode", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
            Assert.True(described, $"print_rich's item to say its signature and words: {window.Assist.ItemDocumentation}");
            Assert.True(window.Session.Web?.For(window.EditorArea.Active!) is not { Serving: true }, "Godot's server answered: the test's hold of its start did not hold");
        }
        finally
        {
            window.Close();
            release.SetResult();
        }
    }

    /// <summary>The card shown with the pointer resting on some text, shown again until it says what is waited for, with the
    /// window's loop run meanwhile (Godot's server's held start keeps it from settling).</summary>
    private static string HoverUntil(MainWindow window, CodeEditor editor, string text, Func<string, bool> says, TimeSpan? within = null)
    {
        Deadline until = Deadline.In((within ?? TimeSpan.FromSeconds(30)).Scaled());
        string card = string.Empty;
        while (true)
        {
            window.MouseMove(new Point(0, 0), Avalonia.Input.RawInputModifiers.None);
            Ui.PumpUntil(() => window.Assist.HoverText is null, "the last card to go", TimeSpan.FromSeconds(30));
            int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal) + 1;
            Point point = editor.TranslatePoint(editor.PointOf(at) + new Point(1, editor.LineHeight / 2), window)!.Value;
            window.MouseMove(point, Avalonia.Input.RawInputModifiers.None);
            Ui.PumpUntil(() => window.Assist.HoverText is not null, $"the card on {text}", TimeSpan.FromSeconds(30));
            card = window.Assist.HoverText!;
            if (says(card) || until.Passed)
            {
                return card;
            }
            Ui.Hold(TimeSpan.FromSeconds(1), "the class reference read meanwhile");
        }
    }

    private (MainWindow Window, TaskCompletionSource Release) Open()
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
        return (window, release);
    }
}
