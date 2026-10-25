namespace Passenger.Text;

/// <summary>How a file's lines end on disk. The editor's lines end at '\n', so a CR file's are held as '\n' and written
/// as '\r'.</summary>
public enum LineSeparator
{
    LF,
    CRLF,
    CR,
}

/// <summary>Each line separator's break and the words for it.</summary>
public static class LineSeparators
{
    /// <summary>The Line Separators menu, in its order.</summary>
    public static IReadOnlyList<LineSeparator> All { get; } = [LineSeparator.CRLF, LineSeparator.LF, LineSeparator.CR];

    /// <summary>The break as the editor holds it, which Enter inserts: a CR file's is '\n'.</summary>
    public static string Held(this LineSeparator separator) => separator == LineSeparator.CRLF ? "\r\n" : "\n";

    /// <summary>The break as the file has it on disk.</summary>
    public static string Written(this LineSeparator separator) => separator switch
    {
        LineSeparator.CRLF => "\r\n",
        LineSeparator.CR => "\r",
        _ => "\n",
    };

    /// <summary>The break escaped, as the tip and menu show it: \r\n.</summary>
    public static string Escaped(this LineSeparator separator) => separator switch
    {
        LineSeparator.CRLF => @"\r\n",
        LineSeparator.CR => @"\r",
        _ => @"\n",
    };

    /// <summary>Its menu item: "LF - Unix and macOS (\n)".</summary>
    public static string Title(this LineSeparator separator) => separator switch
    {
        LineSeparator.CRLF => $"CRLF - Windows ({separator.Escaped()})",
        LineSeparator.CR => $"CR - Classic Mac OS ({separator.Escaped()})",
        _ => $"LF - Unix and macOS ({separator.Escaped()})",
    };

    /// <summary>Its value in an .editorconfig's <c>end_of_line</c>.</summary>
    public static string Value(this LineSeparator separator) => separator.ToString().ToLowerInvariant();
}
