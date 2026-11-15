using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CucumberExpressions;
using Passenger.Text;

namespace Passenger.Workspace.Gherkin;

/// <summary>A step definition's expression made into the regular expression Reqnroll matches a step's whole text with: a
/// Cucumber Expression through Cucumber's own reader with Reqnroll's parameter types, a regular expression anchored at
/// both ends, a method named for its step read as Reqnroll reads one. Ported from Reqnroll's binding builders, so a step
/// matches here as it does in a test run.</summary>
public static partial class StepExpressions
{
    /// <summary>Whether Reqnroll reads an expression as a Cucumber Expression (else a regular expression): Reqnroll's own
    /// detector.</summary>
    public static bool IsCucumberExpression(string expression)
    {
        if (expression.StartsWith('^') || expression.EndsWith('$'))
        {
            return false;
        }
        if (ParameterPlaceholder().IsMatch(expression))
        {
            return true;
        }
        return !CommonRegexPatterns().IsMatch(expression) && !ExtendedRegexPatterns().IsMatch(expression);
    }

    /// <summary>A binding's regular expression, or why Reqnroll could not make one.</summary>
    public static (Regex? Regex, string? Error) RegexOf(StepBinding binding, ReqnrollParameterTypes types)
    {
        try
        {
            // A project's own expression may backtrack without end: it stops where a search of the editor's would.
            if (binding.Expression is null)
            {
                return (new Regex(FromMethodName(binding), RegexOptions.CultureInvariant, TextSearch.Apart), null);
            }
            bool cucumber = binding.CucumberExpression ?? IsCucumberExpression(binding.Expression);
            if (!cucumber)
            {
                return (new Regex(WholeText(binding.Expression), RegexOptions.CultureInvariant, TextSearch.Apart), null);
            }
            Regex made = new ReqnrollCucumberExpression(binding.Expression, types).Regex;
            return (new Regex(made.ToString(), made.Options, TextSearch.Apart), null);
        }
        catch (CucumberExpressionException error)
        {
            return (null, error.Message);
        }
        catch (ArgumentException error)
        {
            return (null, error.Message);
        }
        catch (InvalidOperationException error)
        {
            return (null, error.Message);
        }
    }

    /// <summary>A regular expression's text made to match a whole step, as Reqnroll anchors one.</summary>
    public static string WholeText(string regex) => (regex.StartsWith('^') ? "" : "^") + regex + (regex.EndsWith('$') ? "" : "$");

    // A word character is a letter, mark, number, connector or currency symbol; words are joined by anything else, a minus
    // ending the joint only before something other than a digit (Reqnroll's StepDefinitionRegexCalculator).
    private const string WordConnector = @"[^\w\p{Sc}]*(?!(?<=-)\d)";

