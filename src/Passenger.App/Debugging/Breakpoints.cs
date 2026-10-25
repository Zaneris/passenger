using System.Text.Json.Nodes;
using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Text;

namespace Passenger.App.Debugging;

/// <summary>What every breakpoint has: on or off, whether it stops, a condition, a hit count and a message to log. Its
/// <see cref="Status"/> is what the running debuggers last said of it, and goes back to unknown as their sessions
/// end.</summary>
public abstract class Breakpoint
{
    /// <summary>What each session's debugger last said of it, by the session.</summary>
    private readonly Dictionary<object, (BreakpointStatus Status, string? Message)> said = [];

    public bool Enabled { get; internal set; } = true;
    public string? Condition { get; internal set; }
    public string? HitCondition { get; internal set; }

    /// <summary>Whether a hit stops the program. One that does not is a tracepoint: it logs its message, or "Breakpoint
    /// reached", and lets the program go on.</summary>
    public bool Suspend { get; internal set; } = true;

    /// <summary>A message written to the Debug window's console on each hit, its {expressions} evaluated there.</summary>
    public string? LogMessage { get; internal set; }

    /// <summary>What the debuggers said of it: with several programs debugged at once, bound where any bound it, else unknown
    /// while any has yet to bind it, else refused.</summary>
    public BreakpointStatus Status { get; private set; }

    /// <summary>The debugger's word on a breakpoint that will not be hit ("no symbols loaded").</summary>
    public string? Message { get; private set; }

    /// <summary>Records what one session's debugger said of it.</summary>
    internal void Tell(object session, BreakpointStatus status, string? message)
    {
        said[session] = (status, message);
        Merge();
    }

    /// <summary>Forgets what a session's debugger said of it, as the session ends; true when it had said something.</summary>
    internal bool Forget(object session)
    {
        if (!said.Remove(session))
        {
            return false;
        }
        Merge();
        return true;
    }

    private void Merge() => (Status, Message) = said.Count == 0 ? (BreakpointStatus.Unknown, null)
        : said.Values.Any(s => s.Status == BreakpointStatus.Bound) ? said.Values.First(s => s.Status == BreakpointStatus.Bound)
        : said.Values.Any(s => s.Status == BreakpointStatus.Unknown) ? said.Values.First(s => s.Status == BreakpointStatus.Unknown)
        : said.Values.First();

    /// <summary>How the Breakpoints window and the popup name it: "Program.cs:7", "Any exception".</summary>
    public abstract string Location { get; }
}

/// <summary>A line breakpoint (lines from zero), carried with its file's text while it is open; in Avalonia XAML, on one
/// attribute of the line when it names one (<see cref="XamlBreakpoints"/>).</summary>
public sealed class LineBreakpoint(string path, int line, string? attribute = null) : Breakpoint
{
    public string Path { get; internal set; } = path;

    public int Line => At.Line;

    internal AnchoredLine At { get; } = new(line);

    /// <summary>The XAML attribute it is on, by its name as written, or null for its whole line.</summary>
    public string? Attribute { get; } = attribute;

    public override string Location => $"{System.IO.Path.GetFileName(Path)}:{Line + 1}{(Attribute is { } on ? $" {on}" : string.Empty)}";
}

/// <summary>A CLR exception breakpoint: an exception type by its full name, or "Any exception", which every folder has
/// and none removes, and the scenarios it stops in. "Any exception" stops on what the user's code leaves unhandled; a
/// breakpoint on a type added since stops wherever it is thrown.</summary>
public sealed class ExceptionBreakpoint(string? type) : Breakpoint
{
    /// <summary>The name of the breakpoint on every exception.</summary>
    public const string AnyException = "Any exception";

    /// <summary>The scenarios of "Any exception": thrown anywhere, and handled outside the user's code or not at
    /// all.</summary>
    public const ExceptionScenarios AnyExceptionScenarios = ExceptionScenarios.Thrown | ExceptionScenarios.HandledInExternalCode | ExceptionScenarios.Unhandled;

    /// <summary>The type's full name, or null for every exception.</summary>
    public string? Type { get; } = type;

    public bool IsAny => Type is null;

