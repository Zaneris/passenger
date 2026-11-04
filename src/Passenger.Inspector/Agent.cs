using System.Reflection;
using Avalonia;
using Avalonia.Platform;
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

    /// <summary>The file the program previews, as Avalonia's designer (<see cref="Wire.PreviewVariable"/>), whose loads are
    /// made to record where they make each element; null for an app.</summary>
    internal static string? Previewed { get; private set; }

    /// <summary>Marks the program the designer of a XAML file; called before <see cref="Start"/>.</summary>
    public static void Preview(string file) => Previewed = file;

    /// <param name="overlays">The renderer debug overlays the launch named (<see cref="Wire.OverlaysVariable"/>), or null.</param>
    /// <param name="ends">Whether nobody is to see the app, which ends once it is up drawing where people see it
    /// (<see cref="Wire.UnseenEnds"/>).</param>
    /// <param name="mainLeftOut">Whether the hook starts the app itself in place of its Main (<see cref="Unseen"/>).</param>
    public static void Start(string socket, Thread main, string avalonia, string? overlays, bool ends, bool mainLeftOut) =>
        Serving.Start(socket, new AvaloniaUi(main, avalonia, overlays, ends, mainLeftOut));

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

    /// <summary>Whether the app draws on Avalonia's headless platform, which its setup has chosen by the time its Application is up.</summary>
    private static bool DrawsHeadless() =>
        AvaloniaLocator.Current.GetService<IWindowingPlatform>()?.GetType().Assembly.GetName().Name == "Avalonia.Headless";

    /// <summary>An Avalonia app as the serve loop reaches it.</summary>
    private sealed class AvaloniaUi(Thread main, string avalonia, string? overlays, bool ends, bool mainLeftOut) : IAppUi
    {
        private Dispatcher? dispatcher;
        private Inspector? inspector;
        private bool headless;

        public void Listen(Connection connection)
        {
            // A designer's bindings fail for want of the data the app gives them, which no one is told of.
            if (Previewed is null)
            {
                Bindings.Start(connection);
            }
        }

        public void Open(Connection connection)
        {
            dispatcher = WaitForUi(main);
            headless = DrawsHeadless();
            if (ends && !headless)
            {
                // A Main its build's headless platform was taken to set up chose another: ended before its windows are shown, if it can be.
                string said = "The app draws where people see it, though its build carries Avalonia's headless platform. "
                    + "It was ended as its windows came, as a Passenger nobody sees opens no window on the person's screen";
                Console.Error.WriteLine(said);
                connection.Send(new Reply { Event = Wire.Hello, Hello = Hello(said) });
                connection.Drain();
                Environment.Exit(StartupHook.EndedUnseen);
            }
            Ui = dispatcher;
            inspector = dispatcher.Invoke(() => Current = new Inspector(connection, overlays));
            if (Previewed is { } file)
            {
                dispatcher.Invoke(() => DesignLoads.Wrap(file));
            }
        }

        public Hello Hello(string? problem) => new(Wire.Version, Environment.ProcessId, avalonia, problem) { Headless = headless, MainLeftOut = mainLeftOut };

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
