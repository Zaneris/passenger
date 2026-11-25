using Passenger.Core;
using Passenger.Text;

namespace Passenger.Languages.WinForms;

/// <summary>A design's change (<see cref="DesignerChange"/>) written into the designer file it was read from, touching nothing
/// else: each statement made on a line of its own after (or before) the one it is placed by, indented as that one is, a new
/// component's section under its comment as the designer writes it; a statement taken out with its line, its section's
/// comment with it once nothing of that section is left; one written again in place; a field declared after the field it is
/// placed by, or, in a class declaring no component's, past its <c>#endregion</c> after a blank line (else before its
/// closing brace). Lines break as the file breaks them.</summary>
public static class DesignerWriter
{
    /// <summary>The edits that write the changes into the text the source was read from; none where a change names a
    /// statement or field the source does not have.</summary>
    public static IReadOnlyList<TextEdit> Edits(string text, DesignerSource source, IReadOnlyList<DesignerChange> changes)
    {
        DesignerLayout layout = source.Layout;
        string eol = Eol(text, layout.BodyStart);
        List<(int Start, int End, string Text)> made = [];
        HashSet<int> removed = [.. changes.Where(c => c.Kind == DesignerChangeKinds.Remove).Select(c => c.At)];
        if (changes.Any(c => c.Kind is DesignerChangeKinds.Remove or DesignerChangeKinds.Replace && (c.At < 0 || c.At >= layout.Statements.Count)
            || c.Kind == DesignerChangeKinds.Insert && (c.At < -1 || c.At >= layout.Statements.Count)
            || c.Kind is DesignerChangeKinds.Declare or DesignerChangeKinds.Undeclare && (c.At < (c.Kind == DesignerChangeKinds.Declare ? -1 : 0) || c.At >= layout.Fields.Count)))
        {
            return [];
        }
        foreach (DesignerChange change in changes)
        {
            switch (change.Kind)
            {
                case DesignerChangeKinds.Insert:
                    made.Add(Inserted(text, layout, change, eol));
                    break;
                case DesignerChangeKinds.Replace:
                    (int start, int end) = layout.Statements[change.At];
                    made.Add((start, end, Indented(change.Text ?? string.Empty, TextNavigation.IndentOf(text, start), eol)));
                    break;
                case DesignerChangeKinds.Declare:
                    made.Add(Declared(text, layout, change, eol));
                    break;
                case DesignerChangeKinds.Undeclare when layout.Fields[change.At].Alone:
                    made.Add(Undeclared(text, layout.Fields[change.At]));
                    break;
            }
        }
        made.AddRange(Removed(text, layout, removed));
        return Merged(made);
    }

    /// <summary>The line break the method's lines end with.</summary>
    private static string Eol(string text, int from)
    {
        int end = text.IndexOf('\n', Math.Clamp(from, 0, text.Length));
        return end > 0 && text[end - 1] == '\r' ? "\r\n" : "\n";
    }

    /// <summary>A text of lines broken with '\n' as the file writes it: its later lines indented as its first one is.</summary>
    private static string Indented(string written, string indent, string eol) =>
        string.Join(eol, written.Split('\n').Select((line, i) => i == 0 || line.Length == 0 ? line : indent + line));

    private static (int, int, string) Inserted(string text, DesignerLayout layout, DesignerChange change, string eol)
    {
        int at;
        string indent;
        if (change.At >= 0)
        {
            (int start, int end) = layout.Statements[change.At];
            at = change.Before ? TextNavigation.LineStart(text, start) : TextNavigation.NextLineStart(text, end);
            indent = TextNavigation.IndentOf(text, start);
        }
        else if (layout.Statements.Count > 0)
        {
            at = TextNavigation.LineStart(text, layout.Statements[0].Start);
            indent = TextNavigation.IndentOf(text, layout.Statements[0].Start);
        }
        else
        {
            at = TextNavigation.NextLineStart(text, layout.BodyStart);
            indent = TextNavigation.IndentOf(text, layout.BodyStart) + "    ";
        }
        string header = change.Header is { } section ? $"{indent}// {eol}{indent}// {section}{eol}{indent}// {eol}" : string.Empty;
        return (at, at, header + indent + Indented(change.Text ?? string.Empty, indent, eol) + eol);
    }

