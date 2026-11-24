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
    private static int lastKey;

    /// <summary>What each session's debugger last said of it, by the session, with how many lines below its own a line
    /// breakpoint bound.</summary>
    private readonly Dictionary<object, (BreakpointStatus Status, string? Message, int? Line)> said = [];

    /// <summary>The debuggers' number for it, by which one waiting for it names it: never another breakpoint's.</summary>
    public int Key { get; } = Interlocked.Increment(ref lastKey);

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

    /// <summary>How many lines below its own a line breakpoint bound (above, for less than zero), where the debugger moved it
    /// to the nearest line with code; zero at its own, null while it is not bound. Kept as a distance, so it goes with the
    /// breakpoint's line as edits above it move both.</summary>
    public int? MovedBy { get; private set; }

    /// <summary>Records what one session's debugger said of it, and how far from its own line a line breakpoint bound.</summary>
    internal void Tell(object session, BreakpointStatus status, string? message, int? movedBy = null)
    {
        said[session] = (status, message, movedBy);
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

    private void Merge()
    {
        (Status, Message, int? moved) = said.Count == 0 ? (BreakpointStatus.Unknown, null, null)
            : said.Values.Any(s => s.Status == BreakpointStatus.Bound) ? said.Values.First(s => s.Status == BreakpointStatus.Bound)
            : said.Values.Any(s => s.Status == BreakpointStatus.Unknown) ? said.Values.First(s => s.Status == BreakpointStatus.Unknown)
            : said.Values.First();
        MovedBy = Status == BreakpointStatus.Bound ? moved : null;
    }

    /// <summary>How the Breakpoints window and the popup name it: "Program.cs:7", "Any exception".</summary>
    public abstract string Location { get; }
}

/// <summary>A breakpoint placed in code, on a line or on a method: a temporary one is taken away once it takes a hit (its
/// condition held and its hit count met), so it stops, or logs, once; one waiting for another passes over its hits until
/// that one has taken a hit in the same run, and from then on takes them as any breakpoint does.</summary>
public abstract class CodeBreakpoint : Breakpoint
{
    /// <summary>Taken away once it takes a hit.</summary>
    public bool Temporary { get; internal set; }

    /// <summary>The breakpoint whose hit it waits for, or null for none.</summary>
    public CodeBreakpoint? WaitsFor { get; internal set; }
}

/// <summary>A line breakpoint (lines from zero), carried with its file's text while it is open; in Avalonia XAML, on one
/// attribute of the line when it names one, at the column its name begins (<see cref="XamlBreakpoints"/>); in C#, inside
/// a lambda written on the line when it has a column (<see cref="LambdaBreakpoints"/>).</summary>
public sealed class LineBreakpoint(string path, int line, string? attribute = null, int? column = null) : CodeBreakpoint
{
    public string Path { get; internal set; } = path;

    public int Line => At.Line;

    internal AnchoredLine At { get; } = new(line, column);

    /// <summary>The XAML attribute it is on, by its name as written, or null for its whole line.</summary>
    public string? Attribute { get; } = attribute;

    /// <summary>The column (from zero) of the C# lambda's body it stops inside, carried with the text around it; null for its
    /// whole line and for a XAML attribute's.</summary>
    public int? Column => Attribute is null ? At.Column : null;

    /// <summary>The column (from zero) its XAML attribute's name begins at, carried with the text around it, which tells two
    /// attributes of one name on the line apart; null for none, and for one kept without it, on the first of its name.</summary>
    public int? AttributeColumn => Attribute is null ? null : At.Column;

    /// <summary>Whether it is on part of its line: an attribute or a lambda.</summary>
    public bool OnPart => Attribute is not null || Column is not null;

    public override string Location => $"{System.IO.Path.GetFileName(Path)}:{Line + 1}{(Column is int at ? $":{at + 1}" : string.Empty)}"
        + (Attribute is { } on ? $" {on}" : string.Empty);
}

