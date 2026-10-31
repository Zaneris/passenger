using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Navigation;
using Passenger.Editor;

namespace Passenger.App.ToolWindows;

/// <summary>The Bookmarks window: every bookmark with its file, line and the line's text, in the order they were
/// set; a double-click or Enter goes there, Delete takes it away. It shows the open folder's (<see cref="Bind"/>).</summary>
public sealed class BookmarksView : DockPanel
{
    private BookmarkStore store;
    private readonly Func<string, int, string> lineText;
    private readonly ListBox list;
    private readonly TextBlock placeholder;

    public BookmarksView(BookmarkStore bookmarks, Func<string, int, string> lineText)
    {
        store = bookmarks;
        this.lineText = lineText;
        list = new ListBox
        {
            Classes = { StyleClasses.Tree },
        };
        ReusedRows.Use(list, Row);
        list.DoubleTapped += (_, _) => Go();
        list.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.F4)
            {
                Go();
                e.Handled = true;
            }
            else if (e.Key is Key.Delete or Key.Back && list.SelectedItem is Bookmark gone)
            {
                store.Remove(gone);
                e.Handled = true;
            }
        };
        placeholder = new TextBlock { Classes = { StyleClasses.EmptyText } };
        ActionTips.SetWords(placeholder, AppActions.ToggleBookmark, keys => $"{ActionTips.Named("Toggle Bookmark", keys)} marks the caret's line");
        Children.Add(new Panel { Children = { list, placeholder } });
        store.Changed += OnChanged;
        ListMenus.Attach(list, () => list.SelectedItem is not Bookmark chosen ? [] :
        [
            ListMenus.Item("Go to", "F4", Go),
            ListMenus.Item("Remove Bookmark", "Delete", () => store.Remove(chosen)),
        ]);
        Refresh();
    }

    /// <summary>A bookmark to go to.</summary>
    public event EventHandler<Bookmark>? GoToRequested;

    /// <summary>Shows another folder's bookmarks from now on, its session's in place of the last's.</summary>
    public void Bind(BookmarkStore next)
    {
        store.Changed -= OnChanged;
        store = next;
        store.Changed += OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, string path) => Refresh();

    public IReadOnlyList<Bookmark> Rows => list.ItemsSource as IReadOnlyList<Bookmark> ?? [];

    private void Refresh()
    {
        list.ItemsSource = store.All.ToList();
        placeholder.IsVisible = store.All.Count == 0;
    }

    private void Go()
    {
        if (list.SelectedItem is Bookmark bookmark)
        {
            GoToRequested?.Invoke(this, bookmark);
        }
    }

    /// <summary>A bookmark's row: its file and line, then the line's code, muted.</summary>
    private Control Row()
    {
        TextBlock where = new() { Width = 200, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold };
        TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center, FontFamily = CodeEditor.CodeFont, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Classes.Add(StyleClasses.Muted);
        // The line takes what the row leaves, so a narrow window ends it in an ellipsis rather than cutting it off.
        DockPanel.SetDock(where, Dock.Left);
        DockPanel row = new() { Height = ListRow.Height, Margin = new Thickness(8, 0), Children = { where, text } };
        return row.Showing<Bookmark>(bookmark =>
        {
            where.Text = $"{bookmark.Name}:{bookmark.Line + 1}";
            text.Text = lineText(bookmark.Path, bookmark.Line).Trim();
        });
    }
}
