using Microsoft.VisualStudio.Threading;

namespace Passenger.App;

/// <summary>Async work run to its end by a caller that must stay synchronous: Main and what it calls as the process ends,
/// and a library's callback that must answer before it returns. The caller's thread waits while JoinableTaskFactory runs
/// the work's continuations, so none waits for the thread it holds. Each caller says why it cannot await.</summary>
internal static class Synchronously
{
    private static readonly JoinableTaskFactory Factory = new(new JoinableTaskContext());

    public static void Run(Func<Task> work) => Factory.Run(work);

    public static T Run<T>(Func<Task<T>> work) => Factory.Run(work);
}
