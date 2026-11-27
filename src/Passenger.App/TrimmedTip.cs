using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Passenger.App;

/// <summary>A name or path revealed on hover when its text actually ends in an ellipsis. The tooltip's usual
/// description stays alongside it and is restored after the pointer leaves.</summary>
internal static class TrimmedTip
{
    public static void Attach(TextBlock label) => Attach(label, label);

    public static void Attach(Control owner, TextBlock label, Func<string?>? fullName = null) =>
        Attach(owner, () => label, fullName);

    public static void AttachHeader(Control owner, Func<string?> header, Func<string?>? fullName = null) =>
        Attach(owner, () => owner.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Text == header()), fullName);

    private static void Attach(Control owner, Func<TextBlock?> label, Func<string?>? fullName)
    {
        // Avalonia opens only controls that have a tip, even when its opening handler supplies the content.
        if (ToolTip.GetTip(owner) is null)
        {
            Describe(owner, null);
        }
        (string Previous, object Shown)? expanded = null;
        ToolTip.AddToolTipOpeningHandler(owner, (_, opening) =>
        {
            if (opening.Cancel || ToolTip.GetTip(owner) is not string usual)
            {
                return;
            }
            string? description = usual.Length > 0 ? usual : owner.GetVisualAncestors().OfType<Control>()
                .Select(ToolTip.GetTip).OfType<string>().FirstOrDefault(tip => tip.Length > 0);
            TextBlock? text = label();
            string? full = text is { IsEffectivelyVisible: true } && text.TextLayout.TextLines.Any(line => line.HasCollapsed)
                ? fullName?.Invoke() ?? text.Text : null;
            object? tip = full is { Length: > 0 } ? Wrapped(full, description) : description;
            if (tip is null)
            {
                opening.Cancel = true;
            }
            else if (!ReferenceEquals(tip, usual))
            {
                expanded = (usual, tip);
                owner.SetCurrentValue(ToolTip.TipProperty, tip);
            }
        });
        owner.PropertyChanged += (_, change) =>
        {
            // A reused row may already have a different description by the time its tooltip closes.
            if (change.Property == ToolTip.IsOpenProperty && !ToolTip.GetIsOpen(owner) && expanded is { } tip)
            {
                expanded = null;
                if (ReferenceEquals(ToolTip.GetTip(owner), tip.Shown))
                {
                    owner.SetCurrentValue(ToolTip.TipProperty, tip.Previous);
                }
            }
        };
    }

    public static void Describe(Control owner, string? description) => ToolTip.SetTip(owner, description ?? string.Empty);

    private static TextBlock Wrapped(string full, string? description) => new()
    {
        Text = description is null ? full : description.Contains(full, StringComparison.Ordinal) ? description : $"{full}\n\n{description}",
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 360,
    };
}
