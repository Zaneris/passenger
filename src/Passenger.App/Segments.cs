using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>A few choices side by side of which one is chosen (Find in Files' scopes, a table's Table and Text, a
/// hierarchy's two directions): <see cref="ChoiceRow{TKey}"/>'s toggles in the segments' look (<see cref="Segments"/>).</summary>
public sealed class Segments<TKey> : ChoiceRow<TKey>
    where TKey : notnull
{
    /// <summary>Adds a segment of <paramref name="words"/> for <paramref name="key"/>, unchosen, named <paramref name="name"/>.</summary>
    public ToggleButton Add(TKey key, Words words, string? name = null) => Join(key, Segments.Made(new ChoiceToggle(), words), name);
}

/// <summary>The segments' one look (Shell.axaml's <c>ToggleButton.segment</c>): words at the caption's step, muted, the
/// chosen one on a raised band with the chosen mark under it. A row of them is a <see cref="Segments{TKey}"/>; a segment
/// of its own turns one thing on or off.</summary>
public static class Segments
{
    /// <summary>A toggle of words in a bar that turns one thing on or off (a source's lines shown, a log's timestamps, the
    /// properties at their defaults), chosen while it is on.</summary>
    public static ToggleButton Toggle(Words words) => Made(new ToggleButton(), words);

    internal static T Made<T>(T toggle, Words words)
        where T : ToggleButton
    {
        toggle.Classes.Add(StyleClasses.Segment);
        toggle.Classes.Add(StyleClasses.Caption);
        // Words given empty leave the content to its maker (a name set later), never said again over it.
        if (!words.IsEmpty)
        {
            ShownWords.Set(toggle, ContentControl.ContentProperty, words);
        }
        toggle.VerticalAlignment = VerticalAlignment.Center;
        return toggle;
    }
}
