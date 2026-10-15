using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Docking;

namespace Passenger.Tests;

/// <summary>A tool's empty-state guidance stays readable inside that tool's own viewport, including split subpanes.</summary>
[Collection(Alone.Name)]
public sealed class ToolLayoutTests : IDisposable
{
    private readonly string folder = TestFolders.Create("tool-layout-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_tool_hints_wrap_inside_their_own_panes_through_narrow_and_wide_resizes(bool light)
    {
        File.WriteAllText(Path.Combine(folder, "One.cs"), "class One { }\n");
        MainWindow window = new() { Width = 1200, Height = 800, RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.SetRenderScaling(2);
        window.Show();
        window.OpenFolder(folder);
        window.OpenFile("One.cs");
        DockHost dock = window.DockHost;
        dock.Apply(dock.Capture() with { LeftWidth = 200, RightWidth = 200 });
        try
        {
            foreach (string id in new[] { "hierarchy", "git", "tests", "profiler", "find", "structure", "bookmarks", "localHistory", "problems", "debug" })
            {
                window.ShowToolWindow(id);
                ToolWindow tool = dock[id]!;
                int step = 0;
                foreach (double width in new double[] { 1200, 683, 640, 1200 })
                {
                    window.Width = width;
                    Ui.Settle();
                    TextBlock[] hints = [.. tool.Content.GetVisualDescendants().OfType<TextBlock>()
                        .Where(text => text.Classes.Contains("emptyHint") && text.IsEffectivelyVisible && !string.IsNullOrEmpty(text.Text))];
                    Assert.NotEmpty(hints);
                    foreach (TextBlock hint in hints)
                    {
                        foreach (Control viewport in hint.GetVisualAncestors().OfType<Control>().TakeWhile(control => control != tool.Content).Append(tool.Content))
                        {
                            Point at = hint.TranslatePoint(default, viewport)!.Value;
                            Assert.True(at.X >= 0 && at.Y >= 0 && at.X + hint.Bounds.Width <= viewport.Bounds.Width + 0.5
                                && at.Y + hint.Bounds.Height <= viewport.Bounds.Height + 0.5,
                                $"{id} at {width}px: '{hint.Text}' escapes its own {viewport.Bounds.Size} pane at {at} with size {hint.Bounds.Size}");
                        }
                        Assert.True(hint.TextLayout.Width <= hint.Bounds.Width + 1 && hint.TextLayout.Height <= hint.Bounds.Height + 1,
                            $"{id} at {width}px: '{hint.Text}' clips {hint.TextLayout.Width}x{hint.TextLayout.Height} into {hint.Bounds.Size}");
                        Assert.Equal(TextWrapping.Wrap, hint.TextWrapping);
                    }
                    UiAudit.Capture(window, $"tool-hint-{id}-{width:0}-{step++}-{(light ? "light" : "dark")}");
                }
            }
        }
        finally
        {
            window.Close();
        }
    }
}
