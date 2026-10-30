using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
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
            InspectorTrace.Step($"{problem}: {error}");
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
            InspectorTrace.Step($"{request.Method} {request.Id} failed: {error}");
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
            InspectorTrace.Step($"{request.Method} {request.Id} failed in the app: {error}");
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
        T element = Find(request.Element) ?? throw new Refusal(Words.ElementGone);
        Watch(breakpoint, element, request.Property ?? throw new Refusal("No property was named"), request.HitCondition);
    }

    private static Reply Gone(Request request) => new() { Id = request.Id, Error = Words.ElementGone };

    private static Reply Done(Request request, Action action)
    {
        action();
        return new Reply { Id = request.Id };
    }
}

/// <summary>An agent's input to the app, the part that is the same in either framework: the element or window it names,
/// refused once gone, and where the pointer aims there (<see cref="At"/>). <typeparamref name="T"/> is what the framework's
/// tree is made of, <typeparamref name="TTop"/> its top levels.</summary>
internal abstract class Driving<T, TTop> where T : class where TTop : class, T
{
    public InputDone Do(InputAction action)
    {
        T? element = action.Element is long id ? Find(id) ?? throw new Refusal(Words.ElementGone) : null;
        TTop top = element is not null
            ? TopOf(element) ?? throw NoWindow(element)
            : action.Window is long window && Find(window) is { } named && OpenTop(named) is { } open ? open
            : throw new Refusal(action.Window is null ? "Name an element or a window" : "That window is no longer open");
        Ready(top);
        // The window's own element names its point and takes its keys as any other does.
        T? target = element == top && action.X is not null ? null : element;
        return action.Kind switch
        {
            InputKinds.Click => Click(top, target, action),
            InputKinds.Scroll => Scroll(top, target, action),
            InputKinds.Type => Type(top, element, action),
            InputKinds.Key => Keys(top, element, action),
            _ => throw new Refusal($"The inspector knows no input '{action.Kind}'"),
        };
    }

    /// <summary>Why an element the framework does not show is not acted on, in its own words.</summary>
    protected abstract string Hidden { get; }

    protected abstract T? Find(long id);

    /// <summary>The element as one of the app's open top levels, or null when it is none.</summary>
    protected abstract TTop? OpenTop(T element);

    /// <summary>The top level an element is in, the element itself when it is one; null when it is in none.</summary>
    protected abstract TTop? TopOf(T element);

    /// <summary>Refuses input into a top level that cannot take it now; nothing by default.</summary>
    protected virtual void Ready(TTop top)
    {
    }

    protected abstract InputDone Click(TTop top, T? element, InputAction action);

    protected abstract InputDone Scroll(TTop top, T? element, InputAction action);

    protected abstract InputDone Type(TTop top, T? element, InputAction action);

    protected abstract InputDone Keys(TTop top, T? element, InputAction action);

    /// <summary>A top level's client area in its units, from its top left.</summary>
    protected abstract Box Client(TTop top);

    protected abstract bool IsShown(T element);

    protected abstract bool IsEnabled(T element);

    /// <summary>An element's box in its top level's units; null when it is in none.</summary>
    protected abstract Box? BoxIn(T element, TTop top);

    /// <summary>The element its framework's tree holds an element in, or null at its root.</summary>
    protected abstract T? Parent(T element);

    /// <summary>Whether an element clips what it holds to its box.</summary>
    protected abstract bool Clips(T element);

    /// <summary>The scroll viewer at or around an element, if any.</summary>
    protected abstract T? ScrollerAround(T element);

    /// <summary>What a person's pointer at a point in a top level would be over.</summary>
    protected abstract T? Hit(TTop top, double x, double y);

    /// <summary>Whether an element is another or inside it.</summary>
    protected abstract bool Within(T element, T ancestor);

    /// <summary>An element as a sentence names it (<see cref="Words.Called"/>).</summary>
    protected abstract string Called(T element);

    /// <summary>An element as an agent's answer names it (<see cref="Words.Named"/>).</summary>
    protected abstract string Named(T element);

    protected Refusal NoWindow(T element) => new($"{Called(element)} is in no window");

