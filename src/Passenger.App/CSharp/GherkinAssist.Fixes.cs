using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Bdd;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Workspace.Gherkin;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

internal sealed partial class GherkinAssist
{
    public const string DefineStep = "Define step";

    /// <summary>The feature's reading brought up to its text before its fixes are asked for.</summary>
    public override async Task ReadyAsync(OpenDocument document)
    {
        if (IsFeature(document) && Bdd is { } bdd)
        {
            await bdd.AnalysisAsync(document);
        }
    }

    /// <summary>On a step no definition binds: Define step, and Define all missing steps where the file has more, each into
    /// the binding class that binds most of the feature's steps, else a new <c>…StepDefinitions.cs</c>.</summary>
    public override IReadOnlyList<EditorFix> Fixes(OpenDocument document, int offset)
    {
        if (!IsFeature(document) || Bdd is not { } bdd || bdd.Checks.Of(document) is not { Steps: not null } analysis
            || analysis.StepAt(offset) is not { } step || !Undefined(analysis, step))
        {
            return [];
        }
        Target target = TargetOf(bdd, document.Path, analysis);
        TextRange range = analysis.WholeOf(step);
        List<EditorFix> fixes = [new($"{DefineStep} '{step.Text}'", $"Writes its step definition {target.Shown}", range, string.Empty)
        {
            Elsewhere = (_, _) => DefineAsync(analysis, [step], target),
        }];
        FeatureStep[] missing = [.. analysis.Reading.Steps.Where(s => Undefined(analysis, s))];
        if (missing.Length > 1)
        {
            fixes.Add(new EditorFix($"Define all {missing.Length} missing steps of the feature", $"Writes their step definitions {target.Shown}", range, string.Empty)
            {
                Elsewhere = (_, _) => DefineAsync(analysis, missing, target),
            });
        }
        return fixes;
    }

    private static bool Undefined(FeatureAnalysis analysis, FeatureStep step) =>
        analysis.Matches.TryGetValue(step, out StepMatch? match) && match.Status == MatchStatus.Undefined;

    /// <summary>Where a feature's missing steps are written: a binding class of its project's code (the one binding most of its
    /// steps), or a new file in the project's StepDefinitions folder (else its folder) named after the feature.</summary>
    private sealed record Target(string Path, string? Class, string? Namespace)
    {
        public string Shown => Class is null ? $"in a new file, {System.IO.Path.GetFileName(Path)}" : $"into {Class} ({System.IO.Path.GetFileName(Path)})";
    }

    private static Target TargetOf(BddSource bdd, string feature, FeatureAnalysis analysis)
    {
        ProjectModel? project = bdd.Session.ProjectOf(feature);
        string? folder = project?.Folder;
        StepBinding? most = analysis.Matches.Values.SelectMany(m => m.Bindings).Select(b => b.Binding)
            .Where(b => b.MethodPlace is { } place && (folder is null || PathNames.IsUnder(place.Path, folder)))
            .GroupBy(b => (b.Type, b.MethodPlace!.Path)).OrderByDescending(g => g.Count()).Select(g => g.First()).FirstOrDefault();
        if (most is not null)
        {
            return new Target(most.MethodPlace!.Path, most.ShortType, null);
        }
        string root = folder ?? System.IO.Path.GetDirectoryName(feature)!;
        string into = Directory.Exists(System.IO.Path.Combine(root, "StepDefinitions")) ? System.IO.Path.Combine(root, "StepDefinitions") : root;
        string name = Identifier(analysis.Reading.Feature?.Title is { Length: > 0 } title ? title : System.IO.Path.GetFileNameWithoutExtension(feature)) + "StepDefinitions";
        string path = System.IO.Path.Combine(into, name + ".cs");
        return File.Exists(path) ? new Target(path, name, null) : new Target(path, null, bdd.NamespaceFor(into));
    }

    /// <summary>A title as a C# type's name: its words joined, each begun in capitals.</summary>
    private static string Identifier(string title)
    {
        string joined = string.Concat(title.Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string([.. w.Where(char.IsLetterOrDigit)])).Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return joined.Length == 0 ? "Feature" : char.IsDigit(joined[0]) ? "_" + joined : joined;
    }

