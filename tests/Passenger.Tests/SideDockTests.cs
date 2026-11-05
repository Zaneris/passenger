using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.ToolWindows;

namespace Passenger.Tests;

/// <summary>A tool window made for the bottom, moved to a side dock: its top bar's parts wrap where they don't fit, none drawn
/// over another or past the window's edge, and its two panes stand one over the other, each as wide as the window.</summary>
public sealed class SideDockTests
{
    [AvaloniaTheory]
    [InlineData(ToolWindowIds.Tests)]
    [InlineData(ToolWindowIds.Problems)]
    [InlineData(ToolWindowIds.NuGet)]
    [InlineData(ToolWindowIds.Git)]
    [InlineData(ToolWindowIds.Debug)]
    [InlineData(ToolWindowIds.Containers)]
    [InlineData(ToolWindowIds.Aspire)]
    public void At_a_side_no_part_of_a_tool_windows_top_bar_is_drawn_over_another_or_past_its_edge(string id)
    {
        MainWindow window = Open();
        try
        {
            Control content = AtTheSide(window, id);
            ToolFlow bar = content.GetVisualDescendants().OfType<ToolFlow>().First(b => b.IsEffectivelyVisible);
            Control[] parts = [.. bar.Children.Where(c => c.IsVisible && c.Bounds.Width > 0)];
            Assert.True(parts.Length > 1, $"The {id} window's bar shows {parts.Length} parts at a side");
            foreach (Control part in parts)
            {
                Assert.True(part.Bounds.Right <= bar.Bounds.Width + 0.5, $"{part} in the {id} window's bar ends at {part.Bounds.Right}, past the bar's {bar.Bounds.Width}");
                Control? over = parts.FirstOrDefault(other => other != part && other.Bounds.Intersects(part.Bounds));
                Assert.True(over is null, $"{part} at {part.Bounds} in the {id} window's bar is drawn over {over} at {over?.Bounds}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(ToolWindowIds.Tests)]
    [InlineData(ToolWindowIds.NuGet)]
    [InlineData(ToolWindowIds.Debug)]
    [InlineData(ToolWindowIds.Containers)]
    public void At_a_side_a_tool_windows_panes_stand_one_over_the_other_each_as_wide_as_the_window_and_back_at_the_bottom_side_by_side(string id)
    {
        MainWindow window = Open();
        try
        {
            ToolAnchor home = window.DockHost[id].Anchor;
            PaneSplit panes = AtTheSide(window, id).GetVisualDescendants().OfType<PaneSplit>().First(p => p.IsEffectivelyVisible);
            Assert.True(panes.IsStacked, $"The {id} window's panes stand side by side in {panes.Bounds.Width} at a side");
            Control[] shown = [.. panes.Children.Where(c => c is not HairlineDivider)];
            Assert.All(shown, pane => Assert.True(Math.Abs(pane.Bounds.Width - panes.Bounds.Width) < 1, $"A pane of the {id} window is {pane.Bounds.Width} wide of {panes.Bounds.Width}"));
            Assert.True(shown[0].Bounds.Bottom <= shown[1].Bounds.Top + 1, $"The {id} window's panes overlap: {shown[0].Bounds} and {shown[1].Bounds}");

            window.DockHost.Move(window.DockHost[id], home);
            window.DockHost.Show(id);
            Ui.Settle();
            Assert.False(panes.IsStacked, $"The {id} window's panes stand one over the other at the bottom, {panes.Bounds.Width} wide");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Unit Tests' divider moves its panes in whichever way they stand: the rows at a side.</summary>
    [AvaloniaFact]
    public void At_a_side_the_divider_between_the_panes_moves_them_up_and_down()
    {
        MainWindow window = Open();
        try
        {
            PaneSplit panes = AtTheSide(window, ToolWindowIds.Tests).GetVisualDescendants().OfType<PaneSplit>().First();
            Control upper = panes.Children[0];
            double before = upper.Bounds.Height;
            Point grip = Ui.Centre(panes.Divider, window) + new Point(0, 3);
            Ui.Drag(window, grip, grip + new Point(0, 60));
            Assert.True(Math.Abs(upper.Bounds.Height - (before + 60)) < 1, $"The upper pane was {before} tall, and {upper.Bounds.Height} once its divider was dragged 60 px down");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A tool window moved to the left, shown there at the side's own width: its content.</summary>
    private static Control AtTheSide(MainWindow window, string id)
    {
        DockHost dock = window.DockHost;
        dock.Move(dock[id], ToolAnchor.LeftTop);
        dock.Show(id);
        Ui.Settle();
        return dock[id].Content;
    }

    private static MainWindow Open()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = Passenger.App.State.StateStore.DefaultRoot };
        window.Show();
        Ui.Settle();
        return window;
    }
}
