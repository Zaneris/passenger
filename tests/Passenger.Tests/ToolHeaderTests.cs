using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Every tool window's header, as a person narrows its place: its title, its own tools (the Explorer's view
/// switch, the Terminal's tabs) and its buttons stay apart and whole, the buttons one pitch apart, what has no room going
/// whole, down to the least its place may be; and its Options open their menu on every click that finds it closed.</summary>
public sealed class ToolHeaderTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("tool-headers-"));

    public ToolHeaderTests()
    {
        TestFolders.Write(folder, "Shop.App/Shop.App.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Shop.App/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Shop.App/Shop.App.csproj\" />\n</Solution>\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    /// <summary>Every tool window's header keeps its parts apart and whole from a wide place down to its least, and its
    /// options open their menu on every click that finds it closed, as one flow, each step the check one test made in
    /// an instance of its own.</summary>
    [AvaloniaFact]
    public void A_tool_windows_header_keeps_its_parts_whole_and_its_options_open_their_menu()
    {
        Flow.StepApart<ToolHeaderTests>("every tool windows header keeps its parts apart and whole from a wide place down to its least", t => t.Every_tool_windows_header_keeps_its_parts_apart_and_whole_from_a_wide_place_down_to_its_least());
        Flow.StepApart<ToolHeaderTests>("each tool windows options open their menu on every click that finds it closed", t => t.Each_tool_windows_options_open_their_menu_on_every_click_that_finds_it_closed());
    }

    /// <summary>Each window, docked on the left as Move to puts it, its side dragged narrower step by step to as far as it
    /// goes: at every width its header's parts are apart, inside it and uncut, and its Options still open their menu there.</summary>
    private void Every_tool_windows_header_keeps_its_parts_apart_and_whole_from_a_wide_place_down_to_its_least()
    {
        MainWindow window = Open();
        try
        {
            DockHost dock = window.DockHost;
            List<string> wrong = [];
            foreach (ToolWindow tool in dock.Windows.ToList())
            {
                if (tool.Anchor != ToolAnchor.LeftTop)
                {
                    dock.Move(tool, ToolAnchor.LeftTop);
                }
                dock.Show(tool.Id);
                Ui.Settle();
                if (tool.Id == ToolWindowIds.Terminal)
                {
                    Ui.WaitFor(() => window.TerminalPanel.Active is { IsRunning: true }, "the shell to start");
                }
                Assert.True(tool.IsShown && tool.Mode == ToolMode.Docked, $"{tool.Title} did not show docked on the left");
                double was = double.PositiveInfinity;
                foreach (double width in (double[])[360, 330, 300, 270, 240, 210, 180, 150, 120, 0])
                {
                    DragSide(window, width);
                    double now = dock.ColumnDefinitions[1].ActualWidth;
                    wrong.AddRange(Apart(HeaderOf(tool), $"{tool.Title} {now:0} wide"));
                    if (width == 0)
                    {
                        Assert.True(now < was, $"{tool.Title}'s side, {was:0} wide, did not narrow as its divider was dragged to the stripe");
                        Button options = OptionsOf(tool);
                        Ui.Click(options);
                        Assert.True(OpensFrom(dock, options), $"{tool.Title}'s Options, its side at its least ({now:0} wide), opened no menu");
                        Ui.Click(options);
                    }
                    was = now;
                }
                DragSide(window, 300);
            }
            Assert.True(wrong.Count == 0, $"Headers draw parts over one another or cut them: {string.Join("; ", wrong)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Each window's Options clicked several times over open its menu on each click that found it closed and close
    /// it on each that found it open; and a click on them while another side's window's menu is open opens theirs, the
    /// click that closes the other passed on to them.</summary>
    private void Each_tool_windows_options_open_their_menu_on_every_click_that_finds_it_closed()
    {
        MainWindow window = Open();
        try
        {
            DockHost dock = window.DockHost;
            List<string> wrong = [];
            foreach (ToolWindow tool in dock.Windows.ToList())
            {
                // Another side's window, whose menu lies over none of this one's header (the Explorer's covers the one below).
                string beside = tool.Anchor is ToolAnchor.LeftTop or ToolAnchor.LeftBottom ? ToolWindowIds.Run : ToolWindowIds.Explorer;
                dock.Show(tool.Id);
                dock.Show(beside);
                Ui.Settle();
                ToolWindow other = dock.Windows.First(w => w.Id == beside && w != tool && w.IsShown && w.Mode == ToolMode.Docked);
                Button options = OptionsOf(tool);
                List<string> seen = [];
                for (int click = 0; click < 4; click++)
                {
                    bool was = OpensFrom(dock, options);
                    Ui.Click(options);
                    bool now = OpensFrom(dock, options);
                    seen.Add($"{(was ? "open" : "closed")} to {(now ? "open" : "closed")}");
                    if (now == was)
                    {
                        wrong.Add($"{tool.Title}'s Options, clicked {click + 1} times, went {string.Join(", ", seen)}");
                        break;
                    }
                }
                Button others = OptionsOf(other);
                Ui.Click(others);
                Ui.Click(options);
                if (!OpensFrom(dock, options) || OpensFrom(dock, others))
                {
                    wrong.Add($"{tool.Title}'s Options, clicked while {other.Title}'s menu was open, left "
                        + $"{(OpensFrom(dock, others) ? $"{other.Title}'s" : OpensFrom(dock, options) ? "its own" : "no")} menu open");
                }
                dock.OpenOptions?.Hide();
                Ui.Settle();
            }
            Assert.True(wrong.Count == 0, $"Options opened no menu on a click that found it closed: {string.Join("; ", wrong)}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A window on the folder, with no C# language server, every header's Close shown as the Closed Tool Windows
    /// setting that takes a closed window off its stripe shows it, and the Terminal's shell the system's plain one.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        // Every window's header, whatever this folder's projects bring.
        Ui.ServeEveryWindow(window);
        window.ApplySettings(window.Settings with { ClosedToolWindows = ClosedToolWindows.LeaveStripe });
        window.TerminalPanel.Shell = OperatingSystem.IsWindows() ? ("cmd.exe", []) : ("/bin/sh", []);
        Ui.Settle();
        Assert.True(window.ExplorerView.ViewSwitchButton.IsVisible, "The Explorer shows no view switch on a folder with a solution");
        return window;
    }

    /// <summary>The left side dragged by its divider until it is <paramref name="width"/> wide, or as narrow as it goes.</summary>
    private static void DragSide(MainWindow window, double width)
    {
        DockHost dock = window.DockHost;
        Point grip = Ui.Centre(Divider(dock, c => Grid.GetColumn(c) == 2), window) + new Point(3, 0);
        double side = dock.ColumnDefinitions[1].ActualWidth;
        Ui.Drag(window, grip, new Point(Math.Max(1, grip.X + width - side), grip.Y));
    }

    /// <summary>What is wrong with a header as it is drawn: a part shown (its title, its own tools, a button not gone whole)
    /// over another or past its edge, words or an icon cut by the part holding them, its title or Options gone, or its
    /// buttons not one pitch apart.</summary>
    private static List<string> Apart(Border header, string where)
    {
        List<string> wrong = [];
        Rect bounds = new(header.Bounds.Size);
        List<(string Name, Rect Place, Control Part)> shown = [.. Parts(header)
            .Where(p => p.IsVisible && !ShrinkingRow.HasGone(p) && p.Bounds.Width > 0.5)
            .Select(p => (Name(p), Placed(p, header), p))];
        if (!shown.Any(p => p.Part is TextBlock))
        {
            wrong.Add($"{where}: its title is gone");
        }
        if (!shown.Any(p => p.Name == "Options"))
        {
            wrong.Add($"{where}: its Options are gone");
        }
        foreach ((string name, Rect place, Control part) in shown)
        {
            if (place.X < -0.5 || place.Right > bounds.Right + 0.5)
            {
                wrong.Add($"{where}: {name} stands at {place}, past the header's {bounds}");
            }
            foreach (Control inside in part.GetVisualDescendants().OfType<Control>().Where(c => c is PathIcon or TextBlock && c.IsEffectivelyVisible && c.Bounds.Width > 0.5))
            {
                Rect drawn = Placed(inside, header);
                if (drawn.X < place.X - 0.5 || drawn.Right > place.Right + 0.5)
                {
                    wrong.Add($"{where}: {name}'s {inside.GetType().Name} at {drawn} is cut by {name} at {place}");
                }
            }
        }
        for (int i = 0; i < shown.Count; i++)
        {
            for (int j = i + 1; j < shown.Count; j++)
            {
                if (shown[i].Place.Intersect(shown[j].Place).Width > 0.5)
                {
                    wrong.Add($"{where}: {shown[i].Name} at {shown[i].Place} and {shown[j].Name} at {shown[j].Place} overlap");
                }
            }
        }
        double[] starts = [.. shown.Where(p => p.Part.Parent is Control row && DockPanel.GetDock(row) == Dock.Right).Select(p => p.Place.X).Order()];
        double[] pitches = [.. starts.Zip(starts.Skip(1), (a, b) => b - a)];
        if (pitches.Length > 0 && pitches.Max() - pitches.Min() > 0.5)
        {
            wrong.Add($"{where}: its buttons stand {string.Join(", ", pitches.Select(p => p.ToString("0.#")))} apart, not one pitch");
        }
        return wrong;
    }

    /// <summary>The header's parts: the title and its own tools at its start, its buttons at its end, in their order.</summary>
    private static IEnumerable<Control> Parts(Border header) =>
        ((ToolbarRow)header.Child!).Children.OfType<ShrinkingRow>().OrderBy(r => DockPanel.GetDock(r) == Dock.Right).SelectMany(r => r.Children);

    private static string Name(Control part) => part switch
    {
        TextBlock title => $"the title '{title.Text}'",
        Button button when ToolTip.GetTip(button) is string tip => tip.Split(" (")[0],
        _ => "its own tools",
    };

    private static Rect Placed(Control control, Visual within) =>
        new(control.TranslatePoint(default, within) ?? throw new Xunit.Sdk.XunitException($"{control} is not laid out in the header"), control.Bounds.Size);

    /// <summary>Whether the dock's Options menu is open, opened from these Options.</summary>
    private static bool OpensFrom(DockHost dock, Button options) => dock.OpenOptions is { IsOpen: true } menu && menu.Target == options;

    private static Border HeaderOf(ToolWindow tool) =>
        PaneOf(tool).GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(StyleClasses.ToolHeader));

    private static Button OptionsOf(ToolWindow tool) =>
        HeaderOf(tool).GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Options");

    private static DockPanel PaneOf(ToolWindow tool) =>
        tool.Content.GetVisualAncestors().OfType<DockPanel>().First(p => p.Classes.Contains(StyleClasses.ToolPane));

    private static GridSplitter Divider(DockHost dock, Func<Control, bool> place) =>
        dock.Children.First(place).GetVisualDescendants().OfType<GridSplitter>().Single();
}
