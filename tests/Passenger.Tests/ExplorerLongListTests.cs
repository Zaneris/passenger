using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The Explorer with every folder open, thousands of rows long: its scrollbar's thumb stays under the pointer
/// through a drag, a scroll step hands the rows on screen other files rather than making them again, and what the UI
/// thread does for a step, a file written elsewhere or File Nesting turned off costs what it does in a short list.</summary>
public sealed class ExplorerLongListTests : IDisposable
{
    private readonly List<string> folders = [];

    public void Dispose()
    {
        foreach (string folder in folders)
        {
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void Dragging_the_scrollbars_thumb_down_the_long_list_keeps_it_under_the_pointer_to_the_end_and_moves_no_file()
    {
        string folder = Folder(areas: 20, parts: 6, files: 10);
        int files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();
        MainWindow window = Open(folder);
        ListBox rows = window.ExplorerView.Rows();
        Thumb thumb = ThumbOf(rows);
        Point grip = Centre(thumb, window);
        Rect bar = Bounds(thumb.FindAncestorOfType<ScrollBar>()!, window);
        window.MouseMove(grip, RawInputModifiers.None);
        Ui.Settle();
        window.MouseDown(grip, MouseButton.Left);
        Ui.Settle();
        int steps = 0;
        for (Point at = grip + new Point(0, 12); at.Y < bar.Bottom - (thumb.Bounds.Height / 2) - 2; at += new Point(0, 12))
        {
            window.MouseMove(at, RawInputModifiers.LeftMouseButton);
            Ui.Settle();
            steps++;
            double off = Centre(thumb, window).Y - at.Y;
            Assert.True(Math.Abs(off) <= 2,
                $"After {steps} steps of the drag the thumb's middle is {off:0} px from the pointer: the thumb stopped following it, "
                + "the press on it taken for the start of a drag of the row the scrollbar lies over");
        }
        // On past the track's end, where the thumb stops at its end.
        Point end = new(grip.X, bar.Bottom + 20);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        Ui.Settle();
        window.MouseUp(end, MouseButton.Left);
        Ui.Settle();
        Assert.True(steps >= 20, $"The bar's track gave the drag only {steps} steps");
        Assert.True(rows.ContainerFromItem(window.ExplorerView.Tree.Rows[^1]) is not null,
            "The thumb dragged to the end of its track left the list's last row out of sight");
        Assert.Equal(files, Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count());
        Assert.All(Directory.EnumerateDirectories(folder), area => Assert.Equal(6, Directory.EnumerateDirectories(area).Count()));
        window.Close();
    }

    [AvaloniaFact]
    public void A_scroll_step_down_the_long_list_hands_its_rows_other_files_never_making_a_row_again_and_costs_what_it_does_in_a_short_one()
    {
        (long shortStep, int shortMade) = Steps(Folder(areas: 2, parts: 3, files: 8));
        (long longStep, int longMade) = Steps(Folder(areas: 30, parts: 10, files: 10));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A wheel step allocated {shortStep / 1024} KB on the UI thread over 57 rows, {longStep / 1024} KB over 3,331; a page down made {shortMade} and {longMade} rows again");
        Assert.True(shortMade == 0 && longMade == 0,
            $"Scrolling made {shortMade} rows of the short list and {longMade} of the long one again, where each container keeps its row and is handed "
            + "another file: every row scrolled into view was built and styled anew, which a long list pays for on every frame of a thumb's drag");
        Assert.True(longStep <= shortStep + (64 * 1024),
            $"A wheel step allocated {longStep / 1024} KB on the UI thread over 3,331 rows, against {shortStep / 1024} KB over 57: a step costs the list's length");

        // The least a wheel step allocated on the UI thread, and the rows a page down the bar's track made again.
        (long Least, int Made) Steps(string folder)
        {
            MainWindow window = Open(folder);
            ListBox rows = window.ExplorerView.Rows();
            Point middle = Centre(rows, window);
            long least = long.MaxValue;
            for (int i = 0; i < 5; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                window.MouseWheel(middle, new Vector(0, -1));
                Ui.Settle();
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            HashSet<TextBlock> names = [.. rows.GetRealizedContainers().Select(Name)];
            ExplorerNode top = (ExplorerNode)rows.GetRealizedContainers().MinBy(rows.IndexFromContainer)!.DataContext!;
            Ui.Click(ThumbOf(rows).FindAncestorOfType<ScrollBar>()!.GetVisualDescendants().OfType<RepeatButton>().Single(b => b.Name == "PART_PageDownButton"));
            Assert.True(rows.ContainerFromItem(top) is null, "A click on the bar's track below its thumb left the list where it was");
            int made = rows.GetRealizedContainers().Count(row => !names.Contains(Name(row)));
            window.Close();
            return (least, made);
        }

        static TextBlock Name(Control row) => row.GetVisualDescendants().OfType<TextBlock>().First();
    }

    [AvaloniaFact]
    public void A_file_another_program_writes_into_the_long_list_costs_the_UI_thread_what_it_does_in_a_short_one()
    {
        long shortWrite = Writes(Folder(areas: 2, parts: 3, files: 8));
        long longWrite = Writes(Folder(areas: 30, parts: 10, files: 10));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"A file written elsewhere allocated {shortWrite / 1024} KB on the UI thread until it showed, among 57 rows, {longWrite / 1024} KB among 3,331");
        Assert.True(longWrite <= shortWrite + (256 * 1024),
            $"A file written elsewhere allocated {longWrite / 1024} KB on the UI thread until the Explorer showed it among 3,331 rows, against {shortWrite / 1024} KB "
            + "among 57: the refresh made the whole list again rather than its folder's rows");

        // The least the UI thread allocated from a file written in the last folder until its row showed, of three.
        long Writes(string folder)
        {
            MainWindow window = Open(folder);
            string last = PathNames.Canonical(Directory.EnumerateDirectories(Directory.EnumerateDirectories(folder).Order().Last()).Order().Last());
            long least = long.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                string name = $"Written{i}.cs";
                long before = GC.GetAllocatedBytesForCurrentThread();
                File.WriteAllText(Path.Combine(last, name), "class Written { }\n");
                Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Name == name), $"{name} in the Explorer");
                Ui.Settle();
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            window.Close();
            return least;
        }
    }