    public ExceptionScenarios Scenarios { get; internal set; } = type is null ? AnyExceptionScenarios : ExceptionScenarios.All;

    public override string Location => Type ?? AnyException;
}

/// <summary>A data breakpoint (Set Data Breakpoint on a member in Variables): a field, an auto-property or an array
/// element of one object, watched for a write that changes it. It lasts as long as its session, the object being that
/// session's alone, so no folder keeps it and no other session is sent it.</summary>
public sealed class DataBreakpoint(string dataId, string location, object session) : Breakpoint
{
    /// <summary>What the debugger named the data watched by.</summary>
    public string DataId { get; } = dataId;

    /// <summary>The session whose program has the object.</summary>
    public object Session { get; } = session;

    /// <summary>The member, and the object it is of with its type: "count on counter (Counter)".</summary>
    public override string Location { get; } = location;
}

/// <summary>A property breakpoint, the Live Visual Tree's Break When Value Changes: an Avalonia property of one element of
/// an app being debugged, which stops the app in the code that changes it (a style's, a binding's, code-behind's, an
/// animation's), as a data breakpoint does for a field. It lasts as long as its session, as data breakpoints do, the
/// element being that run's alone, and no other session is sent it. Its condition is C# over the change: <c>newValue</c>,
/// <c>oldValue</c>, <c>control</c>.</summary>
public sealed class PropertyBreakpoint(int id, object session, long element, string described, string property) : Breakpoint
{
    /// <summary>The session debugging the app whose element it watches.</summary>
    public object Session { get; } = session;

    /// <summary>Its number in the session, which the inspector in the app and the debugger's condition name it by.</summary>
    public int Id { get; } = id;

    /// <summary>The element's id in the Live Visual Tree.</summary>
    public long Element { get; } = element;

    /// <summary>The property, by the name the element's details list it under (Owner.Name when attached).</summary>
    public string Property { get; } = property;

    /// <summary>"Background on Button#Save".</summary>
    public override string Location { get; } = $"{property} on {described}";
}

/// <summary>The XAML Binding Errors breakpoint, as an exception breakpoint is for exceptions: on, it stops an Avalonia or WPF app
/// being debugged the moment one of its bindings fails, where the app was when it failed (the inspector in the app calls
/// <see cref="Inspector.Wire.Failed"/> there). Every folder has the one, off until it is turned on, and none removes it.
/// Its condition and log message read the failure as <c>$bindingError</c>.</summary>
public sealed class BindingErrorBreakpoint : Breakpoint
{
    /// <summary>Its name in the Breakpoints window, under "XAML Binding Errors", as "Any exception" is under the CLR's.</summary>
    public const string AnyBindingError = "Any binding error";

    public BindingErrorBreakpoint() => Enabled = false;

    public override string Location => AnyBindingError;
}

public enum BreakpointStatus
{
    /// <summary>No debugger has said, or none is running.</summary>
    Unknown,
    Bound,

    /// <summary>Refused: it will not stop the program (a hit count the debugger cannot read, a line written since the
    /// build), and its <see cref="Breakpoint.Message"/> says why.</summary>
    Invalid,
}

/// <summary>A folder's breakpoints as the store keeps them.</summary>
public sealed record BreakpointsState : IStateDocument<BreakpointsState>
{
    public List<KeptBreakpoint> Breakpoints { get; init; } = [];

    /// <summary>The CLR exception breakpoints, "Any exception" (no type) among them; none kept is "Any exception" as it
    /// starts.</summary>
    public List<KeptExceptionBreakpoint>? Exceptions { get; init; }

    /// <summary>The XAML Binding Errors breakpoint; none kept is off.</summary>
    public KeptBindingErrorBreakpoint? BindingErrors { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>One without a file is left out, and a line before the first is the first; an exception type kept twice is
    /// kept once.</summary>
    public BreakpointsState Clamped() => this with
    {
        Breakpoints = [.. (Breakpoints ?? []).Where(b => b is { Path.Length: > 0 }).Select(b => b with { Line = Math.Max(0, b.Line) })],
        Exceptions = Exceptions is null ? null : [.. Exceptions.Where(e => e is not null && e.Type is null or { Length: > 0 }).DistinctBy(e => e.Type ?? string.Empty)],
    };
}

/// <summary>A breakpoint as kept: its file, its line from zero (and XAML attribute) and what it does, but nothing a
/// debugger said of it.</summary>
public sealed record KeptBreakpoint(string Path, int Line)
{
    public string? Attribute { get; init; }

