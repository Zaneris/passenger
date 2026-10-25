using Passenger.App.Navigation;
using Passenger.App.Workspace;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The window's bookmarks, the open folder's (its session's <see cref="ProjectSession.Bookmarks"/>): Toggle
/// Bookmark on the caret's line, Next and Previous across files, the Bookmarks window, and every open file's ribbons.</summary>
public partial class MainWindow
{
    private ToolWindows.BookmarksView? bookmarksView;

    /// <summary>The open folder's bookmarks.</summary>
    public BookmarkStore Bookmarks => session.Bookmarks;

    private void AddBookmarks()
    {
        // Built as it is first shown, over the session's bookmarks then.
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Bookmarks, "Bookmarks", IconKeys.Bookmark, () =>
        {
            bookmarksView = new(session.Bookmarks, (path, line) =>
                Editors.Find(path) is { HoldsText: true } open && line < open.Text.Snapshot.LineCount ? open.Text.Snapshot.GetLineText(line) : string.Empty);
            bookmarksView.GoToRequested += (_, bookmark) => NavigateTo(bookmark.Path, new TextPosition(bookmark.Line, 0));
            return bookmarksView;
        }, Docking.ToolAnchor.LeftBottom)
        {
            Action = AppActions.ShowBookmarks,
            Menus = [new MenuPlace("Navigate/Bookmarks", 0, 3)],
        });
        AddBookmarkActions();
        Editors.Opened += (_, document) =>
        {
            document.Bookmarks = session.Bookmarks.LinesIn(document.Path);
            document.Text.Changed += (_, _) => session.Bookmarks.Follow(document.Path);
        };
    }

    /// <summary>A session's bookmarks in the Bookmarks window and the open files' ribbons, while it is the window's.</summary>
    private void BindBookmarks(ProjectSession next)
    {
        bookmarksView?.Bind(next.Bookmarks);
        next.Bookmarks.Changed += (_, path) =>
        {
            if (session != next)
            {
                return;
            }
            foreach (OpenDocument document in Editors.Documents.Where(d => d.Path == path))
            {
                document.Bookmarks = next.Bookmarks.LinesIn(path);
            }
        };
        foreach (OpenDocument document in Editors.Documents)
        {
            document.Bookmarks = next.Bookmarks.LinesIn(document.Path);
        }
    }

    /// <summary>Toggle Bookmark on the caret's line, and Next and Previous Bookmark from it through every file.</summary>
    private void AddBookmarkActions()
    {
        actions!.Add(new IdeAction(AppActions.ToggleBookmark, "Toggle Bookmark", "Bookmarks", context => session.Bookmarks.Toggle(context.File!.Path, context.Caret.Line))
        {
            WhyNot = Refusals.NeedsLines,
            Menus = [new MenuPlace("Navigate/Bookmarks", 0, 0)],
        });
        Step(AppActions.NextBookmark, "Next Bookmark", 1);
        Step(AppActions.PreviousBookmark, "Previous Bookmark", -1);

        void Step(string id, string title, int step) => actions.Add(new IdeAction(id, title, "Bookmarks", context =>
        {
            Bookmark next = session.Bookmarks.Next(context.File?.Path, context.File is null ? -1 : context.Caret.Line, step)!;
            NavigateTo(next.Path, new TextPosition(next.Line, 0));
        })
        {
            WhyNot = _ => session.Bookmarks.All.Count == 0 ? "No bookmarks" : null,
            Menus = [new MenuPlace("Navigate/Bookmarks", 0, step > 0 ? 1 : 2)],
        });
    }
}
