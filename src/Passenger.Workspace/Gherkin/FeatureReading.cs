using System.Text.RegularExpressions;
using Gherkin;
using Gherkin.Ast;

namespace Passenger.Workspace.Gherkin;

/// <summary>What a step's keyword makes it: Given, When or Then; an And, But or <c>*</c> takes the kind of the step before
/// it in its scenario (Given for a first step), as Reqnroll binds it.</summary>
public enum StepKind
{
    Given,
    When,
    Then,
}

/// <summary>The keyword a step is written with.</summary>
public enum StepKeywordKind
{
    Context,
    Action,
    Outcome,
    Conjunction,
    Unknown,
}

public enum ScenarioKind
{
    Background,
    Scenario,
    Outline,
}

/// <summary>What a line of a .feature file is, as Gherkin's reader matched it.</summary>
public enum FeatureLineKind
{
    Empty,
    Comment,
    Language,
    Tags,
    Feature,
    Rule,
    Background,
    Scenario,
    Examples,
    Step,
    DocStringSeparator,
    DocString,
    TableRow,
    Description,
    Other,
}

/// <summary>A tag, where it is written (lines and columns from zero).</summary>
public sealed record FeatureTag(int Line, int Column, string Name);

/// <summary>A table's cell: where its value starts and the value, unescaped.</summary>
public sealed record FeatureCell(int Column, int Length, string Value);

public sealed record FeatureRow(int Line, IReadOnlyList<FeatureCell> Cells);

/// <summary>A step's table: its rows, the first its header.</summary>
public sealed record FeatureTable(IReadOnlyList<FeatureRow> Rows);

/// <summary>A step's doc string, from its opening delimiter's line to its closing one's.</summary>
public sealed record FeatureDocString(int Line, int EndLine, string Delimiter, string? ContentType, string Content);

/// <summary>An outline's <c>&lt;name&gt;</c> in a step's text, at its <c>&lt;</c>.</summary>
public sealed record FeaturePlaceholder(int Column, string Name)
{
    /// <summary>Its length with its brackets.</summary>
    public int Length => Name.Length + 2;
}

/// <summary>Lines that fold together, the first staying shown.</summary>
public readonly record struct FeatureFold(int FirstLine, int LastLine);

/// <summary>A mistake Gherkin's reader found, where it found it.</summary>
public sealed record FeatureError(int Line, int Column, string Message);

/// <summary>A titled part: the feature, a rule, an outline's examples.</summary>
public sealed class FeatureHeader(string keyword, string title, int line, int column, IReadOnlyList<FeatureTag> tags)
{
    public string Keyword { get; } = keyword;

    public string Title { get; } = title;

    public int Line { get; } = line;

    public int KeywordColumn { get; } = column;

    public IReadOnlyList<FeatureTag> Tags { get; } = tags;

    /// <summary>Its last line that holds anything.</summary>
    public int EndLine { get; internal set; } = line;
}

/// <summary>An outline's Examples: its header row and the rows each run of the outline takes.</summary>
public sealed class FeatureExamples(string keyword, string title, int line, int column, IReadOnlyList<FeatureTag> tags)
{
    public string Keyword { get; } = keyword;

    public string Title { get; } = title;

    public int Line { get; } = line;

    public int KeywordColumn { get; } = column;

    public IReadOnlyList<FeatureTag> Tags { get; } = tags;

    public FeatureRow? Header { get; internal set; }

    public List<FeatureRow> Rows { get; } = [];

    public int EndLine { get; internal set; } = line;
}

/// <summary>A Background, Scenario or Scenario Outline: its steps, its own tags and those it takes from its feature and
/// rule, and an outline's examples.</summary>
public sealed class FeatureScenario(ScenarioKind kind, string keyword, string title, int line, int column, IReadOnlyList<FeatureTag> tags, FeatureHeader? rule)
{
    public ScenarioKind Kind { get; } = kind;

    public string Keyword { get; } = keyword;

    public string Title { get; } = title;

    public int Line { get; } = line;

