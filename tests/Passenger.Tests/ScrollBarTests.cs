using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.App.ToolWindows;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The real editor, output console and Settings list share thin overlay bars, only for content that needs them.
/// Their full-size pointer target keeps wheel, page and drag scrolling usable without arrows or an opaque track.</summary>
public sealed class ScrollBarTests : IDisposable
{
    private readonly string folder = TestFolders.Create("scrollbars-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Wrapped_text_returns_to_its_original_rows_when_a_resize_stops_needing_the_vertical_thumb()
    {
        string path = Path.Combine(folder, "Threshold.cs");
        File.WriteAllText(path, "short");
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(path);
        Ui.Settle();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            editor.SoftWrap = true;
            // Find the last character that fits in one unscrolled row. A thumb must not change that boundary after
            // narrowing needs more rows: otherwise returning wide can leave every line wrapped and the bar stuck on.
            int length = 1;
            do
            {
                editor.Document.SetText(new string('x', ++length), editor.Selections);
                Ui.Settle();
            }
            while (editor.RowsShown == 1 && length < 250);
            Assert.InRange(length, 2, 249);
            int rows = (int)(editor.Bounds.Height / editor.LineHeight) - 1;
            string text = string.Join('\n', Enumerable.Repeat(new string('x', length - 1), rows));
            editor.Document.SetText(text, editor.Selections);
            editor.GoTo(new TextPosition(0, 0));
            Ui.Settle();
            Assert.Equal(rows, editor.RowsShown);
            AssertHidden(editor);
            foreach (double width in new double[] { 683, 1200, 640, 1200, 683, 1200 })
            {
                window.Width = width;
                Ui.Settle();
                if (width < 1200)
                {
                    Assert.True(editor.RowsShown > rows);
                    Assert.True(Bar(editor, Orientation.Vertical).IsVisible);
                }
                else
                {
                    Assert.Equal(rows, editor.RowsShown);
                    AssertHidden(editor);
                }
                Assert.False(Bar(editor, Orientation.Horizontal).IsVisible);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Editors_consoles_and_lists_show_the_same_arrowless_overlay_only_when_the_content_needs_scrolling(bool light)
    {
        ThemeVariant? before = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.SetRenderScaling(2);
        string path = Path.Combine(folder, "Sample.cs");
        File.WriteAllText(path, "// Short file\n");
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile(path);
        Ui.Settle();
        try
        {
            CodeEditor editor = window.EditorArea.CodeEditor;
            AssertHidden(editor);
            // Problems remain reachable even when a short file has no scrolling thumb.
            editor.SetMarkers([new Marker(new TextRange(3, 5), MarkerKind.Warning, "A warning")]);
            Ui.Settle();
            CodeEditor.StripeMark mark = Assert.Single(editor.StripeMarks, m => m.Kind == StripeMarkKind.Warning);
            Point problem = editor.StripePoint(mark);
            Assert.InRange(problem.X, editor.Bounds.Width - 16, editor.Bounds.Width);
            Click(window, editor, problem);
            Assert.Equal(3, editor.Selections.Primary.Caret);
            UiAudit.Capture(window, $"scrollbars-editor-short-{Theme(light)}");

            editor.Document.SetText(string.Join('\n', Enumerable.Range(0, 120).Select(i => $"// Line {i}: " + new string('x', 200))), editor.Selections);
            Ui.Settle();
            AssertBars(editor);
            editor.Focus();
            ScrollBar vertical = Bar(editor, Orientation.Vertical);
            Thumb thumb = Assert.Single(vertical.GetVisualDescendants().OfType<Thumb>());
            Point start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(0, 90));
            window.MouseUp(start + new Vector(0, 90), MouseButton.Left);
            Ui.Settle();
            Assert.True(editor.ScrollOffset.Y > 0, "Dragging the overlay thumb did not scroll the editor");
            Assert.True(editor.IsFocused, "Dragging the bar took the editor's keys");
            UiAudit.Capture(window, $"scrollbars-editor-long-{Theme(light)}");
            ScrollBar horizontal = Bar(editor, Orientation.Horizontal);
            Thumb across = Assert.Single(horizontal.GetVisualDescendants().OfType<Thumb>());
            Point acrossStart = across.TranslatePoint(new Point(across.Bounds.Width / 2, across.Bounds.Height / 2), window)!.Value;
            window.MouseDown(acrossStart, MouseButton.Left);
            window.MouseMove(acrossStart + new Vector(70, 0));
            window.MouseUp(acrossStart + new Vector(70, 0), MouseButton.Left);
            Ui.Settle();
            Assert.True(editor.ScrollOffset.X > 0, "Dragging the horizontal overlay thumb did not scroll the wide text");
            editor.ScrollTo(default);
            Click(window, vertical, new Point(vertical.Bounds.Width / 2, vertical.Bounds.Height - 20));
            Assert.True(editor.ScrollOffset.Y > 0, "The arrowless track lost page scrolling");
            window.Width = 683;
            Ui.Settle();
            AssertBars(editor);
            UiAudit.Capture(window, $"scrollbars-editor-long-683-{Theme(light)}");
            window.Width = 1200;
            Ui.Settle();
            editor.SoftWrap = true;
            Ui.Settle();
            Assert.True(Bar(editor, Orientation.Vertical).IsVisible);
            Assert.False(Bar(editor, Orientation.Horizontal).IsVisible);
            editor.Document.SetText("short", editor.Selections);
            editor.GoTo(new TextPosition(0, 0));
            Ui.Settle();
            AssertHidden(editor);

            window.ShowToolWindow("run");
            Ui.Settle();
            OutputView output = window.BottomPanel.Run;
            output.Sink.Write("Short output\n");
            output.Flush();
            Ui.Settle();
            AssertHidden(output.Editor);
            output.Sink.Write(string.Join('\n', Enumerable.Range(0, 120).Select(i => $"Output {i}: " + new string('o', 240))));
            output.Flush();
            Ui.Settle();
            AssertBars(output.Editor);
            Point over = output.Editor.TranslatePoint(new Point(80, 80), window)!.Value;
            double atEnd = output.Editor.ScrollOffset.Y;
            window.MouseWheel(over, new Vector(0, 3));
            Ui.Settle();
            Assert.True(output.Editor.ScrollOffset.Y < atEnd, "The console's wheel stopped reaching its text through the overlay");
            UiAudit.Capture(window, $"scrollbars-console-long-{Theme(light)}");
            window.Width = 683;
            Ui.Settle();
            AssertBars(output.Editor);
            UiAudit.Capture(window, $"scrollbars-console-long-683-{Theme(light)}");
            window.Width = 1200;
            output.Clear();
            Ui.Settle();
            AssertHidden(output.Editor);

            SettingsDialog settings = window.ShowSettings();
            settings.SetRenderScaling(2);
            settings.Page = SettingsDialog.KeymapPage;
            Ui.Settle();
            ScrollViewer list = settings.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "SettingsPageScroll");
            ScrollBar listBar = Assert.Single(list.GetVisualDescendants().OfType<ScrollBar>(), b => b.Orientation == Orientation.Vertical && b.TemplatedParent == list);
            Assert.True(listBar.IsVisible);
            Check(listBar);
            ScrollContentPresenter presenter = Assert.Single(list.GetVisualDescendants().OfType<ScrollContentPresenter>(), p => p.TemplatedParent == list);
            Assert.Equal(list.Bounds.Width, presenter.Bounds.Width, 1);
            double old = list.Offset.Y;
            Point inList = list.TranslatePoint(new Point(100, 200), settings)!.Value;
            settings.MouseWheel(inList, new Vector(0, -3));
            Ui.Settle();
            Assert.True(list.Offset.Y > old);
            UiAudit.Capture(settings, $"scrollbars-settings-list-{Theme(light)}");
            settings.Close();
        }
        finally
        {
            window.Close();
            Application.Current.RequestedThemeVariant = before;
        }
    }

    private static string Theme(bool light) => light ? "light" : "dark";

    private static ScrollBar Bar(CodeEditor editor, Orientation orientation) =>
        Assert.Single(editor.GetVisualChildren().OfType<ScrollBar>(), b => b.Orientation == orientation);

    private static void AssertHidden(CodeEditor editor)
    {
        Assert.False(Bar(editor, Orientation.Vertical).IsVisible);
        Assert.False(Bar(editor, Orientation.Horizontal).IsVisible);
    }

    private static void AssertBars(CodeEditor editor)
    {
        foreach (ScrollBar bar in editor.GetVisualChildren().OfType<ScrollBar>())
        {
            Assert.True(bar.IsVisible);
            Check(bar);
        }
    }

    /// <summary>The same shared rule can hold every bar opened by the UI inventory to this shape.</summary>
    public static void Check(ScrollBar bar)
    {
        Assert.True(bar.AllowAutoHide);
        Assert.DoesNotContain(bar.GetVisualDescendants().OfType<RepeatButton>(), b => b.IsEffectivelyVisible && b.Name is "PART_LineUpButton" or "PART_LineDownButton");
        Thumb thumb = Assert.Single(bar.GetVisualDescendants().OfType<Thumb>());
        Assert.Equal(Matrix.Identity, thumb.RenderTransform?.Value ?? Matrix.Identity);
        Assert.InRange(bar.Orientation == Orientation.Vertical ? thumb.Bounds.Width : thumb.Bounds.Height, 1, 6);
        Assert.InRange(bar.Orientation == Orientation.Vertical ? bar.Bounds.Width : bar.Bounds.Height, 10, 12);
        Assert.All(bar.GetVisualDescendants().OfType<Border>().Where(b => b.Name is "VerticalRoot" or "HorizontalRoot"), b =>
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(b.Background).Color));
    }

    private static void Click(Window window, Control control, Point point)
    {
        Point at = control.TranslatePoint(point, window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Ui.Settle();
    }
}
