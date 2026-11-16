using System.Text;

namespace Passenger.Workspace.Gherkin;

/// <summary>A row of an outline's examples, run as a test of its own: its outline, its examples, and its place among the
/// feature's runs (Reqnroll's pickle index: each scenario one, each outline one for each row of examples with a header).</summary>
public sealed record ExampleRow(FeatureScenario Outline, FeatureExamples Examples, FeatureRow Row, int Pickle)
{
    /// <summary>Its values by the header's names, as its outline's placeholders take them.</summary>
    public IReadOnlyDictionary<string, string> Values
    {
        get
        {
            Dictionary<string, string> values = new(StringComparer.Ordinal);
            IReadOnlyList<FeatureCell> names = Examples.Header?.Cells ?? [];
            for (int i = 0; i < names.Count && i < Row.Cells.Count; i++)
            {
                values.TryAdd(names[i].Value, Row.Cells[i].Value);
            }
            return values;
        }
    }

    /// <summary>Its values as one line ("1 | 2 | 3").</summary>
    public string Shown => string.Join(" | ", Row.Cells.Select(c => c.Value));
}

/// <summary>The rows of a feature's outlines, and the row a test of an outline ran, by the arguments its runner names it with:
/// Reqnroll's pickle index where the name gives it, else the row whose values its first arguments are.</summary>
public static class ExampleRows
{
    /// <summary>The name Reqnroll gives the argument carrying a row's pickle index.</summary>
    private const string PickleIndex = "__pickleIndex";

    /// <summary>Every row of the feature's outlines, in its order.</summary>
    public static IReadOnlyList<ExampleRow> Of(FeatureReading reading)
    {
        List<ExampleRow> rows = [];
        int pickle = 0;
        foreach (FeatureScenario scenario in reading.Scenarios.Where(s => s.Kind != ScenarioKind.Background))
        {
            if (scenario.Kind != ScenarioKind.Outline)
            {
                pickle++;
                continue;
            }
            foreach (FeatureExamples examples in scenario.Examples.Where(e => e.Header is not null))
            {
                foreach (FeatureRow row in examples.Rows)
                {
                    rows.Add(new ExampleRow(scenario, examples, row, pickle++));
                }
            }
        }
        return rows;
    }

    /// <summary>The row of an outline a test of it ran, by the test's name ("Add numbers(a: \"1\", b: \"2\", __pickleIndex: \"4\",
    /// exampleTags: [])"); null where its arguments are none of the outline's rows.</summary>
    public static ExampleRow? RowOf(IReadOnlyList<ExampleRow> rows, FeatureScenario outline, string testName)
    {
        IReadOnlyList<(string? Name, string Value)> arguments = ArgumentsOf(testName);
        if (arguments.Count == 0)
        {
            return null;
        }
        List<ExampleRow> its = [.. rows.Where(r => ReferenceEquals(r.Outline, outline))];
        if (arguments.FirstOrDefault(a => a.Name == PickleIndex) is { Name: not null } named && int.TryParse(named.Value, out int pickle)
            && its.FirstOrDefault(r => r.Pickle == pickle) is { } indexed)
        {
            return indexed;
        }
        List<ExampleRow> alike = [.. its.Where(r => r.Row.Cells.Count <= arguments.Count && r.Row.Cells.Select((c, i) => c.Value == arguments[i].Value).All(same => same))];
        if (alike.Count > 1 && alike[0].Row.Cells.Count < arguments.Count && int.TryParse(arguments[alike[0].Row.Cells.Count].Value, out int placed))
        {
            return alike.FirstOrDefault(r => r.Pickle == placed) ?? alike[0];
        }
        return alike.FirstOrDefault();
    }

    /// <summary>The arguments a test's name ends with, each by the name it is given there if any ("a: \"1\"" as a and 1), a
    /// string's quotes and escapes taken off; none for a name with no argument list.</summary>
    public static IReadOnlyList<(string? Name, string Value)> ArgumentsOf(string testName)
    {
        string name = testName.TrimEnd();
        if (!name.EndsWith(')'))
        {
            return [];
        }
        int open = Opening(name);
        if (open < 0)
        {
            return [];
        }
        List<(string?, string)> arguments = [];
        int at = open + 1;
        int end = name.Length - 1;
        while (at < end)
        {
            while (at < end && name[at] == ' ')
            {
                at++;
            }
            string? named = null;
            int colon = NameEnd(name, at, end);
            if (colon > at)
            {
                named = name[at..colon];
                at = colon + 1;
                while (at < end && name[at] == ' ')
                {
                    at++;
                }
            }
            (string value, at) = Value(name, at, end);
            arguments.Add((named, value));
            while (at < end && name[at] is ' ' or ',')
            {
                at++;
            }
        }
        return arguments;
    }

    /// <summary>Where an argument's name ends at its colon, for an argument written "name: value"; -1 for one with no name.</summary>
    private static int NameEnd(string text, int at, int end)
    {
        int i = at;
        while (i < end && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '@'))
        {
            i++;
        }
        return i > at && i < end && text[i] == ':' ? i : -1;
    }

    /// <summary>One argument's value and where it ends: a quoted string without its quotes and escapes, a bracketed list as
    /// written, else what runs to the next comma.</summary>
    private static (string Value, int End) Value(string text, int at, int end)
    {
        if (at < end && text[at] == '"')
        {
            StringBuilder value = new();
            int i = at + 1;
            for (; i < end && text[i] != '"'; i++)
            {
                if (text[i] == '\\' && i + 1 < end)
                {
                    i++;
                    value.Append(text[i] switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '0' => '\0',
                        char other => other,
                    });
                    continue;
                }
                value.Append(text[i]);
            }
            return (value.ToString(), Math.Min(end, i + 1));
        }
        int depth = 0;
        int j = at;
        for (; j < end; j++)
        {
            char c = text[j];
            if (c is '[' or '(' or '{')
            {
                depth++;
            }
            else if (c is ']' or ')' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                break;
            }
        }
        return (text[at..j].Trim(), j);
    }

    /// <summary>Where the argument list a name ends with opens, its strings' brackets passed over; -1 where none does.</summary>
    private static int Opening(string name)
    {
        int depth = 0;
        int open = -1;
        bool quoted = false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (quoted)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == '(')
            {
                if (depth++ == 0)
                {
                    open = i;
                }
            }
            else if (c == ')')
            {
                depth--;
            }
        }
        return depth == 0 ? open : -1;
    }
}
