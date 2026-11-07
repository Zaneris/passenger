using System.Runtime.CompilerServices;

namespace Passenger.Xaml.Syntax;

/// <summary>Whether a walk of a tree may go a level deeper on the stack its thread has left. How much a level takes is the
/// platform's and the build's (a Debug build on Windows x64 takes several times what one on macOS does), so the walk asks,
/// rather than trusting <see cref="XmlTree.DeepestNesting"/> to fit: one short of stack gives the answer it gives past that.
/// Each shortfall is counted on its thread, so what asked a walk can say it read in part.</summary>
public static class StackLeft
{
    [ThreadStatic]
    private static int shortfalls;

    /// <summary>How many walks on this thread have stopped short of stack.</summary>
    public static int Shortfalls => shortfalls;

    /// <summary>Whether there is stack enough for a level more; a level without is counted.</summary>
    public static bool Enough()
    {
        if (RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            return true;
        }
        shortfalls++;
        return false;
    }
}
