using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Passenger.Analyzers;

namespace Passenger.UnitTests.Analyzers;

/// <summary>PSG0004 (<see cref="BlockingWaits"/>) over code compiled in memory: each way a thread can wait on a task is
/// flagged where it is written, a read of a task known to have ended is passed over, and so is a wait inside the
/// libraries' one home for a synchronous wait, Core's Blocking.</summary>
public class BlockingWaitsTests
{
    [Theory]
    [InlineData("((IAsyncResult)task).AsyncWaitHandle.WaitOne();", "((IAsyncResult)task).AsyncWaitHandle")]
    [InlineData("((IAsyncResult)task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));", "((IAsyncResult)task).AsyncWaitHandle")]
    [InlineData("WaitHandle.WaitAll([((IAsyncResult)task).AsyncWaitHandle]);", "((IAsyncResult)task).AsyncWaitHandle")]
    [InlineData("IAsyncResult asked = task; asked.AsyncWaitHandle.WaitOne();", "asked.AsyncWaitHandle")]
    [InlineData("task.Wait();", "task.Wait()")]
    [InlineData("Task.WaitAny(task);", "Task.WaitAny(task)")]
    [InlineData("_ = task.Result;", "task.Result")]
    [InlineData("_ = task.ConfigureAwait(false).GetAwaiter().GetResult();", "task.ConfigureAwait(false).GetAwaiter().GetResult()")]
    public async Task A_thread_waiting_on_a_task_is_flagged_where_it_waits(string code, string wait)
    {
        List<string> flagged = await FlaggedAsync(Within("Asker", code));

        Assert.True(flagged.SequenceEqual([wait]), $"PSG0004 flagged [{string.Join(", ", flagged)}] in\n{code}\nwhere [{wait}] holds a thread "
            + "until the task ends, so a wait that ties up the thread its task needs builds unseen");
    }

    [Theory]
    [InlineData("if (task.IsCompleted) { ((IAsyncResult)task).AsyncWaitHandle.WaitOne(); }")]
    [InlineData("if (!task.IsCompleted) { return 0; } ((IAsyncResult)task).AsyncWaitHandle.WaitOne();")]
    [InlineData("await task; _ = task.Result;")]
    [InlineData("_ = task.IsCompletedSuccessfully ? task.Result : 0;")]
    [InlineData("_ = ((IAsyncResult)task).IsCompleted;")]
    public async Task A_read_of_a_task_known_to_have_ended_or_not_waiting_is_passed_over(string code)
    {
        List<string> flagged = await FlaggedAsync(Within("Asker", code));

        Assert.True(flagged.Count == 0, $"PSG0004 flagged [{string.Join(", ", flagged)}] in\n{code}\nwhich waits for nothing, so code "
            + "written as it should be fails the build");
    }

    [Fact]
    public async Task A_wait_inside_the_libraries_home_is_passed_over_and_the_same_wait_beside_it_flagged()
    {
        const string Wait = "task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing).GetAwaiter().GetResult();";
        string code = "namespace Passenger.Core { " + Within("Blocking", Wait) + " " + Within("Beside", Wait) + " }";

        List<string> flagged = await FlaggedAsync(code, located: true);

        Assert.True(flagged.SequenceEqual(["Beside: " + Wait.TrimEnd(';')]),
            $"PSG0004 flagged [{string.Join(", ", flagged)}], where only the wait outside Passenger.Core.Blocking is one, so a library "
            + (flagged.Any(f => f.StartsWith("Blocking", StringComparison.Ordinal)) ? "has no home the rule lets wait" : "waits outside its home unseen"));
    }

    /// <summary>A class of that name whose async method is given a task and holds the code.</summary>
    private static string Within(string type, string code) =>
        $"public static class {type} {{ public static async Task<int> AskAsync(Task<int> task) {{ {code} await Task.Yield(); return 0; }} }}";

    /// <summary>The text of each place PSG0004 flags in the code, prefixed with the type it is in where
    /// <paramref name="located"/>; the code compiled with no error first, so a case that does not compile fails as such.</summary>
    private static async Task<List<string>> FlaggedAsync(string code, bool located = false)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("using System;\nusing System.Threading;\nusing System.Threading.Tasks;\n" + code,
            new CSharpParseOptions(LanguageVersion.Latest));
        IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is { } name && (name.StartsWith("System.", StringComparison.Ordinal) || name is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => MetadataReference.CreateFromFile(p));
        CSharpCompilation compilation = CSharpCompilation.Create("Waits", [tree], references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Diagnostic[] errors = [.. compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)];
        Assert.True(errors.Length == 0, $"The case does not compile:\n{code}\n{string.Join('\n', errors.Select(e => e.ToString()))}");
        ImmutableArray<Diagnostic> found = await compilation.WithAnalyzers([new BlockingWaits()]).GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return [.. found.Where(d => d.Id == "PSG0004").OrderBy(d => d.Location.SourceSpan.Start).Select(d =>
        {
            SyntaxNode node = tree.GetRoot().FindNode(d.Location.SourceSpan);
            string text = d.Location.SourceTree!.GetText().ToString(d.Location.SourceSpan);
            return located ? $"{node.AncestorsAndSelf().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>().First().Identifier.Text}: {text}" : text;
        })];
    }
}