    /// <summary>The regular expression of a binding with no expression, from its method's name: the step's keyword taken off,
    /// its words joined by anything, each parameter where its name stands in capitals (or P0, P1…), any letter case.</summary>
    private static string FromMethodName(StepBinding binding)
    {
        string name = binding.Method;
        if (NonIdentifier().IsMatch(name))
        {
            return WholeText(name);
        }
        foreach (string prefix in PrefixesOf(binding.Kind))
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..].TrimStart('_', ' ');
                break;
            }
        }
        StringBuilder regex = new("^(?i)");
        int done = 0;
        IEnumerable<(int Position, int Length, int Index)> places = binding.Parameters
            .Select((p, i) => Regex.Match(name, $"_?{Regex.Escape(p.Name.ToUpperInvariant())}_?|_?P{i}_?") is { Success: true } found ? (found.Index, found.Length, i) : (-1, 0, i))
            .Where(p => p.Item1 >= 0)
            .OrderBy(p => p.Item1);
        foreach ((int position, int length, int index) in places)
        {
            if (position < done)
            {
                continue;
            }
            if (position > done)
            {
                regex.Append(Words(name[done..position])).Append(WordConnector);
            }
            string group = binding.Parameters[index].Name;
            regex.Append(CultureInfo.InvariantCulture, $"(?:\"(?<{group}>[^\"]*)\"|'(?<{group}>[^']*)'|(?<{group}>.*?))").Append(WordConnector);
            done = position + length;
        }
        return regex.Append(Words(name[done..])).Append(WordConnector).Append('$').ToString();

        static string Words(string part) => string.IsNullOrEmpty(part) ? "" : WordBoundary().Replace(part.Trim('_'), _ => WordConnector);
    }

    private static IEnumerable<string> PrefixesOf(BindingKind kind) => kind switch
    {
        BindingKind.Given => ["Given"],
        BindingKind.When => ["When"],
        BindingKind.Then => ["Then"],
        _ => ["Given", "When", "Then", "StepDefinition"],
    };

    [GeneratedRegex(@"{\w*}")]
    private static partial Regex ParameterPlaceholder();

    [GeneratedRegex(@"(\([^\)]+[\*\+]\)|\.\*)")]
    private static partial Regex CommonRegexPatterns();

    [GeneratedRegex(@"(\\\.|\\d\+)")]
    private static partial Regex ExtendedRegexPatterns();

    [GeneratedRegex(@"[^\p{Ll}\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{Nl}\p{Nd}\p{Pc}\p{Mn}\p{Mc}]")]
    private static partial Regex NonIdentifier();

    [GeneratedRegex(@"_+|(?<=[\d\p{L}])(?=\p{Lu})|(?<=\p{L})(?=\d)")]
    private static partial Regex WordBoundary();

    /// <summary>Reqnroll's Cucumber Expression: <c>{string}</c> takes double or single quotes, each its own group.</summary>
    private sealed class ReqnrollCucumberExpression(string expression, IParameterTypeRegistry types) : CucumberExpression(expression, types)
    {
        protected override bool HandleStringType(string name, IParameterType parameterType, out string[] regexps, out bool shouldWrapWithCaptureGroup)
        {
            regexps = ParameterTypeConstants.StringParameterRegexps;
            shouldWrapWithCaptureGroup = false;
            return true;
        }
    }
}

/// <summary>The parameter types a project's Cucumber Expressions may name, as Reqnroll registers them: <c>{int}</c>,
/// <c>{float}</c>, <c>{string}</c>, <c>{word}</c>, <c>{}</c> and the other built-ins, each .NET type its step definitions
/// take by its name (<c>{Int32}</c>, <c>{DateTime}</c>, an enum by its full or short name), and each
/// <c>[StepArgumentTransformation]</c> by its name or the type it makes.</summary>
public sealed class ReqnrollParameterTypes : IParameterTypeRegistry
{
    private const string MatchAll = ".*";

    private readonly Dictionary<string, IParameterType> byName = new(StringComparer.Ordinal);

    public ReqnrollParameterTypes(IEnumerable<StepBinding> bindings, IEnumerable<StepTransformation> transformations)
    {
        List<(string Name, string Type, string Regex, bool Snippets)> all =
        [
            ("", "System.Object", MatchAll, false),
            (ParameterTypeConstants.WordParameterName, "System.String", ParameterTypeConstants.WordParameterRegex, false),
            (ParameterTypeConstants.StringParameterName, "System.String", MatchAll, true),
        ];
        foreach ((string type, string regex, string? alias) in BuiltIns)
        {
            all.Add((type[(type.LastIndexOf('.') + 1)..], type, regex, true));
            if (alias is not null)
            {
                all.Add((alias, type, regex, true));
            }
        }
        foreach (StepParameter enumeration in bindings.SelectMany(b => b.Parameters).Where(p => p.IsEnum).DistinctBy(p => p.Type))
        {
            all.Add((enumeration.Type, enumeration.Type, MatchAll, true));
        }
        foreach (StepTransformation transformation in transformations)
        {
            string shortType = transformation.ReturnType[(transformation.ReturnType.LastIndexOfAny(['.', '+']) + 1)..];
            bool builtIn = BuiltIns.Any(b => b.Type == transformation.ReturnType) || transformation.ReturnType == "System.String";
            string name = !builtIn && (transformation.Name is null || transformation.Name == shortType) ? transformation.ReturnType : transformation.Name ?? shortType;
            all.Add((name, transformation.ReturnType, RegexOf(transformation), true));
            if (BuiltIns.FirstOrDefault(b => b.Type == transformation.ReturnType).Alias is { } alias)
            {
                all.Add((alias, transformation.ReturnType, RegexOf(transformation), true));
            }
        }
        foreach (IGrouping<string, (string Name, string Type, string Regex, bool Snippets)> named in all.GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            string[] regexes = [.. named.Select(t => t.Regex).Distinct(StringComparer.Ordinal)];
            byName[named.Key] = new NamedType(named.Key, regexes.Length > 1 && regexes.Contains(MatchAll) ? [MatchAll] : regexes, named.Any(t => t.Snippets));
        }
    }

