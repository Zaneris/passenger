using Passenger.App.Godot;

namespace Passenger.UnitTests.Godot;

/// <summary>Godot's console folded as a console takes it, a batch at a time: each error or warning (its header line and the
/// indented lines of its stack) shown where first seen and then only counted, with when it last came, whatever lines come
/// between; every other line kept as written; an entry a batch ends in going on in the next.</summary>
public class GodotConsoleFoldTests
{
    private const string Every = "ERROR: every frame\n   at: push_error (core/variant/variant_utility.cpp:1023)\n   GDScript backtrace (most recent call first):\n       [0] _process (res://main.gd:7)\n";

    private const string Once = "WARNING: once only\n     at: push_warning (core/variant/variant_utility.cpp:1033)\n     GDScript backtrace (most recent call first):\n         [0] _process (res://main.gd:9)\n";

    private static readonly DateTime Start = new(2026, 11, 2, 9, 0, 0, DateTimeKind.Utc);

    private static string Shown(string text, GodotFolded folded) => string.Concat(folded.Kept.Select(k => text.Substring(k.Start, k.Length)));

    [Fact]
    public void An_error_repeated_between_other_lines_shows_once_where_first_seen_and_is_counted_with_when_it_last_came()
    {
        GodotConsoleFold fold = new();
        string first = "Godot Engine v4.7.2\n" + Every + "frame 10\n" + Every + Once + Every;
        GodotFolded folded = fold.Take(first, Start);
        Assert.Equal("Godot Engine v4.7.2\n" + Every + "frame 10\n" + Once, Shown(first, folded));
        Assert.Equal([("ERROR: every frame", 3, false), ("WARNING: once only", 1, true)], fold.Entries.Select(e => (e.Header, e.Count, e.Warning)));
        Assert.Equal([20, 20 + Every.Length + 9], folded.Made.Select(m => m.At));

        string second = "frame 20\n" + Every;
        GodotFolded more = fold.Take(second, Start.AddSeconds(3));
        Assert.Equal("frame 20\n", Shown(second, more));
        Assert.Equal(fold.Entries[0], Assert.Single(more.Again));
        Assert.Equal((4, Start.AddSeconds(3)), (fold.Entries[0].Count, fold.Entries[0].Last));
    }

    [Fact]
    public void An_entry_a_batch_ends_in_goes_on_in_the_next_and_its_repeats_are_counted_whole()
    {
        GodotConsoleFold fold = new();
        string[] lines = Every.Split('\n');
        string head = lines[0] + "\n" + lines[1] + "\n";
        string rest = lines[2] + "\n" + lines[3] + "\n";
        Assert.Equal(head, Shown(head, fold.Take(head, Start)));
        Assert.Equal(rest + "frame 10\n", Shown(rest + "frame 10\n", fold.Take(rest + "frame 10\n", Start)));
        Assert.Equal(Every, Assert.Single(fold.Entries).Text);
        GodotFolded again = fold.Take(Every, Start.AddSeconds(1));
        Assert.Empty(again.Kept);
        Assert.Equal(2, fold.Entries[0].Count);
    }

    [Fact]
    public void The_games_own_lines_the_other_stream_puts_inside_an_entry_are_kept_and_break_it_not()
    {
        GodotConsoleFold fold = new();
        string[] lines = Every.Split('\n');
        string first = lines[0] + "\nframe 10\n" + string.Join('\n', lines[1..]);
        string printed = "frame 10\n";
        int game = first.IndexOf(printed, StringComparison.Ordinal);
        GodotFolded folded = fold.Take(first, Start, at => at != game);
        Assert.Equal(first, Shown(first, folded));
        Assert.Equal(Every, Assert.Single(fold.Entries).Text);
        string second = lines[0] + "\n" + lines[1] + "\nframe 20\n" + string.Join('\n', lines[2..]);
        int again = second.IndexOf("frame 20", StringComparison.Ordinal);
        Assert.Equal("frame 20\n", Shown(second, fold.Take(second, Start, at => at != again)));
        Assert.Equal(2, fold.Entries[0].Count);
    }

    [Fact]
    public void An_entry_a_batch_cuts_inside_a_line_is_whole_once_its_rest_comes_and_a_repeat_cut_so_is_counted()
    {
        GodotConsoleFold fold = new();
        int cut = Every.IndexOf("backtrace", StringComparison.Ordinal);
        string head = Every[..cut];
        string rest = Every[cut..] + "frame 10\n";
        Assert.Equal(head, Shown(head, fold.Take(head, Start)));
        Assert.Equal(rest, Shown(rest, fold.Take(rest, Start)));
        Assert.Equal(Every, Assert.Single(fold.Entries).Text);
        Assert.Empty(fold.Take(head, Start).Kept);
        Assert.Equal("frame 10\n", Shown(rest, fold.Take(rest, Start)));
        Assert.Equal(2, Assert.Single(fold.Entries).Count);
    }

    [Fact]
    public void Indented_lines_of_no_entry_and_an_entry_forgotten_show_as_written()
    {
        GodotConsoleFold fold = new();
        string text = "  indented by the game\n" + Every;
        Assert.Equal(text, Shown(text, fold.Take(text, Start)));
        fold.Forget(fold.Entries[0]);
        Assert.Equal(Every, Shown(Every, fold.Take(Every, Start)));
        Assert.True(GodotConsoleFold.IsHeader("SCRIPT ERROR: Invalid call"));
        Assert.False(GodotConsoleFold.IsHeader("ERRORS: none"));
    }
}
