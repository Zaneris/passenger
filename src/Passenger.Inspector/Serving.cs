using System.Globalization;
using System.Runtime.CompilerServices;

namespace Passenger.Inspector;

/// <summary>What the serve loop asks of an inspector's framework: the app's failed bindings heard before its Main, its UI
/// waited for and the inspector made there, its hello, a request answered on its UI thread, and an end once Passenger has
/// gone. Nothing here touches Avalonia or WPF.</summary>
internal interface IAppUi
{
    /// <summary>The app's failed bindings heard from now on; throws when they cannot be.</summary>
    void Listen(Connection connection);

    /// <summary>Waits for the app's UI and makes the inspector on its thread; throws when it cannot start there.</summary>
    void Open(Connection connection);

    /// <summary>The app's first word, with why the inspector could not start, if it could not.</summary>
    Hello Hello(string? problem);

    /// <summary>A request answered on the app's UI thread; input's once what it set going is laid out and drawn.</summary>
    Task<Reply> AnswerAsync(Request request);

    /// <summary>Passenger has gone, or the inspector could not start: the bindings are the app's own again, and nothing of the
    /// inspector's is left in its windows.</summary>
    void Close();
}

/// <summary>The inspector's serving inside the app, in either framework: it connects to Passenger and listens to the app's
/// bindings before the app's Main, waits on a thread of its own for the app's UI, says hello, and answers each request
/// there, until Passenger goes.</summary>
internal static class Serving
{
    public static void Start(string socket, IAppUi app)
    {
        InspectorTrace.Step($"connecting to {socket}");
        if (Connection.Open(socket) is not { } connection)
        {
            InspectorTrace.Step("Passenger is not listening");
            return;
        }
        try
        {
            app.Listen(connection);
        }
        catch (Exception error)
        {
            // The tree and the agents' tools still serve an app whose binding errors cannot be heard.
            string problem = $"The inspector could not listen to the app's binding errors: {error.GetBaseException().Message}";
            InspectorTrace.Step(problem);
            connection.Send(new Reply { Event = Wire.BindingFailed, Error = problem });
        }
        Thread thread = new(() => Serve(connection, app)) { IsBackground = true, Name = "Passenger inspector" };
        thread.Start();
    }

    private static void Serve(Connection connection, IAppUi app)
    {
        using Connection held = connection;
        try
        {
            app.Open(connection);
        }
        catch (Exception error)
        {
            InspectorTrace.Step($"could not start: {error}");
            // Said, and written before the connection closes, so Passenger shows why rather than an app gone silent.
            connection.Send(new Reply { Event = Wire.Hello, Hello = app.Hello($"The inspector could not start: {error.GetBaseException().Message}") });
            connection.Drain();
            app.Close();
            return;
        }
        connection.Send(new Reply { Event = Wire.Hello, Hello = app.Hello(null) });
        InspectorTrace.Step("said hello");
        while (connection.Read() is { } request)
        {
            InspectorTrace.Observe(AnswerAsync(app, connection, request));
        }
        InspectorTrace.Step("Passenger has gone");
        app.Close();
    }

    private static async Task AnswerAsync(IAppUi app, Connection connection, Request request)
    {
        Reply reply;
        try
        {
            reply = await app.AnswerAsync(request);
        }
        catch (Exception error)
        {
            reply = new Reply { Id = request.Id, Error = error.Message };
        }
        InspectorTrace.Step($"{request.Method} {request.Id}: {reply.Error ?? (reply.Windows is { } windows ? $"{windows.Count} window(s)" : reply.Tree is { } tree ? $"{tree.Roots.Count} root(s)" : "answered")}");
        connection.Send(reply);
    }
}

/// <summary>The inspector's work on the app's UI thread that is the same in either framework: each request's answer, by
/// the framework's own parts, and a word to Passenger, once, as elements come and go. <typeparamref name="T"/> is what
/// the framework's tree is made of.</summary>
internal abstract class Inspecting<T>(Connection connection) where T : class
{
    /// <summary>How long the app's elements may come and go before Passenger is told its tree changed, once.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromMilliseconds(300);

    private bool told;
    private bool stopped;

    /// <summary>Whether Passenger has asked for the tree since it was last told of a change, so it is told only once.</summary>
    private bool watched;

