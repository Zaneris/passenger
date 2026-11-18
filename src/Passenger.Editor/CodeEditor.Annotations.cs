using Avalonia;

namespace Passenger.Editor;

/// <summary>Where the rows on screen drew each line-end annotation, so a pointer resting on one is told which.</summary>
public sealed partial class CodeEditor
{
    /// <summary>A press of the left button on a <see cref="LineAnnotationLook.Vision"/> hint, which the press is then taken by
    /// (no caret goes there): the hint pressed.</summary>
    public event EventHandler<LineAnnotation>? AnnotationPressed;

    /// <summary>The hint to press under a point, if any: a vision annotation, where something hears of its press.</summary>
    private LineAnnotation? PressedAnnotation(Point at) =>
        AnnotationPressed is not null && AnnotationAt(at) is { Look: LineAnnotationLook.Vision } hint ? hint : null;

    /// <summary>The annotation drawn under a point, if any.</summary>
    private LineAnnotation? AnnotationAt(Point at)
    {
        Point inRows = at + new Vector(0, scroll.Y);
        foreach (RowSlot slot in slots)
        {
            if (slot.Span is null)
            {
                continue;
            }
            foreach ((LineAnnotation annotation, Rect box) in slot.Annotations)
            {
                if (box.Contains(inRows))
                {
                    return annotation;
                }
            }
        }
        return null;
    }

    /// <summary>Where a row on screen drew an annotation of a line (from zero) that says <paramref name="text"/>, in the
    /// control; null where none drew it.</summary>
    public Rect? AnnotationBox(int line, string text)
    {
        foreach (RowSlot slot in slots)
        {
            if (slot.Span is null)
            {
                continue;
            }
            foreach ((LineAnnotation annotation, Rect box) in slot.Annotations)
            {
                if (annotation.Line == line && string.Equals(annotation.Text, text, StringComparison.Ordinal))
                {
                    return box.Translate(new Vector(0, -scroll.Y));
                }
            }
        }
        return null;
    }
}
