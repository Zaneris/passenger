using Avalonia.Controls;
using Avalonia.Data;
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

    public static MenuItem Attach(MenuItem item, string? fullName = null)
    {
        AttachHeader(item, () => item.Header as string, () => fullName);
        return item;
    }

    public static void AttachHeader(Control owner, Func<string?> header, Func<string?>? fullName = null) =>
        Attach(owner, () => header() is { } words
            ? owner.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Text == words)
            : null, fullName);

    private static void Attach(Control owner, Func<TextBlock?> label, Func<string?>? fullName)
    {
        // Avalonia opens only controls that have a tip, even when its opening handler supplies the content.
        if (ToolTip.GetTip(owner) is null)
        {
            Describe(owner, null);
        }
        IDisposable? expanded = null;
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
                // A disposable value lets the original description keep changing underneath the open tip.
                expanded = owner.SetValue(ToolTip.TipProperty, tip, BindingPriority.Animation);
            }
        });
        owner.PropertyChanged += (_, change) =>
        {
            if (change.Property == ToolTip.IsOpenProperty && !ToolTip.GetIsOpen(owner))
            {
                expanded?.Dispose();
                expanded = null;
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
