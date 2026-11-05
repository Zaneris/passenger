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

    private static string Shown(string text, GodotFolded folded) => folded.Held + string.Concat(folded.Kept.Select(k => text.Substring(k.Start, k.Length)));

    private const string PushError = "void Godot.GD.PushError(string) (/root/godot/modules/mono/glue/GodotSharp/GodotSharp/Core/GD.cs:366)";

    private const string Trampoline = "Godot.NativeInterop.godot_variant Main+GodotInternal.<GetGodotMethodTrampolines>g__trampoline_1__Process|0_0(object, "
        + "Godot.NativeInterop.NativeVariantPtrArgs, Godot.NativeInterop.godot_variant_call_error&) (C:\\games\\pins\\.godot\\mono\\temp\\obj\\Debug\\"
        + "Godot.SourceGenerators\\Godot.SourceGenerators.ScriptMethodsGenerator\\Main_ScriptMethods.generated.cs:38)";

    private const string CallViaTrampoline = "Godot.NativeInterop.godot_bool Godot.Bridge.CSharpInstanceBridge.CallViaTrampoline(Godot.NativeInterop.godot_variant("
        + "System.Object, Godot.NativeInterop.NativeVariantPtrArgs, Godot.NativeInterop.godot_variant_call_error&), nint, Godot.NativeInterop.godot_variant**, int, "
        + "Godot.NativeInterop.godot_variant_call_error*, Godot.NativeInterop.godot_variant*) (/root/godot/modules/mono/glue/GodotSharp/GodotSharp/Core/Bridge/CSharpInstanceBridge.cs:161)";

    /// <summary>A C# error as Godot writes it: its at: line, then its backtrace's frames numbered from 0.</summary>
    private static string Sharp(string message, string at, params string[] frames) =>
        $"ERROR: {message}\n   at: {at}\n   C# backtrace (most recent call first):\n" + string.Concat(frames.Select((f, i) => $"       [{i}] {f}\n"));

    private static string Process(int line) => $"void Main._Process(double) (C:\\games\\pins\\Main.cs:{line})";

    /// <summary>GD.PushError's error raised at a line of Main._Process as Godot writes it before the JIT inlines Godot's own
    /// frames: Godot's at: line names the caller, its backtrace starts at PushError.</summary>
    private static string Before(string message, int line) =>
        Sharp(message, $"void Main._Process(double) (res://Main.cs:{line})", PushError, Process(line), Trampoline, CallViaTrampoline);

    /// <summary>The same once the JIT inlined some of PushError's helpers: the at: line names the frame after the caller.</summary>
    private static string Between(string message, int line) =>
        Sharp(message, Trampoline.Replace("C:\\games\\pins\\", "res://", StringComparison.Ordinal).Replace('\\', '/'), PushError, Process(line), Trampoline, CallViaTrampoline);

    /// <summary>The same once PushError holds them all: its own frame gone from the backtrace, the at: line two frames past
    /// the caller.</summary>
    private static string After(string message, int line) => Sharp(message, CallViaTrampoline, Process(line), Trampoline, CallViaTrampoline);

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

    [Fact]
    public void One_error_is_one_entry_however_Godots_own_CSharp_frames_come_and_whatever_its_at_line_then_names()
    {
        GodotConsoleFold fold = new();
        string text = Before("Every frame", 10) + "frame 10\n" + Between("Every frame", 10) + After("Every frame", 10) + "frame 20\n" + After("Every frame", 10);
        Assert.Equal(Before("Every frame", 10) + "frame 10\nframe 20\n", Shown(text, fold.Take(text, Start)));
        Assert.Equal([("ERROR: Every frame", 4)], fold.Entries.Select(e => (e.Header, e.Count)));
        Assert.Equal(Before("Every frame", 10), fold.Entries[0].Text);
    }

    [Fact]
    public void Errors_at_other_lines_or_with_other_words_or_from_another_engine_function_stay_entries_of_their_own()
    {
        GodotConsoleFold fold = new();
        string engine = "ERROR: Parameter \"p_child\" is null.\n   at: add_child (scene/main/node.cpp:1684)\n   GDScript backtrace (most recent call first):\n       [0] _ready (res://main.gd:3)\n";
        string text = Before("Every frame", 10) + After("Every frame", 13) + Before("Only once", 10) + After("Only once", 10) + engine
            + engine.Replace("add_child (scene/main/node.cpp:1684)", "move_child (scene/main/node.cpp:1702)", StringComparison.Ordinal) + engine;
        fold.Take(text, Start);
        Assert.Equal([("ERROR: Every frame", 1), ("ERROR: Every frame", 1), ("ERROR: Only once", 2), ("ERROR: Parameter \"p_child\" is null.", 2),
            ("ERROR: Parameter \"p_child\" is null.", 1)], fold.Entries.Select(e => (e.Header, e.Count)));

        // With no backtrace (Godot before 4.5) the at: line is all there is of its place.
        GodotConsoleFold older = new();
        older.Take("ERROR: Every frame\n   at: void Main._Process(double) (res://Main.cs:10)\nERROR: Every frame\n   at: void Main._Process(double) (res://Main.cs:13)\n", Start);
        Assert.Equal(2, older.Entries.Count);
    }

    [Fact]
    public void An_error_a_batch_ends_inside_before_its_backtrace_is_counted_as_the_one_it_proves_to_be()
    {
        GodotConsoleFold fold = new();
        fold.Take(Before("Every frame", 10) + "frame 10\n", Start);
        string after = After("Every frame", 10);
        int backtrace = after.IndexOf("   C# backtrace", StringComparison.Ordinal);
        GodotFolded first = fold.Take(after[..backtrace], Start.AddSeconds(1));
        Assert.Equal(string.Empty, Shown(after[..backtrace], first));
        Assert.Equal(2, Assert.Single(fold.Entries).Count);
        string rest = after[backtrace..] + "frame 20\n";
        Assert.Equal("frame 20\n", Shown(rest, fold.Take(rest, Start.AddSeconds(2))));
        Assert.Equal((2, Start.AddSeconds(1)), (Assert.Single(fold.Entries).Count, fold.Entries[0].Last));

        // Cut inside a frame's line, and inside a line of Godot's own frame.
        string cut = after[..(after.IndexOf("Main._Process", StringComparison.Ordinal) + 4)];
        fold.Take(cut, Start);
        fold.Take(after[cut.Length..], Start);
        string inGodots = after[..(after.LastIndexOf("Godot.Bridge", StringComparison.Ordinal) + 3)];
        fold.Take(inGodots, Start);
        Assert.Equal("frame 30\n", Shown(after[inGodots.Length..] + "frame 30\n", fold.Take(after[inGodots.Length..] + "frame 30\n", Start)));
        Assert.Equal(4, Assert.Single(fold.Entries).Count);
    }

    [Fact]
    public void An_error_a_batch_ends_inside_that_proves_to_be_another_shows_whole_where_it_came_and_the_one_it_was_taken_for_is_counted_as_before()
    {
        GodotConsoleFold fold = new();
        fold.Take(Before("Every frame", 10), Start);
        string other = Before("Every frame", 20);
        int backtrace = other.IndexOf("   C# backtrace", StringComparison.Ordinal);
        Assert.Empty(fold.Take("frame 10\n" + other[..backtrace], Start.AddSeconds(1)).Kept.Skip(1));
        string rest = other[backtrace..] + "frame 20\n";
        GodotFolded shown = fold.Take(rest, Start.AddSeconds(2));
        Assert.Equal(other + "frame 20\n", Shown(rest, shown));
        Assert.Equal([(1, Start), (1, Start.AddSeconds(1))], fold.Entries.Select(e => (e.Count, e.Last)));
        Assert.Equal((fold.Entries[1], 0), Assert.Single(shown.Made));
        Assert.Contains(fold.Entries[0], shown.Again);
    }

    [Fact]
    public void An_entry_split_anywhere_by_two_batches_is_one_entry_shown_as_written_and_a_repeat_split_so_is_counted()
    {
        for (int cut = 1; cut < Every.Length; cut++)
        {
            GodotConsoleFold fold = new();
            string first = Every[..cut];
            string rest = Every[cut..];
            GodotFolded began = fold.Take(first, Start);
            GodotFolded ended = fold.Take(rest, Start);
            Assert.True(Shown(first, began) + Shown(rest, ended) == Every, $"Split after {first}, the console shows {Shown(first, began) + Shown(rest, ended)}");
            Assert.True(fold.Entries is [{ Header: "ERROR: every frame", Count: 1 }],
                $"Split after {first}, the fold holds {string.Join(" | ", fold.Entries.Select(e => $"{e.Header} x{e.Count}"))}: the Errors filter and its count would leave it out");

            GodotFolded again = fold.Take(first, Start.AddSeconds(1));
            GodotFolded over = fold.Take(rest, Start.AddSeconds(1));
            Assert.True(Shown(first, again) + Shown(rest, over) == string.Empty, $"A repeat split after {first} showed {Shown(first, again) + Shown(rest, over)}");
            Assert.True(fold.Entries is [{ Count: 2 }], $"A repeat split after {first} left the count at {fold.Entries[0].Count}");
        }
    }

    [Fact]
    public void A_header_split_shows_from_its_start_and_its_entry_is_placed_there()
    {
        GodotConsoleFold fold = new();
        Assert.Equal(string.Empty, Shown("ER", fold.Take("ER", Start)));
        const string Rest = "ROR: split header\n   at: res://main.gd:7\n";
        GodotFolded shown = fold.Take(Rest, Start);
        Assert.Equal("ER", shown.Held);
        Assert.Equal("ERROR: split header\n   at: res://main.gd:7\n", Shown(Rest, shown));
        Assert.Equal((fold.Entries[0], 0), Assert.Single(shown.Made));
    }

    [Fact]
    public void A_line_that_only_began_as_a_header_shows_as_written_and_the_entry_before_it_ends_there()
    {
        GodotConsoleFold fold = new();
        fold.Take(Every + "WAR", Start);
        const string Rest = "M UP\n   indented\n";
        Assert.Equal("WARM UP\n   indented\n", Shown(Rest, fold.Take(Rest, Start)));
        Assert.True(fold.Entries is [{ Text: Every, Count: 1 }], $"The entry before the line became {fold.Entries[0].Text}");

        string longer = "SCRIPT";
        Assert.Equal(string.Empty, Shown(longer, fold.Take(longer, Start)));
        Assert.Equal(string.Empty, Shown(" ERR", fold.Take(" ERR", Start)));
        Assert.Equal("SCRIPT ERROR: split twice\n", Shown("OR: split twice\n", fold.Take("OR: split twice\n", Start)));
        Assert.Equal("SCRIPT ERROR: split twice", fold.Entries[^1].Header);
    }

    [Fact]
    public void The_games_own_lines_between_the_halves_of_a_header_are_kept_and_the_header_is_read_whole()
    {
        GodotConsoleFold fold = new();
        fold.Take("ER", Start);
        const string Between = "frame 1\nROR: split header\n";
        GodotFolded shown = fold.Take(Between, Start, at => at >= "frame 1\n".Length);
        Assert.Equal("ERframe 1\nROR: split header\n", Shown(Between, shown));
        Assert.Equal("ERROR: split header", Assert.Single(fold.Entries).Header);

        fold.Take("ER", Start);
        Assert.Equal("frame 2\n", Shown("frame 2\n", fold.Take("frame 2\n", Start, _ => false)));
        Assert.Equal(string.Empty, Shown("ROR: split header\n", fold.Take("ROR: split header\n", Start)));
        Assert.Equal(2, fold.Entries[0].Count);
    }

    [Fact]
    public void The_start_of_a_line_whose_rest_never_comes_is_given_up_as_written_and_a_line_of_the_games_own_is_never_held()
    {
        GodotConsoleFold fold = new();
        Assert.Equal("frame 1\n", Shown("frame 1\nSCRIPT E", fold.Take("frame 1\nSCRIPT E", Start)));
        Assert.Equal("SCRIPT E", fold.LetGo());
        Assert.Null(fold.LetGo());
        Assert.Equal("ERROR\n", Shown("ERROR\n", fold.Take("ERROR\n", Start)));
        Assert.Empty(fold.Entries);

        const string Own = "WARN";
        Assert.Equal(Own, Shown(Own, fold.Take(Own, Start, _ => false)));
        Assert.Null(fold.LetGo());

        fold.Take("ER", Start);
        fold.Clear();
        Assert.Null(fold.LetGo());
    }
}
