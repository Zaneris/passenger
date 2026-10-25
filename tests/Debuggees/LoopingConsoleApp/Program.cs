// A program that never ends on its own, for Pause and evaluation. With no argument it spins in managed code; with
// "sleep" it sleeps in each turn, so a pause lands outside managed code; it says when it is in its loop. With "count"
// a second thread counts while the first stops, to show an evaluation leaves the other threads paused; with
// "parallel" a loop's iterations run on several threads, and with "parallel throws" one of them throws. With "step"
// or "await" the first thread waits, in a call or an await, for a second to pass a line five times; with "waiting" it
// awaits so in a method whose builder tells a debugger which run it is only once that second thread has passed again,
// asked where the method goes on after the await ("waiting first": where it leaves at the await too); with "elsewhere"
// its await goes on on a third thread, where a call takes what the await gave. With "race" the first thread lets a second
// go in a call it steps over, and the second passes a line at once, so a step's end and another thread's breakpoint come
// together, either first; with "race throws" the second throws and catches an exception there too, and the call runs on
// a while longer each turn, so over the turns the step's end meets the exception's handler found.
if (args is ["count"])
{
    Thread counting = new(() =>
    {
        while (true)
        {
            Counter.Value++;
        }
    }) { IsBackground = true };
    counting.Start();
    Thread.Sleep(100);
    Slow slow = new();
    Console.WriteLine("counting"); // stop while counting
    return;
}
if (args is ["parallel", ..])
{
    bool throws = args is [_, "throws"];
    Parallel.For(0, 400, i =>
    {
        int square = i * i; // each iteration
        if (throws && i == 200)
        {
            try
            {
                throw new InvalidOperationException("iteration 200");
            }
            catch (InvalidOperationException)
            {
            }
        }
    });
    return;
}
if (args is ["step" or "await" or "waiting" or "elsewhere"] or ["waiting", "first"])
{
    Thread passing = new(() =>
    {
        for (long pass = 0; ; pass++)
        {
            Passes.Count = pass; // passed by another thread
        }
    }) { IsBackground = true };
    passing.Start();
    if (args is ["await"])
    {
        Passes.Await().GetAwaiter().GetResult();
        return;
    }
    if (args is ["waiting", ..])
    {
        WaitingBuilder.WaitsFirst = args is [_, "first"];
        Passes.AwaitWithWaitingBuilder().GetAwaiter().GetResult();
        return;
    }
    if (args is ["elsewhere"])
    {
        Passes.AwaitGoingOnElsewhere().GetAwaiter().GetResult();
        return;
    }
    Passes.Wait(5); // step over the call while another thread passes
    Console.WriteLine("stepped"); // the call's step ends here
    Console.WriteLine("ran on"); // a step lost runs on to here
    return;
}
if (args is ["race"] or ["race", "throws"])
{
    Race.Throws = args is [_, "throws"];
    new Thread(Race.Run) { IsBackground = true, Name = "Racing" }.Start();
    while (true)
    {
        Race.LetGo(); // step over the call that lets the racing thread go
        Race.WaitForPass(); // the step over the call that lets it go ends here
    }
}
long turns = 0;
bool sleeps = args is ["sleep"];
// Shown by its own ToString, which a thread paused in the sleep cannot run.
TimeSpan nap = TimeSpan.FromMilliseconds(20);
Console.WriteLine("looping");
while (true)
{
    turns++;
    if (sleeps)
    {
        Thread.Sleep(nap);
    }
}

internal static class Counter
{
    public static long Value;

    // Waits for the counting thread, which an evaluation leaves paused.
    public static long Next
    {
        get
        {
            long start = Value;
            while (Value == start)
            {
            }
            return Value;
        }
    }
}

internal class Slow
{
    public string Value
    {
        get
        {
            Thread.Sleep(300);
            return "slow";
        }
    }
}

internal static class Passes
{
    public static long Count;

    // Returns once the other thread has passed its line as many times again, having waited at least once.
    public static void Wait(int times)
    {
        long start = Volatile.Read(ref Count);
        do
        {
            Thread.Sleep(10); // wait for the other thread
        }
        while (Volatile.Read(ref Count) < start + times);
    }

    public static async Task Await()
    {
        await WaitOnAnotherThread(); // step over the await while another thread passes
        Console.WriteLine("awaited"); // the await's step ends here
        Console.WriteLine("ran on after the await"); // a step over the await lost runs on to here
    }

