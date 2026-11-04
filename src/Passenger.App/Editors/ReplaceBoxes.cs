using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Passenger.App.Editors;

/// <summary>A replacement's box, the Find bar's and Find in Files': one line, whose Enter is its window's (replace, or
/// choose), Shift+Enter putting a line break into it at the caret, which the replacement writes as the file's own; its
/// tooltip says so, and what a regex's replacement reads.</summary>
public static class ReplaceBoxes
{
    /// <summary>What the box's tooltip says of what it takes.</summary>
    public const string Tip = "Shift+Enter puts a line break in. With Regex on, \\n, \\t and \\\\ are a line break, a tab and a backslash, and $1 or ${name} a group";

    /// <summary>Gives a box its Shift+Enter and its tooltip; taken before the window's own keys on it, so its Enter never sees one.</summary>
    public static void Take(TextBox box)
    {
        ToolTip.SetTip(box, Tip);
        box.AddHandler(InputElement.KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    private static void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter || e.KeyModifiers != KeyModifiers.Shift || sender is not TextBox box)
        {
            return;
        }
        string text = box.Text ?? string.Empty;
        int start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, text.Length);
        int end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), start, text.Length);
        box.Text = string.Concat(text.AsSpan(0, start), "\n", text.AsSpan(end));
        box.SelectionStart = box.SelectionEnd = box.CaretIndex = start + 1;
        e.Handled = true;
    }
}
