using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace Passenger.App;

/// <summary>Words cut short are shown whole in a tip, every label's one way, installed once for all: the control whose tip
/// the pointer meets (the nearest with one, as the tooltip service finds it) shows above its own words the whole words of
/// each label inside it that ends in an ellipsis, but those its words already say, and a label cut short under no tip is
/// its own while the pointer is on it. A tip that is no words (a control) is shown as it is. The words wrap at the tip's
/// own width, a name or path at its parts (<see cref="Parted"/>).</summary>
public static class TrimmedTip
{
    /// <summary>A tip laid over a control's own words while it shows, taken back as it closes.</summary>
    private static readonly ConditionalWeakTable<Control, IDisposable> Laid = [];

    /// <summary>The whole words given as its tip to a label with none, taken back as the pointer leaves it.</summary>
    private static readonly ConditionalWeakTable<TextBlock, string> Given = [];

    public static void Install()
    {
        InputElement.PointerEnteredEvent.AddClassHandler<TextBlock>((label, _) => Give(label));
        InputElement.PointerExitedEvent.AddClassHandler<TextBlock>((label, _) => TakeBack(label));
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>(Opening);
        ToolTip.IsOpenProperty.Changed.AddClassHandler<Control>((host, change) =>
        {
            if (change.NewValue is false && Laid.TryGetValue(host, out IDisposable? laid))
            {
                Laid.Remove(host);
                laid.Dispose();
            }
        });
    }

    /// <summary>A break between a name's or a path's parts, which draws nothing.</summary>
    public const char PartBreak = '\u200B';

    /// <summary>Where a label's whole words may wrap between their parts: after each dot, slash, backslash or underscore
    /// that has more words after it, a dotted name or a path wrapping there rather than inside a part.</summary>
    public static string Parted(string words)
    {
        StringBuilder parted = new(words.Length + 8);
        for (int i = 0; i < words.Length; i++)
        {
            parted.Append(words[i]);
            if (words[i] is '.' or '/' or '\\' or '_' && i + 1 < words.Length && !char.IsWhiteSpace(words[i + 1]))
            {
                parted.Append(PartBreak);
            }
        }
        return parted.ToString();
    }

    /// <summary>Whether a label shown ends its words in an ellipsis, as it was last laid out.</summary>
    public static bool IsCutShort(TextBlock label)
    {
        if (label.TextTrimming == TextTrimming.None || label.Text is not { Length: > 0 } || !label.IsEffectivelyVisible)
        {
            return false;
        }
        foreach (TextLine line in label.TextLayout.TextLines)
        {
            if (line.HasCollapsed)
            {
                return true;
            }
        }
        return false;
    }

    private static void Give(TextBlock label)
    {
        if (ToolTip.GetTip(label) is null && Shows(label) && IsCutShort(label) && !UnderTip(label))
        {
            string whole = Parted(label.Text!);
            Given.AddOrUpdate(label, whole);
            ToolTip.SetTip(label, whole);
        }
    }

    private static void TakeBack(TextBlock label)
    {
        if (Given.TryGetValue(label, out string? whole))
        {
            Given.Remove(label);
            if (ReferenceEquals(ToolTip.GetTip(label), whole))
            {
                label.ClearValue(ToolTip.TipProperty);
            }
        }
    }

    private static void Opening(Control host, CancelRoutedEventArgs e)
    {
        if (e.Cancel)
        {
            return;
        }
        object? tip = ToolTip.GetTip(host);
        if (host is TextBlock label && Given.TryGetValue(label, out string? given) && ReferenceEquals(tip, given))
        {
            string now = WholeWords(label, string.Empty);
            if (now.Length == 0)
            {
                e.Cancel = true;
            }
            else if (now != given)
            {
                Given.AddOrUpdate(label, now);
                ToolTip.SetTip(label, now);
            }
            return;
        }
        if (tip is string words && WholeWords(host, words) is { Length: > 0 } whole)
        {
            Laid.AddOrUpdate(host, host.SetValue(ToolTip.TipProperty, words.Length == 0 ? whole : $"{whole}\n\n{words}", BindingPriority.Animation)!);
        }
    }

    /// <summary>The whole words of the labels cut short in a control (itself among them), one to a line, but those its tip's
    /// words say already; a control inside it with a tip of its own is that tip's.</summary>
    private static string WholeWords(Control host, string words)
    {
        List<string> whole = [];
        Gather(host);
        return string.Join('\n', whole);

        void Gather(Visual visual)
        {
            if (visual is TextBlock label && IsCutShort(label) && !words.Contains(label.Text!, StringComparison.Ordinal)
                && Parted(label.Text!) is { } parted && !whole.Contains(parted))
            {
                whole.Add(parted);
            }
            foreach (Visual child in visual.GetVisualChildren())
            {
                if (child.IsVisible && (child is not Control inner || ToolTip.GetTip(inner) is null))
                {
                    Gather(child);
                }
            }
        }
    }

    /// <summary>Whether the tooltip service would show a tip of a label's.</summary>
    private static bool Shows(Control control) => ToolTip.GetServiceEnabled(control) && (control.IsEffectivelyEnabled || ToolTip.GetShowOnDisabled(control));

    /// <summary>Whether a control over a label shows its tip in place of one the label had.</summary>
    private static bool UnderTip(TextBlock label)
    {
        for (Visual? above = label.GetVisualParent(); above is not null; above = above.GetVisualParent())
        {
            if (above is Control control && ToolTip.GetTip(control) is not null && Shows(control))
            {
                return true;
            }
        }
        return false;
    }
}
