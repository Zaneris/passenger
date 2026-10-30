using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Passenger.Dap;

namespace Passenger.Inspector.Wpf;

/// <summary>Property breakpoints, the Live Visual Tree's Break When Value Changes, in a WPF app: one element's dependency
/// property watched through WPF's own change notification, whatever changed it. Each change its hit count lets through calls
/// <see cref="Breaks.Changed"/>, where the debugger has a function breakpoint per property breakpoint, so the app stops inside
/// the call that set the value. Non-user code, so Just My Code shows the code that set it as where it stopped.</summary>
[DebuggerNonUserCode]
internal sealed class PropertyBreaks
{
    private readonly Dictionary<int, Watched> watches = [];

    /// <summary>Watches a property of an element for a breakpoint (its hit count Passenger's own words, "==2", ">3", "%2"),
    /// in place of what the breakpoint watched before.</summary>
    public void Watch(int breakpoint, DependencyObject element, string name, string? hitCondition)
    {
        DependencyProperty property = Properties.Find(element, name) ?? throw new Refusal($"{Words.TypeName(element.GetType())} has no property {name}");
        HitCondition? hits = null;
        if (hitCondition is { Length: > 0 })
        {
            hits = HitCondition.TryParse(hitCondition, out HitCondition read, out string? problem) ? read : throw new Refusal(problem);
        }
        Unwatch(breakpoint);
        DependencyPropertyDescriptor descriptor = DependencyPropertyDescriptor.FromProperty(property, element.GetType())
            ?? throw new Refusal($"WPF cannot watch {name} on {Words.TypeName(element.GetType())}");
        Watched watch = new(breakpoint, element, property, descriptor, hits);
        watches[breakpoint] = watch;
        descriptor.AddValueChanged(element, watch.Changed);
    }

    public void Unwatch(int breakpoint)
    {
        if (watches.Remove(breakpoint, out Watched? watch))
        {
            watch.Descriptor.RemoveValueChanged(watch.Element, watch.Changed);
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

    [DebuggerNonUserCode]
    private sealed class Watched(int breakpoint, DependencyObject element, DependencyProperty property, DependencyPropertyDescriptor descriptor, HitCondition? hits)
    {
        private int count;

        /// <summary>The value before the change WPF tells of, which its notification does not carry.</summary>
        private object? last = element.GetValue(property);

        public DependencyObject Element { get; } = element;

        public DependencyPropertyDescriptor Descriptor { get; } = descriptor;

        public void Changed(object? sender, EventArgs e)
        {
            object? before = last;
            object? now = Element.GetValue(property);
            last = now;
            if (hits is { } condition && !condition.IsMet(++count))
            {
                return;
            }
            string change = $"Previous Value: {Details.Format(before)}, New Value: {Details.Format(now)}{By()}";
            Breaks.Changed(breakpoint, Element, property, before, now, change, null);
        }

        /// <summary>What set the value, from where WPF says it now comes: its Style's setters or triggers as the property's
        /// row names them, the rest in the inspector's shared words.</summary>
        private string By()
        {
            try
            {
                ValueSource source = DependencyPropertyHelper.GetValueSource(Element, property);
                string from = Details.SourceOf(source);
                return (from, source.BaseValueSource) switch
                {
                    (ValueSources.Local, _) when source.IsExpression => Words.ChangedByBinding(Details.Bound(Element, property)),
                    (ValueSources.Style, BaseValueSource.StyleTrigger) => $", by {Details.Said(Element, BaseValueSource.StyleTrigger)}",
                    (ValueSources.Style, _) => $", by {Details.Said(Element, BaseValueSource.Style)}",
                    _ => Words.ChangedBy(from),
                };
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            {
                return string.Empty;
            }
        }
    }
}
