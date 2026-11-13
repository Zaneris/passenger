using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.Settings;

/// <summary>An option's row in Settings: its label, what edits it, and a note under the editor. On a page with room for the
/// labels' column beside the widest editor, the label stands at the left with the editor and note beside it; on a
/// narrower page, as every row of it does, the label stands above the editor and the note under it, each as wide as the
/// page lets it be, and a lone check box before its label. An editor that stretches up to a most width (a list, a box of
/// several lines) is given that width where there is room and the page's where there is not; any other takes its own.</summary>
public sealed class OptionRow : Panel
{
    /// <summary>The labels' column beside the editors.</summary>
    public const double LabelWidth = 220;

    /// <summary>The widest an editor stands beside its label: a list's, a box of several lines'.</summary>
    public const double EditorWidth = 420;

    /// <summary>Between the label, the editor and the note, where they stand one over another.</summary>
    private const double Gap = 4;

    /// <summary>Between a check box and the label after it.</summary>
    private const double BoxGap = 8;

    private readonly double labelTop;

    /// <param name="labelTop">Where the label stands beside a tall editor, level with its first line; below zero, centred
    /// on the editor.</param>
    public OptionRow(TextBlock label, Control editor, TextBlock? note, double labelTop)
    {
        Label = label;
        Editor = editor;
        Note = note;
        this.labelTop = labelTop;
        Children.Add(label);
        Children.Add(editor);
        if (note is not null)
        {
            Children.Add(note);
        }
    }

    public TextBlock Label { get; }

    public Control Editor { get; }

    public TextBlock? Note { get; }

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
            Note?.Measure(new Size(width - LabelWidth, double.PositiveInfinity));
            return new Size(LabelWidth + Math.Max(Editor.DesiredSize.Width, Note?.DesiredSize.Width ?? 0), Beside() + Below());
        }
        Editor.Measure(new Size(width, double.PositiveInfinity));
        if (Boxed)
        {
            double lead = Editor.DesiredSize.Width + BoxGap;
            Label.Measure(new Size(Math.Max(0, width - lead), double.PositiveInfinity));
            Note?.Measure(new Size(Math.Max(0, width - lead), double.PositiveInfinity));
            return new Size(lead + Math.Max(Label.DesiredSize.Width, Note?.DesiredSize.Width ?? 0),
                Math.Max(Label.DesiredSize.Height, Editor.DesiredSize.Height) + Below());
        }
        Label.Measure(new Size(width, double.PositiveInfinity));
        Note?.Measure(new Size(width, double.PositiveInfinity));
        return new Size(Math.Max(Label.DesiredSize.Width, Math.Max(Editor.DesiredSize.Width, Note?.DesiredSize.Width ?? 0)),
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
            Note?.Arrange(new Rect(lead, box + Gap, Math.Max(0, width - lead), Note.DesiredSize.Height));
        }
        else if (IsStacked)
        {
            Label.Arrange(new Rect(0, 0, width, Label.DesiredSize.Height));
            double y = Label.DesiredSize.Height + Gap;
            Editor.Arrange(new Rect(0, y, Across(width), Editor.DesiredSize.Height));
            Note?.Arrange(new Rect(0, y + Editor.DesiredSize.Height + Gap, width, Note.DesiredSize.Height));
        }
        else
        {
            double line = Beside();
            double top = Math.Max(0, labelTop);
            Label.Arrange(new Rect(0, top, LabelWidth, line - top));
            Editor.Arrange(new Rect(LabelWidth, 0, Across(width - LabelWidth), line));
            Note?.Arrange(new Rect(LabelWidth, line + Gap, width - LabelWidth, Note.DesiredSize.Height));
        }
        return finalSize;
    }

    /// <summary>The height of the label and editor side by side.</summary>
    private double Beside() => Math.Max(Label.DesiredSize.Height + Math.Max(0, labelTop), Editor.DesiredSize.Height);

    private double Below() => Note is null ? 0 : Gap + Note.DesiredSize.Height;

    /// <summary>The editor's width in the room it has: up to its most width where it stretches to one, else its own.</summary>
    private double Across(double room) =>
        Editor.HorizontalAlignment == HorizontalAlignment.Stretch && double.IsFinite(Editor.MaxWidth)
            ? Math.Min(room, Editor.MaxWidth + Editor.Margin.Left + Editor.Margin.Right)
            : Math.Min(room, Editor.DesiredSize.Width);
}

/// <summary>A field and what acts on it after it (a drop-down and its Add from File…): the field <see cref="Most"/> wide
/// where there is room, narrower where not, so the two keep to the room they have.</summary>
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
