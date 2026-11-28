using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>A text box's text as the person changes it, for a field that takes a change for the person's: a TextBox tells of
/// a change a moment after it, posted, so its word of text the code gave it can come once what the code did next has landed.
/// <see cref="Hear"/> calls back once for each text the box comes to hold other than the one it last heard, which is the
/// text it held as hearing began or was last given through <see cref="Set"/>, as the box's word comes.</summary>
public static class TypedText
{
    private static readonly ConditionalWeakTable<TextBox, List<Heard>> hearing = new();

    /// <summary>Calls <paramref name="typed"/> as the box comes to hold a text other than the one it last heard.</summary>
    public static void Hear(TextBox box, Action typed)
    {
        Heard heard = new() { Text = box.Text ?? string.Empty };
        hearing.GetOrCreateValue(box).Add(heard);
        box.TextChanged += (_, _) =>
        {
            string now = box.Text ?? string.Empty;
            if (now != heard.Text)
            {
                heard.Text = now;
                typed();
            }
        };
    }

    /// <summary>Gives the box text as the code does, which nothing hearing it through <see cref="Hear"/> takes for typing.</summary>
    public static void Set(TextBox box, string? text)
    {
        box.Text = text;
        if (hearing.TryGetValue(box, out List<Heard>? heard))
        {
            foreach (Heard each in heard)
            {
                each.Text = box.Text ?? string.Empty;
            }
        }
    }

    /// <summary>The text one hearing last heard.</summary>
    private sealed class Heard
    {
        public string Text { get; set; } = string.Empty;
    }
}