    public int KeywordColumn { get; } = column;

    public IReadOnlyList<FeatureTag> Tags { get; } = tags;

    /// <summary>The rule it is written under, if any.</summary>
    public FeatureHeader? Rule { get; } = rule;

    /// <summary>Its feature's tags, its rule's and its own, as a test run takes them.</summary>
    public IReadOnlyList<string> AllTags { get; internal set; } = [];

    public List<FeatureStep> Steps { get; } = [];

    public List<FeatureExamples> Examples { get; } = [];

    public int EndLine { get; internal set; } = line;
}

/// <summary>A step: its keyword, its text and where each is, the kind it binds as, its argument and placeholders.</summary>
public sealed class FeatureStep(int line, int column, string keyword, StepKeywordKind keywordKind, StepKind kind, string text, int textColumn, FeatureScenario scenario)
{
    private static readonly Regex Placeholder = new("<([^<>]+)>", RegexOptions.CultureInvariant);

    public int Line { get; } = line;

    public int KeywordColumn { get; } = column;

    /// <summary>The keyword as written, with the space after it where the language writes one ("Given ").</summary>
    public string Keyword { get; } = keyword;

    public StepKeywordKind KeywordKind { get; } = keywordKind;

    public StepKind Kind { get; } = kind;

    public string Text { get; } = text;

    /// <summary>Where its text starts: past its keyword and every space after it, which Gherkin's reader trims.</summary>
    public int TextColumn { get; } = textColumn;

    public FeatureScenario Scenario { get; } = scenario;

    public FeatureDocString? DocString { get; internal set; }

    public FeatureTable? Table { get; internal set; }

    /// <summary>The line its text, doc string or table last ends on.</summary>
    public int EndLine => Math.Max(Line, Math.Max(DocString?.EndLine ?? 0, Table?.Rows[^1].Line ?? 0));

    public IReadOnlyList<FeaturePlaceholder> Placeholders => field ??=
        [.. Placeholder.Matches(Text).Select(m => new FeaturePlaceholder(TextColumn + m.Index, m.Groups[1].Value))];
}

/// <summary>A .feature file as Cucumber's Gherkin reader reads it, kept whole where it finds mistakes so the editor still
/// knows every step: each line matched as the reader matches it, the parts and steps built from those lines, and the
/// reader's own mistakes beside them. Lines and columns count from zero.</summary>
public sealed partial class FeatureReading
{
    private FeatureReading(GherkinDialect dialect) => Dialect = dialect;

    public GherkinDialect Dialect { get; private set; }

    public FeatureHeader? Feature { get; private set; }

    public List<FeatureHeader> Rules { get; } = [];

    /// <summary>Backgrounds, scenarios and outlines, in the file's order.</summary>
    public List<FeatureScenario> Scenarios { get; } = [];

    public List<FeatureStep> Steps { get; } = [];

    public List<FeatureTag> Tags { get; } = [];

    public List<FeatureError> Errors { get; } = [];

    /// <summary>What each line is.</summary>
    public List<FeatureLineKind> Lines { get; } = [];

    /// <summary>A file's text read with <paramref name="defaultLanguage"/> where it names none (<see cref="GherkinDialects.Default"/>).</summary>
    public static FeatureReading Read(string text, string defaultLanguage)
    {
        string language = GherkinDialects.Default(defaultLanguage);
        TokenMatcher matcher = new(language);
        FeatureReading reading = new(matcher.CurrentDialect);
        reading.Match(text, matcher);
        reading.Close();
        reading.Check(text, language);
        return reading;
    }

    /// <summary>The step on a line, its doc string's and table's lines included.</summary>
    public FeatureStep? StepAt(int line) => Steps.FirstOrDefault(s => s.Line <= line && line <= s.EndLine);

    /// <summary>The scenario a line is in, from its tags to its last line.</summary>
    public FeatureScenario? ScenarioAt(int line) => Scenarios.LastOrDefault(s => FirstLineOf(s.Line, s.Tags) <= line && line <= s.EndLine);

