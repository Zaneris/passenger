using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.Core;

namespace Passenger.Inspector;

/// <summary>What the debugger evaluates for a XAML file's binding values while the app is paused (<see cref="Wire.ReadValues"/>),
/// on the app's UI thread, whose dispatcher answers nothing then. An evaluation finds the startup hook's copy of this assembly
/// first, which hands the call to the copy in the inspector's own context, whose state it reads; nothing here names Avalonia, so
/// the startup hook's copy never loads Avalonia into the default context at a version the app does not have.</summary>
public static class BindingValues
{
    /// <summary>The inspector's own copy's <see cref="Read"/>, set in the startup hook's copy.</summary>
    internal static Func<string, object?[]>? Live { get; set; }

    /// <summary>The read (<see cref="XamlRead"/> as JSON) answered as an array: the <see cref="XamlValues"/> as JSON
    /// first, then each value shown at its slot.</summary>
    public static object?[] Read(string read) => Live is { } live ? live(read) : BindingReader.Serve(read);
}

/// <summary>A XAML file's bindings read with their values, on the app's UI thread: each element asked of found among the
/// objects of the app's windows by the place Avalonia's XAML compiler recorded (a root by its x:Class), the instance read the
/// one made, or of several the one nearest the element chosen in the Live Visual Tree, and each binding's path walked from its
/// source through Avalonia's own binding expression (its nodes, read by reflection, as they are internal).</summary>
internal static class BindingReader
{
    /// <summary>The most items an evaluation's array holds, past which the debugger lists them in ranges; values past it are shown
    /// as the app writes them.</summary>
    private const int MostSlots = 100;

    /// <summary>Elements of a tree deeper than this are not looked at: a parent chain this long is a cycle.</summary>
    private const int Deepest = 4096;

    private static readonly BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary><see cref="BindingValues.Read"/> in the inspector's own copy.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static object?[] Serve(string read)
    {
        List<object?> slots = [null];
        XamlValues values;
        try
        {
            XamlRead? asked = JsonSerializer.Deserialize(read, WireJson.Default.XamlRead);
            values = asked is null ? new XamlValues([], "Nothing was asked")
                : !OnUiThread() ? new XamlValues([], "This is not the app's UI thread") { WrongThread = true }
                : Read(Agent.Current, asked, slots, null);
        }
        catch (Exception error)
        {
            slots.RemoveRange(1, slots.Count - 1);
            InspectorTrace.Step($"the bindings could not be read: {error}");
            values = new XamlValues([], $"The inspector could not read the bindings: {error.GetType().Name}: {error.Message}");
        }
        slots[0] = JsonSerializer.Serialize(values, WireJson.Default.XamlValues);
        return [.. slots];
    }

