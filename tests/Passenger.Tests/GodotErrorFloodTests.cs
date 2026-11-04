using Avalonia.Headless.XUnit;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>A Godot game that raises an error every frame, a long session's worth of them: what a burst of them costs the
/// window's thread stays what it cost after a hundred, in Problems' Godot tab and in the console that folds them.</summary>
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
