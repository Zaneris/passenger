using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace Passenger.Inspector.Wpf;

/// <summary>The inspector inside a WPF app, started by the startup hook: it connects to Passenger and turns on WPF's binding
/// diagnostics before the app's Main (<see cref="Bindings"/>), waits for the app's UI thread (the dispatcher its Application
/// was made on, never one of the inspector's making), says hello, and answers each request there. It ends when Passenger goes.</summary>
public static class Agent
{
    /// <summary>How often it looks for the app's UI before it is up.</summary>
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(100);

    /// <summary>How long an answer to input waits for what it set going in an app whose UI thread is never idle.</summary>
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(1);

    /// <summary>A file in the app's environment, PASSENGER_INSPECTOR_TRACE=FILE: the inspector adds each step of its start and
    /// what it answers to it, for an inspector that never says hello or answers wrongly.</summary>
    public const string TraceVariable = "PASSENGER_INSPECTOR_TRACE";

    private static readonly string? TraceFile = Environment.GetEnvironmentVariable(TraceVariable) is { Length: > 0 } file ? file : null;

    private static readonly object Tracer = new();

    /// <summary>A step of the inspector's to the trace file, while <see cref="TraceVariable"/> names one.</summary>
    internal static void Trace(string step)
    {
        if (TraceFile is not { } file)
        {
            return;
        }
        try
        {
            lock (Tracer)
            {
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}/{Environment.CurrentManagedThreadId}] {step}{Environment.NewLine}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A trace that cannot be written is no failure of the app's.
        }
    }

    /// <param name="project">The launched project's folder (<see cref="Wire.ProjectVariable"/>), or null.</param>
    public static void Start(string socket, Thread main, string? project)
    {
        Trace($"connecting to {socket}");
        if (Connection.Open(socket) is not { } connection)
        {
            Trace("Passenger is not listening");
            return;
        }
        Sources.Project = project;
        string? problem = null;
        try
        {
            Bindings.Start(connection);
        }
        catch (Exception error)
        {
            // The tree and the agents' tools still serve an app whose binding errors cannot be heard.
            problem = $"The inspector could not listen to WPF's binding errors: {error.GetBaseException().Message}";
            Trace(problem);
        }
        Thread thread = new(() => Serve(connection, main, problem)) { IsBackground = true, Name = "Passenger inspector" };
        thread.Start();
        Trace($"waiting for the app's UI (the main thread has a dispatcher before Main: {Dispatcher.FromThread(main) is not null})");
    }

    private static void Serve(Connection connection, Thread main, string? problem)
    {
        using Connection held = connection;
        Dispatcher dispatcher;
        Inspector inspector;
        try
        {
            while (true)
            {
                dispatcher = WaitForUi(main);
                Trace($"the app's UI dispatcher is thread {dispatcher.Thread.ManagedThreadId}'s");
                try
                {
                    inspector = dispatcher.Invoke(() => new Inspector(connection));
                    Trace("made on the UI thread");
                    if (WaitForWindow(dispatcher, inspector))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Found while WPF was still making it (registered before its window, so it takes no work yet), or shut down.
                }
                Trace($"that dispatcher took no work (shut down: {dispatcher.HasShutdownStarted}); looking again");
                Thread.Sleep(Look);
            }
        }
        catch (Exception error)
        {
            Trace($"could not start: {error}");
            // Said, and written before the connection closes, so Passenger shows why rather than an app gone silent.
            connection.Send(new Reply { Event = Wire.Hello, Hello = new Hello(Wire.Version, Environment.ProcessId, null, $"The inspector could not start: {error.GetBaseException().Message}") { Framework = Framework() } });
            connection.Drain();
            Bindings.Stop();
            return;
        }
        if (problem is not null)
        {
            connection.Send(new Reply { Event = Wire.BindingFailed, Error = problem });
        }
        connection.Send(new Reply { Event = Wire.Hello, Hello = new Hello(Wire.Version, Environment.ProcessId, null, null) { Framework = Framework() } });
        Trace("said hello");
        while (connection.Read() is { } request)
        {
            _ = AnswerAsync(dispatcher, inspector, connection, request);
        }
        Trace("Passenger has gone");
        Bindings.Stop();
        dispatcher.BeginInvoke(inspector.Stop);
    }

    /// <summary>Until the app shows its first window (its Application is made before its StartupUri's window is), so what is
    /// asked once the app says hello finds it; an app that shows none within a while is said hello to anyway. False when the
    /// dispatcher shut down meanwhile, which then was not the app's.</summary>
    private static bool WaitForWindow(Dispatcher dispatcher, Inspector inspector)
    {
        DateTime until = DateTime.UtcNow + FirstWindow;
        while (DateTime.UtcNow < until)
        {
            if (dispatcher.HasShutdownStarted)
            {
                return false;
            }
            try
            {
                // Asked at Background, after the startup work queued before it; a paused or busy app is asked again.
                DispatcherOperation<int> shown = dispatcher.InvokeAsync(() => inspector.TopLevels().Count, DispatcherPriority.Background);
                if (shown.Task.Wait(TimeSpan.FromSeconds(1)) && shown.Result > 0)
                {
                    Trace($"the app shows {shown.Result} top level(s)");
                    return true;
                }
            }
            catch (Exception error) when (error is InvalidOperationException or TaskCanceledException or AggregateException)
            {
                Trace($"no window to wait for: {error.GetBaseException().Message}");
                return !dispatcher.HasShutdownStarted;
            }
            Thread.Sleep(Look);
        }
        return !dispatcher.HasShutdownStarted;
    }

    /// <summary>How long hello waits for the app's first window.</summary>
    private static readonly TimeSpan FirstWindow = TimeSpan.FromSeconds(30);

    /// <summary>WPF as the window names it, with the version of the WindowsDesktop runtime the app runs ("WPF 10.0.1").</summary>
    private static string Framework()
    {
        try
        {
            string? version = typeof(FrameworkElement).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            int plus = version?.IndexOf('+', StringComparison.Ordinal) ?? -1;
            return $"WPF {(plus > 0 ? version![..plus] : version ?? typeof(FrameworkElement).Assembly.GetName().Version?.ToString(3))}";
        }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            return "WPF";
        }
    }

    /// <summary>The app's UI dispatcher: the main thread's once WPF has made it there (as an Application or a window is made),
    /// else, for an app that makes its Application on a thread of its own, that Application's; read without making one.</summary>
    private static Dispatcher WaitForUi(Thread main)
    {
        for (int looked = 1; ; looked++)
        {
            if (Dispatcher.FromThread(main) is { HasShutdownStarted: false } dispatcher)
            {
                return dispatcher;
            }
            // Application's statics are read only after a while, when the main thread has made no dispatcher of its own.
            if (looked % 20 == 0 && Application.Current?.Dispatcher is { HasShutdownStarted: false } elsewhere)
            {
                return elsewhere;
            }
            if (looked % 50 == 0)
            {
                Trace($"still no dispatcher on the main thread (thread {main.ManagedThreadId}, {main.ThreadState})");
            }
            Thread.Sleep(Look);
        }
    }

    private static async Task AnswerAsync(Dispatcher dispatcher, Inspector inspector, Connection connection, Request request)
    {
        Reply reply;
        try
        {
            bool input = request.Method == Wire.Input;
            if (input)
            {
                Settling settled = await SettleInputAsync(dispatcher, inspector, request.Input);
                if (settled != Settling.Done)
                {
                    throw new Refusal(settled == Settling.Moving
                        ? "The popup is still moving: wait for its opening animation to finish before acting"
                        : "The app's UI is busy: input was not sent; wait for it to finish its current work before acting");
                }
            }
            reply = await dispatcher.InvokeAsync(() => inspector.Handle(request));
            if (input)
            {
                // An automation click runs at Input priority; its popup's layout and opening translation settle afterward.
                // A never-ending animation still answers the click that opened it within Busy; no input is replayed.
                await SettleInputAsync(dispatcher, inspector, null);
            }
        }
        catch (Exception error)
        {
            reply = new Reply { Id = request.Id, Error = error.Message };
        }
        Trace($"{request.Method} {request.Id}: {reply.Error ?? (reply.Windows is { } windows ? $"{windows.Count} window(s)" : reply.Tree is { } tree ? $"{tree.Roots.Count} root(s)" : "answered")}");
        connection.Send(reply);
    }

    private enum Settling { Done, Moving, Busy }

    private static async Task<Settling> SettleInputAsync(Dispatcher dispatcher, Inspector inspector, InputAction? action)
    {
        Stopwatch took = Stopwatch.StartNew();
        bool moving = false;
        while (took.Elapsed < Busy)
        {
            TimeSpan remaining = Busy - took.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return moving ? Settling.Moving : Settling.Busy;
            }
            DispatcherOperation<bool> queued = dispatcher.InvokeAsync(() => inspector.SettleInput(action), DispatcherPriority.Background);
            try
            {
                if (await queued.Task.WaitAsync(remaining))
                {
                    return Settling.Done;
                }
                moving = true;
            }
            catch (TimeoutException)
            {
                queued.Abort();
                return Settling.Busy;
            }
            // Poll the observed transform while allowing WPF's animation/render work to advance on its own dispatcher.
            await Task.Delay(TimeSpan.FromMilliseconds(16));
        }
        return moving ? Settling.Moving : Settling.Busy;
    }
}
