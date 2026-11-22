using Passenger.Editor;
using Passenger.Languages;
using Passenger.Text;
using Passenger.Workspace.Gherkin;

namespace Passenger.App.Bdd;

/// <summary>A .feature file's text as read for the editor, for any thread: Gherkin's reading of it in its project's language,
/// each step matched to its project's step definitions (none where the project is no Reqnroll project or its bindings are
/// not read yet, so nothing is said undefined that is unknown), the marks those make, and the colours laid over the
/// grammar's: every keyword in the file's language, descriptions, an examples table's header in bold, outline placeholders
/// in a number's colour, as Reqnroll's editors show them, and the arguments a matched step gives its definition. An
/// argument its definition cannot convert and a table's column no member takes are marked where they are written, a
/// definition taking more or fewer parameters than the step gives at the step.</summary>
public sealed class FeatureAnalysis
{
    public const string Undefined = "undefined-step";
    public const string Ambiguous = "ambiguous-step";
    public const string Argument = "step-argument";
    public const string Parameters = "step-parameters";
    public const string Column = "table-column";
    public const string Syntax = "gherkin";
    public const string CodeBehind = "code-behind";
    public const string Language = "feature-language";

    private FeatureAnalysis(int version, TextSnapshot text, FeatureReading reading, ReqnrollConfig config, ProjectSteps? steps)
    {
        Version = version;
        Text = text;
        Reading = reading;
        Config = config;
        Steps = steps;
    }

    public int Version { get; }

    /// <summary>Which read of the bindings it was matched against (<see cref="StepCatalog.Version"/>).</summary>
    public int StepsRead { get; private set; }

    public TextSnapshot Text { get; }

    public FeatureReading Reading { get; }

    public ReqnrollConfig Config { get; }

    /// <summary>The project's bindings; null where none are known.</summary>
    public ProjectSteps? Steps { get; }

    public StepMatcher? Matcher => Steps?.Matcher(Config.BindingCultureOf(Reading));

    public IReadOnlyDictionary<FeatureStep, StepMatch> Matches { get; private set; } = new Dictionary<FeatureStep, StepMatch>();

    public IReadOnlyList<Marker> Marks { get; private set; } = [];

    public SyntaxSpan[] Colours { get; private set; } = [];

    /// <summary>Where the colours differ from those the layer showed as it was read; null, nowhere.</summary>
    public TextRange? ColoursDiffer { get; private set; }

    /// <summary>A file's text read in its project's configuration, matched against its project's bindings where known.</summary>
    public static FeatureAnalysis Of(TextSnapshot text, int version, ReqnrollConfig config, ProjectSteps? steps, ShownColours shown, int stepsRead = -1)
    {
        FeatureReading reading = FeatureReading.Read(text.ToString(), GherkinDialects.Default(config.FeatureLanguage));
        FeatureAnalysis analysis = new(version, text, reading, config, steps) { StepsRead = stepsRead };
        StepMatcher? matcher = analysis.Matcher;
        Dictionary<FeatureStep, StepMatch> matches = [];
        if (matcher is not null)
        {
            foreach (FeatureStep step in reading.Steps.Where(StepMatcher.Matchable))
            {
                matches[step] = matcher.Match(step, reading);
            }
        }
        analysis.Matches = matches;
        analysis.Marks = analysis.MarksOf();
        analysis.Colours = analysis.ColoursOf();
        analysis.ColoursDiffer = ColourLayer.Differing(shown, analysis.Colours);
        return analysis;
    }

    /// <summary>The offset of a line and column of the text.</summary>
    public int OffsetOf(int line, int column) => Math.Min(Text.GetLineStart(Math.Min(line, Text.LineCount - 1)) + column, Text.Length);

    /// <summary>The step whose keyword or text an offset is on (or just after).</summary>
    public FeatureStep? StepAt(int offset)
    {
        int line = Text.GetLineFromOffset(Math.Min(offset, Text.Length));
        return Reading.Steps.FirstOrDefault(s => s.Line == line && offset >= OffsetOf(line, s.KeywordColumn));
    }