    /// <summary>What folds, in order of its first line: the feature, each rule, background, scenario and examples, each doc
    /// string, and each table of more than one row, from its first line to its last.</summary>
    public IReadOnlyList<FeatureFold> Folds()
    {
        List<FeatureFold> folds = [];
        if (Feature is not null)
        {
            folds.Add(new FeatureFold(Feature.Line, Feature.EndLine));
        }
        folds.AddRange(Rules.Select(r => new FeatureFold(r.Line, r.EndLine)));
        foreach (FeatureScenario part in Scenarios)
        {
            folds.Add(new FeatureFold(part.Line, part.EndLine));
            folds.AddRange(part.Examples.Select(e => new FeatureFold(e.Line, e.EndLine)));
        }
        foreach (FeatureStep step in Steps)
        {
            if (step.DocString is { } doc)
            {
                folds.Add(new FeatureFold(doc.Line, doc.EndLine));
            }
            if (step.Table is { Rows.Count: > 1 } table)
            {
                folds.Add(new FeatureFold(table.Rows[0].Line, table.Rows[^1].Line));
            }
        }
        return [.. folds.Where(f => f.LastLine > f.FirstLine).OrderBy(f => f.FirstLine).ThenByDescending(f => f.LastLine)];
    }

    private List<FeatureTag> pendingTags = [];
    private FeatureHeader? rule;
    private FeatureScenario? scenario;
    private FeatureExamples? examples;
    private FeatureStep? lastStep;
    private StepKind kindBefore;
    private bool describing;
    private int docStringLine = -1;
    private string? docStringDelimiter;
    private string? docStringType;
    private readonly List<string> docStringLines = [];

