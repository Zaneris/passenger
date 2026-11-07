using System.Runtime.ExceptionServices;

namespace Passenger.UnitTests;

/// <summary>Work on a stack a third of the thread pool's, where a reader of deep text is held to leave room: a reader that
/// runs out ends the process, the test run with it, as it would end the IDE.</summary>
internal static class SmallStack
{
    private const int Size = 512 * 1024;

    public static void Run(Action work)
    {
        ExceptionDispatchInfo? failed = null;
        Thread thread = new(() =>
        {
            try
            {
                work();
            }
            catch (Exception failure)
            {
                failed = ExceptionDispatchInfo.Capture(failure);
            }
        }, Size);
        thread.Start();
        thread.Join();
        failed?.Throw();
    }
}
