using Avalonia;

namespace Passenger.Editor;

/// <summary>Where the last frame drew each line-end annotation, so a pointer resting on one is told which.</summary>
public sealed partial class CodeEditor
{
    /// <summary>The annotations the last frame drew, each with its box in the control: cleared and filled again each frame,
    /// keeping its room.</summary>
    private readonly List<(LineAnnotation Annotation, Rect Box)> drawnAnnotations = [];

    /// <summary>The annotation drawn under a point, if any.</summary>
    private LineAnnotation? AnnotationAt(Point at)
    {
        foreach ((LineAnnotation annotation, Rect box) in drawnAnnotations)
        {
            if (box.Contains(at))
            {
                return annotation;
            }
        }
        return null;
    }

    /// <summary>Where the last frame drew an annotation of a line (from zero) that says <paramref name="text"/>, in the
    /// control; null where it drew none.</summary>
    public Rect? AnnotationBox(int line, string text)
    {
        foreach ((LineAnnotation annotation, Rect box) in drawnAnnotations)
        {
            if (annotation.Line == line && string.Equals(annotation.Text, text, StringComparison.Ordinal))
            {
                return box;
            }
        }
        return null;
    }
}