    /// <summary>Where the pointer acts: the point asked for in the window, or an element's centre (of the part of it its
    /// window and clipping ancestors show), or a point in the element from its top left. An element hidden, disabled, out
    /// of view or covered by another is refused, saying which, rather than acting on whatever is there.</summary>
    protected (double X, double Y) At(TTop top, T? element, InputAction action, string doing)
    {
        Box window = Client(top);
        if (element is null)
        {
            if (action.X is not double x || action.Y is not double y)
            {
                throw new Refusal("Name an element, or a point (x and y) in the window");
            }
            return Contains(window, x, y) ? (x, y) : throw new Refusal($"{Words.Spot(x, y)} is outside the window, which is {Words.Box(window)}");
        }
        string name = Called(element);
        if (!IsShown(element))
        {
            throw new Refusal($"{name} is not shown ({Hidden})");
        }
        if (!IsEnabled(element))
        {
            throw new Refusal($"{name} is disabled: a {doing} would not reach it");
        }
        Box box = BoxIn(element, top) ?? throw NoWindow(element);
        Box? seen = Overlap(box, window);
        T? clipper = null;
        for (T? at = Parent(element); at is not null && at != top; at = Parent(at))
        {
            if (Clips(at) && BoxIn(at, top) is { } clip)
            {
                seen = seen is null ? null : Overlap(seen, clip);
                if (seen is null)
                {
                    clipper ??= at;
                }
            }
        }
        if (seen is null)
        {
            throw new Refusal(clipper is not null && ScrollerAround(clipper) is { } scroller
                ? $"{name} is scrolled out of view inside {Called(scroller)}: scroll it to bring it into view"
                : $"{name} is out of view ({Words.Box(box)} in a window {Words.Box(window)})");
        }
        (double X, double Y) aim = action.X is double ox && action.Y is double oy ? (box.X + ox, box.Y + oy) : (seen.X + (seen.Width / 2), seen.Y + (seen.Height / 2));
        if (!Contains(seen, aim.X, aim.Y))
        {
            throw new Refusal($"{Words.Spot(aim.X, aim.Y)} is outside the part of {name} shown ({Words.Box(seen)})");
        }
        if (Hit(top, aim.X, aim.Y) is { } hit && !Within(hit, element) && !Within(element, hit))
        {
            throw new Refusal($"{name} is covered at {Words.Spot(aim.X, aim.Y)} by {Named(hit)}, which would take the {doing}");
        }
        return aim;
    }

    /// <summary>What took pointer input aimed at an element: the element when the hit is it or inside it, else what was hit
    /// (an ancestor, the element taking no input where it was aimed).</summary>
    protected T? Took(T? element, T? hit) => element is not null && hit is not null && Within(hit, element) ? element : hit;

    /// <summary>Whether a box holds a point, its edges included.</summary>
    private static bool Contains(Box box, double x, double y) => x >= box.X && x <= box.X + box.Width && y >= box.Y && y <= box.Y + box.Height;

    /// <summary>Where two boxes overlap; null when they share no area.</summary>
    private static Box? Overlap(Box a, Box b)
    {
        double left = Math.Max(a.X, b.X);
        double top = Math.Max(a.Y, b.Y);
        double right = Math.Min(a.X + a.Width, b.X + b.Width);
        double bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right > left && bottom > top ? new Box(left, top, right - left, bottom - top) : null;
    }
}

/// <summary>Select Element's end, the same in either framework: a click chooses the element under it (the app never sees
/// that click), Escape chooses nothing, and either ends the choosing with the outline left on what was chosen.</summary>
internal abstract class Picking<T>(Connection connection) where T : class
{
    /// <summary>Turns Select Element on, in every window the app has open, or off.</summary>
    public abstract void Choose(bool on);

    /// <summary>Outlines an element in its window, or takes the outline away.</summary>
    public abstract void Outline(T? element);

