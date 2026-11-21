using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Passenger.Lsp;

namespace Passenger.App.CSharp;

/// <summary>The kinds of member a completion list can be narrowed to, an item's kind by what it names
/// (<see cref="Of"/>): a field takes in a constant and an enum's member, a method no extension method.</summary>
[Flags]
internal enum MemberKinds
{
    None = 0,
    Properties = 1,
    Methods = 2,
    ExtensionMethods = 4,
    Fields = 8,
    Events = 16,
}

internal static class MemberKindsOf
{
    /// <summary>Every kind, in the order the filters stand.</summary>
    public static readonly MemberKinds[] All = [MemberKinds.Properties, MemberKinds.Methods, MemberKinds.ExtensionMethods, MemberKinds.Fields, MemberKinds.Events];

    /// <summary>The kind of member an item names; none for what is no member (a keyword, a type, a template).</summary>
    public static MemberKinds Of(CompletionItem item) => item.Names == Roslyn.Extension.CompletionKind.ExtensionMethod
        ? MemberKinds.ExtensionMethods
        : item.Kind switch
        {
            CompletionItemKind.Property => MemberKinds.Properties,
            CompletionItemKind.Method or CompletionItemKind.Function => MemberKinds.Methods,
            CompletionItemKind.Field or CompletionItemKind.Constant or CompletionItemKind.EnumMember => MemberKinds.Fields,
            CompletionItemKind.Event => MemberKinds.Events,
            _ => MemberKinds.None,
        };

    /// <summary>What the filter of a kind is called, its tip and its name to a screen reader.</summary>
    public static string Words(MemberKinds kind) => kind switch
    {
        MemberKinds.Properties => "Properties",
        MemberKinds.Methods => "Methods",
        MemberKinds.ExtensionMethods => "Extension methods",
        MemberKinds.Fields => "Fields and constants",
        _ => "Events",
    };

    /// <summary>The badge's letter and colour a kind's items show, which its filter shows too.</summary>
    public static (string Letter, string Brush) Badge(MemberKinds kind) => kind switch
    {
        MemberKinds.Properties => ("p", TokenKeys.Syntax_Property),
        MemberKinds.Methods => ("m", TokenKeys.Syntax_Method),
        MemberKinds.ExtensionMethods => ("x", TokenKeys.Syntax_Method),
        MemberKinds.Fields => ("f", TokenKeys.Syntax_Field),
        _ => ("e", TokenKeys.Syntax_Event),
    };
}

/// <summary>The completion list's filters by the kind of member its items name, under a list of a type's members of more
/// than one kind (after its dot): a toggle a kind, showing the badge its items show, any of them on at once, none on
/// showing every item. They leave the keys with the editor, and each list opens with them off.</summary>
internal sealed class CompletionFilters : StackPanel
{
    private readonly Dictionary<MemberKinds, ToggleButton> toggles = [];

    /// <summary>Whether the toggles are being set by the list rather than the person.</summary>
    private bool setting;

    public CompletionFilters()
    {
        Orientation = Orientation.Horizontal;
        Spacing = Gaps.Tight;
        IsVisible = false;
        foreach (MemberKinds kind in MemberKindsOf.All)
        {
            Border badge = Tags.Kind(out TextBlock letter);
            (letter.Text, string brush) = MemberKindsOf.Badge(kind);
            badge.Bind(Border.BackgroundProperty, badge.GetResourceObservable(brush));
            ToggleButton toggle = ToolButtons.Give(new ToggleButton(), badge, MemberKindsOf.Words(kind));
            toggle.IsCheckedChanged += (_, _) =>
            {
                if (!setting)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            };
            toggles[kind] = toggle;
            Children.Add(toggle);
        }
    }

    /// <summary>The person turned a filter on or off.</summary>
    public event EventHandler? Changed;

    /// <summary>The kinds whose filters are on; none for every item.</summary>
    public MemberKinds On => toggles.Where(t => t.Value.IsVisible && t.Value.IsChecked == true).Aggregate(MemberKinds.None, (on, t) => on | t.Key);

    /// <summary>The kinds offered now, each a filter shown.</summary>
    public MemberKinds Offered { get; private set; }

    /// <summary>The filter of a kind, shown or not.</summary>
    public ToggleButton Of(MemberKinds kind) => toggles[kind];

    /// <summary>Filters for the kinds a list's items are of, where they are more than one, else none; with
    /// <paramref name="fresh"/> (a list opening) each off, else those still offered as they were.</summary>
    public void Offer(MemberKinds offered, bool fresh)
    {
        if (MemberKindsOf.All.Count(k => offered.HasFlag(k)) < 2)
        {
            offered = MemberKinds.None;
        }
        Offered = offered;
        setting = true;
        try
        {
            foreach ((MemberKinds kind, ToggleButton toggle) in toggles)
            {
                toggle.IsVisible = offered.HasFlag(kind);
                if (fresh || !toggle.IsVisible)
                {
                    toggle.IsChecked = false;
                }
            }
        }
        finally
        {
            setting = false;
        }
        IsVisible = offered != MemberKinds.None;
    }
}
