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

    /// <summary>An error repeated shows once where first seen, counted, as one entry however Godot's frames
    /// come.</summary>
    [Fact]
    public void A_repeated_error_shows_once_counted() => Cases.All(
        (nameof(An_error_repeated_between_other_lines_shows_once_where_first_seen_and_is_counted_with_when_it_last_came),
            An_error_repeated_between_other_lines_shows_once_where_first_seen_and_is_counted_with_when_it_last_came),
        (nameof(An_entry_a_batch_ends_in_goes_on_in_the_next_and_its_repeats_are_counted_whole),
            An_entry_a_batch_ends_in_goes_on_in_the_next_and_its_repeats_are_counted_whole),
        (nameof(One_error_is_one_entry_however_Godots_own_CSharp_frames_come_and_whatever_its_at_line_then_names),
            One_error_is_one_entry_however_Godots_own_CSharp_frames_come_and_whatever_its_at_line_then_names));

    /// <summary>An entry or header split anywhere by batches is one entry shown as written, the game's own lines
    /// kept.</summary>
    [Fact]
    public void An_entry_split_by_batches_is_whole_once_its_rest_comes() => Cases.All(
        (nameof(An_entry_split_anywhere_by_two_batches_is_one_entry_shown_as_written_and_a_repeat_split_so_is_counted),
            An_entry_split_anywhere_by_two_batches_is_one_entry_shown_as_written_and_a_repeat_split_so_is_counted),
        (nameof(The_start_of_a_line_whose_rest_never_comes_is_given_up_as_written_and_a_line_of_the_games_own_is_never_held),
            The_start_of_a_line_whose_rest_never_comes_is_given_up_as_written_and_a_line_of_the_games_own_is_never_held));

    private void An_error_repeated_between_other_lines_shows_once_where_first_seen_and_is_counted_with_when_it_last_came()
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

    private void An_entry_a_batch_ends_in_goes_on_in_the_next_and_its_repeats_are_counted_whole()
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

    private void One_error_is_one_entry_however_Godots_own_CSharp_frames_come_and_whatever_its_at_line_then_names()
    {
        GodotConsoleFold fold = new();
        string text = Before("Every frame", 10) + "frame 10\n" + Between("Every frame", 10) + After("Every frame", 10) + "frame 20\n" + After("Every frame", 10);
        Assert.Equal(Before("Every frame", 10) + "frame 10\nframe 20\n", Shown(text, fold.Take(text, Start)));
        Assert.Equal([("ERROR: Every frame", 4)], fold.Entries.Select(e => (e.Header, e.Count)));
        Assert.Equal(Before("Every frame", 10), fold.Entries[0].Text);
    }

    private void An_entry_split_anywhere_by_two_batches_is_one_entry_shown_as_written_and_a_repeat_split_so_is_counted()
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

    private void The_start_of_a_line_whose_rest_never_comes_is_given_up_as_written_and_a_line_of_the_games_own_is_never_held()
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