/// <summary>A method breakpoint: a .NET method by its name as C# writes it (Name, Type.Name or Namespace.Type.Name, a
/// generic one's arity as Name&lt;T&gt;, and its parameters' types where only one overload is meant), stopping as each
/// method it names begins, in every module loaded and loaded later. Set at a method's declaration in the editor, it keeps
/// that line of its file (from zero), carried with the file's text as a line breakpoint's is, where the gutter marks it.</summary>
public sealed class MethodBreakpoint(string name, string? path = null, int line = 0) : CodeBreakpoint
{
    /// <summary>The method's name as the debugger is asked for it.</summary>
    public string Name { get; } = name;

    /// <summary>The file it was set in, or null for one set by its name alone.</summary>
    public string? Path { get; internal set; } = path;

    /// <summary>The line its method is declared on in <see cref="Path"/>.</summary>
    public int Line => At.Line;

    internal AnchoredLine At { get; } = new(line);

    public override string Location => Name;
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

    /// <summary>Why it never stops in its scenarios (<see cref="InactiveIn"/>); null while it may.</summary>
    public string? Inactive => InactiveIn(Scenarios);

    public override string Location => Type ?? AnyException;

    /// <summary>Why an exception breakpoint in <paramref name="scenarios"/> never stops, the same wherever it is said (its
    /// Suspend If, an agent's answer): it stops only when one of each row, thrown and handled, holds, so a row with none
    /// leaves it inactive; null where each row has one.</summary>
    public static string? InactiveIn(ExceptionScenarios scenarios) =>
        (scenarios & ExceptionScenarios.Thrown) == 0
            ? "Exception breakpoint is currently inactive because all 'Exception thrown in' options are disabled"
            : (scenarios & (ExceptionScenarios.HandledInUserCode | ExceptionScenarios.HandledInExternalCode | ExceptionScenarios.Unhandled)) == 0
                ? "Exception breakpoint is currently inactive because all 'Exception handler' options are disabled"
                : null;
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

/// <summary>The GDScript Errors breakpoint: on, a Godot game being debugged stops at a script error where the error was
/// raised, its stack and values still there, as Godot's own editor stops it; off, the game goes on past each error, which
/// Problems lists. Every folder has the one, on until it is turned off, and none removes it. Its condition and log message
/// are GDScript, read in the frame that raised the error.</summary>
public sealed class ScriptErrorBreakpoint : Breakpoint
{
    /// <summary>Its name in the Breakpoints window, under "GDScript Errors".</summary>
    public const string AnyScriptError = "Any GDScript error";

    public override string Location => AnyScriptError;
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

/// <summary>Breakpoints that waited for one taken away (<paramref name="Gone"/>, by its place), which stop as plain breakpoints
/// from now on.</summary>
public sealed record WaitEnded(string Gone, IReadOnlyList<CodeBreakpoint> Freed)
{
    /// <summary>What the window says of it, once.</summary>
    public string Words => $"{Plural.Listed([.. Freed.Select(b => b.Location)])} {Plural.Agreeing(Freed.Count, "waits", "wait")} for no other "
        + $"breakpoint now and {Plural.Agreeing(Freed.Count, "stops as a plain breakpoint", "stop as plain breakpoints")}: {Gone}, which "
        + $"{Plural.Agreeing(Freed.Count, "it", "they")} waited for, was removed";
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

    /// <summary>The GDScript Errors breakpoint; none kept is on.</summary>
    public KeptScriptErrorBreakpoint? ScriptErrors { get; init; }

