using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Settings;

/// <summary>An option's row in Settings: its label, what edits it, a note under the editor, and under that what the open
/// folder's .editorconfig files set over it, where they do. On a page with room for the labels' column beside the widest
/// editor, the label stands at the left, its first line level with the editor beside it and the lines under the editor
/// right under it, however far the label runs on; on a narrower page, as every row of it does, the label stands above the
/// editor, and a lone check box before its label, level with its first line, the lines under it right under the label. An
/// editor that stretches up to a most width (a list, a line of text, a number box) is given that width where there is room
/// and the page's where there is not; any other takes its own. The lines under it wrap where the widest editor ends, or at
/// the page's edge before it.</summary>
public sealed class OptionRow : Panel
{
    /// <summary>The labels' column beside the editors, as wide as a form's labels grow (<see cref="FormRows.LabelWidth"/>).</summary>
    public const double LabelWidth = FormRows.LabelWidth;

    /// <summary>The widest an editor stands: a list's, a line of text's, a box of several lines'; a note wraps where it ends.</summary>
    public const double EditorWidth = 420;

    /// <summary>The widest a number box stands: room for the longest number a setting takes, and its buttons.</summary>
    public const double NumberWidth = 140;

    /// <summary>Between the label, the editor and the note, where they stand one over another.</summary>
    private const double Gap = Gaps.Tight;

    /// <summary>Between a check box and the label after it.</summary>
    private const double BoxGap = Gaps.Normal;

    private readonly double labelTop;

    /// <param name="labelTop">Where the label stands beside a tall editor, level with its first line; below zero, its first
    /// line centred on the editor.</param>
    /// <param name="saidOver">Where what the folder's .editorconfig files set over the option is said, for a row they may
    /// set; shown only while it says something.</param>
    public OptionRow(TextBlock label, Control editor, TextBlock? note, double labelTop, TextBlock? saidOver = null)
    {
        Label = label;
        Editor = editor;
        Note = note;
        SaidOver = saidOver;
        this.labelTop = labelTop;
        Children.Add(label);
        Children.Add(editor);
        foreach (TextBlock under in ((TextBlock?[])[note, saidOver]).OfType<TextBlock>())
        {
            Children.Add(under);
        }
    }

    public TextBlock Label { get; }

    public Control Editor { get; }

    public TextBlock? Note { get; }

    /// <summary>What the open folder's .editorconfig files set over the option, under the note.</summary>
    public TextBlock? SaidOver { get; }

    /// <summary>Whether the label stands above the editor (or after its check box), as laid out last.</summary>
    public bool IsStacked { get; private set; }

    /// <summary>Whether a page of a width stands its rows' labels above their editors.</summary>
    public static bool Stacks(double width) => width < LabelWidth + EditorWidth;