    /// <summary>A step's text as a range of the file.</summary>
    public TextRange TextOf(FeatureStep step) => new(OffsetOf(step.Line, step.TextColumn), step.Text.Length);

    /// <summary>A step's keyword and text as a range of the file.</summary>
    public TextRange WholeOf(FeatureStep step) => new(OffsetOf(step.Line, step.KeywordColumn), step.Keyword.Length + step.Text.Length);

    private Marker[] MarksOf()
    {
        List<Marker> marks = [];
        foreach (FeatureError error in Reading.Errors)
        {
            int line = Math.Min(error.Line, Text.LineCount - 1);
            string written = Text.GetLineText(line).TrimEnd('\r');
            int start = Math.Min(error.Column, written.Length);
            int length = Math.Max(written.TrimEnd().Length - start, 0);
            (string code, string message) = error switch
            {
                { NoFeature: true } => (Language, Config.NoFeatureWhy(Reading)),
                { ByGenerator: true } => (CodeBehind, error.Message),
                _ => (Syntax, error.Message),
            };
            marks.Add(Marker.Coded(new TextRange(OffsetOf(line, start), Math.Max(length, 1)), MarkerKind.Error, code, message));
        }
        foreach ((FeatureStep step, StepMatch match) in Matches)
        {
            foreach (RefusedArgument refused in match.Refused)
            {
                marks.Add(Marker.Coded(RangeAt(refused.Line, refused.Column, refused.Length), refused.Fails ? MarkerKind.Error : MarkerKind.Warning,
                    refused.Fails || refused.Unjudged ? Argument : Undefined, refused.Why));
            }
            if (match.Miscounted is { } miscounted)
            {
                marks.Add(Marker.Coded(WholeOf(step), MarkerKind.Error, Parameters, miscounted));
            }
            foreach ((FeatureRow row, FeatureCell cell, string why) in UnreadColumns(step, match))
            {
                marks.Add(Marker.Coded(RangeAt(row.Line, cell.Column, cell.Length), MarkerKind.Warning, Column, why));
            }
            // A step nearly bound is said undefined at the argument its definition refuses.
            if (match.Status == MatchStatus.Undefined && !match.Refused.Any(r => !r.Fails && r.Line == step.Line))
            {
                string why = match.Why is { } said ? $"\n{said}" : string.Empty;
                marks.Add(Marker.Coded(WholeOf(step), MarkerKind.Warning, Undefined, $"No step definition binds this step{why}"));
            }
            else if (match.Status == MatchStatus.Ambiguous)
            {
                marks.Add(Marker.Coded(WholeOf(step), MarkerKind.Error, Ambiguous,
                    $"Several step definitions bind this step: {string.Join(", ", match.Bindings.Select(b => b.Binding.Shown))}"));
            }
        }
        return [.. marks.OrderBy(m => m.Range.Start)];
    }

    /// <summary>A range of the text from a line and column, at least one character long where the text has one.</summary>
    private TextRange RangeAt(int line, int column, int length)
    {
        int start = OffsetOf(line, column);
        return new TextRange(start, Math.Min(Math.Max(length, 1), Math.Max(Text.Length - start, 0)));
    }

