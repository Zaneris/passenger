using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia;
using Passenger.Core;

namespace Passenger.Editor;

/// <summary>The one way a control is given words a person reads: its attached properties (<see cref="TextProperty"/>,
/// <see cref="ContentProperty"/>, <see cref="HeaderProperty"/>, <see cref="TitleProperty"/>, <see cref="PlaceholderTextProperty"/>,
/// <see cref="TipProperty"/>, <see cref="NameProperty"/>, <see cref="HelpTextProperty"/>), set in an initializer as
/// <c>[ShownWords.TextProperty] = words</c>, or <see cref="Set"/> for any other, say the words in the language in force
/// into the control's own property of that name and keep them, so a change of language (<see cref="UiLanguage.Changed"/>,
/// raised on the window's thread) says each again in place, in a view shown or not. A name goes through it too, as
/// verbatim words, so whatever a control shows is what it was given here.</summary>
public sealed class ShownWords
{
    private static readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, Words>> Kept = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), AvaloniaProperty?> Targets = new();

    /// <summary>A TextBlock's or a TextBox's Text.</summary>
    public static readonly AttachedProperty<Words?> TextProperty = AvaloniaProperty.RegisterAttached<ShownWords, AvaloniaObject, Words?>("Text");

    /// <summary>A ContentControl's Content (a button's words, a check box's).</summary>
    public static readonly AttachedProperty<Words?> ContentProperty = AvaloniaProperty.RegisterAttached<ShownWords, AvaloniaObject, Words?>("Content");

    /// <summary>A menu item's or a headered control's Header.</summary>
    public static readonly AttachedProperty<Words?> HeaderProperty = AvaloniaProperty.RegisterAttached<ShownWords, AvaloniaObject, Words?>("Header");

    /// <summary>A window's Title.</summary>
    public static readonly AttachedProperty<Words?> TitleProperty = AvaloniaProperty.RegisterAttached<ShownWords, AvaloniaObject, Words?>("Title");

    /// <summary>A text box's PlaceholderText, shown while it is empty.</summary>
    public static readonly AttachedProperty<Words?> PlaceholderTextProperty = AvaloniaProperty.RegisterAttached<ShownWords, AvaloniaObject, Words?>("PlaceholderText");

    /// <summary>A control's tip (ToolTip.Tip).</summary>
    public static readonly AttachedProperty<Words?> TipProperty = AvaloniaProperty.RegisterAttached<ShownWords, AvaloniaObject, Words?>("Tip");

    /// <summary>A control's accessible name (AutomationProperties.Name).</summary>
    public static readonly AttachedProperty<Words?> NameProperty = AvaloniaProperty.RegisterAttached<ShownWords, AvaloniaObject, Words?>("Name");

    /// <summary>A control's accessible help (AutomationProperties.HelpText).</summary>
    public static readonly AttachedProperty<Words?> HelpTextProperty = AvaloniaProperty.RegisterAttached<ShownWords, AvaloniaObject, Words?>("HelpText");

    static ShownWords()
    {
        Forward(TextProperty, "Text");
        Forward(ContentProperty, "Content");
        Forward(HeaderProperty, "Header");
        Forward(TitleProperty, "Title");
        Forward(PlaceholderTextProperty, "PlaceholderText");
        Forward(TipProperty, Avalonia.Controls.ToolTip.TipProperty);
        Forward(NameProperty, Avalonia.Automation.AutomationProperties.NameProperty);
        Forward(HelpTextProperty, Avalonia.Automation.AutomationProperties.HelpTextProperty);
        UiLanguage.Changed += (_, _) => SayAgain();
    }

    private ShownWords()
    {
    }

    /// <summary>Gives <paramref name="target"/>'s <paramref name="property"/> the words, said in the language in force; null
    /// clears it.</summary>
    public static void Set(AvaloniaObject target, AvaloniaProperty property, Words? words)
    {
        Dictionary<AvaloniaProperty, Words> given = Kept.GetOrCreateValue(target);
        if (words is null)
        {
            given.Remove(property);
            target.ClearValue(property);
            return;
        }
        given[property] = words;
        target.SetValue(property, words.Shown);
    }

    /// <summary>The words a control's property was last given here; null where it was given none.</summary>
    public static Words? Of(AvaloniaObject target, AvaloniaProperty property) =>
        Kept.TryGetValue(target, out Dictionary<AvaloniaProperty, Words>? given) && given.TryGetValue(property, out Words? words) ? words : null;

    /// <summary>Has an attached property of words say them into the control's own property named <paramref name="target"/>,
    /// found by the control's type.</summary>
    private static void Forward(AttachedProperty<Words?> property, string target) => property.Changed.AddClassHandler<AvaloniaObject>((control, change) =>
        Set(control, Targets.GetOrAdd((control.GetType(), target), key => AvaloniaPropertyRegistry.Instance.FindRegistered(control, key.Name))
            ?? throw new InvalidOperationException($"{control.GetType().Name} has no {target} for ShownWords.{property.Name}"), (Words?)change.NewValue));

    /// <summary>Has an attached property of words say them into another attached property (a tip, an accessible name).</summary>
    private static void Forward(AttachedProperty<Words?> property, AvaloniaProperty target) =>
        property.Changed.AddClassHandler<AvaloniaObject>((control, change) => Set(control, target, (Words?)change.NewValue));

    /// <summary>Every kept property said again in the language now in force.</summary>
    private static void SayAgain()
    {
        foreach ((AvaloniaObject target, Dictionary<AvaloniaProperty, Words> given) in Kept.ToList())
        {
            foreach ((AvaloniaProperty property, Words words) in given.ToList())
            {
                target.SetValue(property, words.Shown);
            }
        }
    }
}