    /// <summary>Whether the editor is a check box with no words of its own, which stands before its label on a narrow page.</summary>
    private bool Boxed => Editor is CheckBox { Content: null };

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        IsStacked = Stacks(width);
        if (!IsStacked)
        {
            Editor.Measure(new Size(width - LabelWidth, double.PositiveInfinity));
            IsStacked = Editor.DesiredSize.Width > width - LabelWidth;
        }
        if (!IsStacked)
        {
            Label.Measure(new Size(LabelWidth, double.PositiveInfinity));
            MeasureUnder(NoteRoom(width - LabelWidth));
            (double label, double editor) = Tops();
            return new Size(LabelWidth + Math.Max(Editor.DesiredSize.Width, UnderWidth()),
                Math.Max(label + Label.DesiredSize.Height, editor + Editor.DesiredSize.Height + Below()));
        }
        Editor.Measure(new Size(width, double.PositiveInfinity));
        if (Boxed)
        {
            double lead = Editor.DesiredSize.Width + BoxGap;
            Label.Measure(new Size(Math.Max(0, width - lead), double.PositiveInfinity));
            MeasureUnder(Math.Max(0, NoteRoom(width) - lead));
            (double label, double editor) = Tops();
            return new Size(lead + Math.Max(Label.DesiredSize.Width, UnderWidth()),
                Math.Max(label + Label.DesiredSize.Height + Below(), editor + Editor.DesiredSize.Height));
        }
        Label.Measure(new Size(width, double.PositiveInfinity));
        MeasureUnder(NoteRoom(width));
        return new Size(Math.Max(Label.DesiredSize.Width, Math.Max(Editor.DesiredSize.Width, UnderWidth())),
            Label.DesiredSize.Height + Gap + Editor.DesiredSize.Height + Below());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = finalSize.Width;
        if (IsStacked && Boxed)
        {
            double lead = Editor.DesiredSize.Width + BoxGap;
            (double label, double editor) = Tops();
            Editor.Arrange(new Rect(0, editor, Editor.DesiredSize.Width, Editor.DesiredSize.Height));
            Label.Arrange(new Rect(lead, label, Math.Max(0, width - lead), Label.DesiredSize.Height));
            ArrangeUnder(lead, label + Label.DesiredSize.Height, Math.Max(0, NoteRoom(width) - lead));
        }
        else if (IsStacked)
        {
            Label.Arrange(new Rect(0, 0, width, Label.DesiredSize.Height));
            double y = Label.DesiredSize.Height + Gap;
            Editor.Arrange(new Rect(0, y, Across(width), Editor.DesiredSize.Height));
            ArrangeUnder(0, y + Editor.DesiredSize.Height, NoteRoom(width));
        }
        else
        {
            (double label, double editor) = Tops();
            Label.Arrange(new Rect(0, label, LabelWidth, Label.DesiredSize.Height));
            Editor.Arrange(new Rect(LabelWidth, editor, Across(width - LabelWidth), Editor.DesiredSize.Height));
            ArrangeUnder(LabelWidth, editor + Editor.DesiredSize.Height, NoteRoom(width - LabelWidth));
        }
        return finalSize;
    }

    /// <summary>The note and what is said over the option, those shown, one under the other.</summary>
    private IEnumerable<TextBlock> Under() => ((TextBlock?[])[Note, SaidOver]).OfType<TextBlock>().Where(t => t.IsVisible);

    private void MeasureUnder(double room)
    {
        foreach (TextBlock under in Under())
        {
            under.Measure(new Size(room, double.PositiveInfinity));
        }
    }

    private double UnderWidth() => Under().Select(t => t.DesiredSize.Width).DefaultIfEmpty(0).Max();

    /// <summary>Lays the lines under the editor out from <paramref name="top"/>, each a gap below what is above it.</summary>
    private void ArrangeUnder(double left, double top, double room)
    {
        foreach (TextBlock under in Under())
        {
            top += Gap;
            under.Arrange(new Rect(left, top, room, under.DesiredSize.Height));
            top += under.DesiredSize.Height;
        }
    }

    /// <summary>Where the label and the editor beside it stand from the row's top, as measured: the label where the row
    /// was given beside a tall editor, else its first line centred on the editor, the rest of it running on below.</summary>
    private (double Label, double Editor) Tops()
    {
        if (labelTop >= 0)
        {
            return (labelTop, 0);
        }
        double centre = Label.Margin.Top + Label.Padding.Top + (Label.TextLayout.TextLines is [{ } line, ..] ? line.Height : 0) / 2;
        double middle = Math.Max(centre, Editor.DesiredSize.Height / 2);
        return (middle - centre, middle - Editor.DesiredSize.Height / 2);
    }

    private double Below() => Under().Sum(t => Gap + t.DesiredSize.Height);

    /// <summary>What a note takes of the room beside or under the label: up to where the widest editor ends.</summary>
    private static double NoteRoom(double room) => Math.Min(room, EditorWidth);

    /// <summary>The editor's width in the room it has: up to its most width where it stretches to one, else its own.</summary>
    private double Across(double room) =>
        Editor.HorizontalAlignment == HorizontalAlignment.Stretch && double.IsFinite(Editor.MaxWidth)
            ? Math.Min(room, Editor.MaxWidth + Editor.Margin.Left + Editor.Margin.Right)
            : Math.Min(room, Editor.DesiredSize.Width);
}

/// <summary>A field and what follows it (a drop-down and its Add from File…, a number box and its unit, an action's keys and
/// their Edit Shortcuts): the field <see cref="Most"/> wide where there is room, narrower where not, so the two keep to
/// the room they have; with no most, the field takes all the room the other leaves.</summary>
public sealed class NarrowingField : Panel
{
    public static readonly StyledProperty<double> MostProperty = AvaloniaProperty.Register<NarrowingField, double>(nameof(Most), double.PositiveInfinity);

    private const double Gap = Gaps.Normal;

    private readonly Control field;
    private readonly Control after;

    static NarrowingField() => AffectsMeasure<NarrowingField>(MostProperty);

    public NarrowingField(Control field, Control after)
    {
        this.field = field;
        this.after = after;
        field.Width = double.NaN;
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        Children.Add(field);
        Children.Add(after);
    }

    /// <summary>The field's width where there is room.</summary>
    public double Most
    {
        get => GetValue(MostProperty);
        set => SetValue(MostProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        after.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        double width = FieldWidth(availableSize.Width);
        field.Measure(new Size(width, availableSize.Height));
        // With no most and no bound to the room, the field takes its own width.
        width = double.IsFinite(width) ? width : field.DesiredSize.Width;
        return new Size(width + Gap + after.DesiredSize.Width, Math.Max(field.DesiredSize.Height, after.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = FieldWidth(finalSize.Width);
        field.Arrange(new Rect(0, 0, width, finalSize.Height));
        after.Arrange(new Rect(width + Gap, 0, after.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }

    /// <summary>The field's width in a room: its most, less what the room lacks for the two.</summary>
    private double FieldWidth(double room) => Math.Max(0, Math.Min(Most, room - Gap - after.DesiredSize.Width));
}