    public bool Enabled { get; init; } = true;
    public bool Suspend { get; init; } = true;
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
    public string? LogMessage { get; init; }
}

/// <summary>An exception breakpoint as kept: its type (none for "Any exception"), what it does and its "Suspend
/// If".</summary>
public sealed record KeptExceptionBreakpoint(string? Type)
{
    public bool Enabled { get; init; } = true;
    public bool Suspend { get; init; } = true;
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
    public string? LogMessage { get; init; }
    public bool ThrownInUserCode { get; init; } = true;
    public bool ThrownInExternalCode { get; init; } = true;
    public bool HandledInUserCode { get; init; } = true;
    public bool HandledInExternalCode { get; init; } = true;
    public bool Unhandled { get; init; } = true;
}

/// <summary>The XAML Binding Errors breakpoint as kept: whether it is on and what it does.</summary>
public sealed record KeptBindingErrorBreakpoint
{
    public bool Enabled { get; init; }
    public bool Suspend { get; init; } = true;
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
    public string? LogMessage { get; init; }
}

/// <summary>Every breakpoint: the line breakpoints by file, the CLR exception breakpoints, XAML Binding Errors and the
/// session's data and property breakpoints. A breakpoint in an open file moves with the text around it by an anchor in it,
/// so an edit above it leaves it on its line; one whose line is deleted lands where the deletion did, and two on one line
/// become one.
/// Muted, none of them stops the program, though each keeps whether it is on.</summary>
public sealed class BreakpointStore
{
    private readonly Dictionary<string, List<LineBreakpoint>> byPath = new(StringComparer.Ordinal);
    private readonly List<ExceptionBreakpoint> exceptions = [new(null)];
    private readonly List<DataBreakpoint> data = [];
    private readonly List<PropertyBreakpoint> properties = [];

    /// <summary>The text of each file open, whose breakpoints it carries.</summary>
    private readonly Dictionary<string, TextDocument> open = new(StringComparer.Ordinal);
    private int lastProperty;

    /// <summary>A file's breakpoints changed: added, removed, moved, muted, or the debugger said something.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>The property breakpoints changed: added, removed, muted, what one does, or what the debugger said.</summary>
    public event EventHandler? PropertiesChanged;

    /// <summary>The property breakpoints of the session, in the order they were set.</summary>
    public IReadOnlyList<PropertyBreakpoint> Properties => properties;

    /// <summary>The exception breakpoints changed: added, removed, muted, what one does, or what the debugger said.</summary>
    public event EventHandler? ExceptionsChanged;

    /// <summary>The data breakpoints changed: added, removed, muted, what one does, or what the debugger said.</summary>
    public event EventHandler? DataChanged;

    /// <summary>XAML Binding Errors changed: turned on or off, muted, what it does, or what the debugger said.</summary>
    public event EventHandler? BindingErrorsChanged;

    /// <summary>XAML Binding Errors, the folder's one, off until it is turned on.</summary>
    public BindingErrorBreakpoint BindingErrors { get; } = new();

    public IEnumerable<string> Files => byPath.Where(p => p.Value.Count > 0).Select(p => p.Key);

    /// <summary>Every line breakpoint.</summary>
    public IEnumerable<LineBreakpoint> All => byPath.Values.SelectMany(b => b);

    /// <summary>The exception breakpoints: "Any exception" first, then the types in the order they were added.</summary>
    public IReadOnlyList<ExceptionBreakpoint> Exceptions => exceptions;

    /// <summary>The data breakpoints of the session, in the order they were set.</summary>
    public IReadOnlyList<DataBreakpoint> Data => data;

    /// <summary>Mute Breakpoints: none stops the program, nor logs, until they are unmuted.</summary>
    public bool Muted { get; private set; }

    public IReadOnlyList<LineBreakpoint> In(string path) => byPath.TryGetValue(path, out List<LineBreakpoint>? found) ? found : [];

