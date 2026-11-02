using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;

namespace Passenger.Tests;

/// <summary>A person's names are shown whole, their underscores too: the status bar's path, the editor's tabs, the
/// Explorer's rows and a tab's menu draw a file's and its folder's names as written, and the menu bar names the open
/// folder as the platform shows it, none of its underscores taken for an access key's mark.</summary>
public sealed class NamesShownTests : IDisposable
{
    private readonly string folder = TestFolders.Create("names_shown_");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void The_status_bars_path_tabs_rows_and_menus_show_a_names_underscores_whole()
    {
        string spinner = TestFolders.Write(folder, "game_scripts/test_spinner.gd", "extends Node\n");
        string init = TestFolders.Write(folder, "py_pkg/__init__.py", "\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(spinner);
            Ui.WaitFor(() => Path(window) is ["game_scripts", "test_spinner.gd"],
                () => $"the status bar's path to show game_scripts › test_spinner.gd whole, where it shows {string.Join(" › ", Path(window))}");
            window.OpenFile(init);
            Ui.WaitFor(() => Path(window) is ["py_pkg", "__init__.py"],
                () => $"the status bar's path to show py_pkg › __init__.py whole, where it shows {string.Join(" › ", Path(window))}");
            window.ExplorerView.Reveal(spinner);
            Ui.Settle();
            AllWhole(window, "The window");
            foreach (string name in (string[])["test_spinner.gd", "__init__.py", "game_scripts", "py_pkg"])
            {
                Assert.True(window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && Ui.Drawn(t) == name),
                    $"Nothing in the window draws {name} whole, where its tab and the Explorer's row name it");
            }

            Control tab = window.EditorArea.ActiveGroup.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == "__init__.py" && t.Classes.Contains(StyleClasses.TabName)).FindAncestorOfType<Border>()!;
            tab.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs());
            Ui.Settle();
            Assert.True(tab.ContextMenu?.IsOpen == true, "The tab's menu did not open");
            AllWhole(tab.ContextMenu!, "The tab's menu");
            tab.ContextMenu!.Close();

            string title = System.IO.Path.GetFileName(folder);
            string[] headers = [.. Headers(window.MenuBar)];
            Assert.Contains(title, headers.Select(Ui.ShownByPlatform));
            string[] marked = [.. headers.Where(h => Ui.ShownByPlatform(h) != h.Replace("__", "_", StringComparison.Ordinal))];
            Assert.True(marked.Length == 0, $"The menu bar shows {string.Join(", ", marked)} with an underscore taken for an access key's mark, so a name loses it");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The status bar's path as it draws it, part by part.</summary>
    private static string[] Path(MainWindow window) =>
        [.. window.GetVisualDescendants().OfType<Control>().First(c => c.Name == "BreadcrumbHost").GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible).Select(b => string.Concat(b.GetVisualDescendants().OfType<TextBlock>().Select(Ui.Drawn)))];

    /// <summary>Fails naming every name with an underscore a place shows that it draws otherwise than as written (one cut to
    /// an ellipsis for want of room aside).</summary>
    private static void AllWhole(Visual place, string what)
    {
        string[] cut = [.. place.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && t.Text is { } text && text.Contains('_', StringComparison.Ordinal) && t.TextLayout.TextLines.All(l => !l.HasCollapsed) && Ui.Drawn(t) != text)
            .Select(t => $"'{t.Text}' as '{Ui.Drawn(t)}'")];
        Assert.True(cut.Length == 0, $"{what} draws {string.Join(", ", cut)}, a name's underscore taken for an access key's mark");
    }

    /// <summary>Every item's words of a native menu and its submenus, each brought up to date as the platform opens it.</summary>
    private static IEnumerable<string> Headers(NativeMenu menu)
    {
        foreach (NativeMenuItem item in menu.Items.OfType<NativeMenuItem>().ToList())
        {
            yield return item.Header ?? string.Empty;
            if (item.Menu is { } submenu)
            {
                foreach (string inner in Headers(Ui.Opened(submenu)))
                {
                    yield return inner;
                }
            }
        }
    }
}
