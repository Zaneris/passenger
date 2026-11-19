using System.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>A window reading the output of more children than its process's thread pool has threads, for as long as each
/// runs: its language server's and project host's, and quiet ones read as a run's output is (ChildProcesses.Hear) and as
/// a language server's is (ContiguousReader). No read waits on a thread of the pool, so the window's work and its language
/// server's answers go on beside them. Alone, as it holds the process's pool to as many threads as it has processors.</summary>
[Collection(Alone.Name)]
public sealed class ChildReadsPoolTests
{
    [AvaloniaFact]
    public void A_window_reading_more_childrens_output_than_its_pool_has_threads_still_hears_its_language_servers_answers()
    {
        string root = TestFolders.Create("child-reads");
        TestFolders.Write(root, "Reads.csproj", TestProjects.Library());
        TestFolders.Write(root, "Code.cs", "class Code\n{\n    Missing missing;\n}\n");
        MainWindow window = Ui.OpenFolder(root);
        window.OpenFile("Code.cs");
        OpenDocument code = window.EditorArea.Active!;
        CodeEditor editor = window.EditorArea.CodeEditor;
        int Unknown() => window.CSharpService!.For(code)!.Diagnostics.Count(d => d.Code?.ToString() == "CS0246");
        Ui.WaitFor(() => Unknown() == 1, "Missing's error");

        int threads = Environment.ProcessorCount;
        ThreadPool.GetMaxThreads(out int most, out int ports);
        List<Process> children = [];
        List<Task> reads = [];
        try
        {
            Assert.True(ThreadPool.SetMaxThreads(threads, ports), $"The pool could not be held to {threads} threads");
            for (int each = 0; each < threads * 2; each++)
            {
                Process child = ChildProcesses.Start(Quiet());
                children.Add(child);
                reads.Add(each % 2 == 0
                    ? ChildProcesses.Hear(child, _ => { }, _ => { })
                    : new ContiguousReader(ChildProcesses.OutputStream(child)).ReadAsync(TestContext.Current.CancellationToken).AsTask());
            }
            Ui.PlaceAfter(editor, "Missing missing;");
            window.KeyTextInput(" Absent absent;");
            Ui.WaitFor(() => Unknown() == 2,
                () => $"With {threads * 3} reads of {children.Count} children's output waiting and the pool held to {threads} threads, the language server's answer to an edit never came "
                    + $"({Unknown()} unknown types marked, not 2): a read waits on a thread of the pool, so a window with many children has none left for its work");
            Assert.True(children.All(DotnetProcesses.Runs) && !reads.Any(read => read.IsCompleted),
                "A quiet child ended, or its read did, before the window had answered: the reads did not wait all along");
        }
        finally
        {
            ThreadPool.SetMaxThreads(most, ports);
            foreach (Process child in children)
            {
                ChildProcesses.Kill(child);
            }
            window.Close();
        }
        Ui.Await(Task.WhenAll(reads), "every read of a child ended with it");
    }

    /// <summary>A child that writes nothing until its input ends, which nothing ends but its kill: its input, output and
    /// errors redirected.</summary>
    private static ProcessStartInfo Quiet()
    {
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? ChildProcesses.Command("findstr", ["never written"])
            : ChildProcesses.Command("cat", []);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        return start;
    }
}
