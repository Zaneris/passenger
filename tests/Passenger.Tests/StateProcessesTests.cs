using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App.Docking;
using Passenger.App.State;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Two Passengers on one state (an agent's --mcp beside the person's) changing one document at once while a third
/// reads it: each change is merged over the other's write, and no read is in the way of a write, so none is lost. The
/// writers are this test run again; the test's own process reads.</summary>
public sealed class StateProcessesTests : IDisposable
{
    /// <summary>Names a writer process's state folder and the names it adds, in the test that starts it.</summary>
    [IdentifierHome]
    internal const string WriterVariable = "PASSENGER_TEST_STATE_WRITER";

    private const int Writes = 150;

    private readonly string folder = TestFolders.Create("state-processes-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Two_processes_changing_one_state_document_at_once_while_a_third_reads_it_each_keep_every_change_of_theirs()
    {
        if (Environment.GetEnvironmentVariable(WriterVariable) is { Length: > 0 } writer)
        {
            Write(writer);
            return;
        }
        string state = Path.Combine(folder, "state");
        Directory.CreateDirectory(state);
        TimeSpan limit = TimeSpan.FromMinutes(2);
        Task<ChildResult>[] writers = [.. new[] { "a", "b" }.Select(name => Waiting.Apart(() =>
        {
            System.Diagnostics.ProcessStartInfo start = ChildProcesses.DotnetCommand(["exec", typeof(StateProcessesTests).Assembly.Location,
                "-method", $"{typeof(StateProcessesTests).FullName}.{nameof(Two_processes_changing_one_state_document_at_once_while_a_third_reads_it_each_keep_every_change_of_theirs)}",
                "-noLogo", "-noColor"], folder);
            start.Environment[WriterVariable] = $"{name}{Path.PathSeparator}{state}";
            return ChildProcesses.RunAsync(start, limit: limit.Scaled());
        }))];
        Ui.PumpUntil(() => File.Exists(Ready(state, "a")) && File.Exists(Ready(state, "b")) || writers.Any(w => w.IsCompleted), "both writers to be ready", limit);
        StateStore store = StateStore.Open(state);
        try
        {
            File.WriteAllText(Path.Combine(state, "go"), string.Empty);
            Task<int> reading = Waiting.Apart(() =>
            {
                int reads = 0;
                for (; !writers.All(w => w.IsCompleted); reads++)
                {
                    store.Layout.Peek();
                }
                return reads;
            });
            foreach (Task<ChildResult> writing in writers)
            {
                ChildResult ended = Ui.AwaitPumped(writing, "a writer to end", limit + TimeSpan.FromSeconds(10));
                Assert.True(ended.Succeeded, $"A writer failed:\n{ended.Output}{ended.Error}");
            }
            Assert.True(Ui.AwaitPumped(reading, "the reads to end") > 0, "The test's process never read the document while the writers changed it");
        }
        finally
        {
            store.Release();
        }
        JsonObject windows = JsonNode.Parse(File.ReadAllText(Path.Combine(state, "layout.json")))!["windows"]!.AsObject();
        foreach (string name in new[] { "a", "b" })
        {
            List<int> lost = [.. Enumerable.Range(0, Writes).Where(i => !windows.ContainsKey(Key(name, i)))];
            Assert.True(lost.Count == 0, $"Writer {name} lost {lost.Count} of its {Writes} changes to the other's writes over them: {string.Join(", ", lost.Take(20))}");
        }
    }

    /// <summary>A writer: once both are ready and told to go, adds its windows to the layout one change at a time.</summary>
    private static void Write(string writer)
    {
        string[] parts = writer.Split(Path.PathSeparator, 2);
        (string name, string state) = (parts[0], parts[1]);
        StateStore store = StateStore.Open(state);
        try
        {
            File.WriteAllText(Ready(state, name), string.Empty);
            Ui.PumpUntil(() => File.Exists(Path.Combine(state, "go")), "the test to say go", TimeSpan.FromMinutes(1));
            for (int i = 0; i < Writes; i++)
            {
                string key = Key(name, i);
                store.Layout.ChangeNow(layout => layout with { Windows = new(layout.Windows) { [key] = new DockedWindow(ToolAnchor.LeftTop, i, Shown: true) } });
            }
        }
        finally
        {
            store.Release();
        }
    }

    private static string Ready(string state, string name) => Path.Combine(state, $"ready-{name}");

    private static string Key(string name, int i) => $"{name}{i.ToString(CultureInfo.InvariantCulture)}";
}