    /// <summary>The method breakpoints, in the order they were set.</summary>
    public List<KeptMethodBreakpoint>? Methods { get; init; }

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>One without a file is left out, and a line before the first is the first; an exception type kept twice is
    /// kept once.</summary>
    public BreakpointsState Clamped() => this with
    {
        Breakpoints = [.. (Breakpoints ?? []).Where(b => b is { Path.Length: > 0 }).Select(b => b with { Line = Math.Max(0, b.Line), Column = b.Column is >= 0 ? b.Column : null })],
        Exceptions = Exceptions is null ? null : [.. Exceptions.Where(e => e is not null && e.Type is null or { Length: > 0 }).DistinctBy(e => e.Type ?? string.Empty)],
        Methods = Methods is null ? null : [.. Methods.Where(m => m is { Name.Length: > 0 }).Select(m => m with { Line = Math.Max(0, m.Line) }).DistinctBy(m => m.Name.Trim())],
    };
}

/// <summary>A breakpoint as kept: its file, its line from zero (and XAML attribute with the column its name begins at, or C#
/// lambda's column, from zero) and what it does, but nothing a debugger said of it.</summary>
public sealed record KeptBreakpoint(string Path, int Line)
{
    public string? Attribute { get; init; }
    public int? Column { get; init; }

    public bool Enabled { get; init; } = true;
    public bool Suspend { get; init; } = true;
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
    public string? LogMessage { get; init; }
    public bool Temporary { get; init; }
    public KeptWaitFor? WaitsFor { get; init; }
}

/// <summary>A method breakpoint as kept: its method's name, the file and line (from zero) it was set at, if any, and what it
/// does.</summary>
public sealed record KeptMethodBreakpoint(string Name)
{
    public string? Path { get; init; }
    public int Line { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Suspend { get; init; } = true;
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
    public string? LogMessage { get; init; }
    public bool Temporary { get; init; }
    public KeptWaitFor? WaitsFor { get; init; }
}

/// <summary>The breakpoint another waits for, as kept: a line breakpoint by its file and line (from zero), with its XAML
/// attribute or lambda's column where it has one, or a method breakpoint by its method's name.</summary>
public sealed record KeptWaitFor
{
    public string? Path { get; init; }
    public int Line { get; init; }
    public string? Attribute { get; init; }
    public int? Column { get; init; }
    public string? Method { get; init; }
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

/// <summary>The GDScript Errors breakpoint as kept: whether it is on and what it does.</summary>
public sealed record KeptScriptErrorBreakpoint
{
    public bool Enabled { get; init; } = true;
    public bool Suspend { get; init; } = true;
    public string? Condition { get; init; }
    public string? HitCondition { get; init; }
    public string? LogMessage { get; init; }
}

/// <summary>Every breakpoint: the line breakpoints by file, the method breakpoints, the CLR exception breakpoints, XAML Binding
/// Errors, GDScript Errors and the session's data and property breakpoints. A breakpoint in an open file moves with the text
/// around it by an anchor in it, so an edit above it leaves it on its line; one whose line is deleted lands where the deletion
/// did, and two on one line become one. One taken away takes with it what others wait for, so those wait no more.
/// Muted, none of them stops the program, though each keeps whether it is on.</summary>
public sealed class BreakpointStore
{
    private readonly Dictionary<string, List<LineBreakpoint>> byPath = new(StringComparer.Ordinal);
    private readonly List<MethodBreakpoint> methods = [];
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

    /// <summary>The method breakpoints changed: added, removed, muted, moved, what one does, or what the debugger said. One set
    /// in a file raises <see cref="Changed"/> for its file too.</summary>
    public event EventHandler? MethodsChanged;

    /// <summary>The method breakpoints, in the order they were set.</summary>
    public IReadOnlyList<MethodBreakpoint> Methods => methods;

    /// <summary>Breakpoints that waited for one taken away wait no more, and stop as plain breakpoints from now on.</summary>
    public event EventHandler<WaitEnded>? WaitsEnded;

    /// <summary>The method breakpoints set in a file, which its gutter marks.</summary>
    public IEnumerable<MethodBreakpoint> MethodsIn(string path) => methods.Where(m => m.Path == path);

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

