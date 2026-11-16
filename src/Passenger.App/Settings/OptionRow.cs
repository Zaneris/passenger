using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Settings;

/// <summary>An option's row in Settings: its label, what edits it, a note under the editor, and under that what the open
/// folder's .editorconfig files set over it, where they do. On a page with room for the labels' column beside the widest
/// editor, the label stands at the left with the editor and the lines under it beside it; on a narrower page, as every row
/// of it does, the label stands above the editor, and a lone check box before its label. An editor that stretches up to a
/// most width (a list, a line of text, a number box) is given that width where there is room and the page's where there is
/// not; any other takes its own. The lines under it wrap where the widest editor ends, or at the page's edge before it.</summary>
public sealed class OptionRow : Panel
{
    /// <summary>The labels' column beside the editors.</summary>
    public const double LabelWidth = 220;

    /// <summary>The widest an editor stands: a list's, a line of text's, a box of several lines'; a note wraps where it ends.</summary>
    public const double EditorWidth = 420;

    /// <summary>The widest a number box stands: room for the longest number a setting takes, and its buttons.</summary>
    public const double NumberWidth = 140;

    /// <summary>Between the label, the editor and the note, where they stand one over another.</summary>
    private const double Gap = 4;

    /// <summary>Between a check box and the label after it.</summary>
    private const double BoxGap = 8;

    private readonly double labelTop;

    /// <param name="labelTop">Where the label stands beside a tall editor, level with its first line; below zero, centred
    /// on the editor.</param>
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
            return new Size(LabelWidth + Math.Max(Editor.DesiredSize.Width, UnderWidth()), Beside() + Below());
        }
        Editor.Measure(new Size(width, double.PositiveInfinity));
        if (Boxed)
        {
            double lead = Editor.DesiredSize.Width + BoxGap;
            Label.Measure(new Size(Math.Max(0, width - lead), double.PositiveInfinity));
            MeasureUnder(Math.Max(0, NoteRoom(width) - lead));
            return new Size(lead + Math.Max(Label.DesiredSize.Width, UnderWidth()),
                Math.Max(Label.DesiredSize.Height, Editor.DesiredSize.Height) + Below());
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
            double box = Math.Max(Label.DesiredSize.Height, Editor.DesiredSize.Height);
            Editor.Arrange(new Rect(0, 0, Editor.DesiredSize.Width, box));
            Label.Arrange(new Rect(lead, 0, Math.Max(0, width - lead), box));
            ArrangeUnder(lead, box, Math.Max(0, NoteRoom(width) - lead));
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
            double line = Beside();
            double top = Math.Max(0, labelTop);
            Label.Arrange(new Rect(0, top, LabelWidth, line - top));
            Editor.Arrange(new Rect(LabelWidth, 0, Across(width - LabelWidth), line));
            ArrangeUnder(LabelWidth, line, NoteRoom(width - LabelWidth));
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

    /// <summary>The height of the label and editor side by side.</summary>
    private double Beside() => Math.Max(Label.DesiredSize.Height + Math.Max(0, labelTop), Editor.DesiredSize.Height);

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

    private const double Gap = 8;

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
