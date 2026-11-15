using System.Reflection;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Passenger.Core;

namespace Passenger.Inspector.Maui;

/// <summary>The inspector inside a .NET MAUI app, started by the startup hook and served as every framework's is
/// (<see cref="Serving"/>): it waits for the app's Application and its first window with a page shown, and answers on the
/// thread that window's dispatcher runs, the app's UI thread.</summary>
public static class Agent
{
    /// <summary>How often it looks for the app's UI while the app may be setting itself up (<see cref="Looking"/>).</summary>
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(100);

    /// <summary>How long an answer to input waits for what it set going in an app whose UI thread is never idle.</summary>
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(1);

    /// <summary>How long after input its answer waits for the frame that lays out what the input set going.</summary>
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(50);

    /// <summary>How long hello waits for the app's first window to show a page.</summary>
    private static readonly TimeSpan FirstWindow = TimeSpan.FromSeconds(60);

    /// <param name="project">The launched project's folder (<see cref="Wire.ProjectVariable"/>), which the places MAUI records
    /// of its XAML are under, or null.</param>
    public static void Start(string socket, Assembly maui, string? project)
    {
        Shown.Project = project;
        Serving.Start(socket, new MauiUi(Framework(maui)));
    }

    /// <summary>MAUI as the window names it, with its controls' version ("MAUI 11.0.0").</summary>
    private static string Framework(Assembly maui)
    {
        string? version = maui.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        int plus = version?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        return $"MAUI {(plus > 0 ? version![..plus] : version ?? maui.GetName().Version?.ToString(3))}";
    }

    /// <summary>The app's Application once it has a window whose page its platform has made, and that window's dispatcher;
    /// an app that shows none within a while is answered with what it has, or nothing yet.</summary>
    private static IDispatcher WaitForUi(Looking looking)
    {
        Deadline until = Deadline.In(FirstWindow);
        while (true)
        {
            if (Showing() is (IDispatcher dispatcher, bool shows) && (shows || until.Passed))
            {
                return dispatcher;
            }
            looking.Wait();
        }
    }

    /// <summary>The dispatcher of the app's Application, and whether a window of it shows a page its platform has made; null
    /// before the app has made its Application. Read off the UI thread while the app sets itself up, so a list changing
    /// under the read is read again.</summary>
    private static (IDispatcher Dispatcher, bool Shows)? Showing()
    {
        try
        {
            if (Application.Current is not { Dispatcher: { } dispatcher } app)
            {
                return null;
            }
            bool shows = app.Windows.Any(w => w.Handler is not null && w.Page?.Handler is not null);
            return (dispatcher, shows);
        }
        catch (InvalidOperationException error)
        {
            InspectorTrace.Step($"the app's windows could not be read yet: {error.Message}");
            return null;
        }
    }

    /// <summary>Runs work on the app's UI thread and waits for it there, on the serving thread.</summary>
    private static T Invoke<T>(IDispatcher dispatcher, Func<T> work)
    {
        using ManualResetEventSlim done = new();
        T? result = default;
        Exception? failed = null;
        bool dispatched = dispatcher.Dispatch(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception error)
            {
                // Handed back to the serving thread, which says why the inspector could not start.
                failed = error;
            }
            finally
            {
                done.Set();
            }
        });
        if (!dispatched)
        {
            throw new InvalidOperationException("The app's dispatcher took no work");
        }
        done.Wait();
        return failed is null ? result! : throw new TargetInvocationException(failed);
    }

    /// <summary>A MAUI app as the serve loop reaches it.</summary>
    private sealed class MauiUi(string framework) : IAppUi
    {
        private IDispatcher? dispatcher;
        private Inspector? inspector;

        public void Listen(Connection connection)
        {
            // A MAUI app's failed bindings are not heard yet; the tree and the agents' tools serve it all the same.
        }

        public void Open(Connection connection)
        {
            using Looking looking = new(Look);
            IDispatcher found = WaitForUi(looking);
            inspector = Invoke(found, () => new Inspector(connection));
            dispatcher = found;
            InspectorTrace.Step("made on the UI thread");
        }

        public Hello Hello(string? problem) => new(Wire.Version, Environment.ProcessId, null, problem) { Framework = framework };

        public async Task<Reply> AnswerAsync(Request request)
        {
            IDispatcher ui = dispatcher!;
            Inspector served = inspector!;
            if (request.Method == Wire.Capture)
            {
                return await ui.DispatchAsync(() => served.CaptureAsync(request));
            }
            Reply reply = await ui.DispatchAsync(() => served.Handle(request));
            if (request.Method == Wire.Input && reply.Done is { } done)
            {
                // Once what it set going ran (a click the platform posts as the touch ends) and a frame laid it out, within a moment.
                await Task.WhenAny(Settled(ui), Task.Delay(Busy));
                reply = reply with { Done = await ui.DispatchAsync(() => served.Read(done)) };
            }
            return reply;
        }

        public void Close()
        {
            if (inspector is { } made)
            {
                dispatcher?.Dispatch(made.Stop);
            }
        }

        /// <summary>Done once the UI thread has run what was queued after the input, and once more after a frame.</summary>
        private static async Task Settled(IDispatcher ui)
        {
            await ui.DispatchAsync(static () => { });
            TaskCompletionSource framed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!ui.DispatchDelayed(Frame, () => framed.TrySetResult()))
            {
                return;
            }
            await framed.Task;
        }
    }
}
