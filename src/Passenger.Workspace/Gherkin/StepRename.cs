using System.Text;
using CucumberExpressions.Ast;
using CucumberExpressions.Parsing;

namespace Passenger.Workspace.Gherkin;

/// <summary>A step definition's expression renamed: the steps it binds take the new expression's words around their own
/// arguments, as Reqnroll's Rename Step does. Only an expression of words and parameters is renamed (a Cucumber
/// Expression's text and parameters, a regular expression's text, escapes and capturing groups), to one with the same
/// parameters in the same order; anything else is refused, saying why.</summary>
public sealed class StepRename
{
    private readonly IReadOnlyList<Part> parts;

    private StepRename(IReadOnlyList<Part> parts, string? refused)
    {
        this.parts = parts;
        Refused = refused;
    }

    /// <summary>Why it cannot be renamed; null where it can.</summary>
    public string? Refused { get; }

    public static StepRename Of(string old, string renamed, bool cucumber)
    {
        IReadOnlyList<Part>? before = PartsOf(old, cucumber);
        IReadOnlyList<Part>? after = PartsOf(renamed, cucumber);
        if (before is null || after is null)
        {
            return new StepRename([], "Only an expression of words and parameters can be renamed");
        }
        string[] was = [.. before.Where(p => p.Parameter).Select(p => p.Text)];
        string[] now = [.. after.Where(p => p.Parameter).Select(p => p.Text)];
        if (!was.SequenceEqual(now))
        {
            return new StepRename([], $"The new expression's parameters differ from the old one's: {string.Join(", ", now)} against {string.Join(", ", was)}");
        }
        return new StepRename(after, null);
    }

    /// <summary>A step's text rewritten: the new words around its arguments (each where the old expression matched it, with
    /// the quotes around one that has them); null where it is given fewer arguments than the expression's parameters.</summary>
    public string? Step(string text, IReadOnlyList<StepArgument> arguments)
    {
        StringBuilder written = new();
        int argument = 0;
        foreach (Part part in parts)
        {
            if (!part.Parameter)
            {
                written.Append(part.Text);
                continue;
            }
            if (argument >= arguments.Count)
            {
                return null;
            }
            StepArgument at = arguments[argument++];
            int start = at.Start;
            int end = at.Start + at.Length;
            if (start > 0 && end < text.Length && text[start - 1] is '"' or '\'' && text[end] == text[start - 1])
            {
                start--;
                end++;
            }
            written.Append(text, start, end - start);
        }
        return written.ToString();
    }

    /// <summary>An expression as a C# string literal: a verbatim one's quotes doubled, a plain one's backslashes and quotes escaped.</summary>
    public static string Literal(string expression, bool verbatim) => verbatim
        ? $"@\"{expression.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
        : $"\"{expression.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    /// <summary>An expression's words (as a step writes them) and parameters (as the expression writes them), in order; null
    /// for one with more than those.</summary>
    private static IReadOnlyList<Part>? PartsOf(string expression, bool cucumber)
    {
        try
        {
            return cucumber ? CucumberParts(expression) : RegexParts(expression);
        }
        catch (CucumberExpressions.CucumberExpressionException)
        {
            return null;
        }
    }

    private static List<Part>? CucumberParts(string expression)
    {
        List<Part> parts = [];
        foreach (Node node in new CucumberExpressionParser().Parse(expression).Nodes ?? [])
        {
            switch (node.Type)
            {
                case NodeType.TEXT_NODE:
                    parts.Add(new Part(node.Text, false));
                    break;
                case NodeType.PARAMETER_NODE:
                    parts.Add(new Part($"{{{node.Text}}}", true));
                    break;
                default:
                    return null;
            }
        }
        return parts;
    }

    private static List<Part>? RegexParts(string expression)
    {
        string regex = expression.TrimStart('^');
        if (regex.EndsWith('$') && !regex.EndsWith("\\$", StringComparison.Ordinal))
        {
            regex = regex[..^1];
        }
        List<Part> parts = [];
        StringBuilder words = new();
        for (int at = 0; at < regex.Length; at++)
        {
            char c = regex[at];
            if (c == '\\' && at + 1 < regex.Length && !char.IsLetterOrDigit(regex[at + 1]))
            {
                words.Append(regex[++at]);
            }
            else if (c == '(' && !regex.AsSpan(at).StartsWith("(?"))
            {
                int close = Closing(regex, at);
                if (close < 0)
                {
                    return null;
                }
                Flush();
                parts.Add(new Part(regex[at..(close + 1)], true));
                at = close;
            }
            else if (c is '\\' or '.' or '*' or '+' or '?' or '[' or ']' or '{' or '}' or '|' or '(' or ')' or '^' or '$')
            {
                return null;
            }
            else
            {
                words.Append(c);
            }
        }
        Flush();
        return parts;

        void Flush()
        {
            if (words.Length > 0)
            {
                parts.Add(new Part(words.ToString(), false));
                words.Clear();
            }
        }
    }

    private static int Closing(string regex, int open)
    {
        int depth = 0;
        for (int at = open; at < regex.Length; at++)
        {
            if (regex[at] == '\\')
            {
                at++;
            }
            else if (regex[at] == '(')
            {
                depth++;
            }
            else if (regex[at] == ')' && --depth == 0)
            {
                return at;
            }
        }
        return -1;
    }

    private sealed record Part(string Text, bool Parameter);
}
