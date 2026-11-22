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
/// server's answers go on beside them. Alone, as it holds the process's pool to as many threads for the window's work as
/// it has processors.</summary>
[Collection(Alone.Name)]
public sealed class ChildReadsPoolTests
{
    /// <summary>The pool's threads a headless window test holds all the while it runs, where a person's window holds none:
    /// the window's own thread, which the headless session takes from the pool for its life, and the test runner's, which
    /// waits on it.</summary>
    private const int HeldByTheTest = 2;

    /// <summary>What Passenger starts is read and ended through ChildProcesses, leaving nothing behind: a read failing
    /// partway ends the child and fails the run, a child disposed on another thread as it is killed has what it started
    /// ended too, a helper takes its host's properties and conserves memory unless the person set it, and a window
    /// reading more children's output than its pool has threads still hears its language server.</summary>
    [AvaloniaFact]
    public async Task What_passenger_starts_is_read_and_ended_through_its_one_way_leaving_nothing_behind()
    {
        await Flow.StepApartAsync("a read of a child's output that fails part of the way ends the child and fails the run with what it threw", () => new ChildReadsTests(), t => t.A_read_of_a_childs_output_that_fails_part_of_the_way_ends_the_child_and_fails_the_run_with_what_it_threw());
        await Flow.StepApartAsync("a child disposed on another thread as it is killed has what it started ended too", () => new ChildKillTests(), t => t.A_child_disposed_on_another_thread_as_it_is_killed_has_what_it_started_ended_too());
        await Flow.StepApartAsync("a helper's properties of every kind reach it as its own host gives them", () => new HelperHostTests(), t => t.A_helpers_properties_of_every_kind_reach_it_as_its_own_host_gives_them());
        Flow.StepApart<HelperMemoryTests>("a helper runs conserving memory unless the person set it themselves", t => t.A_helper_runs_conserving_memory_unless_the_person_set_it_themselves());
        Flow.StepApart<ChildReadsPoolTests>("a window reading more children's output than its pool has threads still hears its language server's answers", t => t.A_window_reading_more_childrens_output_than_its_pool_has_threads_still_hears_its_language_servers_answers());
    }

    private void A_window_reading_more_childrens_output_than_its_pool_has_threads_still_hears_its_language_servers_answers()
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

        Assert.True(Thread.CurrentThread.IsThreadPoolThread, "The window's thread is no longer one of the pool's, which the threads held for its work count on");
        int threads = Environment.ProcessorCount;
        ThreadPool.GetMaxThreads(out int most, out int ports);
        List<Process> children = [];
        List<Task> reads = [];
        try
        {
            Assert.True(ThreadPool.SetMaxThreads(threads + HeldByTheTest, ports), $"The pool could not be held to {threads} threads beside the test's own {HeldByTheTest}");
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
                () => $"With {threads * 3} reads of {children.Count} children's output waiting and the pool held to {threads} threads for the window's work, the language server's answer to an edit never came "
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
