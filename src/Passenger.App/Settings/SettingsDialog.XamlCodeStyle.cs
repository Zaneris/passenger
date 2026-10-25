using Avalonia.Controls;
using Passenger.Xaml.Features;

namespace Passenger.App.Settings;

/// <summary>The Editor, Code Style, XAML page: the XML formatter's options, which Reformat Code and Code Cleanup lay
/// out Avalonia's and WPF's XAML by, then the attribute order and empty-element style. A file's .editorconfig says over
/// each, by Rider's keys.</summary>
public sealed partial class SettingsDialog
{
    private void AddXamlCodeStyle()
    {
        XamlFormatSettings now = Working.XamlFormat;
        void Set(Func<XamlFormatSettings, XamlFormatSettings> change) => Change(s => s with { XamlFormat = change(s.XamlFormat) });

        Add(Declared.AttributeStyle);
        Add(Declared.AttributeIndent);
        Add(Declared.SpacesAroundEquals);
        Add(Declared.SpaceAfterLastAttribute);
        Add(Declared.SpaceBeforeSelfClosing);
        Add(Declared.KeepLineBreaks);
        Add(Declared.MaxBlankLines);
        Add(Declared.MultilineElementsOnNewLine);
        Add(Declared.SinglelineElementsOnNewLine);
        Add(Declared.BreaksInsideElementsWithChildren);
        Add(Declared.BreaksInsideMultilineElements);
        Add(Declared.ChildIndent);
        Add(Declared.TextIndent);
        Add(Declared.BlankLineAfterProcessingInstructions);
        Add(Declared.WrapTags);
        Add(Declared.WrapAroundElements);
        Add(Declared.RightMargin);

        TextBox order = new() { Classes = { "search" }, Text = Groups(now.AttributeOrder), AcceptsReturn = true, Width = 420, Height = 210, IsEnabled = now.ReorderAttributes };
        // A group longer than the box scrolls into view rather than being cut at its edge.
        ScrollViewer.SetHorizontalScrollBarVisibility(order, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        order.TextChanged += (_, _) => Set(x => x with { AttributeOrder = string.Join("; ", (order.Text ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) });
        Add(XamlCodeStylePage, "Reorder attributes", Check(now.ReorderAttributes, v =>
        {
            Set(x => x with { ReorderAttributes = v });
            order.IsEnabled = v;
        }), $"{XamlWords} attributes order ordering sort reorder xaml styler x:name grid",
            "XAML Styler's Enable attribute reordering: each attribute in the group below that names it most nearly, a group to a line.");
        Add(XamlCodeStylePage, "Attribute ordering rule groups", order, $"{XamlWords} attributes order ordering rule groups xaml styler");
        Add(Declared.CollapseEmptyElements);
    }

    /// <summary>A rule groups' text as the box shows it, a group to a line.</summary>
    private static string Groups(string order) =>
        string.Join('\n', order.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}
