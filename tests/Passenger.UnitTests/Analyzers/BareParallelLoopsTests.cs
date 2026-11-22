extern alias Analyzers;

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Analyzers::Passenger.Analyzers;

namespace Passenger.UnitTests.Analyzers;

/// <summary>PSG0010 (<see cref="BareParallelLoops"/>) over code compiled in memory: a synchronous Parallel loop, called or
/// named, is flagged wherever ApartScheduler can be reached but inside it, and Parallel.ForEachAsync, whose bodies give their
/// threads back at each wait, is passed over.</summary>
public class BareParallelLoopsTests
{
    [Fact]
    public Task A_synchronous_parallel_loop_is_flagged_where_it_is_written() => Cases.EachAsync<(string code, string loop)>(
    [
        ("Parallel.For(0, 4, i => { });", "Parallel.For(0, 4, i => { })"),
        ("Parallel.ForEach(new[] { 1, 2 }, i => { });", "Parallel.ForEach(new[] { 1, 2 }, i => { })"),
        ("Parallel.Invoke(() => { }, () => { });", "Parallel.Invoke(() => { }, () => { })"),
        ("Action<Action[]> invoke = Parallel.Invoke; invoke([]);", "Parallel.Invoke"),
    ], async row =>
    {
        (string code, string loop) = row;
        List<string> flagged = await FlaggedAsync(Home + Within("Looper", code));

        Assert.True(flagged.SequenceEqual(["Looper: " + loop]), $"PSG0010 flagged [{string.Join(", ", flagged)}] in\n{code}\nwhere [{loop}] runs "
            + "on the thread pool, every thread of which it may take until it ends");
    });

    [Fact]
    public async Task A_loop_inside_the_home_or_an_async_one_or_one_where_the_home_cannot_be_reached_is_passed_over()
    {
        const string Loop = "Parallel.ForEach(new[] { 1 }, i => { });";
        List<string> flagged = await FlaggedAsync(Home.Replace("public static void Run() { }", "public static void Run() { " + Loop + " }")
            + Within("Waiter", "await Parallel.ForEachAsync(new[] { 1 }, async (i, stop) => await Task.Yield());"));
        Assert.True(flagged.Count == 0, $"PSG0010 flagged [{string.Join(", ", flagged)}]: a loop inside Passenger.Core.ApartScheduler, or "
            + "Parallel.ForEachAsync, fails the build though it holds no thread of the pool for the loop's length");

        List<string> unreached = await FlaggedAsync(Within("Elsewhere", Loop));
        Assert.True(unreached.Count == 0, $"PSG0010 flagged [{string.Join(", ", unreached)}] in a program that cannot reach ApartScheduler, "
            + "which has no other way to run the loop");
    }

    /// <summary>A stand-in for Core's ApartScheduler, the rule's home.</summary>
    private const string Home = "namespace Passenger.Core { public sealed class ApartScheduler { public static void Run() { } } } ";

    /// <summary>A class of that name whose async method holds the code.</summary>
    private static string Within(string type, string code) =>
        $"public static class {type} {{ public static async Task AskAsync() {{ {code} await Task.Yield(); }} }}";

    /// <summary>Each place PSG0010 flags, as its class and its text; the code compiled with no error first.</summary>
    private static async Task<List<string>> FlaggedAsync(string code)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("using System;\nusing System.Threading;\nusing System.Threading.Tasks;\n" + code,
            new CSharpParseOptions(LanguageVersion.Latest));
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("Loops", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(errors.Length == 0, $"The case does not compile:\n{code}\n{string.Join('\n', errors.Select(e => e.ToString()))}");
        ImmutableArray<Diagnostic> found = await compilation.WithAnalyzers([new BareParallelLoops()]).GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. found.Where(d => d.Id == "PSG0010").OrderBy(d => d.Location.SourceSpan.Start).Select(d =>
        {
            SyntaxNode node = tree.GetRoot().FindNode(d.Location.SourceSpan);
            string text = d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan);
            return $"{node.AncestorsAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().First().Identifier.Text}: {text}";
        })];
    }
}