    /// <summary>The definitions of steps written as one command of Undo, then shown: into the target class before its closing
    /// brace, or as a new binding class of their own.</summary>
    private async Task<RefactorOutcome> DefineAsync(FeatureAnalysis analysis, IReadOnlyList<FeatureStep> steps, Target target)
    {
        ProjectSteps project = analysis.Steps!;
        CultureInfo culture = CultureOf(analysis.Config.BindingCultureOf(analysis.Reading));
        List<string> taken = [.. project.Bindings.Where(b => b.ShortType == target.Class).Select(b => b.Method)];
        List<StepSnippet> snippets = [];
        foreach (FeatureStep step in steps)
        {
            StepSnippet snippet = StepSnippets.Of(step, analysis.Reading, analysis.Config.SkeletonFor(project.IsSpecFlow), project.IsSpecFlow, culture, taken);
            // Steps written alike (an And repeating a Given) take one definition.
            if (!snippets.Any(s => AttributeOf(s) == AttributeOf(snippet)))
            {
                snippets.Add(snippet);
                taken.Add(snippet.Method);
            }
        }
        string usings = project.IsSpecFlow ? "TechTalk.SpecFlow" : "Reqnroll";
        bool async = analysis.Config.SkeletonFor(project.IsSpecFlow) is SkeletonStyle.AsyncCucumberExpression or SkeletonStyle.AsyncRegex;
        XamlFileChange change;
        if (target.Class is null)
        {
            string body = string.Join("\n", snippets.Select(s => Indented(s.Code, "    ")));
            string made = $"using {usings};\n{(async ? "using System.Threading.Tasks;\n" : string.Empty)}\nnamespace {target.Namespace};\n\n[Binding]\npublic class {System.IO.Path.GetFileNameWithoutExtension(target.Path)}\n{{\n{body}}}\n";
            change = new XamlFileChange(target.Path, null, made);
        }
        else
        {
            change = new XamlFileChange(target.Path, text => InsertInto(text, target.Class, snippets));
        }
        if (await actions.ApplyFileChangesAsync("Define Steps", [change]) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        string written = context.ReadOpenOrDisk(target.Path);
        int at = written.IndexOf(snippets[0].Method + "(", StringComparison.Ordinal);
        if (at >= 0)
        {
            TextSnapshot snapshot = TextSnapshot.From(written);
            int line = snapshot.GetLineFromOffset(at);
            context.Navigate((target.Path, new TextPosition(line, at - snapshot.GetLineStart(line))));
        }
        string said = snippets.Count == 1 ? $"Defined {snippets[0].Method} {target.Shown}" : $"Defined {snippets.Count} steps {target.Shown}";
        return new RefactorOutcome(said, null, [new RefactoredFile(target.Path, target.Class is null ? RefactoredChange.Created : RefactoredChange.Changed, [])], []);
    }

    private static string AttributeOf(StepSnippet snippet) => snippet.Code[..snippet.Code.IndexOf('\n', StringComparison.Ordinal)];

    /// <summary>Methods written into a class of a C# text, after its last member and before its closing brace, at its members'
    /// indentation; none where the text declares no such class.</summary>
    private static IReadOnlyList<TextEdit> InsertInto(string text, string type, IReadOnlyList<StepSnippet> snippets)
    {
        SyntaxTree tree = CSharpParsing.Parse(text);
        if (tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().LastOrDefault(c => c.Identifier.ValueText == type) is not { } declared)
        {
            return [];
        }
        TextSnapshot snapshot = TextSnapshot.From(text);
        string classIndent = LeadingOf(snapshot, declared.SpanStart);
        string indent = declared.Members.FirstOrDefault() is { } member ? LeadingOf(snapshot, member.SpanStart) : classIndent + "    ";
        string methods = string.Join("\n", snippets.Select(s => Indented(s.Code, indent)));
        int close = declared.CloseBraceToken.SpanStart;
        int start = snapshot.GetLineStart(snapshot.GetLineFromOffset(close));
        if (snapshot.GetText(start, close - start).Trim().Length == 0)
        {
            return [new TextEdit(start, 0, (declared.Members.Count > 0 ? "\n" : string.Empty) + methods)];
        }
        return [new TextEdit(close, 0, "\n" + methods + classIndent)];
    }

    /// <summary>The whitespace a line begins with, the line an offset is on.</summary>
    private static string LeadingOf(TextSnapshot text, int offset)
    {
        string line = text.GetLineText(text.GetLineFromOffset(offset));
        return line[..(line.Length - line.TrimStart().Length)];
    }

    /// <summary>Code of lines each ending in a line break, every line that holds anything indented.</summary>
    private static string Indented(string code, string indent) =>
        string.Concat(code.TrimEnd('\n').Split('\n').Select(line => line.Length == 0 ? "\n" : $"{indent}{line}\n"));

    private static CultureInfo CultureOf(string? name)
    {
        try
        {
            return name is null ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
