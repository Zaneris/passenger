using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.Core;

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
                words.AddRange(NameWords(literal));
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
        string attribute = cucumber ? CSharpLiterals.Quoted(expression.ToString()) : CSharpLiterals.Written(expression.ToString(), "@");
        string returns = async ? "async Task" : "void";
        string code = $"[{step.Kind}({attribute})]\npublic {returns} {method}({string.Join(", ", parameters.Select(p => $"{p.Type} {Declared(p.Name)}"))})\n{{\n    throw new PendingStepException();\n}}\n";
        return new StepSnippet(code, method, step.Kind);
    }

    /// <summary>The binding class written for a feature's steps where none binds them, named for its title: its words, parted
    /// at spaces, '-', '_', '.' and '/', each begun in capitals and kept to a name's characters (<see cref="InName"/>),
    /// '_' before one no C# name begins with, then StepDefinitions.</summary>
    public static string ClassFor(string title)
    {
        string joined = string.Concat(title.Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(InName)])).Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return (joined.Length == 0 ? "Feature" : CSharpNames.Starts(joined[0]) ? joined : "_" + joined) + "StepDefinitions";
    }

    /// <summary>Whether a character belongs to a word a name is made of: one that carries a C# name on, but connector
    /// punctuation, which parts words, and a format character, which C# leaves out of a name.</summary>
    private static bool InName(char c) =>
        CSharpNames.Continues(c) && CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format);

    /// <summary>A text's words as a name takes them: its runs of a name's characters, parted at anything else but a format
    /// character, which is left out.</summary>
    private static List<string> NameWords(string text)
    {
        List<string> words = [];
        StringBuilder word = new();
        foreach (char c in text)
        {
            if (InName(c))
            {
                word.Append(c);
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format && word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }
        if (word.Length > 0)
        {
            words.Add(word.ToString());
        }
        return words;
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

    /// <summary>A placeholder's name as a C# parameter's: its words (<see cref="NameWords"/>), first lower case.</summary>
    private static string Identifier(string name)
    {
        List<string> words = NameWords(name);
        if (words.Count == 0)
        {
            return "value";
        }
        string joined = words[0].ToLowerInvariant() + string.Concat(words.Skip(1).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return CSharpNames.Starts(joined[0]) ? joined : "p" + joined;
    }

    /// <summary>A parameter's name as its declaration writes it: one of C#'s reserved words verbatim (<c>@class</c>).</summary>
    private static string Declared(string name) => Keywords.Contains(name) ? "@" + name : name;

    /// <summary>C#'s reserved words, which a parameter named after one is written verbatim to take.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default",
        "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto",
        "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out",
        "override", "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc",
        "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>A literal part, or an argument: its Cucumber Expression, its regular expression, its C# type and the name a
    /// parameter takes from it (null for p0, p1…).</summary>
    private sealed record Part(string? Literal, string Cucumber, string Regex, string Type, string? Name);

    [GeneratedRegex("\"(?<d>[^\"]*)\"|'(?<s>[^']*)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^\p{L}+$")]
    private static partial Regex OneWord();

    [GeneratedRegex(@"[\\{(/]")]
    private static partial Regex CucumberSyntax();

    /// <summary>What a regular expression reads as syntax, which a literal part escapes; its spaces stay as they are.</summary>
    [GeneratedRegex(@"[\\.$^{\[(|)*+?]")]
    private static partial Regex RegexSyntax();
}