    /// <summary>The .NET types Reqnroll knows by name, with each one's regular expression and Cucumber's alias for it.</summary>
    private static readonly (string Type, string Regex, string? Alias)[] BuiltIns =
    [
        ("System.Byte", ParameterTypeConstants.IntParameterRegex, "byte"),
        ("System.Int16", ParameterTypeConstants.IntParameterRegex, null),
        ("System.Int32", ParameterTypeConstants.IntParameterRegex, "int"),
        ("System.Int64", ParameterTypeConstants.IntParameterRegex, "long"),
        ("System.Double", ParameterTypeConstants.FloatParameterRegex, "double"),
        ("System.Single", ParameterTypeConstants.FloatParameterRegex, "float"),
        ("System.Decimal", ParameterTypeConstants.FloatParameterRegex, "decimal"),
        ("System.Boolean", MatchAll, null),
        ("System.Char", MatchAll, null),
        ("System.DateTime", MatchAll, null),
        ("System.Guid", MatchAll, null),
    ];

    /// <summary>The names a Cucumber Expression may name, for completion.</summary>
    public IEnumerable<string> Names => byName.Keys.Where(n => n.Length > 0);

    public IParameterType? LookupByTypeName(string name)
    {
        if (byName.TryGetValue(name, out IParameterType? type))
        {
            return type;
        }
        IParameterType[] byShortName = [.. byName.Where(p => p.Key.EndsWith("." + name, StringComparison.Ordinal) || p.Key.EndsWith("+" + name, StringComparison.Ordinal)).Select(p => p.Value)];
        return byShortName.Length switch
        {
            0 => null,
            1 => byShortName[0],
            _ => throw new InvalidOperationException($"Ambiguous type in a cucumber expression: several step parameter types are named '{name}'. Use the type's full name, or name a StepArgumentTransformation for each."),
        };
    }

    public IEnumerable<IParameterType> GetParameterTypes() => byName.Values;

    /// <summary>A transformation's regular expression as one parameter's: its groups made non-capturing, one too complex
    /// for that taking any text, as Reqnroll's.</summary>
    private static string RegexOf(StepTransformation transformation)
    {
        string? regex = transformation.Regex?.TrimStart('^').TrimEnd('$');
        if (regex is null || Regex.IsMatch(regex, @"\(\?|\\\("))
        {
            return MatchAll;
        }
        string grouped = $"({regex.Replace("(", "(?:", StringComparison.Ordinal)})";
        try
        {
            _ = new Regex(grouped);
            return grouped;
        }
        catch (ArgumentException)
        {
            return MatchAll;
        }
    }

    private sealed class NamedType(string name, string[] regexes, bool snippets) : IParameterType
    {
        public string[] RegexStrings { get; } = regexes;

        public string Name { get; } = name;

        public Type ParameterType => typeof(object);

        public int Weight => 0;

        public bool UseForSnippets { get; } = snippets;
    }
}
