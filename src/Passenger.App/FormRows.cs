using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A form's rows of a label and what it names (Run Configurations, Publish, New Project, Live Templates), written
/// one way: the labels in one column at the rows' left, as wide as the form's longest and wrapping past
/// <see cref="LabelWidth"/>, and each field filling the rest; a label beside a tall field (a list, several lines) stands at
/// its top. The form is the control <see cref="Scope"/> marks, whose rows share the column.</summary>
public static class FormRows
{
    /// <summary>The widest a label grows before it wraps.</summary>
    public const double LabelWidth = 170;

    /// <summary>The labels' column, shared by every row in the form.</summary>
    private const string Labels = "FormLabels";

    /// <summary>Marks a control as a form, whose rows' labels share one column.</summary>
    public static void Scope(Control form) => Grid.SetIsSharedSizeScope(form, true);

    public static Grid Of(string? label, Control field, bool top = false) => Of(label, field, out _, top);

    /// <summary>A row, and its label (left out of a row with none), which a form may mark.</summary>
    public static Grid Of(string? label, Control field, out TextBlock name, bool top = false)
    {
        name = new TextBlock
        {
            Text = label ?? string.Empty,
            MaxWidth = LabelWidth,
            Margin = new Thickness(0, top ? 4 : 0, 12, 0),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        Grid row = new() { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = Labels }, new ColumnDefinition(GridLength.Star) } };
        if (!string.IsNullOrEmpty(label))
        {
            row.Children.Add(name);
            AutomationProperties.SetLabeledBy(field, name);
        }
        Grid.SetColumn(field, 1);
        row.Children.Add(field);
        return row;
    }
}
