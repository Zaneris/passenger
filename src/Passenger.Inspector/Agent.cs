using System.Reflection;
using Avalonia;
using Avalonia.Threading;

namespace Passenger.Inspector;

/// <summary>The inspector inside the app, started by the startup hook in its own context and served as either framework's
/// is (<see cref="Serving"/>): it takes Avalonia's logger for the app's failed bindings before the app's Main, and waits
/// for the app's UI thread to exist, never touching the dispatcher before the app makes it, which would make the wrong
/// thread the UI's.</summary>
public static class Agent
{
    /// <summary>How often it looks for the app's UI before it is up.</summary>
    private static readonly TimeSpan Look = TimeSpan.FromMilliseconds(100);

    /// <summary>The app's UI dispatcher, once the app has made it.</summary>
    internal static Dispatcher? Ui { get; private set; }

    /// <summary>The inspector on the app's UI thread, once made.</summary>
    internal static Inspector? Current { get; private set; }

    /// <summary>Why the app keeps a top level from an agent's input, else null (<see cref="Guard"/>).</summary>
    internal static Func<object, string?>? Refusing { get; private set; }

    /// <summary>Keeps the agent's input from the top levels the app names a reason for, called with each one input
    /// would reach. Set before <see cref="Start"/>, on the app's UI thread.</summary>
    public static void Guard(Func<object, string?> refusing) => Refusing = refusing;

    /// <param name="overlays">The renderer debug overlays the launch named (<see cref="Wire.OverlaysVariable"/>), or null.</param>
    public static void Start(string socket, Thread main, string avalonia, string? overlays) =>
        Serving.Start(socket, new AvaloniaUi(main, avalonia, overlays));

    /// <summary>The app's UI dispatcher once the app has made it and its Application: the main thread's in a desktop app, or
    /// whichever thread made Avalonia's, read without making one.</summary>
    private static Dispatcher WaitForUi(Thread main)
    {
        FieldInfo? made = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.NonPublic | BindingFlags.Static);
        while (true)
        {
            if ((Dispatcher.FromThread(main) ?? made?.GetValue(null) as Dispatcher) is { } dispatcher && HasApplication())
            {
                return dispatcher;
            }
            Thread.Sleep(Look);
        }
    }

    /// <summary>Whether the app's Application is up, after which its dispatcher has its platform's loop.</summary>
    internal static bool HasApplication()
    {
        try
        {
            return Application.Current is not null;
        }
        catch (Exception error)
        {
            // Read off the UI thread while the app is setting up: asked again in a moment.
            InspectorTrace.Step($"the app's Application could not be read yet: {error}");
            return false;
        }
    }

    /// <summary>An Avalonia app as the serve loop reaches it.</summary>
    private sealed class AvaloniaUi(Thread main, string avalonia, string? overlays) : IAppUi
    {
        private Dispatcher? dispatcher;
        private Inspector? inspector;

        public void Listen(Connection connection) => Bindings.Start(connection);

        public void Open(Connection connection)
        {
            dispatcher = WaitForUi(main);
            Ui = dispatcher;
            inspector = dispatcher.Invoke(() => Current = new Inspector(connection, overlays));
        }

        public Hello Hello(string? problem) => new(Wire.Version, Environment.ProcessId, avalonia, problem);

        public async Task<Reply> AnswerAsync(Request request)
        {
            Dispatcher ui = dispatcher!;
            Inspector served = inspector!;
            bool input = request.Method == Wire.Input;
            if (input)
            {
                // Hit testing reads the scene last rendered, so what changed since (a popup opening) is drawn first.
                await ui.InvokeAsync(served.RenderedAsync);
            }
            Reply reply = await ui.InvokeAsync(() => served.Handle(request));
            if (input)
            {
                // Answered once what the input set going is laid out and drawn, so what the agent looks at next shows it.
                await ui.InvokeAsync(served.RenderedAsync);
            }
            return reply;
        }

        public void Close()
        {
            Bindings.Stop();
            if (inspector is { } made)
            {
                dispatcher?.Post(made.Stop);
            }
        }
    }
}