    /// <summary>Every breakpoint as the folder keeps it.</summary>
    public BreakpointsState Capture() => new()
    {
        Breakpoints = [.. All.Select(b => new KeptBreakpoint(b.Path, b.Line)
        {
            Attribute = b.Attribute,
            Enabled = b.Enabled,
            Suspend = b.Suspend,
            Condition = b.Condition,
            HitCondition = b.HitCondition,
            LogMessage = b.LogMessage,
        })],
        Exceptions = [.. exceptions.Select(e => new KeptExceptionBreakpoint(e.Type)
        {
            Enabled = e.Enabled,
            Suspend = e.Suspend,
            Condition = e.Condition,
            HitCondition = e.HitCondition,
            LogMessage = e.LogMessage,
            ThrownInUserCode = e.Scenarios.HasFlag(ExceptionScenarios.ThrownInUserCode),
            ThrownInExternalCode = e.Scenarios.HasFlag(ExceptionScenarios.ThrownInExternalCode),
            HandledInUserCode = e.Scenarios.HasFlag(ExceptionScenarios.HandledInUserCode),
            HandledInExternalCode = e.Scenarios.HasFlag(ExceptionScenarios.HandledInExternalCode),
            Unhandled = e.Scenarios.HasFlag(ExceptionScenarios.Unhandled),
        })],
        BindingErrors = new KeptBindingErrorBreakpoint
        {
            Enabled = BindingErrors.Enabled,
            Suspend = BindingErrors.Suspend,
            Condition = BindingErrors.Condition,
            HitCondition = BindingErrors.HitCondition,
            LogMessage = BindingErrors.LogMessage,
        },
    };

    /// <summary>Sets the breakpoints a folder kept, beside those there are; a line with one already keeps it. The exception
    /// breakpoints are the folder's own.</summary>
    public void Restore(BreakpointsState kept)
    {
        foreach (IGrouping<string, KeptBreakpoint> file in kept.Breakpoints.GroupBy(b => b.Path))
        {
            List<LineBreakpoint> list = For(file.Key);
            foreach (KeptBreakpoint breakpoint in file.Where(b => list.All(there => there.Line != b.Line || there.Attribute != b.Attribute)))
            {
                list.Add(Anchored(new LineBreakpoint(breakpoint.Path, breakpoint.Line, breakpoint.Attribute)
                {
                    Enabled = breakpoint.Enabled,
                    Suspend = breakpoint.Suspend,
                    Condition = breakpoint.Condition,
                    HitCondition = breakpoint.HitCondition,
                    LogMessage = breakpoint.LogMessage,
                }));
            }
            Sort(list);
            Changed?.Invoke(this, file.Key);
        }
        if (kept.Exceptions is { } keptExceptions)
        {
            exceptions.Clear();
            exceptions.Add(new ExceptionBreakpoint(null));
            foreach (KeptExceptionBreakpoint breakpoint in keptExceptions)
            {
                ExceptionBreakpoint restored = breakpoint.Type is null ? exceptions[0] : new ExceptionBreakpoint(breakpoint.Type);
                restored.Enabled = breakpoint.Enabled;
                restored.Suspend = breakpoint.Suspend;
                restored.Condition = breakpoint.Condition;
                restored.HitCondition = breakpoint.HitCondition;
                restored.LogMessage = breakpoint.LogMessage;
                restored.Scenarios = (breakpoint.ThrownInUserCode ? ExceptionScenarios.ThrownInUserCode : 0)
                    | (breakpoint.ThrownInExternalCode ? ExceptionScenarios.ThrownInExternalCode : 0)
                    | (breakpoint.HandledInUserCode ? ExceptionScenarios.HandledInUserCode : 0)
                    | (breakpoint.HandledInExternalCode ? ExceptionScenarios.HandledInExternalCode : 0)
                    | (breakpoint.Unhandled ? ExceptionScenarios.Unhandled : 0);
                if (!restored.IsAny)
                {
                    exceptions.Add(restored);
                }
            }
            ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        }
        if (kept.BindingErrors is { } bindingErrors)
        {
            SetBindingErrors(bindingErrors);
        }
    }

