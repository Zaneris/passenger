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
/// through a drag.</summary>
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