    public Reply Handle(Request request)
    {
        try
        {
            return request.Method switch
            {
                Wire.Tree => new Reply { Id = request.Id, Tree = Watched(request) },
                Wire.Windows => new Reply { Id = request.Id, Windows = Windows() },
                Wire.Details => Find(request.Element) is { } element
                    ? new Reply { Id = request.Id, Details = DetailsOf(element) }
                    : Gone(request),
                Wire.Pick => Done(request, () => Pick(request.On == true)),
                Wire.Highlight => Done(request, () => Outline(Find(request.Element))),
                Wire.Capture => Find(request.Element) is { } shown
                    ? new Reply { Id = request.Id, Picture = Capture(shown, request) }
                    : Gone(request),
                Wire.Input => new Reply { Id = request.Id, Done = Do(request.Input ?? throw new Refusal("No input was given")) },
                Wire.Watch => Done(request, () => Watch(request)),
                Wire.Values => new Reply { Id = request.Id, Values = ValuesOf(request) },
                Wire.Overlays => new Reply { Id = request.Id, Windows = SetOverlays(request) },
                _ => new Reply { Id = request.Id, Error = $"The inspector does not know '{request.Method}'" },
            };
        }
        catch (Refusal refused)
        {
            return new Reply { Id = request.Id, Error = refused.Message };
        }
        catch (Exception error)
        {
            return new Reply { Id = request.Id, Error = $"{error.GetType().Name}: {error.Message}" };
        }
    }

    /// <summary>Passenger has gone: nothing of the inspector's is left in the app's windows.</summary>
    public void Stop()
    {
        stopped = true;
        Stopping();
    }

    /// <summary>The id Passenger knows an element by, the same for as long as it lives.</summary>
    public long IdOf(T element) => ElementIds<T>.Of(element);

    public T? Find(long? id) => ElementIds<T>.Find(id);

    /// <summary>The app's top levels and what each holds, with what an agent's look asks for.</summary>
    protected abstract AppTree Tree(Request request);

    /// <summary>The app's top levels as an agent lists them.</summary>
    protected abstract List<AppWindow> Windows();

    protected abstract ElementDetails DetailsOf(T element);

    /// <summary>Select Element begun or ended.</summary>
    protected abstract void Pick(bool on);

    /// <summary>The outline over an element, or none.</summary>
    protected abstract void Outline(T? element);

    protected abstract Picture Capture(T element, Request request);

    protected abstract InputDone Do(InputAction input);

    /// <summary>A property breakpoint's watch begun, again with its hit count when it was on.</summary>
    protected abstract void Watch(int breakpoint, T element, string property, string? hitCondition);

    protected abstract void Unwatch(int breakpoint);

    /// <summary>A XAML file's bindings with their values; refused by a framework that reads none.</summary>
    protected abstract XamlValues ValuesOf(Request request);

    /// <summary>The renderer debug overlays asked for set, and the windows as they are then; refused by a framework with none.</summary>
    protected abstract List<AppWindow> SetOverlays(Request request);

    /// <summary>Whether an element is one the tree leaves out (the inspector's own), whose coming is no change in the app.</summary>
    protected abstract bool LeftOut(T element);

    /// <summary>Runs once on the app's UI thread after a while.</summary>
    protected abstract void Later(TimeSpan delay, Action action);

    /// <summary>What the framework's inspector ends of its own.</summary>
    protected abstract void Stopping();

    /// <summary>An element came or went: Passenger, if it has the tree, hears once that it changed, a moment after the last.</summary>
    protected void Changed(object? sender)
    {
        if (stopped || (sender is T element && LeftOut(element)) || !watched || told)
        {
            return;
        }
        told = true;
        Later(Settled, () =>
        {
            watched = false;
            if (!stopped)
            {
                connection.Send(new Reply { Event = Wire.TreeChanged });
            }
        });
    }

    private AppTree Watched(Request request)
    {
        watched = true;
        told = false;
        return Tree(request);
    }

    /// <summary>A property breakpoint's watch begun (again, with its hit count) or ended.</summary>
    private void Watch(Request request)
    {
        int breakpoint = request.Breakpoint ?? throw new Refusal("No property breakpoint was named");
        if (request.On == false)
        {
            Unwatch(breakpoint);
            return;
        }
        T element = Find(request.Element) ?? throw new Refusal("That element is no longer in the app");
        Watch(breakpoint, element, request.Property ?? throw new Refusal("No property was named"), request.HitCondition);
    }

