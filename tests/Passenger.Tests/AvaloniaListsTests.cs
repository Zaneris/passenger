using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.LiveTree;
using Passenger.App.Profiling;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The long lists an Avalonia app fills, scrolled with the wheel as <see cref="LongListsTests"/> scrolls the IDE's
/// own: its profiled frames' layout by control and its Live Visual Tree, each of a hundred controls against a thousand,
/// from tests/Debuggees/FramesApp built once (<see cref="ListsApp"/>), whose "--lists" puts a panel of a hundred borders
/// and one of a thousand text blocks in its window. Profiled and debugged through the window, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class AvaloniaListsTests : IClassFixture<AvaloniaListsTests.ListsApp>
{
    private readonly ListsApp app;

    public AvaloniaListsTests(ListsApp app)
    {
        this.app = app;
        app.Reset();
    }

    [AvaloniaFact]
    public void A_scroll_step_down_a_thousand_controls_of_a_type_on_the_frames_page_hands_the_rows_other_controls_never_making_one_again()
    {
        MainWindow window = Opened();
        try
        {
            Ui.Launched(window, window.ProfileAsync("--frames 2 --lists"), "the app to build and start under the profiler", TimeSpan.FromMinutes(4));
            ProfilerView view = window.ProfilerView;
            Ui.WaitFor(() => view.Snapshot is not null, () => $"the app's end to show its snapshot ({view.Status})", TimeSpan.FromMinutes(2));
            view.ShowPage(ProfilerPage.Frames);
            Ui.Settle();
            FramesView frames = view.FramesPage;
            LongListsTests.Holds("The Frames page's layout", "controls", 100, 1000, count =>
            {
                LayoutRow type = frames.Rows.FirstOrDefault(r => r.Depth == 0 && r.Cost.Name == (count == 100 ? "Border" : "TextBlock"))
                    ?? throw new Xunit.Sdk.XunitException($"The Frames page has no row for the app's {count} controls: {string.Join(", ", frames.Rows.Where(r => r.Depth == 0).Select(r => r.Cost.Name))}");
                Assert.True(type.Cost.Controls.Count >= count, $"The Frames page's {type.Cost.Name} lists {type.Cost.Controls.Count} controls, not the app's {count}");
                Press(window, frames.Table, type, "Right");
                Ui.WaitFor(() => frames.Rows.Count(r => r.Depth == 1) >= count, () => $"the {type.Cost.Name} row to open to its controls, where the page lists {frames.Rows.Count} rows");
                return (frames.Table, () => Press(window, frames.Table, type, "Left"));
            });
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_scroll_step_down_a_panels_thousand_elements_in_the_live_visual_tree_hands_the_rows_other_elements_never_making_one_again()
    {
        MainWindow window = Opened();
        try
        {
            Ui.Launched(window, window.DebugAsync("--lists"), "the app to build and start under the debugger", TimeSpan.FromMinutes(4));
            LiveTreeView tree = window.LiveTree;
            Ui.WaitFor(() => tree.Rows.Any(r => r.Element.Name == "Thousand"),
                () => $"the Live Visual Tree to show the app's window ({window.LiveApp?.State} {window.LiveApp?.Problem})", TimeSpan.FromMinutes(2));
            LongListsTests.Holds("The Live Visual Tree", "elements", 100, 1000, count =>
            {
                LiveTreeRow panel = tree.Rows.Single(r => r.Element.Name == (count == 100 ? "Hundred" : "Thousand"));
                HashSet<long> under = [.. panel.Element.Children.Select(c => c.Id)];
                Assert.Equal(count, under.Count);
                Press(window, tree.Tree, panel, "Right");
                Ui.WaitFor(() => tree.Rows.Count(r => under.Contains(r.Element.Id)) == count, () => $"{panel.Element.Name}'s {count} elements in the tree, where it shows {tree.Rows.Count} rows");
                return (tree.Tree, () => Press(window, tree.Tree, tree.Rows.Single(r => r.Element.Name == panel.Element.Name), "Left"));
            });
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debugging to end");
        }
        finally
        {
            window.Close();
        }
    }

    private MainWindow Opened() => Ui.OpenFolder(PathNames.Canonical(app.Folder), 1400, 900, languageServer: false);

    /// <summary>A key pressed on a row of a list, the row chosen and given the keys first as a click on it does.</summary>
    private static void Press(MainWindow window, ListBox list, object row, string key)
    {
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        Ui.Settle();
        Assert.True(list.ContainerFromItem(row)?.Focus() == true, "The row could not take the keys");
        Ui.Press(window, key);
    }

    /// <summary>tests/Debuggees/FramesApp in a folder of its own, restored once for the class's tests.</summary>
    public sealed class ListsApp : SharedProject
    {
        public ListsApp()
            : base("lists-avalonia-")
        {
            AvaloniaApp.Debuggee(Folder, "FramesApp");
            // The first build makes bin, which a reset would take for a file the test added: made now, the folder stays as reset.
            Directory.CreateDirectory(PathOf("bin"));
        }
    }
}
