using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Passenger.App;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A GDScript coloured by what its names are, as Passenger's own reading of it says, in C#'s colours, carried
/// through a key until the next reading draws a name typing replaced. No project.godot, so no Godot server starts; the
/// engine's names where a Godot is found to tell them.</summary>
[Collection(Alone.Name)]
public sealed class GDScriptColoursTests : FileColoursFolder
{

    /// <summary>The one kind the first characters of a stretch's first occurrence are coloured as, or null for none or several.</summary>
    internal static TokenKind? KindAt(CodeEditor editor, string stretch, int length)
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

    /// <summary>A GDScript's names are drawn in C#'s colours for what they are, drawn again by the next reading where
    /// typing overtook one.</summary>
    [AvaloniaFact]
    public void Gdscript_names_are_drawn_in_csharps_colours()
    {
        Flow.StepApart<GDScriptColoursTests>(nameof(A_name_replaced_while_typing_overtook_its_reading_is_drawn_again_by_the_next_reading),
            test => test.A_name_replaced_while_typing_overtook_its_reading_is_drawn_again_by_the_next_reading());
    }

    /// <summary>A name replaced by one of its length loses its colour at the key. Typing on while that reading ran leaves it
    /// out of date, and the next reading, which gives the same name back its colour at the same place, still asks for its
    /// row to be drawn again: the layer's colours are compared as they were shown, not as they were before the name went.</summary>
    private void A_name_replaced_while_typing_overtook_its_reading_is_drawn_again_by_the_next_reading()
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
