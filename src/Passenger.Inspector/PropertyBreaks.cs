using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Core;
using Avalonia.Diagnostics;
using Passenger.Dap;

namespace Passenger.Inspector;

/// <summary>Property breakpoints, the Live Visual Tree's Break When Value Changes: an Avalonia property of one element
/// watched for every change Avalonia makes to it, whatever made it (a style, a binding, code, an animation, its template).
/// Each change the breakpoint's hit count lets through calls <see cref="Breaks.Changed"/>, where Passenger's debugger has
/// a function breakpoint per property breakpoint with its condition: the program stops inside the call that set the value,
/// the code that set it below in the stack. Non-user code, so Just My Code shows that code as where it stopped.</summary>
[DebuggerNonUserCode]
internal sealed class PropertyBreaks
{
    private readonly Dictionary<int, Watched> watches = [];

    /// <summary>Watches a property of an element for a breakpoint (its hit count Passenger's own words, "==2", ">3", "%2"),
    /// in place of what the breakpoint watched before.</summary>
    public void Watch(int breakpoint, AvaloniaObject element, string name, string? hitCondition)
    {
        AvaloniaProperty property = Find(element, name) ?? throw new Refusal($"{Words.TypeName(element.GetType())} has no property {name}");
        HitCondition? hits = null;
        if (hitCondition is { Length: > 0 })
        {
            hits = HitCondition.TryParse(hitCondition, out HitCondition read, out string? problem) ? read : throw new Refusal(problem);
        }
        Unwatch(breakpoint);
        Watched watch = new(breakpoint, element, property, hits);
        watches[breakpoint] = watch;
        element.PropertyChanged += watch.Changed;
    }

    public void Unwatch(int breakpoint)
    {
        if (watches.Remove(breakpoint, out Watched? watch))
        {
            watch.Element.PropertyChanged -= watch.Changed;
        }
    }

    /// <summary>Passenger has gone: nothing is watched.</summary>
    public void Stop()
    {
        foreach (int breakpoint in watches.Keys.ToList())
        {
            Unwatch(breakpoint);
        }
    }

    /// <summary>An element's Avalonia property by the name the Live Visual Tree lists it under: its own (or one it took
    /// from another type), else an attached one as Owner.Name.</summary>
    private static AvaloniaProperty? Find(AvaloniaObject element, string name)
    {
        int dot = name.LastIndexOf('.');
        return dot < 0
            ? AvaloniaPropertyRegistry.Instance.FindRegistered(element, name)
            : AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(element.GetType())
                .FirstOrDefault(p => p.Name == name[(dot + 1)..] && Words.TypeName(p.OwnerType) == name[..dot]);
    }

    [DebuggerNonUserCode]
    private sealed class Watched(int breakpoint, AvaloniaObject element, AvaloniaProperty property, HitCondition? hits)
    {
        private int count;

        public AvaloniaObject Element { get; } = element;

        public void Changed(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property != property || (hits is { } condition && !condition.IsMet(++count)))
            {
                return;
            }
            (string by, SourcePlace? at) = By(e);
            string change = $"Previous Value: {Shown.Format(Element, property.Name, e.OldValue)}, New Value: {Shown.Format(Element, property.Name, e.NewValue)}{by}";
            Breaks.Changed(breakpoint, Element, property, e.OldValue, e.NewValue, change, at is null ? null : JsonSerializer.Serialize(at, WireJson.Default.SourcePlace));
        }

        /// <summary>What set the value: the control itself (SetCurrentValue, kept until anything else sets it), else from
        /// the priority Avalonia set it at, a binding (as its XAML writes it), a style (its selector, and apart where it is
        /// written), an animation, the template, inheritance, or the element's local value.</summary>
        private (string By, SourcePlace? At) By(AvaloniaPropertyChangedEventArgs e)
        {
            try
            {
                if (!property.IsDirect && Element.GetDiagnostic(property).IsOverriddenCurrentValue)
                {
                    return (Words.ChangedBy(ValueSources.Current), null);
                }
                switch (e.Priority)
                {
                    case BindingPriority.Style or BindingPriority.StyleTrigger when Element is Visual visual && new Styling(visual).Winner(property) is { } won:
                        return ($", by {(won.Source == ValueSources.Theme ? "theme" : "style")} {won.Detail}", won.Place);
                    case BindingPriority.LocalValue when BindingOperations.GetBindingExpressionBase(Element, property) is UntypedBindingExpressionBase { Priority: BindingPriority.LocalValue } bound:
                        return (Words.ChangedByBinding(bound.Description), null);
                    default:
                        return (Words.ChangedBy(Details.SourceOf(e.Priority)), null);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ArgumentException)
            {
                // Read in the middle of Avalonia's own change: what set it goes unsaid rather than stopping the app.
                return (string.Empty, null);
            }
        }
    }
}
