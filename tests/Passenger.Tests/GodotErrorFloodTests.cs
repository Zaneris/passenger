using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App.Godot;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot game that raises an error every frame, a long session's worth of them: what a burst of them costs the
/// window's thread stays what it cost after a hundred, in Problems' Godot tab and in the console that folds them, which
/// folds an error whole wherever its reads end.</summary>
public sealed class GodotErrorFloodTests
{
    [AvaloniaFact]
    public void A_burst_of_a_games_errors_costs_Problems_no_more_after_thousands_before_it_than_after_a_hundred()
    {
        ProblemsModel problems = new();
        problems.BeginGodot();
        problems.Shown = ProblemsTab.Godot;
        Raise(problems, 0, 100);
        Ui.Settle();
        long small = Raise(problems, 100, 200);
        Raise(problems, 300, 5000);
        Ui.Settle();
        long large = Raise(problems, 5300, 200);
        Assert.True(large < small * 2 + 65536,
            $"200 errors raised after 5,300 allocated {large:N0} bytes on the window's thread, and after 100 {small:N0}: each error costs what every error before it does");
        Ui.Settle();
        Assert.Equal(5500, problems.GodotProblems.Count);
        Assert.Contains(problems.Rows, r => r.Problem?.Message == "frame 5499 went wrong");
    }

    [AvaloniaFact]
    public void A_burst_of_a_games_new_errors_costs_its_folded_console_no_more_after_thousands_before_it_than_after_a_hundred()
    {
        OutputView console = new() { Fold = new GodotConsoleFold() };
        Window window = new() { Width = 900, Height = 500, Content = console };
        window.Show();
        try
        {
            Ui.Settle();
            Write(console, 0, 100);
            Ui.Settle();
            long small = Write(console, 100, 100);
            Write(console, 200, 3000);
            Ui.Settle();
            long large = Write(console, 3200, 100);
            Assert.True(large < small * 2 + 65536,
                $"100 errors written after 3,200 allocated {large:N0} bytes on the window's thread, and after 100 {small:N0}: each batch walks every error folded before it");
            Assert.Contains("ERROR: frame 3299 went wrong", console.Document.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A flood's reads end anywhere: a header cut inside its first word shows whole with the rest of it and is an entry
    /// of the console's errors, and the start of a line whose rest never comes still shows, before what the console says next.</summary>
    [AvaloniaFact]
    public void An_error_cut_between_two_reads_shows_and_folds_whole_and_a_start_with_no_rest_shows_as_the_console_speaks()
    {
        OutputView console = new() { Fold = new GodotConsoleFold() };
        Window window = new() { Width = 900, Height = 500, Content = console };
        window.Show();
        try
        {
            Ui.Settle();
            console.Sink.Write("ER", ConsoleStream.Error);
            console.Flush();
            console.Sink.Write("ROR: cut between reads\n   at: res://main.gd:7\n", ConsoleStream.Error);
            console.Flush();
            const string Whole = "ERROR: cut between reads\n   at: res://main.gd:7\n";
            Assert.Equal(Whole, console.Document.Snapshot.ToString());
            Assert.True(console.Fold!.Entries is [{ Header: "ERROR: cut between reads" }],
                $"The fold holds {console.Fold.Entries.Count} entries: the Errors filter and its count leave out an error cut between two reads");

            console.Sink.Write("SCRIPT E", ConsoleStream.Error);
            console.Flush();
            console.Append("Process finished\n");
            Assert.Equal(Whole + "SCRIPT EProcess finished\n", console.Document.Snapshot.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A game's new errors written to its console a batch each, as it writes one a frame, and the bytes the
    /// window's thread allocated meanwhile.</summary>
    private static long Write(OutputView console, int first, int count)
    {
        string[] batches = [.. Enumerable.Range(first, count).Select(i => $"ERROR: frame {i} went wrong\n   at: push_error (core/variant/variant_utility.cpp:1023)\n")];
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (string batch in batches)
        {
            console.Sink.Write(batch, ConsoleStream.Error);
            console.Flush();
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Errors raised one after another, as a game raises them each frame, and the bytes the window's thread
    /// allocated meanwhile.</summary>
    private static long Raise(ProblemsModel problems, int first, int count)
    {
        Problem[] raised = [.. Enumerable.Range(first, count).Select(i => new Problem(ProblemSeverity.Error, $"frame {i} went wrong", null, "/game/main.gd", new TextPosition(6, 0), null))];
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (Problem problem in raised)
        {
            problems.RaiseGodot(problem);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