    /// <summary>GDScript Errors, the folder's one, on until it is turned off; its changes raise <see cref="ExceptionsChanged"/>.</summary>
    public ScriptErrorBreakpoint ScriptErrors { get; } = new();

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
            Column = b.At.Column,
            Enabled = b.Enabled,
            Suspend = b.Suspend,
            Condition = b.Condition,
            HitCondition = b.HitCondition,
            LogMessage = b.LogMessage,
            Temporary = b.Temporary,
            WaitsFor = Kept(b.WaitsFor),
        })],
        Methods = [.. methods.Select(m => new KeptMethodBreakpoint(m.Name)
        {
            Path = m.Path,
            Line = m.Line,
            Enabled = m.Enabled,
            Suspend = m.Suspend,
            Condition = m.Condition,
            HitCondition = m.HitCondition,
            LogMessage = m.LogMessage,
            Temporary = m.Temporary,
            WaitsFor = Kept(m.WaitsFor),
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
        ScriptErrors = new KeptScriptErrorBreakpoint
        {
            Enabled = ScriptErrors.Enabled,
            Suspend = ScriptErrors.Suspend,
            Condition = ScriptErrors.Condition,
            HitCondition = ScriptErrors.HitCondition,
            LogMessage = ScriptErrors.LogMessage,
        },
    };

    /// <summary>The breakpoint one waits for as it is kept.</summary>
    private static KeptWaitFor? Kept(CodeBreakpoint? waited) => waited switch
    {
        LineBreakpoint line => new KeptWaitFor { Path = line.Path, Line = line.Line, Attribute = line.Attribute, Column = line.At.Column },
        MethodBreakpoint method => new KeptWaitFor { Method = method.Name },
        _ => null,
    };

    /// <summary>The breakpoint set that a kept one waits for names, if there is one.</summary>
    private CodeBreakpoint? Found(KeptWaitFor? waited) => waited switch
    {
        { Method: { } name } => FindMethod(name),
        { Path: { } path } => In(path).FirstOrDefault(b => b.Line == waited.Line && b.Attribute == waited.Attribute && b.At.Column == waited.Column),
        _ => null,
    };

    /// <summary>Sets the breakpoints a folder kept, beside those there are; a line with one already keeps it, and a method
    /// named already keeps its own. What each waits for is found among them once all are set. The exception breakpoints are
    /// the folder's own.</summary>
    public void Restore(BreakpointsState kept)
    {
        List<(CodeBreakpoint Breakpoint, KeptWaitFor WaitsFor)> waiting = [];
        foreach (IGrouping<string, KeptBreakpoint> file in kept.Breakpoints.GroupBy(b => b.Path))
        {
            List<LineBreakpoint> list = For(file.Key);
            foreach (KeptBreakpoint breakpoint in file.Where(b => list.All(there => there.Line != b.Line || there.Attribute != b.Attribute || there.At.Column != b.Column)))
            {
                LineBreakpoint restored = Anchored(new LineBreakpoint(breakpoint.Path, breakpoint.Line, breakpoint.Attribute, breakpoint.Column)
                {
                    Enabled = breakpoint.Enabled,
                    Suspend = breakpoint.Suspend,
                    Condition = breakpoint.Condition,
                    HitCondition = breakpoint.HitCondition,
                    LogMessage = breakpoint.LogMessage,
                    Temporary = breakpoint.Temporary,
                });
                list.Add(restored);
                if (breakpoint.WaitsFor is { } waits)
                {
                    waiting.Add((restored, waits));
                }
            }
            Sort(list);
        }
        foreach (KeptMethodBreakpoint breakpoint in kept.Methods ?? [])
        {
            if (FindMethod(breakpoint.Name) is not null)
            {
                continue;
            }
            MethodBreakpoint restored = Anchored(new MethodBreakpoint(breakpoint.Name.Trim(), breakpoint.Path, breakpoint.Line)
            {
                Enabled = breakpoint.Enabled,
                Suspend = breakpoint.Suspend,
                Condition = breakpoint.Condition,
                HitCondition = breakpoint.HitCondition,
                LogMessage = breakpoint.LogMessage,
                Temporary = breakpoint.Temporary,
            });
            methods.Add(restored);
            if (breakpoint.WaitsFor is { } waits)
            {
                waiting.Add((restored, waits));
            }
        }
        foreach ((CodeBreakpoint breakpoint, KeptWaitFor waits) in waiting)
        {
            if (Found(waits) is { } found && CanWaitFor(breakpoint, found))
            {
                breakpoint.WaitsFor = found;
            }
        }
        foreach (string path in kept.Breakpoints.Select(b => b.Path).Concat((kept.Methods ?? []).Select(m => m.Path).OfType<string>()).Distinct(StringComparer.Ordinal))
        {
            Changed?.Invoke(this, path);
        }
        if (kept.Methods is { Count: > 0 })
        {
            MethodsChanged?.Invoke(this, EventArgs.Empty);
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
        if (kept.ScriptErrors is { } scriptErrors)
        {
            SetScriptErrors(scriptErrors);
        }
    }

    /// <summary>Adds the breakpoints a file of them held (Import) beside those there are: a line or method with one already,
    /// and an exception type with one already ("Any exception" always), keeps its own as it is. How many it added.</summary>
    public int Import(BreakpointsState file)
    {
        int before = All.Count() + methods.Count + exceptions.Count;
        Restore(file with { Exceptions = null, BindingErrors = null, ScriptErrors = null });
        bool added = false;
        foreach (KeptExceptionBreakpoint kept in file.Exceptions ?? [])
        {
            if (kept.Type is not { } type || FindException(type) is not null)
            {
                continue;
            }
            ExceptionBreakpoint exception = new(type.Trim())
            {
                Enabled = kept.Enabled,
                Suspend = kept.Suspend,
                Condition = kept.Condition,
                HitCondition = kept.HitCondition,
                LogMessage = kept.LogMessage,
                Scenarios = (kept.ThrownInUserCode ? ExceptionScenarios.ThrownInUserCode : 0)
                    | (kept.ThrownInExternalCode ? ExceptionScenarios.ThrownInExternalCode : 0)
                    | (kept.HandledInUserCode ? ExceptionScenarios.HandledInUserCode : 0)
                    | (kept.HandledInExternalCode ? ExceptionScenarios.HandledInExternalCode : 0)
                    | (kept.Unhandled ? ExceptionScenarios.Unhandled : 0),
            };
            exceptions.Add(exception);
            added = true;
        }
        if (added)
        {
            ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        }
        return All.Count() + methods.Count + exceptions.Count - before;
    }

    /// <summary>GDScript Errors as kept, or as a folder's is at first (on).</summary>
    private void SetScriptErrors(KeptScriptErrorBreakpoint kept)
    {
        ScriptErrors.Enabled = kept.Enabled;
        ScriptErrors.Suspend = kept.Suspend;
        ScriptErrors.Condition = kept.Condition;
        ScriptErrors.HitCondition = kept.HitCondition;
        ScriptErrors.LogMessage = kept.LogMessage;
        ExceptionsChanged?.Invoke(this, EventArgs.Empty);
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
        if (methods.Count > 0)
        {
            List<string> placed = [.. methods.Select(m => m.Path).OfType<string>().Distinct(StringComparer.Ordinal)];
            foreach (MethodBreakpoint method in methods)
            {
                method.At.Detach();
            }
            methods.Clear();
            foreach (string path in placed)
            {
                Changed?.Invoke(this, path);
            }
            MethodsChanged?.Invoke(this, EventArgs.Empty);
        }
        exceptions.Clear();
        exceptions.Add(new ExceptionBreakpoint(null));
        ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        SetBindingErrors(new KeptBindingErrorBreakpoint());
        SetScriptErrors(new KeptScriptErrorBreakpoint());
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
        MethodsChanged?.Invoke(this, EventArgs.Empty);
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
        if (exceptions.Aggregate(false, (any, e) => e.Forget(session) | any) | ScriptErrors.Forget(session))
        {
            ExceptionsChanged?.Invoke(this, EventArgs.Empty);
        }
        if (methods.Aggregate(false, (any, m) => m.Forget(session) | any))
        {
            RaiseMethods();
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

    /// <summary>Sets a breakpoint on a line, or removes those there (its XAML attributes' and C# lambdas' too).</summary>
    public void Toggle(string path, int line)
    {
        List<LineBreakpoint> list = For(path);
        List<LineBreakpoint> there = [.. list.Where(b => b.Line == line)];
        Unanchor(there);
        if (list.RemoveAll(b => b.Line == line) == 0)
        {
            list.Add(Anchored(new LineBreakpoint(path, line)));
            Sort(list);
        }
        Changed?.Invoke(this, path);
        WaitNoMore(there);
    }

    /// <summary>Sets a breakpoint on the XAML attribute whose name begins at a column (from zero) of a line, or removes the
    /// one there; the line's own and its other attributes' stay.</summary>
    public void ToggleAttribute(string path, int line, string attribute, int column)
    {
        List<LineBreakpoint> list = For(path);
        LineBreakpoint? existing = OnAttribute(path, line, attribute, column);
        if (existing is not null)
        {
            list.Remove(existing);
            existing.At.Detach();
        }
        else
        {
            list.Add(Anchored(new LineBreakpoint(path, line, attribute, column)));
            Sort(list);
        }
        Changed?.Invoke(this, path);
        WaitNoMore(existing is null ? [] : [existing]);
    }

    /// <summary>The breakpoint on the XAML attribute whose name begins at a column (from zero) of a line: each one's attribute
    /// found by its column in the file's text while it is open (<see cref="XamlBreakpoints.AttributeRange"/>), as it is
    /// drawn and sent, else by its column alone.</summary>
    public LineBreakpoint? OnAttribute(string path, int line, string attribute, int column)
    {
        TextSnapshot? text = open.TryGetValue(path, out TextDocument? document) ? document.Snapshot : null;
        return In(path).FirstOrDefault(b => b.Line == line && b.Attribute == attribute && PlaceOf(b) == column);

        int? PlaceOf(LineBreakpoint b) => text is null ? b.AttributeColumn
            : XamlBreakpoints.AttributeRange(text, line, attribute, b.AttributeColumn) is { } range ? range.Start - text.GetLineStart(line) : null;
    }

    /// <summary>The breakpoint on a line, on a XAML attribute of it or at a C# lambda's column (from zero) on it, set if there
    /// is none.</summary>
    public LineBreakpoint Set(string path, int line, string? attribute = null, int? column = null)
    {
        List<LineBreakpoint> list = For(path);
        if (list.FirstOrDefault(b => b.Line == line && b.Attribute == attribute && b.Column == column) is { } existing)
        {
            return existing;
        }
        LineBreakpoint added = Anchored(new LineBreakpoint(path, line, attribute, column));
        list.Add(added);
        Sort(list);
        Changed?.Invoke(this, path);
        return added;
    }

    /// <summary>Makes a file's breakpoints inside C# lambdas that are gone (<paramref name="gone"/>, given a line and a column
    /// from zero) their lines' own, each keeping what it does; one on a line holding its own already goes, the line's
    /// staying as it was.</summary>
    public void ToLines(string path, Func<int, int, bool> gone)
    {
        List<LineBreakpoint> lost = [.. In(path).Where(b => b.Column is int column && gone(b.Line, column))];
        if (lost.Count == 0)
        {
            return;
        }
        List<LineBreakpoint> list = For(path);
        List<LineBreakpoint> removed = [];
        foreach (LineBreakpoint breakpoint in lost)
        {
            if (list.Any(b => b.Line == breakpoint.Line && !b.OnPart))
            {
                list.Remove(breakpoint);
                breakpoint.At.Detach();
                removed.Add(breakpoint);
            }
            else
            {
                breakpoint.At.ToLine();
            }
        }
        Sort(list);
        Changed?.Invoke(this, path);
        WaitNoMore(removed);
    }

    /// <summary>A file's breakpoints in order: by line, a line's own before its attributes' and its lambdas', those by their
    /// columns.</summary>
    private static void Sort(List<LineBreakpoint> list) =>
        list.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line)
            : a.OnPart != b.OnPart ? a.OnPart.CompareTo(b.OnPart)
            : (a.At.Column ?? -1).CompareTo(b.At.Column ?? -1));

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

    /// <summary>Sets a temporary breakpoint on a line, taken away once it takes a hit, or removes those on that line.</summary>
    public void ToggleTemporary(string path, int line)
    {
        if (In(path).Any(b => b.Line == line))
        {
            Toggle(path, line);
            return;
        }
        List<LineBreakpoint> list = For(path);
        list.Add(Anchored(new LineBreakpoint(path, line) { Temporary = true }));
        Sort(list);
        Changed?.Invoke(this, path);
    }

    /// <summary>The method breakpoint on a method by its name, set if there is none, at the line (from zero) of a file where
    /// it is declared when it is set from there.</summary>
    public MethodBreakpoint AddMethod(string name, string? path = null, int line = 0)
    {
        if (FindMethod(name) is { } existing)
        {
            return existing;
        }
        MethodBreakpoint added = Anchored(new MethodBreakpoint(name.Trim(), path, line));
        methods.Add(added);
        RaiseMethods(path);
        return added;
    }

    /// <summary>Sets a method breakpoint on a method by its name, declared at a line (from zero) of a file, or removes the one
    /// on it.</summary>
    public void ToggleMethod(string name, string path, int line)
    {
        if (FindMethod(name) is { } existing)
        {
            Remove(existing);
        }
        else
        {
            AddMethod(name, path, line);
        }
    }

    /// <summary>The method breakpoint on a method by its name, if one is set.</summary>
    public MethodBreakpoint? FindMethod(string name) => methods.FirstOrDefault(m => string.Equals(m.Name, name.Trim(), StringComparison.Ordinal));

    /// <summary>Whether a breakpoint may wait for another: never itself, nor one that waits, however far along, for it, which
    /// would leave both waiting for good.</summary>
    public static bool CanWaitFor(CodeBreakpoint breakpoint, CodeBreakpoint waited)
    {
        for (CodeBreakpoint? at = waited; at is not null; at = at.WaitsFor)
        {
            if (ReferenceEquals(at, breakpoint))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Makes a breakpoint temporary or not, and what it waits for (null for nothing); one it cannot wait for
    /// (<see cref="CanWaitFor"/>) is refused.</summary>
    public void Arrange(CodeBreakpoint breakpoint, bool temporary, CodeBreakpoint? waitsFor)
    {
        if (waitsFor is not null && !CanWaitFor(breakpoint, waitsFor))
        {
            throw new ArgumentException($"{breakpoint.Location} cannot wait for {waitsFor.Location}, which waits for it");
        }
        if (breakpoint.Temporary == temporary && ReferenceEquals(breakpoint.WaitsFor, waitsFor))
        {
            return;
        }
        breakpoint.Temporary = temporary;
        breakpoint.WaitsFor = waitsFor;
        Raise(breakpoint);
    }

    /// <summary>Breakpoints taken away: those waiting for one of them wait no more, stopping as plain breakpoints from now on,
    /// told once for each one taken away (<see cref="WaitsEnded"/>).</summary>
    private void WaitNoMore(IReadOnlyCollection<Breakpoint> gone)
    {
        if (gone.Count == 0)
        {
            return;
        }
        List<(CodeBreakpoint Waiting, CodeBreakpoint Waited)> freed = [.. All.Concat<CodeBreakpoint>(methods)
            .Where(b => b.WaitsFor is { } waited && gone.Contains(waited)).Select(b => (b, b.WaitsFor!))];
        foreach ((CodeBreakpoint waiting, _) in freed)
        {
            waiting.WaitsFor = null;
            Raise(waiting);
        }
        foreach (IGrouping<CodeBreakpoint, (CodeBreakpoint Waiting, CodeBreakpoint Waited)> by in freed.GroupBy(f => f.Waited))
        {
            WaitsEnded?.Invoke(this, new WaitEnded(by.Key.Location, [.. by.Select(f => f.Waiting)]));
        }
    }

    /// <summary>The method breakpoints changed, and the file of one set in it.</summary>
    private void RaiseMethods(string? path = null)
    {
        if (path is not null)
        {
            Changed?.Invoke(this, path);
        }
        MethodsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records what a session's debugger said of the method breakpoints sent, in the order they were sent; those not
    /// sent (off, or muted) go back to unknown.</summary>
    public void ReportMethods(object session, IReadOnlyList<(BreakpointStatus Status, string? Message)> reports, IReadOnlyList<MethodBreakpoint> sent)
    {
        foreach (MethodBreakpoint breakpoint in methods)
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
        foreach (string path in methods.Select(m => m.Path).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            Changed?.Invoke(this, path);
        }
        MethodsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records what a session's debugger said of one method breakpoint since it was sent (bound as a module loaded).</summary>
    public void ReportMethod(object session, MethodBreakpoint breakpoint, BreakpointStatus status, string? message)
    {
        breakpoint.Tell(session, status, message);
        RaiseMethods(breakpoint.Path);
    }

    /// <summary>Removes a breakpoint; "Any exception" stays, turned off by its box instead, as XAML Binding Errors
    /// is.</summary>
    public void Remove(Breakpoint breakpoint)
    {
        if (breakpoint is LineBreakpoint line && For(line.Path).Remove(line))
        {
            line.At.Detach();
            Changed?.Invoke(this, line.Path);
            WaitNoMore([line]);
        }
        else if (breakpoint is MethodBreakpoint method && methods.Remove(method))
        {
            method.At.Detach();
            RaiseMethods(method.Path);
            WaitNoMore([method]);
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
            sent[i].Tell(session, reports[i].Status, reports[i].Message, reports[i].Line is int bound ? bound - sent[i].Line : null);
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

    /// <summary>Records what a session's debugger said of GDScript Errors (unknown when it was not sent).</summary>
    public void ReportScriptErrors(object session, BreakpointStatus status, string? message)
    {
        ScriptErrors.Tell(session, status, message);
        ExceptionsChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Records what a session's debugger said of one exception breakpoint since it was sent (a condition a hit could
    /// not tell).</summary>
    public void ReportException(object session, ExceptionBreakpoint breakpoint, BreakpointStatus status, string? message)
    {
        breakpoint.Tell(session, status, message);
        ExceptionsChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>Records what a session's debugger said of one data breakpoint since it was sent (a condition a hit could not
    /// tell).</summary>
    public void ReportData(object session, DataBreakpoint breakpoint, BreakpointStatus status, string? message)
    {
        breakpoint.Tell(session, status, message);
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
        foreach (MethodBreakpoint method in methods.Where(m => m.Path is { } path && PathNames.Within(path, from)).ToList())
        {
            string was = method.Path!;
            method.Path = PathNames.Moved(was, from, to);
            Changed?.Invoke(this, was);
            RaiseMethods(method.Path);
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
        foreach (MethodBreakpoint method in MethodsIn(path))
        {
            method.At.Attach(text);
        }
    }

    /// <summary>A file closed: its breakpoints stay on the lines they were carried to.</summary>
    public void Detach(string path)
    {
        open.Remove(path);
        Unanchor(In(path));
        foreach (MethodBreakpoint method in MethodsIn(path))
        {
            method.At.Detach();
        }
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
        bool methodsMoved = false;
        foreach (MethodBreakpoint method in MethodsIn(path))
        {
            methodsMoved |= method.At.Follow();
        }
        if (methodsMoved)
        {
            RaiseMethods(path);
        }
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
            List<LineBreakpoint> unique = [.. list.GroupBy(b => (b.Line, b.Attribute, b.At.Column)).Select(g => g.First())];
            List<LineBreakpoint> merged = [.. list.Except(unique)];
            Unanchor(merged);
            Sort(unique);
            list.Clear();
            list.AddRange(unique);
            Changed?.Invoke(this, path);
            WaitNoMore(merged);
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

    private MethodBreakpoint Anchored(MethodBreakpoint breakpoint)
    {
        if (breakpoint.Path is { } path && open.TryGetValue(path, out TextDocument? text))
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
        else if (breakpoint is MethodBreakpoint method)
        {
            RaiseMethods(method.Path);
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
