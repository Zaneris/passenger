using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Problems' File tab put in order as a server's diagnostics land after each typing pause: listing a file's
/// thousands of problems again costs each a few small allocations, never one for each comparison of their sort.</summary>
[Collection(Beside.Name)]
public sealed class ProblemsOrderTests
{
    [Fact]
    public void Listing_a_files_thousands_of_problems_again_allocates_nothing_for_each_comparison_of_their_sort()
    {
        long few = Listed(300);
        long many = Listed(3000);
        long perProblem = (many - few) / 2700;
        TestContext.Current.TestOutputHelper?.WriteLine($"Listing a file's problems again allocated {few / 1024} KB for 300, {many / 1024} KB for 3,000");
        Assert.True(perProblem < 256,
            $"Listing a file's 3,000 problems again allocated {many / 1024} KB, its 300 {few / 1024} KB: {perProblem} bytes for each problem more, as if their sort boxed what it compares");
    }

    /// <summary>What listing a file's problems again allocates on this thread, the same problems made again and come in
    /// another order, the least of three.</summary>
    private static long Listed(int count)
    {
        ProblemsModel problems = new();
        problems.ShowFile("Warned.cs", Made(count, order: 0));
        long least = long.MaxValue;
        for (int time = 1; time <= 3; time++)
        {
            Problem[] again = Made(count, order: time);
            long before = GC.GetAllocatedBytesForCurrentThread();
            problems.ShowFile("Warned.cs", again);
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        Assert.Equal(count, problems.FileProblems.Count);
        return least;
    }

    /// <summary>A file's problems, errors and warnings by turns, each on a line of its own, in the order a shuffle seeded by
    /// <paramref name="order"/> gives.</summary>
    private static Problem[] Made(int count, int order)
    {
        Problem[] made = new Problem[count];
        for (int i = 0; i < count; i++)
        {
            made[i] = new Problem(i % 3 == 0 ? ProblemSeverity.Error : ProblemSeverity.Warning, $"The variable 'unused{i}' is declared but never used",
                "CS0168", "/project/Warned.cs", new TextPosition(i + 4, 12), "Warned");
        }
        new Random(order).Shuffle(made);
        return made;
    }
}
