using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;

namespace Passenger.Tests;

/// <summary>What a person sees go wrong in a window, a dialog or a part of one, however narrow: words cut short with no
/// ellipsis or wrap, and two things a person reads or presses drawn one over the other, each as far as what clips it
/// shows it. A field's own options inside it and what one holds inside another are laid over each other on purpose. The
/// code editor draws its own rows and a scroll bar its own thumb, so neither is read here.</summary>
internal static class LayoutFaults
{
    /// <summary>The faults of <paramref name="part"/>, measured in <paramref name="window"/>, each saying what and where.</summary>
    public static List<string> Of(Visual window, Visual part, string what)
    {
        List<string> found = [];
        Control[] drawn = [.. part.GetSelfAndVisualDescendants().OfType<Control>().Where(c => (c is TextBlock { Text.Length: > 0 } or Button or ComboBox or TextBox)
            && c.IsEffectivelyVisible && Drawn(c) && c.FindAncestorOfType<Passenger.Editor.CodeEditor>() is null && c.FindAncestorOfType<ScrollBar>() is null)];
        foreach (TextBlock text in drawn.OfType<TextBlock>().Where(t => t.Bounds.Width >= 1))
        {
            double room = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
            if (text.TextLayout.Width > room + 0.5)
            {
                found.Add($"{what} cuts '{text.Text}' to {room:0.#} px of its {text.TextLayout.Width:0.#}, where words with no room end in an ellipsis");
            }
        }
        Rect[] seen = [.. drawn.Select(c => Visible(window, c))];
        for (int i = 0; i < drawn.Length; i++)
        {
            for (int j = i + 1; j < drawn.Length; j++)
            {
                Rect both = seen[i].Intersect(seen[j]);
                // A field's own options stand inside it, as Find's do, its text kept clear of them by its padding.
                bool inField = (drawn[i] is TextBox && seen[i].Contains(seen[j])) || (drawn[j] is TextBox && seen[j].Contains(seen[i]));
                if (both.Width > 1 && both.Height > 1 && !inField && !drawn[i].IsVisualAncestorOf(drawn[j]) && !drawn[j].IsVisualAncestorOf(drawn[i]))
                {
                    found.Add($"{what} draws {Named(drawn[i])} and {Named(drawn[j])} one over the other, across {both.Width:0.#} by {both.Height:0.#} px");
                }
            }
        }
        return found;
    }

    /// <summary>Holds a dialog to no faults as it opened, at its least width where it can be narrowed (its MinWidth), and
    /// drawn at display scale 2 as at 1; it is left as it was.</summary>
    public static void Hold(Window dialog, string what)
    {
        double width = dialog.Width;
        double scale = dialog.RenderScaling;
        List<string> found = [];
        try
        {
            foreach (double each in (double[])[1, 2])
            {
                dialog.SetRenderScaling(each);
                Ui.Settle();
                found.AddRange(Of(dialog, dialog, $"{what} at scale {each}"));
                if (dialog.CanResize && dialog.MinWidth > 0 && dialog.SizeToContent is SizeToContent.Manual or SizeToContent.Height && dialog.Bounds.Width > dialog.MinWidth)
                {
                    dialog.Width = dialog.MinWidth;
                    Ui.Settle();
                    found.AddRange(Of(dialog, dialog, $"{what} at its least width, {dialog.MinWidth:0}, at scale {each}"));
                    dialog.Width = width;
                    Ui.Settle();
                }
            }
        }
        finally
        {
            dialog.SetRenderScaling(scale);
            Ui.Settle();
        }
        Assert.True(found.Count == 0, string.Join("\n", found.Distinct()));
    }

    /// <summary>Whether a control is drawn at all: one laid out but clear (a toolbar widget gone whole keeps its bounds, at
    /// no opacity) is not.</summary>
    private static bool Drawn(Visual control) => control.GetSelfAndVisualAncestors().All(v => v.Opacity > 0);

    /// <summary>What of a control is drawn: its place cut to each ancestor that clips what it holds.</summary>
    private static Rect Visible(Visual window, Visual control)
    {
        Rect seen = Placed(window, control);
        for (Visual? up = control.GetVisualParent(); up is not null && up != window; up = up.GetVisualParent())
        {
            if (up.ClipToBounds)
            {
                seen = seen.Intersect(Placed(window, up));
            }
        }
        return seen;
    }

    private static Rect Placed(Visual window, Visual control) =>
        new(control.TranslatePoint(default, window) ?? default, control.Bounds.Size);

    private static string Named(Control control) => control switch
    {
        TextBlock text => $"'{text.Text}'",
        ContentControl { Content: string words } => $"'{words}'",
        _ => $"the {control.GetType().Name} {ToolTip.GetTip(control) ?? control.Name}".TrimEnd(),
    };
}
