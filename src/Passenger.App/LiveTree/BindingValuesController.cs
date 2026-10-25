using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.CSharp;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Dap;
using Passenger.Editor;
using Passenger.Inspector;
using Passenger.Text;

namespace Passenger.App.LiveTree;

/// <summary>A debugged Avalonia app's binding values after an .axaml file's bindings on screen, a failed one marked, and
/// on hover the path walked step by step. Paused, the debugger evaluates the inspector's read on the UI thread
/// (<see cref="Wire.ReadValues"/>), each value as Variables shows it; running, the inspector answers for the control
/// chosen in the Live Visual Tree, which also tells apart an element's instances (a template's). Read once a stop or a
/// choice, for the lines shown. A UI-thread object.</summary>
public sealed class BindingValuesController
{
    /// <summary>The longest value shown after a line.</summary>
    private const int Longest = 60;

    private readonly EditorArea editors;
    private readonly LiveTreeController tree;
    private readonly Debounce soon;
    private readonly Dictionary<OpenDocument, Reading> readings = [];
    private ProjectSession? session;
    private LiveApp? app;

    /// <summary>Bumped by whatever makes what was read out of date: a stop or a run, another control chosen, the app's controls
    /// or values changed.</summary>
    private int generation;
    private bool refreshing;
    private bool again;

    public BindingValuesController(EditorArea editors, LiveTreeController tree)
    {
        this.editors = editors;
        this.tree = tree;
        soon = new Debounce(TimeSpan.FromMilliseconds(100), RefreshAsync, Hold);
        tree.ChosenChanged += (_, _) => Invalidate();
        editors.ActiveChanged += (_, _) => soon.Restart();
        editors.Opened += (_, document) => Follow(document);
        editors.Closed += (_, document) => readings.Remove(document);
        editors.ForEachEditor(editor => editor.ScrollChanged += (_, _) => soon.Restart());
    }

    /// <summary>The waits and reads under way, from a change to the values it brings landing.</summary>
    private int reading;

    /// <summary>Whether values are being read for the files shown, or about to be, so a wait for it ends with what the
    /// editor shows once the stop is read.</summary>
    public bool IsReading => reading > 0 || refreshing;

    private IDisposable Hold()
    {
        reading++;
        return new Held(this);
    }

