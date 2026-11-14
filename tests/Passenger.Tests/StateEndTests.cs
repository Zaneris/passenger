using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App.Docking;
using Passenger.App.State;

namespace Passenger.Tests;

/// <summary>Passenger's end writing again the state's changes whose writes were refused, held to its 2 s however long
/// another Passenger holds a file's lock: held to a time, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class StateEndTests : IDisposable
{
    private readonly string settings = TestFolders.Create("settings-");

    public void Dispose() => TestFolders.Delete(settings);

    /// <summary>A change refused as Passenger closes whose lock another Passenger then holds and never lets go (one that
    /// hangs) is given up within the end's 2 s and logged lost, where each try waited out its whole wait for the lock and
    /// a try of the document's own held it longer still.</summary>
    [AvaloniaFact]
    public void Passengers_end_gives_up_within_its_two_seconds_a_change_whose_lock_another_Passenger_holds()
    {
        StateStore store = StateStore.Open(settings);
        StateDocument<DockLayout> layout = store.Layout;
        layout.ChangeNow(kept => kept with { Windows = new(kept.Windows) { ["first"] = new DockedWindow(ToolAnchor.LeftTop, 0, Shown: true) } });
        FileInfo file = new(layout.Path) { IsReadOnly = true };
        try
        {
            layout.Set(layout.Value with { Windows = new(layout.Value.Windows) { ["closing"] = new DockedWindow(ToolAnchor.LeftTop, 1, Shown: true) } });
            store.Release();
            Assert.True(Ui.IdeLog().Contains($"{layout.Path} could not be written as it was flushed", StringComparison.Ordinal),
                "The flush as Passenger closed went ahead though layout.json was read-only, so nothing here was refused");
            TimeSpan took;
            using (new FileStream(layout.Path + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                long began = Stopwatch.GetTimestamp();
                StateStore.LandWaiting();
                took = Stopwatch.GetElapsedTime(began);
            }
            Assert.True(took < TimeSpan.FromSeconds(5),
                $"Passenger's end waited {took.TotalSeconds:0.0} s for a change whose lock another Passenger held, as long as a write waits for the lock, where it gives up within 2 s");
            Assert.Contains($"{layout.Path} could not be written before Passenger ended", Ui.IdeLog(), StringComparison.Ordinal);
        }
        finally
        {
            file.IsReadOnly = false;
            StateStore.LandWaiting();
        }
        Assert.True(JsonNode.Parse(File.ReadAllText(layout.Path))?["windows"]?["closing"] is not null,
            "The change given up while its lock was held did not land once let go, so it waits on in the store for no end");
    }
}
