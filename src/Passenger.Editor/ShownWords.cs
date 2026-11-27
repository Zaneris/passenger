using System.Runtime.CompilerServices;
using Avalonia;
using Passenger.Core;

namespace Passenger.Editor;

/// <summary>The one way a control is given words a person reads: <see cref="Set"/> says them in the language in force and
/// keeps them with the control and its property, so a change of language (<see cref="UiLanguage.Changed"/>, raised on the
/// window's thread) says each again in place, in a view shown or not. A name goes through it too, as verbatim words, so
/// whatever a control shows is what it was given here.</summary>
public static class ShownWords
{
    private static readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, Words>> Kept = new();

    static ShownWords() => UiLanguage.Changed += (_, _) => SayAgain();

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
