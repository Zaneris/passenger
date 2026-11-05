using Avalonia;

namespace Passenger.Editor;

/// <summary>Where the rows on screen drew each line-end annotation, so a pointer resting on one is told which.</summary>
public sealed partial class CodeEditor
{
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