    /// <summary>XAML Binding Errors as kept, or as a folder's is at first (off).</summary>
    private void SetBindingErrors(KeptBindingErrorBreakpoint kept)
    {
        BindingErrors.Enabled = kept.Enabled;
        BindingErrors.Suspend = kept.Suspend;
        BindingErrors.Condition = kept.Condition;
        BindingErrors.HitCondition = kept.HitCondition;
        BindingErrors.LogMessage = kept.LogMessage;
        BindingErrorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Takes every breakpoint away, as another folder opens: each folder's are its own, "Any exception" as it
    /// starts and XAML Binding Errors off until the folder says otherwise, and none muted.</summary>
    public void Clear()
    {
        Muted = false;
        foreach (string path in Files.ToList())
        {
            Unanchor(byPath[path]);
            byPath[path].Clear();
            Changed?.Invoke(this, path);
        }
        exceptions.Clear();
        exceptions.Add(new ExceptionBreakpoint(null));
        ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        SetBindingErrors(new KeptBindingErrorBreakpoint());
        ClearData();
    }

    /// <summary>Mutes every breakpoint, or unmutes them (Mute Breakpoints).</summary>
    public void SetMuted(bool muted)
    {
        if (Muted == muted)
        {
            return;
        }
        Muted = muted;
        foreach (string path in Files.ToList())
        {
            Changed?.Invoke(this, path);
        }
        ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        DataChanged?.Invoke(this, EventArgs.Empty);
        PropertiesChanged?.Invoke(this, EventArgs.Empty);
        BindingErrorsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The data breakpoint on what a session's debugger names by a data id, set to stop at every write that changes
    /// it; the one there is already when there is.</summary>
    public DataBreakpoint AddData(string dataId, string location, object session)
    {
        if (FindData(dataId, session) is { } existing)
        {
            return existing;
        }
        DataBreakpoint added = new(dataId, location, session);
        data.Add(added);
        DataChanged?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <summary>The data breakpoint on what a session's debugger names by a data id, if one is set.</summary>
    public DataBreakpoint? FindData(string dataId, object session) => data.FirstOrDefault(d => d.DataId == dataId && d.Session == session);

    /// <summary>The sessions the breakpoints go to, from their start to their end.</summary>
    private readonly HashSet<object> sessions = [];

    /// <summary>A session starts: it is sent the breakpoints, and tells what its debugger made of each.</summary>
    public void Join(object session) => sessions.Add(session);

    /// <summary>A session ends: what its debugger said is forgotten, and its data and property breakpoints go with its
    /// objects and its app's elements.</summary>
    public void Leave(object session)
    {
        sessions.Remove(session);
        foreach ((string path, List<LineBreakpoint> list) in byPath)
        {
            if (list.Aggregate(false, (any, b) => b.Forget(session) | any))
            {
                Changed?.Invoke(this, path);
            }
        }
        if (exceptions.Aggregate(false, (any, e) => e.Forget(session) | any))
        {
            ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        }
        if (BindingErrors.Forget(session))
        {
            BindingErrorsChanged?.Invoke(this, EventArgs.Empty);
        }
        if (data.RemoveAll(d => d.Session == session) > 0)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        bool forgotten = properties.Aggregate(false, (any, p) => p.Forget(session) | any);
        if (properties.RemoveAll(p => p.Session == session) > 0)
        {
            forgotten = true;
        }
        if (forgotten)
        {
            PropertiesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Lets every data and property breakpoint go, as another folder opens.</summary>
    private void ClearData()
    {
        if (data.Count > 0)
        {
            data.Clear();
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        if (properties.Count > 0)
        {
            properties.Clear();
            PropertiesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The property breakpoint on a property of an element of the app a session debugs (by its Live Visual Tree id),
    /// set to stop at every change; the one there is already when there is.</summary>
    public PropertyBreakpoint AddProperty(object session, long element, string described, string property)
    {
        if (FindProperty(session, element, property) is { } existing)
        {
            return existing;
        }
        PropertyBreakpoint added = new(++lastProperty, session, element, described, property);
        properties.Add(added);
        PropertiesChanged?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <summary>The property breakpoint on a property of an element of a session's app, if one is set.</summary>
    public PropertyBreakpoint? FindProperty(object session, long element, string property) =>
        properties.FirstOrDefault(p => p.Session == session && p.Element == element && p.Property == property);

    /// <summary>Records what a session's debugger said of its property breakpoints; those it was not asked of (off, or
    /// muted) go back to unknown.</summary>
    public void ReportProperties(object session, IReadOnlyList<(PropertyBreakpoint Breakpoint, BreakpointStatus Status, string? Message)> reports)
    {
        foreach (PropertyBreakpoint breakpoint in properties.Where(p => p.Session == session))
        {
            (BreakpointStatus status, string? message) = reports.FirstOrDefault(r => ReferenceEquals(r.Breakpoint, breakpoint)) is { Breakpoint: not null } said
                ? (said.Status, said.Message)
                : (BreakpointStatus.Unknown, null);
            breakpoint.Tell(session, status, message);
        }
        PropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records what a session's debugger said of one property breakpoint since it was sent (bound as its app loaded).</summary>
    public void ReportProperty(object session, PropertyBreakpoint breakpoint, BreakpointStatus status, string? message)
    {
        breakpoint.Tell(session, status, message);
        PropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets a breakpoint on a line, or removes those there (its XAML attributes' too).</summary>
    public void Toggle(string path, int line)
    {
        List<LineBreakpoint> list = For(path);
        Unanchor(list.Where(b => b.Line == line));
        if (list.RemoveAll(b => b.Line == line) == 0)
        {
            list.Add(Anchored(new LineBreakpoint(path, line)));
            Sort(list);
        }
        Changed?.Invoke(this, path);
    }

    /// <summary>Sets a breakpoint on a XAML attribute of a line, or removes the one there; the line's own and its other
    /// attributes' stay.</summary>
    public void ToggleAttribute(string path, int line, string attribute)
    {
        List<LineBreakpoint> list = For(path);
        if (list.FirstOrDefault(b => b.Line == line && b.Attribute == attribute) is { } existing)
        {
            list.Remove(existing);
            existing.At.Detach();
        }
        else
        {
            list.Add(Anchored(new LineBreakpoint(path, line, attribute)));
            Sort(list);
        }
        Changed?.Invoke(this, path);
    }

    /// <summary>The breakpoint on a line, or on a XAML attribute of it, set if there is none.</summary>
    public LineBreakpoint Set(string path, int line, string? attribute = null)
    {
        List<LineBreakpoint> list = For(path);
        if (list.FirstOrDefault(b => b.Line == line && b.Attribute == attribute) is { } existing)
        {
            return existing;
        }
        LineBreakpoint added = Anchored(new LineBreakpoint(path, line, attribute));
        list.Add(added);
        Sort(list);
        Changed?.Invoke(this, path);
        return added;
    }

    /// <summary>A file's breakpoints in order: by line, a line's own before its attributes'.</summary>
    private static void Sort(List<LineBreakpoint> list) =>
        list.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : (a.Attribute is null ? 0 : 1).CompareTo(b.Attribute is null ? 0 : 1));

    /// <summary>Turns a line's breakpoint off or back on, keeping it; sets one if there is none.</summary>
    public void ToggleEnabled(string path, int line)
    {
        if (In(path).FirstOrDefault(b => b.Line == line) is { } existing)
        {
            existing.Enabled = !existing.Enabled;
            Changed?.Invoke(this, path);
        }
        else
        {
            Toggle(path, line);
        }
    }

    /// <summary>The breakpoint on an exception type (a full name, or "Any exception"), set to stop wherever the exception is
    /// thrown; the one there is already when there is.</summary>
    public ExceptionBreakpoint AddException(string type)
    {
        string name = type.Trim();
        if (FindException(name) is { } existing)
        {
            return existing;
        }
        ExceptionBreakpoint added = new(name);
        exceptions.Add(added);
        ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <summary>The breakpoint on an exception type by its full name, or "Any exception".</summary>
    public ExceptionBreakpoint? FindException(string type) => type.Trim() == ExceptionBreakpoint.AnyException
        ? exceptions[0]
        : exceptions.FirstOrDefault(e => string.Equals(e.Type, type.Trim(), StringComparison.Ordinal));

    /// <summary>Which scenarios an exception breakpoint stops in.</summary>
    public void SetScenarios(ExceptionBreakpoint breakpoint, ExceptionScenarios scenarios)
    {
        if (breakpoint.Scenarios != scenarios)
        {
            breakpoint.Scenarios = scenarios;
            ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetCondition(LineBreakpoint breakpoint, string? condition, string? hitCondition) =>
        Update(breakpoint, breakpoint.Enabled, breakpoint.Suspend, condition, hitCondition, breakpoint.LogMessage);

    /// <summary>Changes what a breakpoint does, as its popup or the Breakpoints window does; blank text is none.</summary>
    public void Update(Breakpoint breakpoint, bool enabled, bool suspend, string? condition, string? hitCondition, string? logMessage)
    {
        breakpoint.Enabled = enabled;
        breakpoint.Suspend = suspend;
        breakpoint.Condition = string.IsNullOrWhiteSpace(condition) ? null : condition.Trim();
        breakpoint.HitCondition = string.IsNullOrWhiteSpace(hitCondition) ? null : hitCondition.Trim();
        breakpoint.LogMessage = string.IsNullOrWhiteSpace(logMessage) ? null : logMessage;
        Raise(breakpoint);
    }

    /// <summary>Sets a breakpoint that does not stop (Shift+click in the gutter), or removes the one on that line.</summary>
    public void ToggleTracepoint(string path, int line)
    {
        if (In(path).Any(b => b.Line == line))
        {
            Toggle(path, line);
            return;
        }
        LineBreakpoint tracepoint = Anchored(new LineBreakpoint(path, line) { Suspend = false });
        List<LineBreakpoint> list = For(path);
        list.Add(tracepoint);
        Sort(list);
        Changed?.Invoke(this, path);
    }

    /// <summary>Removes a breakpoint; "Any exception" stays, turned off by its box instead, as XAML Binding Errors
    /// is.</summary>
    public void Remove(Breakpoint breakpoint)
    {
        if (breakpoint is LineBreakpoint line && For(line.Path).Remove(line))
        {
            line.At.Detach();
            Changed?.Invoke(this, line.Path);
        }
        else if (breakpoint is ExceptionBreakpoint { IsAny: false } exception && exceptions.Remove(exception))
        {
            ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (breakpoint is DataBreakpoint watched && data.Remove(watched))
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (breakpoint is PropertyBreakpoint property && properties.Remove(property))
        {
            PropertiesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Records what a session's debugger said of a file's breakpoints, in the order they were sent.</summary>
    public void Report(object session, string path, IReadOnlyList<(BreakpointStatus Status, string? Message, int? Line)> reports, IReadOnlyList<LineBreakpoint> sent)
    {
        for (int i = 0; i < Math.Min(reports.Count, sent.Count); i++)
        {
            sent[i].Tell(session, reports[i].Status, reports[i].Message);
        }
        Changed?.Invoke(this, path);
    }

    /// <summary>Records what a session's debugger said of XAML Binding Errors (unknown when it was not sent).</summary>
    public void ReportBindingErrors(object session, BreakpointStatus status, string? message)
    {
        (BreakpointStatus was, string? said) = (BindingErrors.Status, BindingErrors.Message);
        BindingErrors.Tell(session, status, message);
        if (BindingErrors.Status != was || BindingErrors.Message != said)
        {
            BindingErrorsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Records what a session's debugger said of the exception breakpoints sent, in the order they were sent; those
    /// not sent (off, or muted) go back to unknown.</summary>
    public void ReportExceptions(object session, IReadOnlyList<(BreakpointStatus Status, string? Message)> reports, IReadOnlyList<ExceptionBreakpoint> sent)
    {
        foreach (ExceptionBreakpoint breakpoint in exceptions)
        {
            int at = IndexOf(sent, breakpoint);
            (BreakpointStatus status, string? message) = at >= 0 && at < reports.Count ? reports[at] : (BreakpointStatus.Unknown, null);
            breakpoint.Tell(session, status, message);
        }
        ExceptionsChanged?.Invoke(this, EventArgs.Empty);

        static int IndexOf(IReadOnlyList<ExceptionBreakpoint> list, ExceptionBreakpoint item)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (ReferenceEquals(list[i], item))
                {
                    return i;
                }
            }
            return -1;
        }
    }

    /// <summary>Records what a session's debugger said of its data breakpoints sent, in the order they were sent; those not
    /// sent (off, or muted) go back to unknown.</summary>
    public void ReportData(object session, IReadOnlyList<(BreakpointStatus Status, string? Message)> reports, IReadOnlyList<DataBreakpoint> sent)
    {
        foreach (DataBreakpoint breakpoint in data.Where(d => d.Session == session))
        {
            int at = -1;
            for (int i = 0; i < sent.Count; i++)
            {
                if (ReferenceEquals(sent[i], breakpoint))
                {
                    at = i;
                    break;
                }
            }
            (BreakpointStatus status, string? message) = at >= 0 && at < reports.Count ? reports[at] : (BreakpointStatus.Unknown, null);
            breakpoint.Tell(session, status, message);
        }
        DataChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Carries the breakpoints of a file, or of every file in a folder, to where it moved: the debugger hears
    /// that the old path has none and the new one has them.</summary>
    public void Move(string from, string to)
    {
        foreach (string path in byPath.Keys.Where(p => PathNames.Within(p, from)).ToList())
        {
            string moved = PathNames.Moved(path, from, to);
            List<LineBreakpoint> list = byPath[path];
            byPath.Remove(path);
            foreach (LineBreakpoint breakpoint in list)
            {
                breakpoint.Path = moved;
            }
            byPath[moved] = list;
            Changed?.Invoke(this, path);
            Changed?.Invoke(this, moved);
        }
        foreach (string path in open.Keys.Where(p => PathNames.Within(p, from)).ToList())
        {
            open.Remove(path, out TextDocument? text);
            open[PathNames.Moved(path, from, to)] = text!;
        }
    }

    /// <summary>A file opened: its breakpoints move with its text until it closes.</summary>
    public void Attach(string path, TextDocument text)
    {
        open[path] = text;
        foreach (LineBreakpoint breakpoint in In(path))
        {
            breakpoint.At.Attach(text);
        }
    }

    /// <summary>A file closed: its breakpoints stay on the lines they were carried to.</summary>
    public void Detach(string path)
    {
        open.Remove(path);
        Unanchor(In(path));
    }

    /// <summary>The session ended with files still open, which the next session follows: every file is let go as if
    /// closed, its breakpoints kept on the lines they were carried to.</summary>
    public void DetachAll()
    {
        foreach (string path in open.Keys.ToList())
        {
            Detach(path);
        }
    }

    /// <summary>Reads the lines of a file's breakpoints after an edit to it, their anchors having carried them.</summary>
    public void Follow(string path)
    {
        if (!byPath.TryGetValue(path, out List<LineBreakpoint>? list) || list.Count == 0)
        {
            return;
        }
        bool moved = false;
        foreach (LineBreakpoint breakpoint in list)
        {
            moved |= breakpoint.At.Follow();
        }
        if (moved)
        {
            List<LineBreakpoint> unique = [.. list.GroupBy(b => (b.Line, b.Attribute)).Select(g => g.First())];
            Unanchor(list.Except(unique));
            Sort(unique);
            list.Clear();
            list.AddRange(unique);
            Changed?.Invoke(this, path);
        }
    }

    private LineBreakpoint Anchored(LineBreakpoint breakpoint)
    {
        if (open.TryGetValue(breakpoint.Path, out TextDocument? text))
        {
            breakpoint.At.Attach(text);
        }
        return breakpoint;
    }

    private static void Unanchor(IEnumerable<LineBreakpoint> breakpoints)
    {
        foreach (LineBreakpoint breakpoint in breakpoints.ToList())
        {
            breakpoint.At.Detach();
        }
    }

    private void Raise(Breakpoint breakpoint)
    {
        if (breakpoint is LineBreakpoint line)
        {
            Changed?.Invoke(this, line.Path);
        }
        else if (breakpoint is DataBreakpoint)
        {
            DataChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (breakpoint is PropertyBreakpoint)
        {
            PropertiesChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (breakpoint is BindingErrorBreakpoint)
        {
            BindingErrorsChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<LineBreakpoint> For(string path)
    {
        if (!byPath.TryGetValue(path, out List<LineBreakpoint>? list))
        {
            list = [];
            byPath[path] = list;
        }
        return list;
    }
}