    /// <summary>The choosing ended on an element, or on none, and Passenger told which.</summary>
    protected void Picked(T? chosen)
    {
        Choose(false);
        Outline(chosen);
        connection.Send(chosen is null
            ? new Reply { Event = Wire.PickEnded }
            : new Reply { Event = Wire.Picked, Element = ElementIds<T>.Of(chosen) });
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

/// <summary>The inspector's words, the same in either framework: a type and an element as the tree and a sentence name
/// them, a value as the window shows it, where a value came from as the details and a property breakpoint say it, text cut
/// to its longest, and a box or a point in a window's units as a refusal names it.</summary>
internal static class Words
{
    /// <summary>Why an element Passenger or an agent named is not acted on: it has gone from the app since.</summary>
    public const string ElementGone = "That element is no longer in the app";

    /// <summary>The longest a value's words run before they are cut.</summary>
    private const int LongestValue = 300;

    /// <summary>The longest text an element's line carries.</summary>
    private const int LongestShown = 200;

    /// <summary>The longest an element's text runs where a sentence names the element by it.</summary>
    private const int LongestInName = 40;

    /// <summary>A type as the tree names it: no namespace, no generic arity.</summary>
    public static string TypeName(Type type)
    {
        string name = type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : name[..tick];
    }

    /// <summary>An element as a sentence names it: its type, then #name when it has one.</summary>
    public static string Describe(Type type, string? name) => name is { Length: > 0 } ? $"{TypeName(type)}#{name}" : TypeName(type);

    /// <summary>An element as a sentence names it, <paramref name="described"/>, then the text it shows when it is known by
    /// that (<paramref name="shows"/>, null for an element with a name).</summary>
    public static string Called(string described, string? shows) => shows is { Length: > 0 } ? $"{described} \"{Cut(shows, LongestInName)}\"" : described;

    /// <summary>An element as an agent's answer names it: as a sentence calls it, then the control whose template made it.</summary>
    public static string Named(string called, string? templatedParent) => templatedParent is null ? called : $"{called} in {templatedParent}'s template";

    /// <summary>The text an element shows as its line carries it.</summary>
    [return: NotNullIfNotNull(nameof(text))]
    public static string? Shows(string? text) => text is null ? null : Cut(text, LongestShown);

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
        return Cut(text, LongestValue);
    }

    /// <summary>The values of the properties named that an element has (<paramref name="valueOf"/>, null for one it has
    /// not), as an agent's look asks for them; null when it has none of them.</summary>
    public static Dictionary<string, string>? Values(IReadOnlyList<string> names, Func<string, string?> valueOf)
    {
        Dictionary<string, string> values = [];
        foreach (string name in names)
        {
            if (valueOf(name) is { } value)
            {
                values[name] = value;
            }
        }
        return values.Count == 0 ? null : values;
    }

    /// <summary>Whether a CLR property is one the inspector reads: it has a getter and takes no index.</summary>
    public static bool Readable(PropertyInfo property) => property.GetMethod is not null && property.GetIndexParameters().Length == 0;

    /// <summary>The names of a type's public CLR properties the inspector reads, as a binding path can name them.</summary>
    public static IEnumerable<string> ReadableNames(Type type) => type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(Readable).Select(p => p.Name);

    /// <summary>An object's public CLR property by name as the inspector gives it (<paramref name="format"/>), or what its
    /// getter threw; null when it has no such property to read.</summary>
    public static string? Read(object owner, string name, Func<object?, string> format)
    {
        if (owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not { } clr || !Readable(clr))
        {
            return null;
        }
        try
        {
            return format(clr.GetValue(owner));
        }
        catch (TargetInvocationException error)
        {
            return $"({error.InnerException?.GetType().Name ?? error.GetType().Name}: {error.InnerException?.Message})";
        }
    }

    /// <summary>What gave a property its value (one of <see cref="ValueSources"/>) where the details name it apart from its
    /// row: what an inherited value's ancestor had, what overrides a setter.</summary>
    public static string Giver(string source) => source switch
    {
        ValueSources.Current => "the control itself (SetCurrentValue)",
        ValueSources.Local => "its local value",
        ValueSources.Animation => "an animation",
        ValueSources.Style => "a style",
        ValueSources.Theme => "its default style",
        ValueSources.Template => "its template",
        ValueSources.Inherited => "a value it inherits",
        _ => "a value",
    };

    /// <summary>The detail of a row whose value the control set itself.</summary>
    public static string SetItself => $"Set by {Giver(ValueSources.Current)}";

    /// <summary>An inherited value's detail: the ancestor it comes from and what gave it its value there.</summary>
    public static string InheritedFrom(string ancestor, string source) => $"From {ancestor} ({Giver(source)})";

    /// <summary>What a property breakpoint says set a value, after its change: ", by an animation", ", its local value";
    /// nothing where its source says nothing of what set it.</summary>
    public static string ChangedBy(string source) => source switch
    {
        ValueSources.Local => $", {Giver(source)}",
        ValueSources.Inherited => ", inherited",
        ValueSources.Current or ValueSources.Animation or ValueSources.Style or ValueSources.Theme or ValueSources.Template => $", by {Giver(source)}",
        _ => string.Empty,
    };

    /// <summary>What a property breakpoint says of a change a binding made, the binding as its XAML writes it.</summary>
    public static string ChangedByBinding(string? binding) => $", by its binding {binding}";

    /// <summary>What a scroll viewer shows: its offset in its extent, and its viewport.</summary>
    public static string Scrolled(string viewer, double x, double y, double width, double height, double viewportWidth, double viewportHeight) =>
        string.Create(CultureInfo.InvariantCulture, $"{viewer} at {x:0.##},{y:0.##} of {width:0.##}x{height:0.##} (viewport {viewportWidth:0.##}x{viewportHeight:0.##})");

    /// <summary>A password's text as its box shows it, a dot for each character, wherever the inspector gives it.</summary>
    public static string Dots(string? text) => new('•', text?.Length ?? 0);

    /// <summary>A box as the protocol carries it, to a hundredth of a unit.</summary>
    public static Box Boxed(double x, double y, double width, double height) => new(Math.Round(x, 2), Math.Round(y, 2), Math.Round(width, 2), Math.Round(height, 2));

    /// <summary>"10,20 300x40".</summary>
    public static string Box(Box box) => string.Create(CultureInfo.InvariantCulture, $"{box.X:0.##},{box.Y:0.##} {box.Width:0.##}x{box.Height:0.##}");

    /// <summary>"10,20".</summary>
    public static string Spot(double x, double y) => string.Create(CultureInfo.InvariantCulture, $"{x:0.##},{y:0.##}");

    /// <summary>Text no longer than <paramref name="longest"/>, its end cut to "...".</summary>
    private static string Cut(string text, int longest) => text.Length > longest ? text[..(longest - 3)] + "..." : text;

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
            // The app's own code, whose failure is its value's description.
            InspectorTrace.Step($"{value.GetType()}'s ToString failed: {error}");
            return $"({error.GetType().Name} from its ToString)";
        }
        string full = value.GetType().FullName ?? string.Empty;
        return said is null || said == full ? TypeName(value.GetType()) : said;
    }
}