    private static (int, int, string) Declared(string text, DesignerLayout layout, DesignerChange change, string eol)
    {
        if (change.At >= 0)
        {
            (int start, int end, _) = layout.Fields[change.At];
            int at = TextNavigation.NextLineStart(text, end);
            return (at, at, TextNavigation.IndentOf(text, start) + change.Text + eol);
        }
        if (layout.FieldsEnd < text.Length && text[layout.FieldsEnd] == '}')
        {
            int line = TextNavigation.LineStart(text, layout.FieldsEnd);
            return (line, line, TextNavigation.IndentOf(text, layout.FieldsEnd) + "    " + change.Text + eol);
        }
        // Past an #endregion: a blank line, then the field, as the designer writes the first of them.
        return (layout.FieldsEnd, layout.FieldsEnd, eol + eol + TextNavigation.IndentOf(text, layout.FieldsEnd) + change.Text);
    }

    /// <summary>A field's line taken out, with the blank line before it where it stood alone between that and the class's
    /// closing brace.</summary>
    private static (int, int, string) Undeclared(string text, (int Start, int End, bool Alone) field)
    {
        int start = TextNavigation.LineStart(text, field.Start);
        int end = TextNavigation.NextLineStart(text, field.End);
        int before = TextNavigation.LineStart(text, Math.Max(0, start - 1));
        if (start > 0 && Blank(text, before, start) && text.AsSpan(end).TrimStart().StartsWith("}"))
        {
            start = before;
        }
        return (start, end, string.Empty);
    }

    /// <summary>The removed statements' lines, a run of them taking its section's comment above it where the section ends with
    /// the run (the next statement heads a section of its own, or none follows).</summary>
    private static IEnumerable<(int, int, string)> Removed(string text, DesignerLayout layout, HashSet<int> removed)
    {
        foreach (int first in removed.Order())
        {
            if (removed.Contains(first - 1))
            {
                continue;
            }
            int last = first;
            while (removed.Contains(last + 1))
            {
                last++;
            }
            int start = TextNavigation.LineStart(text, layout.Statements[first].Start);
            int end = TextNavigation.NextLineStart(text, layout.Statements[last].End);
            int after = last + 1 < layout.Statements.Count ? TextNavigation.LineStart(text, layout.Statements[last + 1].Start) : TextNavigation.LineStart(text, layout.BodyEnd);
            if (Header(text, start) is int header && (last + 1 >= layout.Statements.Count || Header(text, after) is not null))
            {
                start = header;
            }
            yield return (start, end, string.Empty);
        }
    }

    /// <summary>Where a section's comment starts when it stands just above a line: three comment lines, the middle one a name.</summary>
    private static int? Header(string text, int line)
    {
        int third = TextNavigation.LineStart(text, Math.Max(0, line - 1));
        int second = TextNavigation.LineStart(text, Math.Max(0, third - 1));
        int first = TextNavigation.LineStart(text, Math.Max(0, second - 1));
        return line > 0 && Comment(text, third) is "" && Comment(text, second) is { Length: > 0 } && Comment(text, first) is "" ? first : null;
    }

    /// <summary>A line's comment, trimmed, where the line is a comment alone; null for any other line.</summary>
    private static string? Comment(string text, int line)
    {
        string written = text[line..TextNavigation.LineEnd(text, line)].Trim();
        return written.StartsWith("//", StringComparison.Ordinal) ? written[2..].Trim() : null;
    }

    private static bool Blank(string text, int start, int end) => string.IsNullOrWhiteSpace(text[start..end]);

    /// <summary>The edits in order, those that touch made one, so no two overlap or meet.</summary>
    private static List<TextEdit> Merged(List<(int Start, int End, string Text)> made)
    {
        List<TextEdit> edits = [];
        (int Start, int End, string Text)? current = null;
        foreach ((int start, int end, string written) in made.OrderBy(m => m.Start).ThenBy(m => m.End))
        {
            if (current is { } open && start <= open.End)
            {
                current = (open.Start, Math.Max(open.End, end), open.Text + written);
                continue;
            }
            if (current is { } done)
            {
                edits.Add(new TextEdit(done.Start, done.End - done.Start, done.Text));
            }
            current = (start, end, written);
        }
        if (current is { } final)
        {
            edits.Add(new TextEdit(final.Start, final.End - final.Start, final.Text));
        }
        return edits;
    }
}
