using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Passenger.Workspace.Gherkin;

/// <summary>A missing step's definition, as C# text (its attribute and method, with no indentation and a line break after
/// each line), and its method's name.</summary>
public sealed record StepSnippet(string Code, string Method, StepKind Kind);

/// <summary>A step definition written for a step no definition binds, as Reqnroll's editors write one: the attribute of its
/// kind with a Cucumber Expression (<c>{int}</c>, <c>{decimal}</c>, <c>{string}</c> for the numbers and quoted text in it)
/// or, as the project's skeleton style says, a regular expression in a verbatim string; an outline's placeholders made
/// parameters named for them and typed by their examples' values; a doc string's <c>string multilineText</c> and a table's
/// <c>DataTable dataTable</c> (SpecFlow's <c>Table table</c>) last; its method named for its keyword and words, async where
/// the style is; a body that says the step is pending.</summary>
public static partial class StepSnippets
{
    /// <summary>A step's snippet in its reading, in the binding culture's numbers, its method's name not one of <paramref name="taken"/>.</summary>
    public static StepSnippet Of(FeatureStep step, FeatureReading reading, SkeletonStyle style, bool specFlow, CultureInfo culture, IEnumerable<string> taken)
    {
        bool cucumber = style is SkeletonStyle.CucumberExpression or SkeletonStyle.AsyncCucumberExpression;
        bool async = style is SkeletonStyle.AsyncCucumberExpression or SkeletonStyle.AsyncRegex;
        List<Part> parts = Parts(step, culture);
        StringBuilder expression = new();
        List<string> words = [];
        List<(string Type, string Name)> parameters = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Part part in parts)
        {
            if (part.Literal is { } literal)
            {
                expression.Append(cucumber ? EscapeCucumber(literal) : RegexSyntax().Replace(literal, m => "\\" + m.Value));
                words.AddRange(WordSplit().Split(literal).Where(w => w.Length > 0));
                continue;
            }
            expression.Append(cucumber ? part.Cucumber : part.Regex);
            string name = part.Name ?? $"p{parameters.Count}";
            while (!names.Add(name))
            {
                name += parameters.Count;
            }
            parameters.Add((part.Type, name));
        }
        if (step.DocString is not null)
        {
            parameters.Add(("string", "multilineText"));
        }
        if (step.Table is not null)
        {
            parameters.Add(specFlow ? ("Table", "table") : ("DataTable", "dataTable"));
        }
        string method = step.Kind + string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        method = Unique(async ? method + "Async" : method, taken);
        string attribute = cucumber ? $"\"{expression.ToString().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : $"@\"{expression.ToString().Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        string returns = async ? "async Task" : "void";
        string code = $"[{step.Kind}({attribute})]\npublic {returns} {method}({string.Join(", ", parameters.Select(p => $"{p.Type} {p.Name}"))})\n{{\n    throw new PendingStepException();\n}}\n";
        return new StepSnippet(code, method, step.Kind);
    }

    /// <summary>A name not among those taken: itself, else with the first number after it that makes it new.</summary>
    private static string Unique(string name, IEnumerable<string> taken)
    {
        HashSet<string> used = new(taken, StringComparer.Ordinal);
        if (!used.Contains(name))
        {
            return name;
        }
        int number = 2;
        while (used.Contains(name + number))
        {
            number++;
        }
        return name + number;
    }

    /// <summary>A literal part of a Cucumber Expression: what Cucumber reads as syntax escaped.</summary>
    private static string EscapeCucumber(string text) => CucumberSyntax().Replace(text, m => "\\" + m.Value);

    /// <summary>A step's text as its literal parts and its arguments, left to right, the longer of two that overlap kept.</summary>
    private static List<Part> Parts(FeatureStep step, CultureInfo culture)
    {
        string text = step.Text;
        List<(int Start, int Length, Part Part)> found = [];
        foreach (FeaturePlaceholder placeholder in step.Placeholders)
        {
            found.Add((placeholder.Column - step.TextColumn, placeholder.Length, PlaceholderPart(placeholder.Name, step, culture)));
        }
        foreach (Match quoted in Quoted().Matches(text))
        {
            string inner = quoted.Groups["d"].Success ? quoted.Groups["d"].Value : quoted.Groups["s"].Value;
            string regex = quoted.Value[0] == '"' ? "\"([^\"]*)\"" : "'([^']*)'";
            found.Add((quoted.Index, quoted.Length, new Part(null, "{string}", regex, "string", OneWord().IsMatch(inner) ? char.ToLowerInvariant(inner[0]) + inner[1..] : null)));
        }
        string separator = Regex.Escape(culture.NumberFormat.NumberDecimalSeparator);
        foreach (Match number in Regex.Matches(text, $@"(?<![\w.,])-?\d+(?:{separator}\d+)?(?![\w])"))
        {
            bool whole = !number.Value.Contains(culture.NumberFormat.NumberDecimalSeparator, StringComparison.Ordinal);
            found.Add((number.Index, number.Length, whole ? new Part(null, "{int}", "(.*)", "int", null) : new Part(null, "{decimal}", "(.*)", "decimal", null)));
        }
        List<Part> parts = [];
        int at = 0;
        foreach ((int start, int length, Part part) in found.OrderBy(f => f.Start).ThenByDescending(f => f.Length))
        {
            if (start < at)
            {
                continue;
            }
            if (start > at)
            {
                parts.Add(new Part(text[at..start], "", "", "", null));
            }
            parts.Add(part);
            at = start + length;
        }
        if (at < text.Length)
        {
            parts.Add(new Part(text[at..], "", "", "", null));
        }
        return parts;
    }

    /// <summary>An outline's placeholder as a parameter named for it, typed by its examples' values: whole numbers an int,
    /// numbers a decimal, single words a word, anything else any text.</summary>
    private static Part PlaceholderPart(string name, FeatureStep step, CultureInfo culture)
    {
        List<string> values = [];
        foreach (FeatureExamples examples in step.Scenario.Examples)
        {
            int column = examples.Header?.Cells.ToList().FindIndex(c => c.Value == name) ?? -1;
            if (column >= 0)
            {
                values.AddRange(examples.Rows.Where(r => column < r.Cells.Count).Select(r => r.Cells[column].Value));
            }
        }
        string parameter = Identifier(name);
        if (values.Count > 0 && values.All(v => long.TryParse(v, NumberStyles.Integer, culture, out _)))
        {
            return new Part(null, "{int}", "(.*)", "int", parameter);
        }
        if (values.Count > 0 && values.All(v => decimal.TryParse(v, NumberStyles.Number, culture, out _)))
        {
            return new Part(null, "{decimal}", "(.*)", "decimal", parameter);
        }
        if (values.Count > 0 && values.All(v => v.Length > 0 && !v.Any(char.IsWhiteSpace)))
        {
            return new Part(null, "{word}", "(.*)", "string", parameter);
        }
        return new Part(null, "{}", "(.*)", "string", parameter);
    }

    /// <summary>A placeholder's name as a C# parameter's: its letters and digits, first lower case.</summary>
    private static string Identifier(string name)
    {
        string[] words = WordSplit().Split(name).Where(w => w.Length > 0).ToArray();
        if (words.Length == 0)
        {
            return "value";
        }
        string joined = words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return char.IsDigit(joined[0]) ? "p" + joined : joined;
    }

    /// <summary>A literal part, or an argument: its Cucumber Expression, its regular expression, its C# type and the name a
    /// parameter takes from it (null for p0, p1…).</summary>
    private sealed record Part(string? Literal, string Cucumber, string Regex, string Type, string? Name);

    [GeneratedRegex("\"(?<d>[^\"]*)\"|'(?<s>[^']*)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^\p{L}+$")]
    private static partial Regex OneWord();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex WordSplit();

    [GeneratedRegex(@"[\\{(/]")]
    private static partial Regex CucumberSyntax();

    /// <summary>What a regular expression reads as syntax, which a literal part escapes; its spaces stay as they are.</summary>
    [GeneratedRegex(@"[\\.$^{\[(|)*+?]")]
    private static partial Regex RegexSyntax();
}
