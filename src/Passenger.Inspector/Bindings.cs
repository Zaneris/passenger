using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Logging;

namespace Passenger.Inspector;

/// <summary>The app's failed bindings, as Avalonia logs them in its Binding area: a sink of the inspector's takes
/// Avalonia's logger from before the app's Main, and again over whichever sink the app sets (its LogToTrace), handing
/// the app's sink everything it asked for, so the app logs as it always did. Each failure is known by its control's
/// XAML element, property, path and message (<see cref="BindingFailures"/>); in a debugged app each, repeats too, also
/// calls <see cref="Breaks.BindingFailed"/> before Avalonia goes on. Non-user code, so the stop there shows the app's
/// own code below as where it stopped.</summary>
[DebuggerNonUserCode]
internal static partial class Bindings
{
    private static readonly Dictionary<string, Type?> Types = [];
    private static Connection? connection;
    private static volatile bool stopped;

    /// <summary>Whether a sink of the inspector's has this call already, the app's own sink wrapping one of them.</summary>
    [ThreadStatic]
    private static bool recording;

    /// <summary>Takes Avalonia's logger now, before the app's Main, and keeps it from then on.</summary>
    public static void Start(Connection to)
    {
        connection = to;
        BindingFailures.Start(to);
        Take();
        Thread watch = new(Watch) { IsBackground = true, Name = "Passenger inspector's binding log" };
        watch.Start();
    }

    /// <summary>Passenger has gone: the app's own sink is Avalonia's again.</summary>
    public static void Stop()
    {
        stopped = true;
        if (Logger.Sink is Sink ours)
        {
            Logger.Sink = ours.Inner;
        }
    }

    /// <summary>Takes the logger again whenever the app has set a sink of its own: quickly while the app sets itself up,
    /// before any window binds, then now and then.</summary>
    private static void Watch()
    {
        DateTime up = DateTime.MaxValue;
        while (!stopped)
        {
            Take();
            if (up == DateTime.MaxValue && Agent.HasApplication())
            {
                up = DateTime.UtcNow;
            }
            TimeSpan since = DateTime.UtcNow - up;
            Thread.Sleep(up == DateTime.MaxValue ? 5 : since < TimeSpan.FromSeconds(10) ? 100 : 1000);
        }
    }

    private static void Take()
    {
        if (!stopped && Logger.Sink is not Sink)
        {
            Logger.Sink = new Sink(Logger.Sink);
        }
    }

    /// <summary>A failed binding as Avalonia logged it, from the thread that evaluated it (the app's UI thread).</summary>
    private static void Record(LogEventLevel level, object? source, string template, object?[] values)
    {
        Dictionary<string, object?> named = Named(template, values);
        object? bound = named.GetValueOrDefault("Property");
        string property = bound switch
        {
            AvaloniaProperty { IsAttached: true } attached => $"{Words.TypeName(attached.OwnerType)}.{attached.Name}",
            AvaloniaProperty own => own.Name,
            { } other => other.ToString() ?? "(unknown)",
            null => "(unknown)",
        };
        string path = named.GetValueOrDefault("Expression")?.ToString() ?? string.Empty;
        string? point = named.GetValueOrDefault("ExpressionErrorPoint")?.ToString();
        string message = named.TryGetValue("Message", out object? said) ? said?.ToString() ?? string.Empty : Formatted(template, named);
        string control = source switch
        {
            Visual visual => Details.Describe(visual),
            null => "(unknown)",
            _ => Words.TypeName(source.GetType()),
        };
        // Avalonia's attached properties are read on the object's own thread, which is the one logging, in every app.
        AvaloniaObject? made = source as AvaloniaObject;
        bool onItsThread = made?.CheckAccess() == true;
        SourcePlace? place = onItsThread ? Details.Place(made!) : null;
        string key = $"{place?.File}\n{place?.Line}\n{place?.Column}\n{control}\n{property}\n{path}\n{point}\n{message}";
        // Under a debugger every failure, a repeat or one past the most counted, calls where Avalonia Binding Errors stops.
        bool debugged = Debugger.IsAttached;
        BindingFailure? failure = BindingFailures.Record(key, debugged, (id, listed) =>
        {
            // Past the most listed, a debugged app's failure still stops, without the nearest names to offer.
            (string? type, string? member, IReadOnlyList<string> nearest) = listed ? Missing(message) : (null, null, []);
            return new BindingFailure(id, level.ToString(), control, property, path, point, message, place, type, member, nearest, 1);
        });
        if (failure is not null && debugged && !stopped)
        {
            object? dataContext = onItsThread && made is StyledElement styled ? styled.DataContext : null;
            failure = failure with { DataContext = dataContext is null ? null : Words.TypeName(dataContext.GetType()) };
            Breaks.BindingFailed(new BindingError(made, bound as AvaloniaProperty, dataContext, failure,
                JsonSerializer.Serialize(failure, WireJson.Default.BindingFailure)));
        }
    }

