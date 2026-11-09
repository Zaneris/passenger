using Passenger.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>How Reformat Code lays out a GDScript beyond its indentation (the file's .editorconfig's, else Godot's tabs):
/// Settings › Code Style › GDScript, each at the style guide's own and said over by a file's .editorconfig
/// (<c>max_line_length</c>, <c>gdscript_blank_lines_around_functions</c>, <c>gdscript_trailing_comma</c>).</summary>
public sealed record GDScriptFormatSettings
{
    /// <summary>Line length: "Keep individual lines of code under 100 characters".</summary>
    public const int GuideLineLength = 100;

    public const int ShortestLine = 40;
    public const int LongestLine = 400;

    /// <summary>Whether a line past <see cref="LineLength"/> is wrapped inside its brackets.</summary>
    public bool WrapLongLines { get; init; } = true;

    public int LineLength { get; init; } = GuideLineLength;

    /// <summary>Blank lines around a function or class at the script's top level (Blank lines: two), at most these in an
    /// inner class.</summary>
    public int BlankLinesAroundFunctions { get; init; } = 2;

    /// <summary>Trailing comma: a multi-line array, dictionary or enum ends its last line with a comma.</summary>
    public bool TrailingCommas { get; init; } = true;

    /// <summary>The .editorconfig keys read.</summary>
    public static IReadOnlyList<string> EditorConfigKeys { get; } =
        ["max_line_length", "gdscript_blank_lines_around_functions", "gdscript_trailing_comma"];

    /// <summary>Each value brought into what it allows.</summary>
    public GDScriptFormatSettings Clamped() => this with
    {
        LineLength = Math.Clamp(LineLength, ShortestLine, LongestLine),
        BlankLinesAroundFunctions = Math.Clamp(BlankLinesAroundFunctions, 1, 2),
    };

    /// <summary>These settings with what a file's .editorconfig says over them (<c>max_line_length = off</c> turns wrapping
    /// off); a value that does not read as its option's is passed over, as editorconfig's are.</summary>
    public GDScriptFormatSettings Over(Func<string, string?> editorConfig)
    {
        GDScriptFormatSettings read = this;
        string? length = editorConfig("max_line_length");
        if (EditorConfig.Word(length) == "off")
        {
            read = read with { WrapLongLines = false };
        }
        else if (EditorConfig.Number(length) is int columns and >= ShortestLine and <= LongestLine)
        {
            read = read with { WrapLongLines = true, LineLength = columns };
        }
        if (EditorConfig.Number(editorConfig("gdscript_blank_lines_around_functions")) is int blanks and >= 1 and <= 2)
        {
            read = read with { BlankLinesAroundFunctions = blanks };
        }
        if (EditorConfig.Flag(editorConfig("gdscript_trailing_comma")) is bool commas)
        {
            read = read with { TrailingCommas = commas };
        }
        return read;
    }

    /// <summary>The formatter's options: these, with the file's indentation.</summary>
    public GDScriptFormatOptions Options(bool useTabs, int indentSize, int tabWidth) =>
        new(useTabs, indentSize, tabWidth, WrapLongLines ? LineLength : 0, BlankLinesAroundFunctions, TrailingCommas);
}
