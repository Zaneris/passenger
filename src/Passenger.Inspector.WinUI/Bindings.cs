using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Passenger.Inspector.WinUI;

/// <summary>A WinUI app's failed bindings, as its DebugSettings tell of each (BindingFailed, with binding tracing on), told
/// to Passenger as every framework's are (<see cref="BindingFailures"/>): the element and property a binding sets, its path,
/// the part that failed, the type that lacks it and the members nearest it, and where a named element's XAML is. Raised on
/// the UI thread.</summary>
internal static partial class Bindings
{
    private static Inspector? inspector;
    private static object? settings;

    /// <summary>What failed before WinUI told of the app's elements, told once it has, so each is placed at its XAML.</summary>
    private static List<string>? early = [];

    /// <summary>Every failure WinUI has told of, so a binding found failing as the tree is known is told only where WinUI's
    /// own telling came before the inspector heard.</summary>
    private static readonly HashSet<string> told = [];
    private static EventInfo? failed;
    private static Delegate? handler;

    /// <summary>The inspector a failure's element is found by, once made.</summary>
    public static void Inspecting(Inspector inspecting) => inspector = inspecting;

    /// <summary>Hears the app's failed bindings from now on, on the UI thread, where WinUI's DebugSettings answer; once.</summary>
    public static void Start()
    {
        if (handler is not null)
        {
            return;
        }
        object application = Projected.Static("Microsoft.UI.Xaml.Application", "Current") ?? throw new InvalidOperationException("The app has no Application");
        settings = Projected.Get(application, "DebugSettings") ?? throw new InvalidOperationException("The app's Application has no DebugSettings");
        Projected.Set(settings, "IsBindingTracingEnabled", true);
        failed = settings.GetType().GetEvent("BindingFailed") ?? throw new InvalidOperationException("The app's DebugSettings tell of no failed binding");
        handler = Projected.Handler(failed.EventHandlerType!, new Action<object?, object>(Heard));
        failed.AddEventHandler(settings, handler);
        InspectorTrace.Step($"listening to the app's failed bindings, on thread {Environment.CurrentManagedThreadId}");
    }

    /// <summary>WinUI has told of the app's elements: what failed before is told now, then what failed before the inspector
    /// heard (<see cref="FailedBefore"/>), and the rest as it fails.</summary>
    public static void TreeKnown()
    {
        try
        {
            List<string> held = early ?? [];
            early = null;
            foreach (string message in held)
            {
                Tell(message);
            }
            foreach (string message in inspector is null ? [] : FailedBefore(inspector).Where(m => !told.Contains(m)))
            {
                InspectorTrace.Step($"a binding had failed before: {message[..Math.Min(message.Length, 300)]}");
                Tell(message);
            }
        }
        catch (Exception error)
        {
            // Posted to WinUI's own queue: nothing the inspector meets may end the app.
            InspectorTrace.Step($"the bindings made before could not be read: {error}");
        }
    }

    public static void Stop()
    {
        if (settings is not null && failed is not null && handler is not null)
        {
            failed.RemoveEventHandler(settings, handler);
        }
        (inspector, settings, failed, handler, early) = (null, null, null, null, null);
        told.Clear();
    }

    private static void Heard(object? sender, object args)
    {
        try
        {
            string message = Projected.Get(args, "Message") as string ?? string.Empty;
            InspectorTrace.Step($"a binding failed{(early is null ? string.Empty : " before the tree was known")}: {message[..Math.Min(message.Length, 300)]}");
            told.Add(message);
            if (early is { } held)
            {
                held.Add(message);
                return;
            }
            Failed(message);
        }
        catch (Exception error)
        {
            // WinUI's own event: nothing the inspector meets may end the app.
            InspectorTrace.Step($"a failed binding could not be told: {error}");
        }
    }

    /// <summary>A failure told as it happens, and where the app is debugged, the app stopped there (<see cref="Breaks"/>), as
    /// XAML Binding Errors stops it: WinUI raises BindingFailed on the thread that evaluated the binding, as it fails, so the
    /// stack is the one that failed. The control is found where its element is named, its DataContext read from it.</summary>
    private static void Failed(string message)
    {
        bool debugged = Debugger.IsAttached;
        if (Tell(message, debugged) is not { } failure || !debugged || inspector is not { } inspecting)
        {
            return;
        }
        Match targeted = Target().Match(message);
        object? control = targeted.Success && targeted.Groups["name"].Value is { Length: > 0 } name && name != "null"
            && inspecting.Named(targeted.Groups["type"].Value, name) is { } element ? inspecting.ObjectOf(element) : null;
        object? property = control?.GetType().GetProperty($"{failure.Property}Property", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
        object? dataContext = control is null ? null : Projected.Get(control, "DataContext");
        failure = failure with { DataContext = dataContext is null ? null : Words.TypeName(dataContext.GetType()) };
        Breaks.BindingFailed(new BindingError(control, property, dataContext, failure, JsonSerializer.Serialize(failure, WireJson.Default.BindingFailure)));
    }

    /// <summary>A failure recorded, as it happened (where the app is <paramref name="debugged"/>, each one made to stop at)
    /// or as found once the tree was known; null where it is neither listed nor stopped at.</summary>
    private static BindingFailure? Tell(string message, bool debugged = false) =>
        BindingFailures.Record(message, debugged, (id, listed) => Read(id, message, listed));

    /// <summary>A failure as WinUI words it ("BindingExpression path error: 'Nmae' property not found on 'App.Person'. …
    /// target element is 'Microsoft.UI.Xaml.Controls.TextBlock' (Name='Caption'); target property is 'Text' …").</summary>
    private static BindingFailure Read(int id, string message, bool listed)
    {
        string? member = Missing().Match(message) is { Success: true } missing ? missing.Groups["member"].Value : null;
        string? type = member is not null ? Missing().Match(message).Groups["type"].Value : null;
        string path = PathOf().Match(message) is { Success: true } named ? named.Groups["path"].Value : Words.Unknown;
        Match targeted = Target().Match(message);
        string target = targeted.Success ? targeted.Groups["type"].Value : Words.Unknown;
        string? name = targeted.Success && targeted.Groups["name"].Value is { Length: > 0 } given && given != "null" ? given : null;
        string property = Property().Match(message) is { Success: true } set ? set.Groups["property"].Value : Words.Unknown;
        IReadOnlyList<string> nearest = listed && member is not null && type is not null && Projected.Type(type) is { } lacking
            ? BindingFailures.Nearest(member, Words.ReadableNames(lacking))
            : [];
        string control = target[(target.LastIndexOf('.') + 1)..] + (name is null ? string.Empty : $"#{name}");
        SourcePlace? place = name is not null && inspector?.Named(target, name) is { } element ? inspector.PlaceOf(element) : null;
        return new BindingFailure(id, "Error", control, property, path, member, message.Trim(), place, type?[(type.LastIndexOf('.') + 1)..], member, nearest, 1);
    }

    [GeneratedRegex(@"'(?<member>[^']+)' property not found on '(?<type>[^']+)'")]
    private static partial Regex Missing();

    [GeneratedRegex(@"Path='(?<path>[^']*)'")]
    private static partial Regex PathOf();

    [GeneratedRegex(@"target element is '(?<type>[^']+)' \(Name='(?<name>[^']*)'\)")]
    private static partial Regex Target();

    [GeneratedRegex(@"target property is '(?<property>[^']+)'")]
    private static partial Regex Property();
}