    private static Reply Gone(Request request) => new() { Id = request.Id, Error = "That element is no longer in the app" };

    private static Reply Done(Request request, Action action)
    {
        action();
        return new Reply { Id = request.Id };
    }
}

/// <summary>Why the inspector will not do what it was asked (the element is disabled, covered, gone), in words an agent
/// reads as they are.</summary>
internal sealed class Refusal(string message) : Exception(message);

/// <summary>The ids Passenger knows the app's elements by, each the same for as long as the element lives: the Live Visual
/// Tree's, an agent's, and an Avalonia app's profiler frames', so an element named in one is the one in the others. The
/// app's UI thread's alone.</summary>
internal static class ElementIds<T> where T : class
{
    private static readonly ConditionalWeakTable<T, object> Ids = new();
    private static readonly Dictionary<long, WeakReference<T>> ById = [];
    private static long next;

    public static long Of(T element)
    {
        if (Ids.TryGetValue(element, out object? known))
        {
            return (long)known;
        }
        long id = ++next;
        Ids.Add(element, id);
        if (ById.Count > 4096 && ById.Count % 4096 == 0)
        {
            foreach (long dead in ById.Where(e => !e.Value.TryGetTarget(out _)).Select(e => e.Key).ToList())
            {
                ById.Remove(dead);
            }
        }
        ById[id] = new WeakReference<T>(element);
        return id;
    }

    public static T? Find(long? id) => id is long known && ById.TryGetValue(known, out WeakReference<T>? held) && held.TryGetTarget(out T? element) ? element : null;
}

/// <summary>A file the app's environment names (<see cref="Wire.TraceVariable"/>): the inspector adds each step of its start and
/// what it answers to it, for an inspector that never says hello or answers wrongly.</summary>
internal static class InspectorTrace
{
    private static readonly string? TraceFile = Environment.GetEnvironmentVariable(Wire.TraceVariable) is { Length: > 0 } file ? file : null;

    private static readonly object Tracer = new();

    /// <summary>Work the inspector starts and nothing awaits: its fault goes to the trace as it faults, never to the
    /// app's own handler of unobserved tasks once the task is collected.</summary>
    public static void Observe(Task work) =>
        _ = work.ContinueWith(static faulted => Step($"unawaited work failed: {faulted.Exception}"), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>A step of the inspector's to the trace file, while <see cref="Wire.TraceVariable"/> names one.</summary>
    public static void Step(string step)
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
}

/// <summary>The inspector's words for types, values and places, the same in either framework: a type as the tree names it,
/// a value as the window shows it, and a box or a point in a window's units as a refusal names it.</summary>
internal static class Words
{
    /// <summary>A type as the tree names it: no namespace, no generic arity.</summary>
    public static string TypeName(Type type)
    {
        string name = type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>The longest a value's words run before they are cut.</summary>
    private const int Longest = 300;

    /// <summary>A value as the window shows it: text quoted, then the framework's own words for it (<paramref name="framed"/>,
    /// null when it has none), then its invariant text, else what it says it is.</summary>
    public static string Value(object? value, Func<object, string?> framed)
    {
        string text = value switch
        {
            null => "(null)",
            string s => $"\"{s}\"",
            _ => framed(value) ?? (value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : Described(value)),
        };
        return text.Length > Longest ? text[..(Longest - 3)] + "..." : text;
    }

    /// <summary>A box as the protocol carries it, to a hundredth of a unit.</summary>
    public static Box Boxed(double x, double y, double width, double height) => new(Math.Round(x, 2), Math.Round(y, 2), Math.Round(width, 2), Math.Round(height, 2));

    /// <summary>"10,20 300x40".</summary>
    public static string Box(double x, double y, double width, double height) => string.Create(CultureInfo.InvariantCulture, $"{x:0.##},{y:0.##} {width:0.##}x{height:0.##}");

    /// <summary>"10,20".</summary>
    public static string Spot(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"{x:0.##},{y:0.##}");

    /// <summary>What an object says it is, or its type when it says nothing more (an object's ToString is its type's name).</summary>
    private static string Described(object value)
    {
        string? said;
        try
        {
            said = value.ToString();
        }
        catch (Exception error)
        {
            return $"({error.GetType().Name} from its ToString)";
        }
        string full = value.GetType().FullName ?? string.Empty;
        return said is null || said == full ? TypeName(value.GetType()) : said;
    }
}
