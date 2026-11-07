using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Passenger.App;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A GDScript coloured by what its names are, as Passenger's own reading of it says, in C#'s colours: its functions
/// as methods, its variables as fields, its constants, signals, enums and their values, its classes and the engine's, an
/// engine method, and its locals and parameters in the text's own colour as C#'s are; each drawn in its token's colour in
/// both themes, and carried through a key until the next reading. No project.godot, so no Godot server starts; the engine's
/// names where a Godot is found to tell them.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptColoursTests : FileColoursFolder
{
    private const string Script = "class_name Hero\nextends Node\n\nsignal hit(amount)\nenum Mood { CALM, ANGRY }\nconst LIMIT = 3\nvar health := 10\n\n\n"
        + "func heal(by: int) -> void:\n\tvar gained := by * LIMIT\n\thealth += gained\n\thit.emit(gained)\n\tvar mood := Mood.ANGRY\n\tprint(mood, Hero, get_tree())\n";

    /// <summary>The one kind the first characters of a stretch's first occurrence are coloured as, or null for none or several.</summary>
    private static TokenKind? KindAt(CodeEditor editor, string stretch, int length)
    {
        Passenger.Text.TextSnapshot text = editor.Document.Snapshot;
        int start = text.ToString().IndexOf(stretch, StringComparison.Ordinal);
        TokenKind[] kinds = new TokenKind[length];
        Array.Fill(kinds, TokenKind.Text);
        foreach (SyntaxSpan span in editor.Highlighter!.GetSpans(text, start, length))
        {
            for (int i = Math.Max(span.Start, start); i < Math.Min(span.End, start + length); i++)
            {
                kinds[i - start] = span.Kind;
            }
        }
        return kinds.Distinct().ToArray() is [var one] ? one : null;
    }

    [AvaloniaFact]
    public void A_gdscripts_names_are_drawn_in_csharps_colours_for_what_they_are_in_both_themes()
    {
        bool engine = GodotExecutables.Find(null) is { Exists: true };
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            CodeEditor editor = Open(window, "hero.gd", Script);
            (string Stretch, TokenKind Kind)[] named =
            [
                ("heal(", TokenKind.Method),
                ("health +", TokenKind.Field),
                ("LIMIT\n", TokenKind.Constant),
                ("hit.", TokenKind.Event),
                ("Mood.", TokenKind.Enum),
                ("ANGRY\n", TokenKind.EnumMember),
                ("Hero,", TokenKind.Class),
                ("gained\n", TokenKind.Local),
                ("by *", TokenKind.Parameter),
                .. engine ? [("Node", TokenKind.Class), ("get_tree", TokenKind.Method), ("emit", TokenKind.Method)] : Array.Empty<(string, TokenKind)>(),
            ];
            string Name((string Stretch, TokenKind) n) => n.Stretch.TrimEnd('\n', ' ', '+', '.', ',', '*', '(');
            Ui.WaitFor(() => named.All(n => KindAt(editor, n.Stretch, Name(n).Length) == n.Kind),
                () => "each name in its kind's colour: " + string.Join(", ", named.Select(n => $"{Name(n)} {KindAt(editor, n.Stretch, Name(n).Length)} (not {n.Kind})")),
                TimeSpan.FromSeconds(90));
            foreach (ThemeVariant theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                window.RequestedThemeVariant = theme;
                string text = editor.Document.Snapshot.ToString();
                foreach ((string stretch, TokenKind kind) in named)
                {
                    string name = stretch.TrimEnd('\n', ' ', '+', '.', ',', '*', '(');
                    string token = kind is TokenKind.Local or TokenKind.Parameter ? TokenKeys.Editor_Text : $"Passenger.Syntax.{kind}";
                    Color expected = Token(theme, token);
                    Color ink = Ink(window, editor, text.IndexOf(stretch, StringComparison.Ordinal), name.Length);
                    Assert.True(Distance(ink, expected) < 90, $"In the {theme} theme {name} is drawn {ink}, not its {kind}'s {expected}");
                }
            }

            Ui.PlaceAfter(editor, "\thealth");
            editor.Focus();
            window.KeyTextInput("x");
            Assert.Equal(TokenKind.Field, KindAt(editor, "healthx", "healthx".Length));
            Ui.PumpUntil(() => KindAt(editor, "healthx", "healthx".Length) is not TokenKind.Field,
                "healthx, which no member is, no longer a field's colour once the script is read again", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A name replaced by one of its length loses its colour at the key. Typing on while that reading ran leaves it
    /// out of date, and the next reading, which gives the same name back its colour at the same place, still asks for its
    /// row to be drawn again: the layer's colours are compared as they were shown, not as they were before the name went.</summary>
    [AvaloniaFact]
    public void A_name_replaced_while_typing_overtook_its_reading_is_drawn_again_by_the_next_reading()
    {
        const string Wealthy = "extends Node\n\nvar health := 10\nvar wealth := 20\n\n\nfunc heal() -> void:\n\tprint(health)\n\tprint(1)\n";
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            CodeEditor editor = Open(window, "wealthy.gd", Wealthy);
            Ui.WaitFor(() => KindAt(editor, "health)", "health".Length) == TokenKind.Field, () => "health's use in a field's colour", TimeSpan.FromSeconds(90));
            int at = editor.Document.Snapshot.ToString().IndexOf("health)", StringComparison.Ordinal);

            Task held = window.Session.HoldNextScriptReading(release.Task);
            editor.Focus();
            editor.Select(new Passenger.Text.TextRange(at, "health".Length));
            window.KeyTextInput("wealth");
            Assert.NotEqual(TokenKind.Field, KindAt(editor, "wealth)", "wealth".Length));
            Ui.PumpUntil(() => held.IsCompleted, "the reading of the replaced name to be held", TimeSpan.FromSeconds(30));

            Ui.PlaceAfter(editor, "print(1");
            window.KeyTextInput("2");
            List<Passenger.Text.TextRange> redrawn = [];
            IHighlighter layer = editor.Highlighter!;
            void Heard(object? sender, EventArgs e)
            {
                if (layer.ChangedWithin is { } range)
                {
                    redrawn.Add(range);
                }
            }
            layer.Changed += Heard;
            release.SetResult();
            Ui.WaitFor(() => KindAt(editor, "wealth)", "wealth".Length) == TokenKind.Field, () => "wealth, a member too, in a field's colour once the script is read again",
                TimeSpan.FromSeconds(30));
            layer.Changed -= Heard;
            Assert.True(redrawn.Any(r => r.Start <= at && at < r.End), $"wealth's row was never asked to be drawn again, so it stays plain until something else draws it; asked: {string.Join(", ", redrawn)}");
        }
        finally
        {
            release.TrySetResult();
            window.Close();
        }
    }
}