    private void Match(string text, TokenMatcher matcher)
    {
        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i].TrimEnd('\r');
            Token token = new(new GherkinLine(raw, i + 1), new Location(i + 1, 1));
            Lines.Add(docStringLine >= 0 ? InDocString(matcher, token, i) : Outside(matcher, token, i));
        }
    }

    private FeatureLineKind InDocString(TokenMatcher matcher, Token token, int line)
    {
        if (matcher.Match_DocStringSeparator(token))
        {
            if (lastStep is not null)
            {
                lastStep.DocString = new FeatureDocString(docStringLine, line, docStringDelimiter!, docStringType, string.Join('\n', docStringLines));
            }
            docStringLine = -1;
            return FeatureLineKind.DocStringSeparator;
        }
        matcher.Match_Other(token);
        docStringLines.Add(token.MatchedText ?? "");
        return FeatureLineKind.DocString;
    }

    private FeatureLineKind Outside(TokenMatcher matcher, Token token, int line)
    {
        int indent = token.Line.Indent;
        if (matcher.Match_Empty(token))
        {
            return FeatureLineKind.Empty;
        }
        if (Feature is null && matcher.Match_Language(token))
        {
            Dialect = matcher.CurrentDialect;
            return FeatureLineKind.Language;
        }
        if (matcher.Match_Comment(token))
        {
            return FeatureLineKind.Comment;
        }
        if (matcher.Match_TagLine(token))
        {
            foreach (GherkinLineSpan span in token.Line.GetTags())
            {
                FeatureTag tag = new(line, span.Column - 1, span.Text);
                pendingTags.Add(tag);
                Tags.Add(tag);
            }
            describing = false;
            return FeatureLineKind.Tags;
        }
        if (matcher.Match_FeatureLine(token))
        {
            Feature ??= new FeatureHeader(token.MatchedKeyword, token.MatchedText ?? "", line, indent, TakeTags());
            describing = true;
            return FeatureLineKind.Feature;
        }
        if (matcher.Match_RuleLine(token))
        {
            rule = new FeatureHeader(token.MatchedKeyword, token.MatchedText ?? "", line, indent, TakeTags());
            Rules.Add(rule);
            scenario = null;
            examples = null;
            lastStep = null;
            describing = true;
            return FeatureLineKind.Rule;
        }
        if (matcher.Match_BackgroundLine(token))
        {
            Begin(new FeatureScenario(ScenarioKind.Background, token.MatchedKeyword, token.MatchedText ?? "", line, indent, TakeTags(), rule));
            return FeatureLineKind.Background;
        }
        if (matcher.Match_ScenarioLine(token))
        {
            ScenarioKind kind = Dialect.ScenarioOutlineKeywords.Contains(token.MatchedKeyword, StringComparer.Ordinal) ? ScenarioKind.Outline : ScenarioKind.Scenario;
            Begin(new FeatureScenario(kind, token.MatchedKeyword, token.MatchedText ?? "", line, indent, TakeTags(), rule));
            return FeatureLineKind.Scenario;
        }
        if (matcher.Match_ExamplesLine(token))
        {
            examples = new FeatureExamples(token.MatchedKeyword, token.MatchedText ?? "", line, indent, TakeTags());
            scenario?.Examples.Add(examples);
            lastStep = null;
            describing = true;
            return FeatureLineKind.Examples;
        }
        if (matcher.Match_StepLine(token))
        {
            AddStep(token, line, indent);
            return FeatureLineKind.Step;
        }
        if (matcher.Match_DocStringSeparator(token))
        {
            docStringLine = line;
            docStringDelimiter = token.MatchedKeyword;
            docStringType = string.IsNullOrEmpty(token.MatchedText) ? null : token.MatchedText;
            docStringLines.Clear();
            describing = false;
            return FeatureLineKind.DocStringSeparator;
        }
        if (matcher.Match_TableRow(token))
        {
            AddRow(token, line);
            return FeatureLineKind.TableRow;
        }
        return describing ? FeatureLineKind.Description : FeatureLineKind.Other;
    }

    private List<FeatureTag> TakeTags()
    {
        List<FeatureTag> taken = pendingTags;
        pendingTags = [];
        return taken;
    }

    private void Begin(FeatureScenario next)
    {
        scenario = next;
        examples = null;
        lastStep = null;
        kindBefore = StepKind.Given;
        describing = true;
        IEnumerable<FeatureTag> inherited = (Feature?.Tags ?? []).Concat(rule?.Tags ?? []);
        next.AllTags = [.. inherited.Concat(next.Tags).Select(t => t.Name).Distinct(StringComparer.Ordinal)];
        Scenarios.Add(next);
    }

    private void AddStep(Token token, int line, int indent)
    {
        if (scenario is null || examples is not null)
        {
            // A step outside a scenario, or after an outline's examples: the reader's mistake, kept out of any scenario.
            lastStep = null;
            return;
        }
        string keyword = token.MatchedKeyword;
        StepKeywordKind keywordKind = KindOf(Dialect.GetStepKeywordType(keyword));
        StepKind kind = keywordKind switch
        {
            StepKeywordKind.Context => StepKind.Given,
            StepKeywordKind.Action => StepKind.When,
            StepKeywordKind.Outcome => StepKind.Then,
            _ => kindBefore,
        };
        kindBefore = kind;
        string raw = token.Line.GetLineText(0);
        int textColumn = Math.Min(indent + keyword.Length, raw.Length);
        while (textColumn < raw.Length && char.IsWhiteSpace(raw[textColumn]))
        {
            textColumn++;
        }
        lastStep = new FeatureStep(line, indent, keyword, keywordKind, kind, token.MatchedText ?? "", textColumn, scenario);
        scenario.Steps.Add(lastStep);
        Steps.Add(lastStep);
        describing = false;
    }

    private static StepKeywordKind KindOf(StepKeywordType? type) => type switch
    {
        StepKeywordType.Context => StepKeywordKind.Context,
        StepKeywordType.Action => StepKeywordKind.Action,
        StepKeywordType.Outcome => StepKeywordKind.Outcome,
        StepKeywordType.Conjunction => StepKeywordKind.Conjunction,
        _ => StepKeywordKind.Unknown,
    };

    private void AddRow(Token token, int line)
    {
        List<FeatureCell> cells = [];
        foreach (GherkinLineSpan span in token.Line.GetTableCells())
        {
            int column = span.Column - 1;
            cells.Add(new FeatureCell(column, CellLength(token.Line, column), span.Text));
        }
        FeatureRow row = new(line, cells);
        describing = false;
        if (examples is not null)
        {
            if (examples.Header is null)
            {
                examples.Header = row;
            }
            else
            {
                examples.Rows.Add(row);
            }
            return;
        }
        if (lastStep is not null)
        {
            lastStep.Table = new FeatureTable([.. lastStep.Table?.Rows ?? [], row]);
        }
    }

    /// <summary>A cell's length as written, up to the space before its closing bar: its value unescaped may be shorter.</summary>
    private static int CellLength(GherkinLine line, int column)
    {
        string text = line.GetLineText(0);
        int at = column;
        while (at < text.Length && text[at] != '|')
        {
            at += text[at] == '\\' && at + 1 < text.Length ? 2 : 1;
        }
        int end = at;
        while (end > column && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }
        return end - column;
    }

    /// <summary>Each part's last line: the line before what follows it, less the empty lines and comments at its end.</summary>
    private void Close()
    {
        int last = Lines.Count - 1;
        for (int i = 0; i < Scenarios.Count; i++)
        {
            FeatureScenario current = Scenarios[i];
            int next = i + 1 < Scenarios.Count ? FirstLineOf(Scenarios[i + 1].Line, Scenarios[i + 1].Tags) : last + 1;
            FeatureHeader? nextRule = Rules.FirstOrDefault(r => r.Line > current.Line && FirstLineOf(r.Line, r.Tags) < next);
            if (nextRule is not null)
            {
                next = FirstLineOf(nextRule.Line, nextRule.Tags);
            }
            current.EndLine = LastFilled(current.Line, next - 1);
            for (int e = 0; e < current.Examples.Count; e++)
            {
                FeatureExamples one = current.Examples[e];
                int after = e + 1 < current.Examples.Count ? FirstLineOf(current.Examples[e + 1].Line, current.Examples[e + 1].Tags) : current.EndLine + 1;
                one.EndLine = LastFilled(one.Line, after - 1);
            }
        }
        for (int r = 0; r < Rules.Count; r++)
        {
            int next = r + 1 < Rules.Count ? FirstLineOf(Rules[r + 1].Line, Rules[r + 1].Tags) : last + 1;
            Rules[r].EndLine = LastFilled(Rules[r].Line, next - 1);
        }
        if (Feature is not null)
        {
            Feature.EndLine = LastFilled(Feature.Line, last);
        }
    }

    private static int FirstLineOf(int line, IReadOnlyList<FeatureTag> tags) => tags.Count > 0 ? Math.Min(line, tags[0].Line) : line;

    private int LastFilled(int from, int to)
    {
        int end = Math.Min(to, Lines.Count - 1);
        while (end > from && Lines[end] is FeatureLineKind.Empty or FeatureLineKind.Comment)
        {
            end--;
        }
        return end;
    }

    /// <summary>The mistakes Gherkin's own parser finds, all of them rather than the first.</summary>
    private void Check(string text, string language)
    {
        Parser parser = new() { StopAtFirstError = false };
        try
        {
            parser.Parse(new TokenScanner(new StringReader(text)), new TokenMatcher(language));
        }
        catch (CompositeParserException composite)
        {
            foreach (ParserException error in composite.Errors)
            {
                Add(error);
            }
        }
        catch (ParserException error)
        {
            Add(error);
        }
    }

    private void Add(ParserException error)
    {
        int line = Math.Max((error.Location?.Line ?? 1) - 1, 0);
        int column = Math.Max((error.Location?.Column ?? 1) - 1, 0);
        Errors.Add(new FeatureError(line, column, ErrorPlace().Replace(error.Message, "")));
    }

    /// <summary>The place the reader writes before each of its messages, "(6:3): ", which the editor shows apart.</summary>
    [GeneratedRegex(@"^\(\d+:\d+\):\s*")]
    private static partial Regex ErrorPlace();
}
