using System.Globalization;

namespace Passenger.Workspace.Gherkin;

/// <summary>How Reformat Code lays out a .feature file, as Reqnroll's editors do: an indentation unit, which parts indent
/// under the part above them, a table's cell padding and whether its numbers stand to the right, each from the file's
/// .editorconfig <c>gherkin_*</c> keys where it says them, else Reqnroll's defaults.</summary>
public sealed record GherkinFormatOptions(string Indent, bool IndentFeatureChildren, bool IndentRuleChildren, bool IndentSteps, bool IndentAndSteps,
    bool IndentDataTable, bool IndentDocString, bool IndentExamples, bool IndentExamplesTable, int CellPadding, bool RightAlignNumbers)
{
    public static readonly GherkinFormatOptions Default = new("    ", false, false, true, false, true, true, false, true, 1, true);

    /// <summary>The .editorconfig keys read.</summary>
    public static IReadOnlyList<string> EditorConfigKeys { get; } =
    [
        "gherkin_indent_feature_children", "gherkin_indent_rule_children", "gherkin_indent_steps", "gherkin_indent_and_steps", "gherkin_indent_datatable",
        "gherkin_indent_docstring", "gherkin_indent_examples", "gherkin_indent_examples_table", "gherkin_table_cell_padding_size",
        "gherkin_table_cell_right_align_numeric_content",
    ];

    /// <summary>The options a file's .editorconfig says (<paramref name="config"/>, a key's value or null), over the defaults,
    /// its indentation <paramref name="indent"/> unless it says tabs; a value that does not read as its key's is passed over.</summary>
    public static GherkinFormatOptions Of(Func<string, string?> config, string indent)
    {
        bool Flag(string key, bool fallback) => bool.TryParse(config(key), out bool said) ? said : fallback;
        GherkinFormatOptions d = Default;
        return new GherkinFormatOptions(
            string.Equals(config("indent_style"), "tab", StringComparison.OrdinalIgnoreCase) ? "\t" : indent,
            Flag("gherkin_indent_feature_children", d.IndentFeatureChildren),
            Flag("gherkin_indent_rule_children", d.IndentRuleChildren),
            Flag("gherkin_indent_steps", d.IndentSteps),
            Flag("gherkin_indent_and_steps", d.IndentAndSteps),
            Flag("gherkin_indent_datatable", d.IndentDataTable),
            Flag("gherkin_indent_docstring", d.IndentDocString),
            Flag("gherkin_indent_examples", d.IndentExamples),
            Flag("gherkin_indent_examples_table", d.IndentExamplesTable),
            int.TryParse(config("gherkin_table_cell_padding_size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int padding) && padding is >= 0 and <= 8 ? padding : d.CellPadding,
            Flag("gherkin_table_cell_right_align_numeric_content", d.RightAlignNumbers));
    }
}

/// <summary>A .feature file laid out (<see cref="GherkinFormatOptions"/>): each header, tag, step, table row and doc string
/// at its level, each table's columns aligned, a doc string's lines kept as they stand within it, a comment at the level of
/// what follows it, a step's keyword and text one space apart, descriptions and lines Gherkin reads as nothing left as
/// written, every line's trailing whitespace gone but a doc string's own. Lines and their breaks stay where they are. Pure.</summary>
public static class GherkinFormatter
{
    public static string Format(string text, GherkinFormatOptions options, string language)
    {
        FeatureReading reading = FeatureReading.Read(text, language);
        string[] lines = text.Split('\n');
        string?[] laid = new string?[lines.Length];
        int?[] levels = new int?[lines.Length];
        int child = options.IndentFeatureChildren ? 1 : 0;
        if (reading.Feature is { } feature)
        {
            levels[feature.Line] = 0;
        }
        foreach (FeatureHeader rule in reading.Rules)
        {
            levels[rule.Line] = child;
        }
        foreach (FeatureScenario scenario in reading.Scenarios)
        {
            int at = scenario.Rule is null ? child : child + (options.IndentRuleChildren ? 1 : 0);
            levels[scenario.Line] = at;
            foreach (FeatureStep step in scenario.Steps)
            {
                int stepAt = at + (options.IndentSteps ? 1 : 0) + (options.IndentAndSteps && step.KeywordKind == StepKeywordKind.Conjunction ? 1 : 0);
                levels[step.Line] = stepAt;
                laid[step.Line] = Indent(stepAt) + step.Keyword + step.Text;
                if (step.Table is { } table)
                {
                    Table(table.Rows, stepAt + (options.IndentDataTable ? 1 : 0));
                }
                if (step.DocString is { } doc)
                {
                    DocString(doc, stepAt + (options.IndentDocString ? 1 : 0));
                }
            }
            foreach (FeatureExamples examples in scenario.Examples)
            {
                int examplesAt = at + (options.IndentExamples ? 1 : 0);
                levels[examples.Line] = examplesAt;
                if (examples.Header is { } header)
                {
                    Table([header, .. examples.Rows], examplesAt + (options.IndentExamplesTable ? 1 : 0));
                }
            }
        }
        for (int line = lines.Length - 1, next = 0; line >= 0; line--)
        {
            FeatureLineKind kind = line < reading.Lines.Count ? reading.Lines[line] : FeatureLineKind.Empty;
            if (levels[line] is { } level && laid[line] is null)
            {
                laid[line] = Indent(level) + lines[line].Trim();
                next = level;
            }
            else if (laid[line] is not null && levels[line] is { } placed)
            {
                next = placed;
            }
            else if (kind is FeatureLineKind.Tags or FeatureLineKind.Comment)
            {
                laid[line] = Indent(next) + lines[line].Trim();
            }
            else if (kind == FeatureLineKind.Empty)
            {
                laid[line] = string.Empty;
            }
            else if (kind == FeatureLineKind.Language)
            {
                laid[line] = lines[line].Trim();
            }
        }
        for (int line = 0; line < lines.Length; line++)
        {
            laid[line] ??= lines[line].TrimEnd('\r').TrimEnd() + (lines[line].EndsWith('\r') ? "\r" : string.Empty);
            if (lines[line].EndsWith('\r') && !laid[line]!.EndsWith('\r'))
            {
                laid[line] += "\r";
            }
        }
        return string.Join('\n', laid);

        string Indent(int level) => string.Concat(Enumerable.Repeat(options.Indent, level));

        void Table(IReadOnlyList<FeatureRow> rows, int level)
        {
            int columns = rows.Max(r => r.Cells.Count);
            int[] widths = new int[columns];
            foreach (FeatureRow row in rows)
            {
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    widths[c] = Math.Max(widths[c], Written(row, c).Length);
                }
            }
            string pad = new(' ', options.CellPadding);
            foreach (FeatureRow row in rows)
            {
                System.Text.StringBuilder laidRow = new(Indent(level));
                laidRow.Append('|');
                for (int c = 0; c < row.Cells.Count; c++)
                {
                    string cell = Written(row, c);
                    bool right = options.RightAlignNumbers && decimal.TryParse(row.Cells[c].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
                    laidRow.Append(pad).Append(right ? cell.PadLeft(widths[c]) : cell.PadRight(widths[c])).Append(pad).Append('|');
                }
                levels[row.Line] = level;
                laid[row.Line] = laidRow.ToString();
            }
        }

        string Written(FeatureRow row, int cell) => lines[row.Line].TrimEnd('\r').Substring(row.Cells[cell].Column, row.Cells[cell].Length);

        void DocString(FeatureDocString doc, int level)
        {
            string opened = lines[doc.Line].TrimEnd('\r');
            int was = opened.Length - opened.TrimStart().Length;
            for (int line = doc.Line; line <= doc.EndLine && line < lines.Length; line++)
            {
                string written = lines[line].TrimEnd('\r');
                int indent = written.Length - written.TrimStart().Length;
                bool delimiter = line == doc.Line || line == doc.EndLine;
                // A content line's trailing whitespace is the doc string's value; only its delimiters' goes.
                string rest = written.Trim().Length == 0 ? string.Empty : delimiter ? written.Trim() : written[Math.Min(was, indent)..];
                levels[line] = level;
                laid[line] = rest.Length == 0 ? string.Empty : Indent(level) + rest;
            }
        }
    }
}
