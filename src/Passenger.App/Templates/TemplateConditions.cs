using System.Text.RegularExpressions;

namespace Passenger.App.Templates;

/// <summary>The conditions a template's options are enabled by, as dotnet new writes them.</summary>
public static class TemplateConditions
{
    /// <summary>How deep brackets and negations are read inside one another, past what any template writes and short of what
    /// this reader reaches on a thread's stack: past it a condition is one that does not read.</summary>
    private const int DeepestNesting = 64;

    /// <summary>Whether an option is enabled by its template's condition ("UseMSTestSdk &amp;&amp; (TestRunner == MSTest)") over
    /// the values the options have now, a word no option is named being itself; one that does not read leaves it enabled.</summary>
    public static bool IsEnabled(string? condition, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrWhiteSpace(condition))
        {
            return true;
        }
        List<string> tokens = Tokens(condition);
        int at = 0;
        int nesting = 0;
        try
        {
            bool enabled = Or();
            return at != tokens.Count || enabled;
        }
        catch (FormatException)
        {
            return true;
        }

        bool Or()
        {
            bool value = And();
            while (Next("||"))
            {
                value = And() | value;
            }
            return value;
        }

        bool And()
        {
            bool value = Not();
            while (Next("&&"))
            {
                value = Not() & value;
            }
            return value;
        }

        bool Not()
        {
            if (++nesting > DeepestNesting)
            {
                throw new FormatException();
            }
            bool value = Operand();
            nesting--;
            return value;
        }

        bool Operand()
        {
            if (Next("!"))
            {
                return !Not();
            }
            if (Next("("))
            {
                bool inner = Or();
                return Next(")") ? inner : throw new FormatException();
            }
            string left = Word();
            if (Next("=="))
            {
                return string.Equals(left, Word(), StringComparison.OrdinalIgnoreCase);
            }
            if (Next("!="))
            {
                return !string.Equals(left, Word(), StringComparison.OrdinalIgnoreCase);
            }
            return left.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        string Word()
        {
            if (at >= tokens.Count || tokens[at] is "==" or "!=" or "&&" or "||" or "!" or "(" or ")")
            {
                throw new FormatException();
            }
            string token = tokens[at++];
            return token[0] is '"' or '\'' ? token[1..^1] : values.TryGetValue(token, out string? value) ? value : token;
        }

        bool Next(string token)
        {
            if (at < tokens.Count && tokens[at] == token)
            {
                at++;
                return true;
            }
            return false;
        }
    }

    /// <summary>A template's condition or computed value as its tokens: quoted texts, operators, brackets and words.</summary>
    internal static List<string> Tokens(string expression) =>
        [.. Regex.Matches(expression, @"""[^""]*""|'[^']*'|==|!=|&&|\|\||[!()]|[^\s!=&|()""']+").Select(m => m.Value)];
}