    /// <summary>The values of a message's template by their names, as Avalonia's sinks read them.</summary>
    private static Dictionary<string, object?> Named(string template, object?[] values)
    {
        Dictionary<string, object?> named = [];
        int i = 0;
        foreach (Match hole in Hole().Matches(template))
        {
            if (i < values.Length)
            {
                named[hole.Groups["name"].Value] = values[i++];
            }
        }
        return named;
    }

    private static string Formatted(string template, Dictionary<string, object?> named) =>
        Hole().Replace(template, hole => named.GetValueOrDefault(hole.Groups["name"].Value)?.ToString() ?? string.Empty);

    /// <summary>For a member the source's type does not have: the type, the member, and the type's members nearest its name.</summary>
    private static (string? Type, string? Member, IReadOnlyList<string> Nearest) Missing(string message)
    {
        if (NoMember().Match(message) is not { Success: true } found)
        {
            return (null, null, []);
        }
        string type = found.Groups["type"].Value;
        string member = found.Groups["member"].Value;
        return (type, member, TypeNamed(type) is { } known ? BindingFailures.Nearest(member, Members(known)) : []);
    }

    /// <summary>A type the app has loaded, by the name Avalonia's message gives it (its Type.ToString()).</summary>
    private static Type? TypeNamed(string name)
    {
        if (Types.TryGetValue(name, out Type? known))
        {
            return known;
        }
        Type? found = null;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (assembly.GetType(name, throwOnError: false) is { } type)
                {
                    found = type;
                    break;
                }
            }
            catch (Exception error) when (error is ArgumentException or FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException)
            {
                // A name this assembly cannot read (a generic's arguments from elsewhere): the next may.
            }
        }
        Types[name] = found;
        return found;
    }

    /// <summary>What a binding path can name on a type, as Avalonia's reflection bindings read it: its readable properties,
    /// an AvaloniaObject's Avalonia properties, and methods of one parameter or none (bound as commands).</summary>
    private static HashSet<string> Members(Type type)
    {
        HashSet<string> names = new(Words.ReadableNames(type), StringComparer.Ordinal);
        if (typeof(AvaloniaObject).IsAssignableFrom(type))
        {
            foreach (AvaloniaProperty property in AvaloniaPropertyRegistry.Instance.GetRegistered(type))
            {
                names.Add(property.Name);
            }
        }
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!method.IsSpecialName && !method.IsGenericMethodDefinition && method.GetParameters().Length <= 1
                && method.GetBaseDefinition().DeclaringType != typeof(object) && method.Name is not ("Equals" or "GetHashCode" or "ToString" or "Deconstruct"))
            {
                names.Add(method.Name);
            }
        }
        return names;
    }

    [GeneratedRegex(@"\{(?<name>\w+)\}")]
    private static partial Regex Hole();

    /// <summary>Avalonia's words for a path naming what its source's type does not have, from its property accessor
    /// lookup and its INotifyPropertyChanged plugin.</summary>
    [GeneratedRegex(@"^Could not find (?:a matching property accessor for|CLR property) '(?<member>[^']+)' on '(?<type>[^']+)'")]
    private static partial Regex NoMember();

    /// <summary>The inspector's sink over the app's own (none, if the app logs nothing): Avalonia's Binding area at Warning
    /// and above is recorded, and the app's sink has whatever it asks for, as before.</summary>
    private sealed class Sink(ILogSink? inner) : ILogSink
    {
        public ILogSink? Inner { get; } = inner;

        public bool IsEnabled(LogEventLevel level, string area) =>
            (level >= LogEventLevel.Warning && area == LogArea.Binding && !stopped) || Inner?.IsEnabled(level, area) == true;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            bool outer = !recording;
            if (outer && level >= LogEventLevel.Warning && area == LogArea.Binding && !stopped)
            {
                try
                {
                    Record(level, source, messageTemplate, propertyValues);
                }
                catch (Exception error)
                {
                    // Never the app's failure: the inspector's own is said as one more binding failure of its own words.
                    InspectorTrace.Step($"a binding error could not be read: {error}");
                    connection?.Send(new Reply { Event = Wire.BindingFailed, Error = $"The inspector could not read a binding error: {error.Message}" });
                }
            }
            if (Inner?.IsEnabled(level, area) != true)
            {
                return;
            }
            recording = true;
            try
            {
                Inner.Log(level, area, source, messageTemplate, propertyValues);
            }
            finally
            {
                recording = !outer;
            }
        }
    }
}
