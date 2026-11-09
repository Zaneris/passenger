using System.Diagnostics;
using System.Text.RegularExpressions;
using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>A child run to its end with its output read as bytes as it comes (<see cref="ChildProcesses.RunAsync{T}"/>, as
/// every git call is): a read that fails part of the way ends the child and fails the run with what the read threw, which
/// git's runner takes as a failure.</summary>
[Collection(Beside.Name)]
public sealed class ChildReadsTests
{
    /// <summary>A child writing without end, whose output's read fails after its first 64 KB, is ended, and the run fails
    /// with the read's IOException: neither the child, writing to a pipe nobody reads, nor the run waits for good.</summary>
    [Fact]
    public async Task A_read_of_a_childs_output_that_fails_part_of_the_way_ends_the_child_and_fails_the_run_with_what_it_threw()
    {
        ProcessStartInfo start = ChildProcesses.Command(Debuggees.Apphost("PassengerCases"), ["chatter"]);
        int child = 0;
        void Started(object? sender, ChildStart started)
        {
            if (ReferenceEquals(started.Start, start))
            {
                child = started.ProcessId;
            }
        }
        ChildProcesses.Started += Started;
        try
        {
            Task<ChildRead<int>> run = ChildProcesses.RunAsync(start, FailingAsync, cancel: TestContext.Current.CancellationToken);
            Assert.True(await Waiting.Within(run, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken),
                "The run whose read failed never ended: its child, no longer read, was left writing");
            IOException failed = await Assert.ThrowsAsync<IOException>(() => run);
            Assert.Equal("The output broke off", failed.Message);
            Assert.True(child > 0, "The child was never started");
            Assert.True(Processes.Gone(child), $"The child whose output's read failed ({Processes.Describe(child)}) was left running");
        }
        finally
        {
            ChildProcesses.Started -= Started;
        }

        static async Task<int> FailingAsync(Stream output)
        {
            byte[] taken = new byte[64 * 1024];
            int read = 0;
            int now;
            while (read < taken.Length && (now = await output.ReadAsync(taken.AsMemory(read))) > 0)
            {
                read += now;
            }
            throw new IOException("The output broke off");
        }
    }

    /// <summary>A child's output and errors are read only through ChildProcesses (Output, Errors, OutputStream, ErrorStream,
    /// Hear, RunAsync): its readers, which the process's disposal never closes under a read, are the only streams a child
    /// started on a desktop of its own has. Read from src's C# as written, ChildProcesses itself passed over.</summary>
    [Fact]
    public void No_childs_output_or_errors_are_read_past_ChildProcesses()
    {
        string root = Debuggees.Root;
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == "src/Passenger.Core/ChildProcesses.cs" || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            string[] lines = File.ReadAllLines(file);
            for (int line = 0; line < lines.Length; line++)
            {
                if (Regex.IsMatch(lines[line], @"\.Standard(?:Output|Error)\b"))
                {
                    found.Add($"{relative}:{line + 1}: {lines[line].Trim()}");
                }
            }
        }
        Assert.True(found.Count == 0, $"A child's output or errors read from its process misses the streams of one started on a desktop of its own, and a read "
            + $"of them may be closed under it as the process is disposed: read them through ChildProcesses (Output, Errors, OutputStream, ErrorStream, "
            + $"Hear, RunAsync):\n{string.Join('\n', found)}");
    }
}