    /// <summary>Whether this is the app's UI thread, its dispatcher looked up without making one.</summary>
    private static bool OnUiThread()
    {
        Dispatcher? ui = Agent.Ui ?? typeof(Dispatcher).GetField("s_uiThread", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Dispatcher;
        return ui?.CheckAccess() == true;
    }

    /// <summary>The bindings asked of, each value shown added to <paramref name="slots"/> when the debugger reads them, and each
    /// property read added to <paramref name="watched"/> when the app is to say when it changes.</summary>
    public static XamlValues Read(Inspector? inspector, XamlRead read, List<object?>? slots, List<(AvaloniaObject, AvaloniaProperty)>? watched)
    {
        Visual? chosen = inspector?.Find(read.Chosen);
        HashSet<string> classes = [.. read.Elements.Select(e => e.Class).OfType<string>()];
        string name = Path.GetFileName(read.File);
        Dictionary<string, Dictionary<(int, int), List<AvaloniaObject>>> byFile = new(StringComparer.Ordinal);
        Dictionary<string, List<AvaloniaObject>> byClass = new(StringComparer.Ordinal);
        foreach (AvaloniaObject made in Everything())
        {
            if (classes.Count > 0 && made.GetType().FullName is { } type && classes.Contains(type))
            {
                (byClass.TryGetValue(type, out List<AvaloniaObject>? roots) ? roots : byClass[type] = []).Add(made);
            }
            if (Details.Place(made) is { } place && string.Equals(Path.GetFileName(place.File), name, StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<(int, int), List<AvaloniaObject>> places = byFile.TryGetValue(place.File, out Dictionary<(int, int), List<AvaloniaObject>>? known) ? known : byFile[place.File] = [];
                (places.TryGetValue((place.Line, place.Column), out List<AvaloniaObject>? there) ? there : places[(place.Line, place.Column)] = []).Add(made);
            }
        }
        // The build's path may name the file through another link than the IDE's (/var, /private/var): the one ending most alike, then most alike in case.
        Dictionary<(int, int), List<AvaloniaObject>> inFile = byFile.Count == 0 ? []
            : byFile.MaxBy(p => (SharedTail(p.Key, read.File, StringComparison.OrdinalIgnoreCase), SharedTail(p.Key, read.File, StringComparison.Ordinal))).Value;
        List<ElementValues> elements = [];
        foreach (XamlSpot spot in read.Elements)
        {
            // Where the element's name starts, or its '<' one before it.
            List<AvaloniaObject> instances = inFile.TryGetValue((spot.Line, spot.Column), out List<AvaloniaObject>? at)
                || inFile.TryGetValue((spot.Line, spot.Column - 1), out at) ? [.. at] : [];
            if (spot.Class is { } root && byClass.TryGetValue(root, out List<AvaloniaObject>? roots))
            {
                instances.AddRange(roots.Where(r => !instances.Contains(r)));
            }
            if (instances.Count == 0)
            {
                continue;
            }
            AvaloniaObject? instance = instances.Count == 1 ? instances[0] : Nearest(instances, chosen);
            if (instance is null)
            {
                elements.Add(new ElementValues(spot.Line, spot.Column, null, null, instances.Count, []));
                continue;
            }
            List<BoundValue> bindings = [];
            foreach (string property in spot.Properties)
            {
                bindings.Add(Bound(instance, property, slots, watched));
            }
            elements.Add(new ElementValues(spot.Line, spot.Column, Called(instance), instance is Visual visual && inspector is not null ? inspector.IdOf(visual) : null,
                instances.Count, bindings));
        }
        return new XamlValues(elements, null);
    }

    /// <summary>Every object of the app's windows and popups: their visual trees, which hold what templates made, and their
    /// logical trees, which hold what is not shown (a tab not chosen).</summary>
    private static List<AvaloniaObject> Everything()
    {
        List<AvaloniaObject> all = [];
        HashSet<AvaloniaObject> seen = new(ReferenceEqualityComparer.Instance);
        Stack<AvaloniaObject> left = new();
        foreach (TopLevel top in Inspector.AllTopLevels())
        {
            left.Push(top);
        }
        while (left.TryPop(out AvaloniaObject? at))
        {
            if (!seen.Add(at))
            {
                continue;
            }
            all.Add(at);
            if (at is Visual visual)
            {
                foreach (Visual child in visual.GetVisualChildren())
                {
                    left.Push(child);
                }
            }
            if (at is ILogical logical)
            {
                foreach (ILogical child in logical.LogicalChildren)
                {
                    if (child is AvaloniaObject made)
                    {
                        left.Push(made);
                    }
                }
            }
        }
        return all;
    }

    /// <summary>How many of two paths' last parts are the same, compared so, the file's name first.</summary>
    private static int SharedTail(string a, string b, StringComparison comparison)
    {
        string[] first = a.Split('/', '\\');
        string[] second = b.Split('/', '\\');
        int shared = 0;
        while (shared < first.Length && shared < second.Length
            && string.Equals(first[^(shared + 1)], second[^(shared + 1)], comparison))
        {
            shared++;
        }
        return shared;
    }

    /// <summary>Of an element's instances, the one nearest the element chosen: the one whose nearest ancestor shared
    /// with it is nearest it (itself, an ancestor of it, a part of the same template's instance); none when two are as
    /// near, or none is chosen.</summary>
    private static AvaloniaObject? Nearest(List<AvaloniaObject> instances, Visual? chosen)
    {
        if (chosen is null)
        {
            return null;
        }
        Dictionary<AvaloniaObject, int> up = new(ReferenceEqualityComparer.Instance);
        int depth = 0;
        for (AvaloniaObject? at = chosen; at is not null && depth < Deepest; at = Parent(at))
        {
            up.TryAdd(at, depth++);
        }
        AvaloniaObject? best = null;
        int nearest = int.MaxValue;
        bool tied = false;
        foreach (AvaloniaObject instance in instances)
        {
            int apart = int.MaxValue;
            int steps = 0;
            for (AvaloniaObject? at = instance; at is not null && steps < Deepest; at = Parent(at), steps++)
            {
                if (up.TryGetValue(at, out int shared))
                {
                    apart = shared;
                    break;
                }
            }
            if (apart < nearest)
            {
                (best, nearest, tied) = (instance, apart, false);
            }
            else if (apart == nearest)
            {
                tied = true;
            }
        }
        return tied || nearest == int.MaxValue ? null : best;
    }

    private static AvaloniaObject? Parent(AvaloniaObject of) =>
        (of as Visual)?.GetVisualParent() as AvaloniaObject ?? (of as ILogical)?.LogicalParent as AvaloniaObject;

    private static string Called(AvaloniaObject made) => made is Visual visual ? Details.Describe(visual) : Words.TypeName(made.GetType());

    /// <summary>A property of an element by the name its attribute has: its own, or an attached one as Owner.Name (the owner's
    /// XML prefix left off), or its own written in full (TextBlock.Text).</summary>
    private static AvaloniaProperty? PropertyNamed(AvaloniaObject made, string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0)
        {
            return AvaloniaPropertyRegistry.Instance.FindRegistered(made, name);
        }
        string owner = name[..dot];
        owner = owner[(owner.LastIndexOf(':') + 1)..];
        string property = name[(dot + 1)..];
        return AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(made.GetType()).FirstOrDefault(p => p.Name == property && Words.TypeName(p.OwnerType) == owner)
            ?? (Words.TypeName(made.GetType()) == owner || made.GetType().Name == owner ? AvaloniaPropertyRegistry.Instance.FindRegistered(made, property) : null);
    }

    /// <summary>A binding on a property of an instance: the property's value now and the binding's path walked step by step; a
    /// property with no binding now (code set a value since) has its value and no path.</summary>
    private static BoundValue Bound(AvaloniaObject instance, string name, List<object?>? slots, List<(AvaloniaObject, AvaloniaProperty)>? watched)
    {
        if (PropertyNamed(instance, name) is not { } property)
        {
            return new BoundValue(name, string.Empty, null, [], $"{Words.TypeName(instance.GetType())} has no property {name}", null);
        }
        watched?.Add((instance, property));
        UntypedBindingExpressionBase? expression;
        try
        {
            expression = BindingOperations.GetBindingExpressionBase(instance, property) as UntypedBindingExpressionBase;
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException)
        {
            expression = null;
        }
        ShownValue value = Shown(instance.GetValue(property), slots, property.PropertyType);
        if (expression is null)
        {
            return new BoundValue(name, string.Empty, value, [], null, null) { Unbound = true };
        }
        bool failed = expression.ErrorType == BindingErrorType.Error;
        List<PathStep> steps = Steps(expression, instance, slots, failed, out string? failedAt);
        return new BoundValue(name, expression.Description, value, steps, failed ? ErrorOf(expression) ?? "The binding failed" : null, failedAt);
    }

    /// <summary>A binding's path from its source, each step with its value: a template binding's templated parent and its
    /// property, else each node of Avalonia's binding expression (a DataContext, a named element, a member read), a step the
    /// walk never reached with none; of a failed binding, the first step with no value is where it failed.</summary>
    private static List<PathStep> Steps(UntypedBindingExpressionBase expression, AvaloniaObject instance, List<object?>? slots, bool failed, out string? failedAt)
    {
        failedAt = null;
        List<PathStep> steps = [];
        if (expression.GetType().Name == "TemplateBindingExpression")
        {
            AvaloniaObject? parent = (instance as StyledElement)?.TemplatedParent as AvaloniaObject;
            steps.Add(new PathStep("TemplatedParent", Shown(parent, slots)));
            if (Field(expression, "_property") is AvaloniaProperty source)
            {
                steps.Add(new PathStep(source.Name, parent is null ? null : Shown(parent.GetValue(source), slots)));
            }
            return steps;
        }
        if (Field(expression, "_nodes") is not IList nodes)
        {
            return steps;
        }
        if (nodes.Count == 0 || !IsSource(nodes[0]))
        {
            // A binding given its source (Source, a resource) starts from it rather than from a node of its own.
            object? source = Field(expression, "_source") is WeakReference<object> held && held.TryGetTarget(out object? target) ? target : null;
            steps.Add(new PathStep("Source", Shown(source, slots)));
        }
        bool reached = true;
        foreach (object? node in nodes)
        {
            if (node is null)
            {
                continue;
            }
            string step = StepName(node);
            object? value = Property(node, "Value");
            bool has = reached && value != AvaloniaProperty.UnsetValue;
            if (!has && reached && failed)
            {
                failedAt = step;
            }
            reached = has;
            steps.Add(new PathStep(step, has ? Shown(value, slots) : null));
        }
        return steps;
    }

    private static bool IsSource(object? node)
    {
        for (Type? type = node?.GetType(); type is not null; type = type.BaseType)
        {
            if (type.Name == "SourceNode")
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A node of a binding's path as the path writes it: a member's name, an Avalonia property's, a source's
    /// (DataContext, #Name, $parent[Type]), else Avalonia's own text for it.</summary>
    private static string StepName(object node)
    {
        if (Property(node, "PropertyName") is string member)
        {
            return member;
        }
        if (Property(node, "Property") is AvaloniaProperty property)
        {
            return property.IsAttached ? $"({Words.TypeName(property.OwnerType)}.{property.Name})" : property.Name;
        }
        switch (node.GetType().Name)
        {
            case "DataContextNode":
                return "DataContext";
            case "ParentDataContextNode":
                return "$parent.DataContext";
            case "TemplatedParentNode":
                return "$templatedParent";
        }
        StringBuilder written = new();
        try
        {
            node.GetType().GetMethod("BuildString", Instance, [typeof(StringBuilder)])?.Invoke(node, [written]);
        }
        catch (TargetInvocationException)
        {
            // Named by its kind below.
        }
        string type = node.GetType().Name;
        return written.Length > 0 ? written.ToString().TrimStart('.') : type.EndsWith("Node", StringComparison.Ordinal) ? type[..^4] : type;
    }

    /// <summary>Why a binding failed, as Avalonia keeps it: a broken path's own words, without the path and the place it failed,
    /// which its message repeats and the steps say.</summary>
    private static string? ErrorOf(UntypedBindingExpressionBase expression) =>
        Field(expression, "_error") is { } error && Property(error, "Exception") is Exception exception
            ? exception is BindingChainException && Field(exception, "_message") is string { Length: > 0 } own ? own : exception.Message
            : null;

    private static object? Field(object of, string name)
    {
        for (Type? type = of.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetField(name, Instance | BindingFlags.DeclaredOnly) is { } field)
            {
                return field.GetValue(of);
            }
        }
        return null;
    }

    private static object? Property(object of, string name)
    {
        try
        {
            return of.GetType().GetProperty(name, Instance) is { GetMethod: not null } property && property.GetIndexParameters().Length == 0 ? property.GetValue(of) : null;
        }
        catch (Exception error) when (error is TargetInvocationException or AmbiguousMatchException)
        {
            return null;
        }
    }

    /// <summary>A value shown: written as the app has it, of its own type (a null of the type it is declared as), and, read by
    /// the debugger, kept at the next slot of its array.</summary>
    private static ShownValue Shown(object? value, List<object?>? slots, Type? declared = null)
    {
        int slot = 0;
        if (slots is not null && slots.Count < MostSlots && value != AvaloniaProperty.UnsetValue)
        {
            slot = slots.Count;
            slots.Add(value);
        }
        return new ShownValue(Format(value), value is not null ? TypeName(value.GetType()) : declared is not null ? TypeName(declared) : "object", slot);
    }

    /// <summary>A value as C# shows one: a string or a character quoted, a number as C# writes it, an object that says
    /// nothing of itself by its type in braces.</summary>
    public static string Format(object? value)
    {
        string text = value switch
        {
            null => "null",
            string s => Quoted(s, '"'),
            char c => Quoted(c.ToString(), '\''),
            bool b => b ? "true" : "false",
            Enum e => e.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ when value == AvaloniaProperty.UnsetValue => "(unset)",
            _ => value.ToString() is { } said && said != value.GetType().ToString() ? said : $"{{{TypeName(value.GetType())}}}",
        };
        return text.Length > 300 ? text[..297] + "..." : text;
    }

    private static string Quoted(string text, char quote)
    {
        StringBuilder quoted = new();
        quoted.Append(quote);
        foreach (char c in text)
        {
            quoted.Append(c switch
            {
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                '\\' => "\\\\",
                _ when c == quote => $"\\{quote}",
                _ => c.ToString(),
            });
        }
        return quoted.Append(quote).ToString();
    }

    /// <summary>A type as C# writes it: a keyword for the built-in ones, else its namespace, name and type arguments.</summary>
    public static string TypeName(Type type)
    {
        if (PrimitiveTypes.Of(type)?.Keyword is { } keyword)
        {
            return keyword;
        }
        if (type.IsArray)
        {
            return $"{TypeName(type.GetElementType()!)}[]";
        }
        string name = type.IsNested ? $"{TypeName(type.DeclaringType!)}.{type.Name}" : $"{(type.Namespace is { Length: > 0 } ns ? ns + "." : string.Empty)}{type.Name}";
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick < 0 ? name : $"{name[..tick]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
    }
}

/// <summary>The properties each XAML file's values read over the socket showed, watched while the app runs: a change to any of
/// them tells Passenger once, a quarter second after the first, that what it shows is out of date. Its controls are held
/// weakly, so a window of the app's that closes goes with its controls whatever was shown of them. A UI-thread object.</summary>
internal sealed class ValueWatch(Connection connection)
{
    private static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(250);

    private readonly Dictionary<string, List<(WeakReference<AvaloniaObject> Instance, AvaloniaProperty Property)>> byFile = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<AvaloniaObject, HashSet<AvaloniaProperty>> watched = new();
    private bool telling;

    /// <summary>Watches these of a file's from now, with those it watched of it before (<paramref name="more"/>) or in their place.</summary>
    public void Watch(string file, IEnumerable<(AvaloniaObject Instance, AvaloniaProperty Property)> properties, bool more)
    {
        if (!more || !byFile.TryGetValue(file, out List<(WeakReference<AvaloniaObject>, AvaloniaProperty)>? kept))
        {
            byFile[file] = kept = [];
        }
        kept.AddRange(properties.Select(p => (new WeakReference<AvaloniaObject>(p.Instance), p.Property)));
        Unhook();
        foreach (List<(WeakReference<AvaloniaObject> Instance, AvaloniaProperty Property)> those in byFile.Values)
        {
            // Those of controls collected since are let go of.
            those.RemoveAll(p => !p.Instance.TryGetTarget(out _));
            foreach ((WeakReference<AvaloniaObject> held, AvaloniaProperty property) in those)
            {
                if (held.TryGetTarget(out AvaloniaObject? instance))
                {
                    if (!watched.TryGetValue(instance, out HashSet<AvaloniaProperty>? read))
                    {
                        read = [];
                        watched.Add(instance, read);
                        instance.PropertyChanged += OnChanged;
                    }
                    read.Add(property);
                }
            }
        }
    }

    public void Stop()
    {
        Unhook();
        byFile.Clear();
    }

    private void Unhook()
    {
        foreach (KeyValuePair<AvaloniaObject, HashSet<AvaloniaProperty>> instance in watched)
        {
            instance.Key.PropertyChanged -= OnChanged;
        }
        watched.Clear();
    }

    private void OnChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (telling || sender is not AvaloniaObject instance || !watched.TryGetValue(instance, out HashSet<AvaloniaProperty>? those) || !those.Contains(change.Property))
        {
            return;
        }
        telling = true;
        DispatcherTimer.RunOnce(() =>
        {
            telling = false;
            connection.Send(new Reply { Event = Wire.ValuesChanged });
        }, Gather);
    }
}