    private sealed class Held(BindingValuesController owner) : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (!done)
            {
                done = true;
                owner.reading--;
            }
        }
    }

    /// <summary>How values are read: by the debugger's evaluation at a stop, or from the app as it runs.</summary>
    private enum Mode
    {
        Paused,
        Running,
    }

    /// <summary>What was read of one file in one generation: each element asked of (by the place its build recorded), what
    /// the app said of those it made, and why none could be read, if none could.</summary>
    private sealed class Reading(int generation)
    {
        public int Generation { get; } = generation;

        public HashSet<(int Line, int Column)> Asked { get; } = [];

        public Dictionary<(int Line, int Column), ElementValues> Elements { get; } = [];

        /// <summary>Why nothing was read, in short (after the line) and in full (on hover).</summary>
        public (string Short, string Full)? Problem { get; set; }
    }

    /// <summary>Follows a session from now: its debugger's stops and runs, and the app it debugs.</summary>
    public void Bind(ProjectSession next)
    {
        UiThread.Check();
        if (session is { } previous)
        {
            previous.Debug.StateChanged -= OnDebugStateChanged;
            previous.Debug.SelectionChanged -= OnSelectedAppChanged;
            previous.LiveAppChanged -= OnSelectedAppChanged;
        }
        session = next;
        next.Debug.StateChanged += OnDebugStateChanged;
        next.Debug.SelectionChanged += OnSelectedAppChanged;
        next.LiveAppChanged += OnSelectedAppChanged;
        FollowApp(next.LiveApp);
        Invalidate();
    }

    private void OnDebugStateChanged(object? sender, EventArgs e) => Invalidate();

    private void OnSelectedAppChanged(object? sender, EventArgs e)
    {
        UiThread.Check();
        FollowApp(session?.LiveApp);
        Invalidate();
    }

    private void FollowApp(LiveApp? next)
    {
        if (app == next)
        {
            return;
        }
        if (app is not null)
        {
            app.StateChanged -= OnAppChanged;
            app.TreeChanged -= OnAppChanged;
            app.ValuesChanged -= OnAppChanged;
        }
        app = next;
        if (next is not null)
        {
            next.StateChanged += OnAppChanged;
            next.TreeChanged += OnAppChanged;
            next.ValuesChanged += OnAppChanged;
        }
        Invalidate();
    }

    private void OnAppChanged(object? sender, EventArgs e)
    {
        if (sender == app)
        {
            Invalidate();
        }
    }

    private void Invalidate()
    {
        generation++;
        soon.Restart();
    }

    /// <summary>An .axaml file's values go with its lines as it is edited (<see cref="OpenDocument.LiveValues"/>), then are
    /// shown again from its text once typing pauses.</summary>
    private void Follow(OpenDocument document)
    {
        if (!XamlBreakpoints.Compiled(document.Path))
        {
            return;
        }
        document.Text.Changed += (_, _) => soon.Restart();
    }

    private Mode? ModeNow() => session?.Debug is { Session: { } debugged } debug && app is { State: LiveAppState.Connected or LiveAppState.Waiting } live
        ? debugged.State == DebugState.Paused && debug.Stop is not null ? Mode.Paused
            : debugged.State == DebugState.Running && tree.Chosen is not null && live.State == LiveAppState.Connected ? Mode.Running
            : null
        : null;

    /// <summary>Reads what the files shown want and has not been read, one refresh at a time, the last word winning.</summary>
    private async Task RefreshAsync()
    {
        UiThread.Check();
        if (refreshing)
        {
            again = true;
            return;
        }
        refreshing = true;
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            do
            {
                again = false;
                await RefreshOnceAsync();
            }
            while (again);
        }
        finally
        {
            refreshing = false;
        }
    }

    /// <summary>The stop the values shown were read at, while paused.</summary>
    private DebugStop? readAt;

    /// <summary>Reads again when what is shown was read before the stop now (read a moment after the program paused, which
    /// raises nothing of its own).</summary>
    public void CatchUp()
    {
        UiThread.Check();
        if (ModeNow() == Mode.Paused && readAt != session?.Debug.Stop)
        {
            Invalidate();
        }
    }

    private async Task RefreshOnceAsync()
    {
        int now = generation;
        Mode? mode = ModeNow();
        readAt = mode == Mode.Paused ? session?.Debug.Stop : null;
        if (mode is null)
        {
            foreach (OpenDocument document in readings.Keys)
            {
                document.LiveValues = [];
            }
            readings.Clear();
            return;
        }
        foreach ((OpenDocument document, (int first, int last)) in Shown())
        {
            if (!readings.TryGetValue(document, out Reading? read) || read.Generation != now)
            {
                readings[document] = read = new Reading(now);
            }
            IReadOnlyList<XamlBinding> bindings = XamlBindings.In(document.Text.Snapshot);
            // A screen above and below too, so a scroll seldom asks again.
            int margin = Math.Max(20, last - first);
            List<XamlBinding> wanted = [.. bindings.Where(b => b.EndLine >= first - margin && b.Line <= last + margin)];
            Dictionary<(int, int), XamlSpot> spots = [];
            foreach (XamlBinding binding in wanted)
            {
                if (Spot(document, binding) is not { } spot || read.Asked.Contains(spot))
                {
                    continue;
                }
                spots[spot] = spots.TryGetValue(spot, out XamlSpot? known)
                    ? known with { Properties = [.. known.Properties, binding.Property] }
                    : new XamlSpot(spot.Line, spot.Column, binding.Class, [binding.Property]);
            }
            if (spots.Count > 0)
            {
                XamlRead asked = new(document.Path, [.. spots.Values], tree.Chosen) { More = read.Asked.Count > 0 ? true : null };
                (XamlValues values, string? why) = await ReadAsync(mode.Value, asked);
                if (now != generation || ModeNow() != mode)
                {
                    // What was read is out of date already: the refresh the change asked for reads again.
                    again = true;
                    return;
                }
                read.Asked.UnionWith(spots.Keys);
                read.Problem = values.Problem is { } problem ? (problem, why ?? problem) : null;
                foreach (ElementValues element in values.Elements)
                {
                    read.Elements[(element.Line, element.Column)] = element;
                }
            }
            document.LiveValues = Shown(document, read, bindings, wanted);
        }
    }

    /// <summary>The .axaml files the editors show, each with the lines on screen (of every editor showing it).</summary>
    private Dictionary<OpenDocument, (int First, int Last)> Shown()
    {
        Dictionary<OpenDocument, (int First, int Last)> shown = [];
        foreach (EditorGroup group in editors.Groups)
        {
            if (group.Active is not { } document || !XamlBreakpoints.Compiled(document.Path))
            {
                continue;
            }
            (int first, int last) = group.Editor.LinesShown;
            shown[document] = shown.TryGetValue(document, out (int First, int Last) known) ? (Math.Min(known.First, first), Math.Max(known.Last, last)) : (first, last);
        }
        return shown;
    }

    /// <summary>Where a binding's element was in the build the app runs, as Avalonia records it (from one, an XML reader's
    /// lines); none for an element on a line written since.</summary>
    private (int Line, int Column)? Spot(OpenDocument document, XamlBinding binding)
    {
        DebugController? debug = session?.Debug;
        if (debug is not null && !debug.IsBuiltLine(document.Path, binding.Line))
        {
            return null;
        }
        TextPosition built = debug?.BuiltPlace(document.Path, binding.Line, binding.Column) ?? new TextPosition(binding.Line, binding.Column);
        return (built.Line + 1, built.Column + 1);
    }

    /// <summary>What the app read, and, when it read nothing, why in full (its <see cref="XamlValues.Problem"/> is the short of it).</summary>
    private async Task<(XamlValues Values, string? Why)> ReadAsync(Mode mode, XamlRead read)
    {
        if (mode == Mode.Paused)
        {
            return await EvaluateAsync(read);
        }
        try
        {
            return (await app!.ValuesAsync(read), null);
        }
        catch (LiveAppException error)
        {
            return (new XamlValues([], error.Message), error.Message);
        }
    }

    /// <summary>The read evaluated by the debugger on the app's UI thread: on the thread it stopped on, else on its main thread,
    /// which is the UI thread of a desktop app; each value then as Variables shows it, from the array the read answers. The
    /// runtime evaluates nothing on a thread waiting outside .NET (the UI thread idle at a stop on another thread, or paused).</summary>
    private async Task<(XamlValues Values, string? Why)> EvaluateAsync(XamlRead read)
    {
        if (session?.Debug is not { Session: { State: DebugState.Paused } debugged, Stop: { } stop })
        {
            return (new XamlValues([], null), null);
        }
        string expression = $"{Wire.ReadValues}({SymbolDisplay.FormatLiteral(JsonSerializer.Serialize(read, WireJson.Default.XamlRead), quote: true)})";
        string? why = null;
        bool elsewhere = false;
        try
        {
            IReadOnlyList<DapThread> threads = await debugged.ThreadsAsync();
            // Main's thread is known by the debugger's mark (an app may name it anything), else the first listed.
            List<int> tried = [stop.Thread];
            tried.AddRange((threads.Any(t => t.IsMain == true) ? threads.Where(t => t.IsMain == true) : threads.Take(1)).Select(t => t.Id));
            foreach (int thread in tried.Distinct())
            {
                if ((await debugged.StackTraceAsync(thread)).FirstOrDefault() is not { } top)
                {
                    continue;
                }
                EvaluateResponse answer;
                try
                {
                    answer = await debugged.EvaluateAsync(expression, top.Id, "watch");
                }
                catch (DapException error)
                {
                    why = error.Reason;
                    continue;
                }
                if (answer.VariablesReference == 0)
                {
                    why = answer.Result;
                    continue;
                }
                IReadOnlyList<DapVariable> items = await debugged.VariablesAsync(answer.VariablesReference);
                XamlValues? values = items.Count > 0 && DebugController.StringValue(items[0].Value) is { } json ? JsonSerializer.Deserialize(json, WireJson.Default.XamlValues) : null;
                if (values is null)
                {
                    why = $"the app answered {answer.Result}";
                    continue;
                }
                if (values.WrongThread == true)
                {
                    elsewhere |= thread == stop.Thread;
                    why = values.Problem;
                    continue;
                }
                return (AsVariables(values, items), null);
            }
        }
        catch (DapException error)
        {
            why = error.Reason;
        }
        catch (JsonException error)
        {
            why = $"the app's answer could not be read: {error.Message}";
        }
        why ??= "no thread of the app's could read them";
        return elsewhere
            ? (new XamlValues([], "shown at a stop on the app's UI thread, not this one"),
                $"This stop is on another thread than the app's UI thread, which waits where the debugger can evaluate nothing ({why}). "
                + "Values show at a stop in code the UI thread runs, or as the app runs with a control chosen in the Live Visual Tree.")
            : (new XamlValues([], "nothing could be evaluated where the app stopped"), why);
    }

    /// <summary>The values read, each as the debugger shows its slot of the read's array (a string quoted, an object by its
    /// DebuggerDisplay or its type), with the type it gives.</summary>
    private static XamlValues AsVariables(XamlValues values, IReadOnlyList<DapVariable> items)
    {
        Dictionary<int, DapVariable> slots = [];
        foreach (DapVariable item in items)
        {
            if (item.Name.Length > 2 && item.Name[0] == '[' && int.TryParse(item.Name.AsSpan(1, item.Name.Length - 2), out int slot) && slot > 0)
            {
                slots[slot] = item;
            }
        }
        // A null's type is the array's element type to the debugger: the app's says what it is declared as.
        ShownValue? Shown(ShownValue? value) => value is { Slot: > 0 } && slots.TryGetValue(value.Slot, out DapVariable? item)
            ? value with { Text = item.Value, Type = item.Type is { Length: > 0 } type && item.Value != "null" ? type : value.Type }
            : value;
        return values with
        {
            Elements = [.. values.Elements.Select(element => element with
            {
                Bindings = [.. element.Bindings.Select(bound => bound with { Value = Shown(bound.Value), Steps = [.. bound.Steps.Select(step => step with { Value = Shown(step.Value) })] })],
            })],
        };
    }

    /// <summary>What a file shows after its lines: each binding's value, or that it failed, or of an element made more than
    /// once with none chosen how many there are; and why none could be read, after the first binding wanted.</summary>
    private IReadOnlyList<LineAnnotation> Shown(OpenDocument document, Reading read, IReadOnlyList<XamlBinding> bindings, IReadOnlyList<XamlBinding> wanted)
    {
        List<LineAnnotation> shown = [];
        if (read.Problem is { } problem && wanted.FirstOrDefault() is { } first)
        {
            shown.Add(new LineAnnotation(first.EndLine, $"binding values: {problem.Short}", LineAnnotationKind.InlineValue));
        }
        HashSet<(int, int)> counted = [];
        foreach (XamlBinding binding in bindings)
        {
            if (Spot(document, binding) is not { } spot || !read.Elements.TryGetValue(spot, out ElementValues? element))
            {
                continue;
            }
            string label = Label(binding.Property);
            if (element.Bindings.Count == 0)
            {
                if (element.Instances > 1 && counted.Add(spot))
                {
                    shown.Add(new LineAnnotation(binding.EndLine, $"{label}: made {element.Instances} times, choose one in the Live Visual Tree", LineAnnotationKind.InlineValue));
                }
                continue;
            }
            if (element.Bindings.FirstOrDefault(b => b.Property == binding.Property) is not { } bound)
            {
                continue;
            }
            if (bound.Failure is { } failure)
            {
                string at = bound.FailedAt is { } step ? $" at {step}" : string.Empty;
                shown.Add(new LineAnnotation(binding.EndLine, $"{label}: binding failed{at}: {Cut(failure)}", LineAnnotationKind.Failed));
            }
            else if (bound.Value is { } value)
            {
                string since = bound.Unbound == true ? " (set since: no binding now)" : string.Empty;
                shown.Add(new LineAnnotation(binding.EndLine, $"{label}: {Cut(value.Text)}{since}", LineAnnotationKind.InlineValue));
            }
        }
        return shown;
    }

    /// <summary>The card for a binding the pointer rests on: its property's value on the control read and its type, then its
    /// path walked from its source, each step's value and type; for one that failed, why, at which step; null off a binding,
    /// or when nothing was read for it.</summary>
    public DebuggerHover? HoverAt(OpenDocument document, int offset)
    {
        UiThread.Check();
        if (!readings.TryGetValue(document, out Reading? read) || read.Generation != generation
            || XamlBindings.In(document.Text.Snapshot).FirstOrDefault(b => offset >= b.Range.Start && offset <= b.Range.End) is not { } binding)
        {
            return null;
        }
        if (Spot(document, binding) is not { } spot || !read.Elements.TryGetValue(spot, out ElementValues? element))
        {
            return read.Problem is { } problem ? new DebuggerHover(string.Empty, $"Binding values: {problem.Full}") : null;
        }
        string label = Label(binding.Property);
        if (element.Bindings.FirstOrDefault(b => b.Property == binding.Property) is not { } bound)
        {
            return element.Instances > 1
                ? new DebuggerHover(string.Empty, $"This element is made {element.Instances} times: choose one of them in the Live Visual Tree to see its values")
                : null;
        }
        List<string> lines = [$"{element.Control}.{label} = {Typed(bound.Value)}"];
        for (int i = 0; i < bound.Steps.Count; i++)
        {
            PathStep step = bound.Steps[i];
            string value = step.Value is { } known ? $" = {Typed(known)}"
                : step.Name == bound.FailedAt ? ": failed here"
                : ": not reached";
            lines.Add($"{(i == 0 ? string.Empty : "→ ")}{step.Name}{value}");
        }
        string? why = bound.Failure is { } failure
            ? $"Binding failed{(bound.FailedAt is { } at ? $" at {at}" : string.Empty)}: {failure}"
            : bound.Unbound == true ? "No binding gives it its value now: something set it since" : null;
        return new DebuggerHover(string.Join('\n', lines), why);
    }

    /// <summary>A value with its type before it: <c>{string} "Ada"</c>.</summary>
    private static string Typed(ShownValue? value) => value is null
        ? "(not read)"
        : value.Type.Length > 0 && value.Text != value.Type && value.Text != $"{{{value.Type}}}" ? $"{{{value.Type}}} {value.Text}" : value.Text;

    /// <summary>A property as its attribute names it, an XML prefix left off: Text, Grid.Row.</summary>
    private static string Label(string property) => property[(property.LastIndexOf(':') + 1)..];

    private static string Cut(string value) => value.Length <= Longest ? value : value[..(Longest - 1)] + "…";
}