    [AvaloniaFact]
    public void Turning_file_nesting_off_reopens_every_folder_of_the_long_solution_and_its_chosen_row_costing_per_folder_what_a_short_one_does()
    {
        (long shortCost, int shortOpen) = Refreshes(Solution(areas: 2, parts: 3, files: 8));
        (long longCost, int longOpen) = Refreshes(Solution(areas: 30, parts: 10, files: 10));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Turning file nesting off allocated {shortCost / 1024} KB on the UI thread over {shortOpen} open rows, {longCost / 1024} KB over {longOpen}");
        Assert.True(longCost / longOpen <= shortCost / shortOpen,
            $"Turning file nesting off allocated {longCost / longOpen / 1024} KB on the UI thread for each of {longOpen} open rows, against "
            + $"{shortCost / shortOpen / 1024} KB for each of {shortOpen}: reopening a folder cost the list's length");

        // What the UI thread allocated from File Nesting's OK until the Explorer showed its rows again, and the rows open.
        (long Cost, int Open) Refreshes(string folder)
        {
            MainWindow window = Open(folder);
            ExplorerView view = window.ExplorerView;
            List<string> open = [.. view.Tree.Rows.Where(r => r.IsExpanded).Select(r => r.Path ?? r.Name)];
            ExplorerNode chosen = view.Tree.Rows[^2];
            Ui.ClickRow(window, chosen);
            FileNestingDialog dialog = OpenFileNesting(window);
            Ui.Click(dialog.NestBox);
            long before = GC.GetAllocatedBytesForCurrentThread();
            Ui.Click(dialog.OkButton);
            Ui.WaitFor(() => !dialog.IsVisible, "the File Nesting dialog to close");
            Ui.Settle();
            long cost = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(open, view.Tree.Rows.Where(r => r.IsExpanded).Select(r => r.Path ?? r.Name));
            Assert.True(view.SelectedNodes.Select(n => n.Path).SequenceEqual([chosen.Path]),
                $"Turning file nesting off left [{string.Join(", ", view.SelectedNodes.Select(n => n.Name))}] chosen, where {chosen.Name} was");
            window.Close();
            return (cost, open.Count);
        }
    }

    /// <summary>File Nesting... from the Explorer's options in its header, as a person opens it.</summary>
    private static FileNestingDialog OpenFileNesting(MainWindow window)
    {
        Control pane = window.ExplorerView.GetVisualAncestors().OfType<Control>().First(a => a.GetType().Name == "ToolPane");
        Ui.Click(pane.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Options"));
        Ui.Choose(window.DockHost.OpenOptions, "Appearance", "File Nesting…");
        Ui.WaitFor(() => window.Frontmost is FileNestingDialog, "the File Nesting dialog");
        return (FileNestingDialog)window.Frontmost;
    }

    /// <summary>A folder of areas, each of parts, each of files, with nothing else in it.</summary>
    private string Folder(int areas, int parts, int files)
    {
        string folder = PathNames.Canonical(TestFolders.Create("long-"));
        folders.Add(folder);
        for (int a = 0; a < areas; a++)
        {
            for (int p = 0; p < parts; p++)
            {
                for (int f = 0; f < files; f++)
                {
                    TestFolders.Write(folder, $"Area{a:00}/Part{p:00}/File{f:00}.cs", "class C { }\n");
                }
            }
        }
        return folder;
    }

    /// <summary>A solution of one project, its folder holding areas, each of parts, each of files.</summary>
    private string Solution(int areas, int parts, int files)
    {
        string folder = Folder(0, 0, 0);
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Shop/Shop.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "Shop/Shop.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        for (int a = 0; a < areas; a++)
        {
            for (int p = 0; p < parts; p++)
            {
                for (int f = 0; f < files; f++)
                {
                    TestFolders.Write(folder, $"Shop/Area{a:00}/Part{p:00}/File{f:00}.cs", "class C { }\n");
                }
            }
        }
        return folder;
    }

    /// <summary>The window on a folder, every folder of the Explorer opened as a click on its chevron opens it.</summary>
    private static MainWindow Open(string folder)
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        ExplorerTree tree = window.ExplorerView.Tree;
        for (int i = 0; i < tree.Rows.Count; i++)
        {
            if (tree.Rows[i] is { HasChildren: true, IsExpanded: false } row)
            {
                tree.Toggle(row);
            }
        }
        Ui.Settle();
        return window;
    }

    private static Thumb ThumbOf(ListBox rows) =>
        rows.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Orientation.Vertical && b.IsEffectivelyVisible)
            .GetVisualDescendants().OfType<Thumb>().First();

    private static Point Centre(Control control, Visual within) => Bounds(control, within).Center;

    private static Rect Bounds(Control control, Visual within) =>
        new(control.TranslatePoint(default, within) ?? throw new Xunit.Sdk.XunitException($"{control.GetType().Name} is not laid out"), control.Bounds.Size);
}
