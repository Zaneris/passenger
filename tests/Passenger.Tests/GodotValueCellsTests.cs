using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App.Godot;
using Passenger.App.LiveTree;

namespace Passenger.Tests;

/// <summary>The properties pane's value editors over a stand-in for what holds the value, a game's answer held back as
/// long as a test needs: an item form's changes go one at a time, each made on what the one before left, whichever way the
/// person moved on, and a dictionary's Add gives a key of its type. No game runs: what reaches a game is held by
/// GodotValueFormsTests.</summary>
public sealed class GodotValueCellsTests
{
    /// <summary>What a form sends, each held until the test lets it land as the game's answer.</summary>
    private sealed class Sends
    {
        private readonly Queue<(object? Value, TaskCompletionSource<object?> Answer)> held = new();

        public List<object?> Sent { get; } = [];

        public int Waiting => held.Count;

        public Task<object?> SendAsync(object? value)
        {
            Sent.Add(value);
            TaskCompletionSource<object?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            held.Enqueue((value, answer));
            return answer.Task;
        }

        /// <summary>The first send held lands, the game holding what was sent.</summary>
        public void Land()
        {
            (object? value, TaskCompletionSource<object?> answer) = held.Dequeue();
            answer.SetResult(value);
        }

        /// <summary>The first send held is refused.</summary>
        public void Refuse(string why) => held.Dequeue().Answer.SetException(new LiveAppException(why));
    }

    private sealed class Tuner : IGodotTuner
    {
        public List<string> Said { get; } = [];

        public void Settled(long element)
        {
        }

        public void Keep(GodotTuned tuned, Control from)
        {
        }

        public void ShowPopup(Control popup, Control from)
        {
        }

        public void ClosePopup()
        {
        }

        public void Say(string words) => Said.Add(words);
    }

    private static GodotItemsEditor Form(object? value, GodotHint hint, Sends sends, Tuner tuner, GodotType type = GodotType.Array)
    {
        GodotItemsEditor form = new("waves", type, value, hint, tuner, sends.SendAsync, () => { }, (_, _) => new Border());
        new Window { Width = 500, Height = 500, Content = form }.Show();
        Ui.Settle();
        return form;
    }

    private static readonly GodotHint Ints = new(GodotEditing.Items) { Element = GodotType.Int };

    private static GodotArray IntArray(params long[] items) => new(items.Cast<object?>()) { Element = GodotElement.Of(GodotType.Int) };

    /// <summary>A field typed into from the start, and Enter.</summary>
    private static void Type(TextBox field, string text)
    {
        Window window = (Window)TopLevel.GetTopLevel(field)!;
        Ui.WaitFor(() => field.IsEffectivelyVisible && field.Focus() && field.IsFocused, () => $"the field showing {field.Text} to take the keys");
        field.SelectAll();
        window.KeyTextInput(text);
        Ui.Press(window, "ENTER");
    }

    private static Button Remove(GodotItemsEditor form, string tip) =>
        form.GetVisualDescendants().OfType<Button>().First(b => Equals(ToolTip.GetTip(b), tip));

    [AvaloniaFact]
    public void An_item_added_while_an_edit_is_on_its_way_is_added_to_the_edit()
    {
        Sends sends = new();
        GodotItemsEditor form = Form(IntArray(1), Ints, sends, new Tuner());
        Type(form.Fields[0], "9");
        Assert.Equal(1, sends.Waiting);

        Ui.Click(form.Add);
        Assert.True(sends.Sent.Count == 1, "Add went out over the edit still on its way, from the value before it");

        sends.Land();
        Ui.WaitFor(() => sends.Waiting == 1, () => $"Add's turn, once the edit landed ({sends.Sent.Count} sent)");
        Assert.Equal([9L, 0L], Assert.IsType<GodotArray>(sends.Sent[^1]));
        sends.Land();
        Ui.WaitFor(() => form.Fields.Count == 2, "the rows to show what the game holds");
        Assert.Equal("9", form.Fields[0].Text);
    }

    [AvaloniaFact]
    public void Each_row_removed_while_the_one_before_is_on_its_way_is_the_row_that_was_asked_for()
    {
        Sends sends = new();
        GodotItemsEditor form = Form(IntArray(10, 20, 30, 40), Ints, sends, new Tuner());
        Button second = Remove(form, "Remove item 1");
        Button last = Remove(form, "Remove item 3");

        Ui.Click(second);
        Ui.Click(last);
        Assert.True(sends.Sent.Count == 1, "The second removal went out over the first, still on its way");

        sends.Land();
        Ui.WaitFor(() => sends.Waiting == 1, () => $"the second removal's turn ({sends.Sent.Count} sent)");
        Assert.Equal([10L, 30L], Assert.IsType<GodotArray>(sends.Sent[^1]));
    }

    [AvaloniaFact]
    public void An_edit_of_a_row_the_person_moved_past_is_sent_after_the_one_on_its_way()
    {
        Sends sends = new();
        GodotItemsEditor form = Form(IntArray(1, 2), Ints, sends, new Tuner());
        TextBox second = form.Fields[1];
        Type(form.Fields[0], "7");
        Type(second, "8");
        Assert.True(sends.Sent.Count == 1, "The second edit went out over the first, still on its way");

        sends.Land();
        Ui.WaitFor(() => sends.Waiting == 1, () => $"the second edit's turn ({sends.Sent.Count} sent)");
        Assert.Equal([7L, 8L], Assert.IsType<GodotArray>(sends.Sent[^1]));
    }

    [AvaloniaFact]
    public void A_removal_the_game_refuses_leaves_the_rows_and_what_waits_behind_it_acts_on_the_row_it_asked_for()
    {
        Sends sends = new();
        Tuner tuner = new();
        GodotItemsEditor form = Form(IntArray(10, 20, 30), Ints, sends, tuner);
        Button first = Remove(form, "Remove item 0");
        Button last = Remove(form, "Remove item 2");

        Ui.Click(first);
        Ui.Click(last);
        sends.Refuse("the game said no");
        Ui.WaitFor(() => sends.Waiting == 1, () => $"the second removal's turn ({sends.Sent.Count} sent; {string.Join(", ", tuner.Said)})");
        Assert.Contains("waves was not set: the game said no", tuner.Said);
        Assert.Equal([10L, 20L], Assert.IsType<GodotArray>(sends.Sent[^1]));
    }

    [AvaloniaFact]
    public void Add_on_a_dictionary_of_bool_keys_sends_the_key_it_lacks_and_says_so_where_it_has_both()
    {
        GodotHint hint = new(GodotEditing.Entries) { Key = GodotType.Bool, Element = GodotType.Int };
        GodotDictionary typed = new([new(true, 1L)]) { KeyType = GodotElement.Of(GodotType.Bool), ValueType = GodotElement.Of(GodotType.Int) };
        Sends sends = new();
        Tuner tuner = new();
        GodotItemsEditor form = Form(typed, hint, sends, tuner, GodotType.Dictionary);

        Ui.Click(form.Add);
        GodotDictionary sent = Assert.IsType<GodotDictionary>(sends.Sent[^1]);
        Assert.Equal([true, false], sent.Entries.Select(e => e.Key));
        Assert.Equal(typed.KeyType, sent.KeyType);
        sends.Land();
        Ui.WaitFor(() => form.Fields.Count == 4, "the two entries' rows");

        Ui.Click(form.Add);
        Assert.True(sends.Sent.Count == 1, "A dictionary of bool keys holding both has no key left to send");
        Assert.Contains("No new bool key can be made for waves", tuner.Said);
    }
}
