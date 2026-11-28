using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>A form's rows of a label and what it names (Run Configurations, Publish, a feed, Clone, a breakpoint, a
/// refactoring), written one way: the labels in one column at the rows' left, as wide as the form's longest and wrapping
/// past <see cref="LabelWidth"/>, each with no colon, and each field filling the rest, named by its label for a screen
/// reader, its notes under it; a label beside a tall field (a list, a table, several lines) stands at its top. The form is
/// the control <see cref="Scope"/> marks, whose rows share the column. UiRulesTests fails label rows laid out by hand.</summary>
public static class FormRows
{
    /// <summary>The widest a label grows before it wraps, a form's and a Settings row's alike.</summary>
    public const double LabelWidth = 220;

    /// <summary>The room between a form's rows, a fold's rows too.</summary>
    public const double Between = Gaps.Wide;

    /// <summary>The labels' column, shared by every row in the form.</summary>
    private const string Labels = "FormLabels";

    /// <summary>Marks a control as a form, whose rows' labels share one column.</summary>
    public static void Scope(Control form) => Grid.SetIsSharedSizeScope(form, true);

    public static Grid Of(Words? label, Control field, bool top = false) => Of(label, field, [], out _, top);

    /// <summary>A row with notes under its field (<see cref="Note"/>), each a little below what is above it.</summary>
    public static Grid Of(Words? label, Control field, IReadOnlyList<TextBlock> notes, bool top = false) => Of(label, field, notes, out _, top);

    /// <summary>A row, and its label (left out of a row with none), which a form may mark.</summary>
    public static Grid Of(Words? label, Control field, out TextBlock name, bool top = false) => Of(label, field, [], out name, top);

    private static Grid Of(Words? label, Control field, IReadOnlyList<TextBlock> notes, out TextBlock name, bool top)
    {
        name = new TextBlock
        {
            [ShownWords.TextProperty] = label ?? Words.Verbatim(string.Empty),
            MaxWidth = LabelWidth,
            Margin = new Thickness(0, top ? Gaps.Tight : 0, Gaps.Wide, 0),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        Grid row = new() { ColumnDefinitions = { new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = Labels }, new ColumnDefinition(GridLength.Star) } };
        // The field's row takes what height the row is given past its notes, so a list a page fills with fills it.
        row.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        if (label is { IsEmpty: false })
        {
            row.Children.Add(name);
            AutomationProperties.SetLabeledBy(field is BrowseRow browse ? browse.Field : field, name);
        }
        Grid.SetColumn(field, 1);
        row.Children.Add(field);
        foreach (TextBlock note in notes)
        {
            row.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            note.Margin = new Thickness(0, Gaps.Tight, 0, 0);
            Grid.SetColumn(note, 1);
            Grid.SetRow(note, row.RowDefinitions.Count - 1);
            row.Children.Add(note);
        }
        return row;
    }

    /// <summary>A field's note, under it in its row: muted, at the caption's size, wrapping, or for a path
    /// (<paramref name="path"/>) on one line, cut short by its folders.</summary>
    public static TextBlock Note(Words? words = null, bool path = false) => new()
    {
        Classes = { StyleClasses.Muted, StyleClasses.Caption },
        [ShownWords.TextProperty] = words,
        TextWrapping = path ? TextWrapping.NoWrap : TextWrapping.Wrap,
        TextTrimming = path ? TextTrimming.PathSegmentEllipsis : TextTrimming.None,
    };
}
