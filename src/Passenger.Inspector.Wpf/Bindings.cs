using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Diagnostics;

namespace Passenger.Inspector.Wpf;

/// <summary>A WPF app's failed bindings, its System.Windows.Data Error and Warning lines: WPF's binding trace is turned on at
/// Warning before Main, debugger or not, a listener takes each line as WPF words it, and BindingDiagnostics.BindingFailed gives
/// the binding itself (its element, property, path and XAML place). Each distinct failure is sent once and its count as it
/// repeats; past the most it lists, failures are only counted, their count said (untold) as repeats are. In a debugged app
/// each also calls <see cref="Breaks.BindingFailed"/>. Non-user code, so a stop there shows the app's own code below.</summary>
[DebuggerNonUserCode]
internal static partial class Bindings
{
    private static readonly Dictionary<string, Type?> Types = [];
    private static Connection? connection;
    private static Listener? listener;
    private static bool diagnosed;
    private static volatile bool stopped;

    /// <summary>The line WPF's trace wrote last on this thread, which BindingFailed, raised right after it, names by its code.</summary>
    [ThreadStatic]
    private static (int Code, string Text)? written;

    /// <summary>Turns WPF's binding trace and diagnostics on now, before the app's Main, and listens to them from then on.</summary>
    public static void Start(Connection to)
    {
        connection = to;
        BindingFailures.Start(to);
        try
        {
            // An explicit refresh is what makes WPF trace with no debugger attached; the source is set up after it.
            PresentationTraceSources.Refresh();
            TraceSource source = PresentationTraceSources.DataBindingSource;
            source.Switch.Level |= SourceLevels.Warning;
            listener = new Listener();
            source.Listeners.Add(listener);
            diagnosed = typeof(BindingDiagnostics).GetProperty("IsEnabled", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is true;
            if (diagnosed)
            {
                BindingDiagnostics.BindingFailed += OnFailed;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or TypeLoadException or MissingMemberException or TargetInvocationException)
        {
            connection?.Send(new Reply { Event = Wire.BindingFailed, Error = $"The inspector could not turn on WPF's binding trace: {error.GetBaseException().Message}" });
        }
    }

    /// <summary>Passenger has gone: WPF's binding trace is the app's own again.</summary>
    public static void Stop()
    {
        stopped = true;
        if (diagnosed)
        {
            BindingDiagnostics.BindingFailed -= OnFailed;
        }
        if (listener is not null)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
        }
    }

    private static void OnFailed(object? sender, BindingFailedEventArgs e)
    {
        try
        {
            string text = written is { } line && line.Code == e.Code ? line.Text : e.Message;
            written = null;
            Record(e.EventType, e.Code, text, e.Binding);
        }
        catch (Exception error)
        {
            // Never the app's failure: the inspector's own is said as one more binding failure of its own words.
            InspectorTrace.Step($"a binding error could not be read: {error}");
            connection?.Send(new Reply { Event = Wire.BindingFailed, Error = $"The inspector could not read a binding error: {error.Message}" });
        }
    }

    /// <summary>A line of WPF's binding trace: kept for the BindingFailed that follows it, or, without WPF's diagnostics (the
    /// app's environment lost ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO), recorded from its words alone.</summary>
    private static void Traced(TraceEventType type, int code, string text)
    {
        if (stopped)
        {
            return;
        }
        if (diagnosed)
        {
            written = (code, text);
            return;
        }
        try
        {
            Record(type, code, text, null);
        }
        catch (Exception error)
        {
            InspectorTrace.Step($"a binding trace line could not be read: {error}");
            connection?.Send(new Reply { Event = Wire.BindingFailed, Error = $"The inspector could not read a binding error: {error.Message}" });
        }
    }

    /// <summary>A failed binding, from the thread that evaluated it.</summary>
    private static void Record(TraceEventType type, int code, string text, BindingExpressionBase? binding)
    {
        if (stopped || type is not (TraceEventType.Critical or TraceEventType.Error or TraceEventType.Warning))
        {
            return;
        }
        DependencyObject? target = binding?.Target;
        DependencyProperty? targetProperty = binding?.TargetProperty;
        bool onItsThread = target?.CheckAccess() == true;
        string control = target is not null ? Details.Describe(target)
            : TargetElement().Match(text) is { Success: true } named ? (named.Groups["name"].Value is { Length: > 0 } name ? $"{named.Groups["type"].Value}#{name}" : named.Groups["type"].Value)
            : "(unknown)";
        string property = targetProperty is not null
            ? Wpf.Properties.NameOf(targetProperty, target is not null && !targetProperty.OwnerType.IsAssignableFrom(target.GetType()) && Attached(targetProperty, target))
            : TargetProperty().Match(text) is { Success: true } said ? said.Groups["property"].Value : "(unknown)";
        string path = PathOf(binding) ?? (PathWritten().Match(text) is { Success: true } written ? written.Groups["path"].Value : string.Empty);
        string message = Message(text, code);
        SourcePlace? place = onItsThread ? Sources.Place(target!) : null;
        string key = $"{place?.File}\n{place?.Line}\n{place?.Column}\n{control}\n{property}\n{path}\n{message}";
        bool debugged = Debugger.IsAttached;
        BindingFailure? failure = BindingFailures.Record(key, debugged, (id, listed) =>
        {
            (string? sourceType, string? member, IReadOnlyList<string> nearest) = listed ? Missing(text, binding) : (null, null, []);
            string level = type == TraceEventType.Warning ? "Warning" : "Error";
            return new BindingFailure(id, level, control, property, path, member, message, place, sourceType, member, nearest, 1);
        });
        if (failure is not null && debugged && !stopped)
        {
            object? dataContext = onItsThread && target is FrameworkElement framed ? framed.DataContext : onItsThread && target is FrameworkContentElement content ? content.DataContext : null;
            failure = failure with { DataContext = dataContext is null ? null : Words.TypeName(dataContext.GetType()) };
            Breaks.BindingFailed(new BindingError(target, targetProperty, dataContext, failure, JsonSerializer.Serialize(failure, WireJson.Default.BindingFailure)));
        }
    }

    private static bool Attached(DependencyProperty property, DependencyObject target)
    {
        try
        {
            return System.ComponentModel.DependencyPropertyDescriptor.FromProperty(property, target.GetType()) is not { IsAttached: false };
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>A binding's path as its XAML writes it.</summary>
    private static string? PathOf(BindingExpressionBase? binding) => binding?.ParentBindingBase switch
    {
        Binding { Path.Path: { Length: > 0 } path } => path,
        Binding { XPath: { Length: > 0 } xpath } => xpath,
        Binding => ".",
        MultiBinding multi => string.Join(", ", multi.Bindings.OfType<Binding>().Select(b => b.Path?.Path ?? ".")),
        _ => null,
    };

    /// <summary>WPF's words for what failed, without what the trace adds after them (the expression, its data item and target,
    /// which Passenger shows apart) or its hash codes (which differ each run), and its number as WPF's line has it.</summary>
    private static string Message(string text, int code)
    {
        int extra = text.IndexOf(" BindingExpression:", StringComparison.Ordinal);
        string what = (extra > 0 ? text[..extra] : text).Trim();
        what = HashCode().Replace(what, string.Empty);
        return $"{what} (System.Windows.Data Error: {code.ToString(CultureInfo.InvariantCulture)})";
    }

    /// <summary>For a path naming what its source's type does not have: the type, the member, and the type's members nearest
    /// its name, the type read from the object the path failed on, else by its name.</summary>
    private static (string? Type, string? Member, IReadOnlyList<string> Nearest) Missing(string text, BindingExpressionBase? binding)
    {
        if (NoProperty().Match(text) is not { Success: true } found)
        {
            return (null, null, []);
        }
        string member = found.Groups["member"].Value;
        string named = found.Groups["type"].Value;
        Type? type = SourceType(binding, named) ?? TypeNamed(named);
        return (type?.FullName ?? named, member, type is null ? [] : BindingFailures.Nearest(member, Members(type)));
    }

    /// <summary>The type of the object a binding's path failed on, when it is the one WPF named: the object it last read
    /// from, or one reached by walking its path from its data item.</summary>
    private static Type? SourceType(BindingExpressionBase? binding, string named)
    {
        if (binding is not BindingExpression expression)
        {
            return null;
        }
        if (expression.ResolvedSource?.GetType() is { } resolved && resolved.Name == named)
        {
            return resolved;
        }
        object? at = expression.DataItem;
        foreach (string step in (expression.ParentBinding.Path?.Path ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (at is null || at.GetType().Name == named)
            {
                break;
            }
            at = at.GetType().GetProperty(step.Trim(), BindingFlags.Public | BindingFlags.Instance) is { } property && Words.Readable(property)
                ? Read(property, at)
                : null;
        }
        return at?.GetType() is { } type && type.Name == named ? type : null;
    }

    private static object? Read(PropertyInfo property, object from)
    {
        try
        {
            return property.GetValue(from);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    /// <summary>A type the app has loaded by the name WPF's message gives it (no namespace), its own assemblies before WPF's.</summary>
    private static Type? TypeNamed(string name)
    {
        lock (Types)
        {
            if (Types.TryGetValue(name, out Type? known))
            {
                return known;
            }
            Type? found = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => (a.GetName().Name?.StartsWith("System", StringComparison.Ordinal) ?? false) || (a.GetName().Name?.StartsWith("Presentation", StringComparison.Ordinal) ?? false)))
            {
                try
                {
                    found = assembly.GetTypes().FirstOrDefault(t => t.Name == name);
                }
                catch (ReflectionTypeLoadException partial)
                {
                    found = partial.Types.FirstOrDefault(t => t?.Name == name);
                }
                if (found is not null)
                {
                    break;
                }
            }
            Types[name] = found;
            return found;
        }
    }

    /// <summary>What a binding path can name on a type, as WPF reads it: its readable properties and, on a dependency object,
    /// its dependency properties.</summary>
    private static HashSet<string> Members(Type type)
    {
        HashSet<string> names = new(Words.ReadableNames(type), StringComparer.Ordinal);
        if (typeof(DependencyObject).IsAssignableFrom(type))
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (field.FieldType == typeof(DependencyProperty) && field.GetValue(null) is DependencyProperty property)
                {
                    names.Add(property.Name);
                }
            }
        }
        return names;
    }

    /// <summary>WPF's words for a path naming a property its source's type does not have (its error 40).</summary>
    [GeneratedRegex(@"'(?<member>[^']+)' property not found on '[^']*' ''(?<type>[^']+)'")]
    private static partial Regex NoProperty();

    [GeneratedRegex(@"target element is '(?<type>[^']+)' \(Name='(?<name>[^']*)'\)")]
    private static partial Regex TargetElement();

    [GeneratedRegex(@"target property is '(?<property>[^']+)'")]
    private static partial Regex TargetProperty();

    [GeneratedRegex(@"BindingExpression:Path=(?<path>[^;]*);")]
    private static partial Regex PathWritten();

    [GeneratedRegex(@" \(HashCode=-?\d+\)")]
    private static partial Regex HashCode();

    /// <summary>The inspector's listener on WPF's binding trace: each line as WPF formats it.</summary>
    [DebuggerNonUserCode]
    private sealed class Listener : TraceListener
    {
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
            Traced(eventType, id, message ?? string.Empty);

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args) =>
            Traced(eventType, id, args is { Length: > 0 } && format is not null ? string.Format(CultureInfo.InvariantCulture, format, args) : format ?? string.Empty);

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
        }
    }
}
