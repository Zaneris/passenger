using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Passenger.Core;

namespace Passenger.Inspector.Wpf;

/// <summary>The inspector inside a WPF app, started by the startup hook and served as either framework's is
/// (<see cref="Serving"/>): it turns on WPF's binding diagnostics before the app's Main (<see cref="Bindings"/>), and waits
/// for the app's UI thread (the dispatcher its Application was made on, never one of the inspector's making) and its first
/// window.</summary>
public static class Agent
{
    /// <summary>How often it looks for the app's UI while the app may be setting itself up (<see cref="Looking"/>).</summary>
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(100);

    /// <summary>How long the main thread has to make a dispatcher of its own before Application's statics are read.</summary>
    private static readonly TimeSpan MainThreadFirst = TimeSpan.FromSeconds(2);

    /// <summary>How often the trace says the main thread still has no dispatcher.</summary>
    private static readonly TimeSpan StillNone = TimeSpan.FromSeconds(5);

    /// <summary>How long an answer to input waits for what it set going in an app whose UI thread is never idle.</summary>
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(1);

    /// <param name="project">The launched project's folder (<see cref="Wire.ProjectVariable"/>), or null.</param>
    public static void Start(string socket, Thread main, string? project)
    {
        Sources.Project = project;
        Serving.Start(socket, new WpfUi(main));
    }

    /// <summary>Until the app shows its first window (its Application is made before its StartupUri's window is), so what is
    /// asked once the app says hello finds it; an app that shows none within a while is said hello to anyway. False when the
    /// dispatcher shut down meanwhile, which then was not the app's.</summary>
    private static bool WaitForWindow(Dispatcher dispatcher, Inspector inspector, Looking looking)
    {
        Deadline until = Deadline.In(FirstWindow);
        Task<int>? asked = null;
        while (!until.Passed)
        {
            if (dispatcher.HasShutdownStarted)
            {
                return false;
            }
            try
            {
                // Asked at Background, after the startup work queued before it; a paused or busy app is looked at until it answers.
                Task<int> shown = asked ??= dispatcher.InvokeAsync(() => inspector.TopLevels().Count, DispatcherPriority.Background).Task;
                if (shown.IsCompletedSuccessfully && shown.Result > 0)
                {
                    InspectorTrace.Step($"the app shows {shown.Result} top level(s)");
                    return true;
                }
                if (shown.IsFaulted || shown.IsCanceled)
                {
                    InspectorTrace.Step($"no window to wait for: {shown.Exception?.GetBaseException().Message ?? "the dispatcher took no work"}");
                    return !dispatcher.HasShutdownStarted;
                }
                if (shown.IsCompleted)
                {
                    // No window yet: asked again.
                    asked = null;
                }
            }
            catch (InvalidOperationException error)
            {
                InspectorTrace.Step($"no window to wait for: {error.Message}");
                return !dispatcher.HasShutdownStarted;
            }
            looking.Wait();
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
    /// else, for an app that makes its Application on a thread of its own, that Application's, read without making one once
    /// the main thread has had its while.</summary>
    private static Dispatcher WaitForUi(Thread main, Looking looking)
    {
        long began = Stopwatch.GetTimestamp();
        long said = began;
        while (true)
        {
            if (Dispatcher.FromThread(main) is { HasShutdownStarted: false } dispatcher)
            {
                return dispatcher;
            }
            if (Stopwatch.GetElapsedTime(began) >= MainThreadFirst && Application.Current?.Dispatcher is { HasShutdownStarted: false } elsewhere)
            {
                return elsewhere;
            }
            if (Stopwatch.GetElapsedTime(said) >= StillNone)
            {
                InspectorTrace.Step($"still no dispatcher on the main thread (thread {main.ManagedThreadId}, {main.ThreadState})");
                said = Stopwatch.GetTimestamp();
            }
            looking.Wait();
        }
    }

    /// <summary>A WPF app as the serve loop reaches it.</summary>
    private sealed class WpfUi(Thread main) : IAppUi
    {
        private Dispatcher? dispatcher;
        private Inspector? inspector;

        public void Listen(Connection connection)
        {
            InspectorTrace.Step($"the main thread has a dispatcher before Main: {Dispatcher.FromThread(main) is not null}");
            Bindings.Start(connection);
        }

        public void Open(Connection connection)
        {
            using Looking looking = new(Look);
            while (true)
            {
                Dispatcher found = WaitForUi(main, looking);
                InspectorTrace.Step($"the app's UI dispatcher is thread {found.Thread.ManagedThreadId}'s");
                try
                {
                    Inspector made = found.Invoke(() => new Inspector(connection));
                    InspectorTrace.Step("made on the UI thread");
                    if (WaitForWindow(found, made, looking))
                    {
                        (dispatcher, inspector) = (found, made);
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Found while WPF was still making it (registered before its window, so it takes no work yet), or shut down.
                }
                InspectorTrace.Step($"that dispatcher took no work (shut down: {found.HasShutdownStarted}); looking again");
                looking.Wait();
            }
        }

        public Hello Hello(string? problem) => new(Wire.Version, Environment.ProcessId, null, problem) { Framework = Framework() };

        public async Task<Reply> AnswerAsync(Request request)
        {
            Dispatcher ui = dispatcher!;
            Inspector served = inspector!;
            bool input = request.Method == Wire.Input;
            if (input)
            {
                // What changed since is laid out first (a popup just opened).
                await ui.InvokeAsync(served.Settle, DispatcherPriority.Loaded);
            }
            Reply reply = await ui.InvokeAsync(() => served.Handle(request));
            if (input)
            {
                // Once what it set going ran (an automation click comes at Input priority) and is laid out, within a moment.
                await Task.WhenAny(ui.InvokeAsync(served.Settle, DispatcherPriority.Background).Task, Task.Delay(Busy));
            }
            return reply;
        }

        public void Close()
        {
            Bindings.Stop();
            if (inspector is { } made)
            {
                dispatcher?.BeginInvoke(made.Stop);
            }
        }
    }
}
