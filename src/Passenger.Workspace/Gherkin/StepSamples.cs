using System.Text;
using System.Text.RegularExpressions;
using CucumberExpressions.Ast;
using CucumberExpressions.Parsing;
using Passenger.Core;

namespace Passenger.Workspace.Gherkin;

/// <summary>A step's text written from a step definition, with the holes the person fills: each parameter's, named by its
/// type ("[int]").</summary>
public sealed record StepSample(string Text, IReadOnlyList<StepArgument> Holes);

/// <summary>What completion writes for a step definition, as Reqnroll's editors sample one: a Cucumber Expression's words with
/// each parameter a hole ({string}'s between quotes), an optional part written and an alternation's first choice; a
/// regular expression's words with its escapes as their characters, each capturing group a hole for its parameter but one
/// that is a choice of words, which shows its first; a method named for its step, its words. An expression too free to write
/// as words has none.</summary>
public static partial class StepSamples
{
    public static StepSample? Of(StepBinding binding)
    {
        try
        {
            if (binding.Expression is null)
            {
                return FromMethod(binding);
            }
            return binding.CucumberExpression ?? StepExpressions.IsCucumberExpression(binding.Expression)
                ? FromCucumber(binding.Expression)
                : FromRegex(binding.Expression, binding.Parameters);
        }
        catch (CucumberExpressions.CucumberExpressionException)
        {
            return null;
        }
    }

    /// <summary>A type as a C# programmer writes it: its keyword where it has one ("int"), else its name.</summary>
    public static string TypeWords(string type) => PrimitiveTypes.Named(type)?.Keyword ?? type[(type.LastIndexOfAny(['.', '+']) + 1)..];

    private static StepSample FromCucumber(string expression)
    {
        Node root = new CucumberExpressionParser().Parse(expression);
        Writer writer = new();
        foreach (Node node in root.Nodes ?? [])
        {
            Write(node, writer);
        }
        return writer.Done();

        static void Write(Node node, Writer writer)
        {
            switch (node.Type)
            {
                case NodeType.TEXT_NODE:
                    writer.Text(node.Text);
                    break;
                case NodeType.PARAMETER_NODE:
                    string name = node.Text;
                    if (name == "string")
                    {
                        writer.Text("\"");
                        writer.Hole("[string]");
                        writer.Text("\"");
                    }
                    else
                    {
                        writer.Hole($"[{(name.Length == 0 ? "value" : name)}]");
                    }
                    break;
                case NodeType.OPTIONAL_NODE:
                    foreach (Node part in node.Nodes ?? [])
                    {
                        Write(part, writer);
                    }
                    break;
                case NodeType.ALTERNATION_NODE when node.Nodes?.FirstOrDefault() is { } first:
                    foreach (Node part in first.Nodes ?? [])
                    {
                        Write(part, writer);
                    }
                    break;
            }
        }
    }

    private static StepSample? FromRegex(string expression, IReadOnlyList<StepParameter> parameters)
    {
        string regex = expression.TrimStart('^');
        if (regex.EndsWith('$') && !regex.EndsWith("\\$", StringComparison.Ordinal))
        {
            regex = regex[..^1];
        }
        Writer writer = new();
        int parameter = 0;
        for (int at = 0; at < regex.Length; at++)
        {
            char c = regex[at];
            if (c == '\\' && at + 1 < regex.Length)
            {
                char next = regex[++at];
                if (char.IsLetterOrDigit(next))
                {
                    return null;
                }
                writer.Text(next.ToString());
            }
            else if (c == '(')
            {
                int close = Closing(regex, at);
                if (close < 0)
                {
                    return null;
                }
                string inner = regex[(at + 1)..close];
                bool captures = !inner.StartsWith('?');
                string body = captures ? inner : inner.StartsWith("?:", StringComparison.Ordinal) ? inner[2..] : inner;
                if (Words().IsMatch(body))
                {
                    writer.Text(body.Split('|')[0]);
                }
                else if (captures)
                {
                    writer.Hole($"[{(parameter < parameters.Count ? TypeWords(parameters[parameter].Type) : "value")}]");
                }
                else
                {
                    return null;
                }
                parameter += captures ? 1 : 0;
                at = close;
            }
            else if (c is '.' or '*' or '+' or '?' or '[' or '{' or '|' or ')')
            {
                return null;
            }
            else
            {
                writer.Text(c.ToString());
            }
        }
        return writer.Done();
    }

    /// <summary>Where the group opening at <paramref name="open"/> closes, past escapes and inner groups; -1 for none.</summary>
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

    private static StepSample FromMethod(StepBinding binding)
    {
        string name = Regex.Replace(binding.Method, "^(Given|When|Then|StepDefinition)_?", "", RegexOptions.IgnoreCase);
        string[] words = WordBreaks().Split(name).Where(w => w.Length > 0).ToArray();
        Writer writer = new();
        for (int i = 0; i < words.Length; i++)
        {
            if (i > 0)
            {
                writer.Text(" ");
            }
            int index = Enumerable.Range(0, binding.Parameters.Count)
                .FirstOrDefault(p => words[i] == binding.Parameters[p].Name.ToUpperInvariant() || words[i] == $"P{p}", -1);
            if (index >= 0)
            {
                writer.Hole($"[{TypeWords(binding.Parameters[index].Type)}]");
            }
            else
            {
                writer.Text(words[i].ToLowerInvariant());
            }
        }
        return writer.Done();
    }

    [GeneratedRegex(@"^[\w ]+(\|[\w ]+)+$")]
    private static partial Regex Words();

    [GeneratedRegex(@"_+|(?<=\p{Ll})(?=\p{Lu})|(?<=\p{L})(?=\d)")]
    private static partial Regex WordBreaks();

    private sealed class Writer
    {
        private readonly StringBuilder text = new();
        private readonly List<StepArgument> holes = [];

        public void Text(string words) => text.Append(words);

        public void Hole(string said)
        {
            holes.Add(new StepArgument(text.Length, said.Length));
            text.Append(said);
        }

        public StepSample Done() => new(text.ToString(), holes);
    }
}