    // Apart from the await, whose line a breakpoint would otherwise take to mean the lambda's.
    private static Task WaitOnAnotherThread() => Task.Run(() => Wait(5));

    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(WaitingBuilder))]
    public static async Task AwaitWithWaitingBuilder()
    {
        await WaitOnAnotherThread(); // step over the await whose builder waits
        Console.WriteLine("awaited with a waiting builder"); // the waiting builder's step ends here
        Console.WriteLine("ran on past the waiting builder"); // a step over the waiting builder's await lost runs on to here
    }

    public static async Task AwaitGoingOnElsewhere()
    {
        string said = Say(await new ElsewhereAwaiter()); // step over the await that goes on elsewhere
        Console.WriteLine(said); // the step over the await that goes on elsewhere ends here
        Console.WriteLine("ran on past the await that went on elsewhere"); // a step over the await that goes on elsewhere lost runs on to here
    }

    private static string Say(string text) => $"said {text}";
}

internal static class Race
{
    public static bool Throws;
    public static long Passes;
    private static int go;
    private static int turn;

    // The racing thread's exception finds its handler a while after it is let go: this runs on, longer each turn (forty
    // lengths, over again), so over the turns the step over it ends as the handler is found.
    public static void LetGo()
    {
        Volatile.Write(ref go, 1);
        for (int spin = Throws ? turn++ % 40 * 600 : 0; spin > 0; spin--)
        {
        }
    }

    public static void WaitForPass()
    {
        while (Volatile.Read(ref go) == 1)
        {
        }
    }

    public static void Run()
    {
        while (true)
        {
            while (Volatile.Read(ref go) == 0)
            {
            }
            Passes++; // passed by the racing thread
            if (Throws)
            {
                try
                {
                    throw new RaceException();
                }
                catch (RaceException)
                {
                }
            }
            Volatile.Write(ref go, 0);
        }
    }
}

internal sealed class RaceException() : Exception("thrown by the racing thread");

// What an await waits for, done on a thread of its own, which goes on with the awaiting method there.
internal readonly struct ElsewhereAwaiter : System.Runtime.CompilerServices.ICriticalNotifyCompletion
{
    public ElsewhereAwaiter GetAwaiter() => this;

    public bool IsCompleted => false;

    public string GetResult() => "elsewhere";

    public void OnCompleted(Action continuation) => UnsafeOnCompleted(continuation);

    public void UnsafeOnCompleted(Action continuation) => new Thread(() => continuation()).Start();
}

// Task's own builder, but asked by a debugger which run of its method this is (Task's builder's ObjectIdForDebugger, an
// evaluation) on a thread other than the one the method began on (or on any, WaitsFirst), it answers only once the
// passing thread has passed again: an evaluation runs its thread alone, so the answer never comes while it runs, as one
// that waits on what a paused thread holds never comes.
internal struct WaitingBuilder
{
    public static bool WaitsFirst;

    private System.Runtime.CompilerServices.AsyncTaskMethodBuilder builder;
    private int beganOn;

    public static WaitingBuilder Create() =>
        new() { builder = System.Runtime.CompilerServices.AsyncTaskMethodBuilder.Create(), beganOn = Environment.CurrentManagedThreadId };

    public Task Task => builder.Task;

    internal object ObjectIdForDebugger
    {
        get
        {
            if (WaitsFirst || Environment.CurrentManagedThreadId != beganOn)
            {
                long passed = Volatile.Read(ref Passes.Count);
                SpinWait.SpinUntil(() => Volatile.Read(ref Passes.Count) > passed);
            }
            return builder.Task;
        }
    }

    public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine =>
        builder.Start(ref stateMachine);

    public void SetStateMachine(System.Runtime.CompilerServices.IAsyncStateMachine stateMachine) => builder.SetStateMachine(stateMachine);

    public void SetResult() => builder.SetResult();

    public void SetException(Exception exception) => builder.SetException(exception);

    public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : System.Runtime.CompilerServices.INotifyCompletion where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine =>
        builder.AwaitOnCompleted(ref awaiter, ref stateMachine);

    public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
        where TAwaiter : System.Runtime.CompilerServices.ICriticalNotifyCompletion where TStateMachine : System.Runtime.CompilerServices.IAsyncStateMachine =>
        builder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
}