    /// <summary>The names of a matched step's table its definition's table helpers set no member from (through a
    /// transformation where the definition takes the table as another type), with why; those no read takes. None where the
    /// definition's parameters are not as many as the step gives, as a run fails before it reads the table.</summary>
    private IEnumerable<(FeatureRow Row, FeatureCell Cell, string Why)> UnreadColumns(FeatureStep step, StepMatch match)
    {
        if (step.Table is not { } table || Steps is null || match is not { Status: MatchStatus.Defined, Miscounted: null, Bindings: [{ Binding: { Parameters.Count: > 0 } binding }] })
        {
            return [];
        }
        int last = binding.Parameters.Count - 1;
        StepParameter parameter = binding.Parameters[last];
        TableRead[] reads = StepConversions.IsTable(parameter.Type)
            ? [.. binding.Tables.Where(t => t.Parameter == last)]
            : [.. StepConversions.MakersOf(parameter, Steps.Transformations).Where(t => t.Parameters.Count > 0 && StepConversions.IsTable(t.Parameters[0]))
                .Take(1).SelectMany(t => t.Tables.Where(r => r.Parameter == 0))];
        if (reads.Length == 0)
        {
            return [];
        }
        IEnumerable<(FeatureRow Row, FeatureCell Cell, bool Unjudged)> unread = TableColumns.Unread(table, reads[0]);
        foreach (TableRead other in reads.Skip(1))
        {
            HashSet<FeatureCell> alsoUnread = [.. TableColumns.Unread(table, other).Select(u => u.Cell)];
            unread = unread.Where(u => alsoUnread.Contains(u.Cell));
        }
        return [.. unread.Select(u => (u.Row, u.Cell, u.Unjudged ? TableColumns.Unjudged(reads[0], u.Cell.Value) : TableColumns.Why(reads[0], u.Cell.Value)))];
    }

    private SyntaxSpan[] ColoursOf()
    {
        List<SyntaxSpan> spans = [];
        void Add(int line, int column, int length, TokenKind kind)
        {
            if (length > 0)
            {
                spans.Add(new SyntaxSpan(OffsetOf(line, column), length, kind));
            }
        }
        for (int line = 0; line < Reading.Lines.Count && line < Text.LineCount; line++)
        {
            if (Reading.Lines[line] == FeatureLineKind.Description)
            {
                string written = Text.GetLineText(line).TrimEnd('\r');
                int indent = written.Length - written.TrimStart().Length;
                Add(line, indent, written.Trim().Length, TokenKind.DocComment);
            }
        }
        if (Reading.Feature is { } feature)
        {
            Add(feature.Line, feature.KeywordColumn, feature.Keyword.Length, TokenKind.Keyword);
        }
        foreach (FeatureHeader rule in Reading.Rules)
        {
            Add(rule.Line, rule.KeywordColumn, rule.Keyword.Length, TokenKind.Keyword);
        }
        foreach (FeatureScenario scenario in Reading.Scenarios)
        {
            Add(scenario.Line, scenario.KeywordColumn, scenario.Keyword.Length, TokenKind.Keyword);
            foreach (FeatureExamples examples in scenario.Examples)
            {
                Add(examples.Line, examples.KeywordColumn, examples.Keyword.Length, TokenKind.Keyword);
                if (examples.Header is { } header)
                {
                    foreach (FeatureCell cell in header.Cells)
                    {
                        Add(header.Line, cell.Column, cell.Length, TokenKind.Bold);
                    }
                }
            }
        }
        foreach (FeatureStep step in Reading.Steps)
        {
            Add(step.Line, step.KeywordColumn, step.Keyword.TrimEnd().Length, TokenKind.Keyword);
            foreach (FeaturePlaceholder placeholder in step.Placeholders)
            {
                Add(step.Line, placeholder.Column, placeholder.Length, TokenKind.Number);
            }
            if (Matches.TryGetValue(step, out StepMatch? match) && match is { Status: MatchStatus.Defined, Bindings: [{ Arguments: { } arguments }] })
            {
                foreach (StepArgument argument in arguments)
                {
                    Add(step.Line, step.TextColumn + argument.Start, argument.Length, TokenKind.Constant);
                }
            }
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        // Nested spans of one place (a placeholder in a header's cell) keep the first.
        List<SyntaxSpan> kept = [];
        foreach (SyntaxSpan span in spans)
        {
            if (kept.Count == 0 || span.Start >= kept[^1].End)
            {
                kept.Add(span);
            }
        }
        return [.. kept];
    }
}
