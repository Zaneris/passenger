using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;

namespace Passenger.Editor;

/// <summary>The one way a peer of Passenger's tells a screen reader that one of its properties changed: a pattern's property
/// (a row's open state, a value) only while the peer gives that pattern, since a system's bridge may ask the pattern of the
/// peer as it hears the change, and a pattern refused then fails the system's own call, which ends Passenger.</summary>
public static class PeerChanges
{
    /// <summary>Each pattern's properties, by the provider a peer gives for the pattern, and whether a peer gives it.</summary>
    private static readonly Dictionary<AutomationProperty, (Type Provider, Func<AutomationPeer, bool> Gives)> Patterns = Made();

    /// <summary>Tells a reader <paramref name="property"/> went from <paramref name="was"/> to <paramref name="now"/>, unless
    /// it is a property of a pattern the peer does not give now.</summary>
    public static void Tell(AutomationPeer peer, AutomationProperty property, object? was, object? now)
    {
        if (!Patterns.TryGetValue(property, out (Type Provider, Func<AutomationPeer, bool> Gives) pattern) || pattern.Gives(peer))
        {
            peer.RaisePropertyChangedEvent(property, was, now);
        }
    }

    /// <summary>The provider a peer gives for the pattern a property belongs to; null for a property every peer has.</summary>
    public static Type? PatternOf(AutomationProperty property) => Patterns.TryGetValue(property, out (Type Provider, Func<AutomationPeer, bool> Gives) pattern) ? pattern.Provider : null;

    private static Dictionary<AutomationProperty, (Type, Func<AutomationPeer, bool>)> Made()
    {
        Dictionary<AutomationProperty, (Type, Func<AutomationPeer, bool>)> made = [];
        Add<IExpandCollapseProvider>(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty);
        Add<IRangeValueProvider>(RangeValuePatternIdentifiers.IsReadOnlyProperty, RangeValuePatternIdentifiers.MinimumProperty, RangeValuePatternIdentifiers.MaximumProperty,
            RangeValuePatternIdentifiers.ValueProperty);
        Add<IScrollProvider>(ScrollPatternIdentifiers.HorizontallyScrollableProperty, ScrollPatternIdentifiers.HorizontalScrollPercentProperty,
            ScrollPatternIdentifiers.HorizontalViewSizeProperty, ScrollPatternIdentifiers.VerticallyScrollableProperty, ScrollPatternIdentifiers.VerticalScrollPercentProperty,
            ScrollPatternIdentifiers.VerticalViewSizeProperty);
        Add<ISelectionItemProvider>(SelectionItemPatternIdentifiers.IsSelectedProperty, SelectionItemPatternIdentifiers.SelectionContainerProperty);
        Add<ISelectionProvider>(SelectionPatternIdentifiers.CanSelectMultipleProperty, SelectionPatternIdentifiers.IsSelectionRequiredProperty, SelectionPatternIdentifiers.SelectionProperty);
        Add<IToggleProvider>(TogglePatternIdentifiers.ToggleStateProperty);
        Add<IValueProvider>(ValuePatternIdentifiers.IsReadOnlyProperty, ValuePatternIdentifiers.ValueProperty);
        return made;

        void Add<T>(params AutomationProperty[] properties)
            where T : class
        {
            foreach (AutomationProperty property in properties)
            {
                made.Add(property, (typeof(T), peer => peer.GetProvider<T>() is not null));
            }
        }
    }
}
